using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal sealed class DispatchScheduler
    {
        internal readonly struct SlotClaim
        {
            public readonly Entity Vehicle;
            public readonly int Target;
            public readonly Entity ReleasedVehicle;
            public readonly bool CommitHold;
            public readonly bool ClearIdle;

            public SlotClaim(Entity vehicle, int target, Entity releasedVehicle, bool commitHold, bool clearIdle)
            {
                Vehicle = vehicle;
                Target = target;
                ReleasedVehicle = releasedVehicle;
                CommitHold = commitHold;
                ClearIdle = clearIdle;
            }
        }

        internal readonly struct RetireDecision
        {
            public readonly Entity Vehicle;
            public readonly string Reason;

            public RetireDecision(Entity vehicle, string reason)
            {
                Vehicle = vehicle;
                Reason = reason;
            }
        }

        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Func<Entity, bool> m_IsDispatchRuntimeManagedLine;
        private readonly Func<Entity, int[]> m_GetAppliedWorkbenchDepartureMinutes;
        private readonly Func<Entity, int> m_GetWorkbenchOriginHoldLimitMinutes;
        private readonly Func<Entity, float> m_ReadDispatchCache;
        private readonly Func<Entity, float> m_ReadLineLapCache;
        private readonly Func<Entity, Entity> m_ResolveRuntimeControllerVehicle;
        private readonly Func<Entity, DynamicBuffer<RouteWaypoint>, bool> m_IsLineStable;
        private readonly Func<Entity, VehicleState, float, DynamicBuffer<RouteWaypoint>, bool> m_ShouldHoldSpawnForNearestRunningCandidate;
        private readonly Func<Entity, DynamicBuffer<RouteWaypoint>, float, float, bool, bool> m_HasBorderlineOriginArrivalCandidate;
        private readonly Action<Entity, int, Entity, Entity, DynamicBuffer<RouteWaypoint>, int, uint, string> m_LogDispatchSlotHeld;
        private readonly Action<Entity, int, int, int> m_RecordLineSpawnTriggerSummary;
        private readonly List<SlotClaim> m_SlotClaims = new List<SlotClaim>();
        private readonly List<RetireDecision> m_RetireDecisions = new List<RetireDecision>();

        internal IReadOnlyList<SlotClaim> SlotClaims => m_SlotClaims;
        internal IReadOnlyList<RetireDecision> RetireDecisions => m_RetireDecisions;

        public DispatchScheduler(
            DispatchRuntimeSystem runtime,
            Func<Entity, bool> isDispatchRuntimeManagedLine,
            Func<Entity, int[]> getAppliedWorkbenchDepartureMinutes,
            Func<Entity, int> getWorkbenchOriginHoldLimitMinutes,
            Func<Entity, float> readDispatchCache,
            Func<Entity, float> readLineLapCache,
            Func<Entity, Entity> resolveRuntimeControllerVehicle,
            Func<Entity, DynamicBuffer<RouteWaypoint>, bool> isLineStable,
            Func<Entity, VehicleState, float, DynamicBuffer<RouteWaypoint>, bool> shouldHoldSpawnForNearestRunningCandidate,
            Func<Entity, DynamicBuffer<RouteWaypoint>, float, float, bool, bool> hasBorderlineOriginArrivalCandidate,
            Action<Entity, int, Entity, Entity, DynamicBuffer<RouteWaypoint>, int, uint, string> logDispatchSlotHeld,
            Action<Entity, int, int, int> recordLineSpawnTriggerSummary)
        {
            m_Runtime = runtime;
            m_IsDispatchRuntimeManagedLine = isDispatchRuntimeManagedLine;
            m_GetAppliedWorkbenchDepartureMinutes = getAppliedWorkbenchDepartureMinutes;
            m_GetWorkbenchOriginHoldLimitMinutes = getWorkbenchOriginHoldLimitMinutes;
            m_ReadDispatchCache = readDispatchCache;
            m_ReadLineLapCache = readLineLapCache;
            m_ResolveRuntimeControllerVehicle = resolveRuntimeControllerVehicle;
            m_IsLineStable = isLineStable;
            m_ShouldHoldSpawnForNearestRunningCandidate = shouldHoldSpawnForNearestRunningCandidate;
            m_HasBorderlineOriginArrivalCandidate = hasBorderlineOriginArrivalCandidate;
            m_LogDispatchSlotHeld = logDispatchSlotHeld;
            m_RecordLineSpawnTriggerSummary = recordLineSpawnTriggerSummary;
        }

        public void Tick(int nowMin)
        {
            m_SlotClaims.Clear();
            m_RetireDecisions.Clear();
            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);

            try
            {
                foreach (Entity line in lines)
                {
                    if (!m_Runtime.EntityManager.Exists(line))
                        continue;
                    if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
                        continue;
                    if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> wps) || wps.Length < 2)
                        continue;
                    if (!m_IsLineStable(line, wps))
                        continue;

                    bool useWorkbenchSchedule = m_IsDispatchRuntimeManagedLine(line);
                    int[] appliedTargets = useWorkbenchSchedule
                        ? m_GetAppliedWorkbenchDepartureMinutes(line)
                        : null;
                    if (useWorkbenchSchedule && (appliedTargets == null || appliedTargets.Length == 0))
                        continue;

                    int originHoldLimitMinutes = useWorkbenchSchedule
                        ? m_GetWorkbenchOriginHoldLimitMinutes(line)
                        : DispatchRuntimeSystem.SPAWN_LEAD_MIN;

                    uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
                    string lineTag = "线路" + line.Index;
                    float cachedLapFrames = m_ReadLineLapCache(line);
                    List<Entity> runtimeVehicles = new List<Entity>(rvs.Length);
                    HashSet<Entity> seenRuntimeVehicles = new HashSet<Entity>();

                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity runtimeVehicle = m_ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                        if (runtimeVehicle == Entity.Null || !m_Runtime.EntityManager.Exists(runtimeVehicle))
                            continue;
                        if (!seenRuntimeVehicles.Add(runtimeVehicle))
                            continue;

                        runtimeVehicles.Add(runtimeVehicle);
                        m_Runtime.LogRouteVehicleOwnerMismatch(line, runtimeVehicle, "schedule-buffer");
                    }

                    bool lineHasHistory = false;
                    for (int i = 0; i < runtimeVehicles.Count; i++)
                    {
                        Entity vehicle = runtimeVehicles[i];
                        if (m_Runtime.m_LapObservations.TryFrames(vehicle, out uint lapFrames) && lapFrames > 0)
                        {
                            lineHasHistory = true;
                            break;
                        }
                    }

                    if (!lineHasHistory && cachedLapFrames > 0f)
                        lineHasHistory = true;

                    for (int i = 0; i < runtimeVehicles.Count; i++)
                    {
                        Entity vehicle = runtimeVehicles[i];
                        if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                            continue;
                        if (state != VehicleState.Idle && state != VehicleState.Holding)
                            continue;
                        if (!m_Runtime.NeedsMaintenance(vehicle) && m_Runtime.CanFinishNextLap(vehicle))
                            continue;

                        m_RetireDecisions.Add(new RetireDecision(vehicle, "在站维护/里程不足"));
                    }

                    int slot = useWorkbenchSchedule && appliedTargets.Length > 0 ? appliedTargets[0] : NextSlotMin(nowMin);
                    int maxSlots = useWorkbenchSchedule
                        ? appliedTargets.Length
                        : DispatchRuntimeSystem.SPAWN_LEAD_MIN / DispatchRuntimeSystem.SLOT_INTERVAL + 1;
                    int dispatchCycleMinutes = useWorkbenchSchedule
                        ? ScheduledHeadwayMinutes(appliedTargets)
                        : DispatchRuntimeSystem.SLOT_INTERVAL;
                    int nextAppliedTargetIndex = useWorkbenchSchedule
                        ? NextScheduledTargetIndex(nowMin, appliedTargets)
                        : -1;
                    int previousAppliedTarget = useWorkbenchSchedule
                        ? PreviousScheduledTarget(nowMin, appliedTargets)
                        : -1;

                    float lineDurationFrames = 0f;
                    {
                        float maxLapFrames = 0f;
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity vehicle = runtimeVehicles[i];
                            if (m_Runtime.m_LapObservations.TryFrames(vehicle, out uint lapFrames) && lapFrames > maxLapFrames)
                                maxLapFrames = lapFrames;
                        }

                        if (maxLapFrames > 0f)
                        {
                            lineDurationFrames = maxLapFrames;
                        }
                        else if (cachedLapFrames > 0f)
                        {
                            lineDurationFrames = cachedLapFrames;
                            for (int i = 0; i < runtimeVehicles.Count; i++)
                            {
                                Entity vehicle = runtimeVehicles[i];
                                if (!m_Runtime.m_LapObservations.TryFrames(vehicle, out uint lapFrames) || lapFrames == 0)
                                    m_Runtime.m_LapObservations.SetFrames(vehicle, (uint)cachedLapFrames);
                            }
                        }
                        else
                        {
                            lineDurationFrames = m_Runtime.CalculateLineDuration(line) * 60f;
                        }
                    }

                    int dispatchScanLimitMinutes = originHoldLimitMinutes;
                    if (useWorkbenchSchedule)
                    {
                        float scanSpawnLeadFrames = EstimateSpawnLeadFrames(line, lineDurationFrames);
                        float scanSpawnTriggerFrames = scanSpawnLeadFrames
                            + originHoldLimitMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
                        dispatchScanLimitMinutes = math.max(
                            originHoldLimitMinutes,
                            (int)math.ceil(scanSpawnTriggerFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE));
                    }

                    for (int s = useWorkbenchSchedule ? -1 : 0; s < maxSlots; s++)
                    {
                        if (useWorkbenchSchedule)
                        {
                            if (s < 0)
                            {
                                slot = previousAppliedTarget;
                                if (slot < 0)
                                    continue;
                            }
                            else
                            {
                                int slotIndex = (nextAppliedTargetIndex + s) % appliedTargets.Length;
                                slot = appliedTargets[slotIndex];
                                if (slot == previousAppliedTarget)
                                    continue;
                            }
                        }

                        int minsToSlot = useWorkbenchSchedule
                            ? DispatchLeadMinutes(nowMin, slot)
                            : MinutesUntil(nowMin, slot);
                        if (IsExpired(nowMin, slot))
                        {
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }
                        if (minsToSlot > dispatchScanLimitMinutes)
                        {
                            if (useWorkbenchSchedule && s >= 0)
                                break;
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        bool spawnOnlyScan = useWorkbenchSchedule && minsToSlot > originHoldLimitMinutes;

                        Entity currentOccupier = Entity.Null;
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity vehicle = runtimeVehicles[i];
                            if (!m_Runtime.m_VehicleView.TryGetSlot(vehicle, out int currentSlot) || currentSlot != slot)
                                continue;
                            if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState currentState) || currentState != VehicleState.Running)
                                continue;

                            currentOccupier = vehicle;
                            break;
                        }

                        if (currentOccupier != Entity.Null)
                        {
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        Entity currentHolder = Entity.Null;
                        int currentHolderTier = 99;
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity vehicle = runtimeVehicles[i];
                            if (!m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin) || targetMin != slot)
                                continue;

                            currentHolder = vehicle;
                            if (m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState holderState)
                                && (holderState == VehicleState.Idle || holderState == VehicleState.Holding))
                            {
                                currentHolderTier = 0;
                            }
                            else
                            {
                                currentHolderTier = 1;
                            }
                            break;
                        }

                        if (currentHolder != Entity.Null && currentHolderTier == 0)
                        {
                            m_LogDispatchSlotHeld(line, slot, currentHolder, line, wps, nowMin, nowFrame, "idle-or-holding-holder");
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        if (currentHolder != Entity.Null
                            && currentHolderTier == 1
                            && m_Runtime.m_VehicleView.TryGetState(currentHolder, out VehicleState currentHolderState)
                            && currentHolderState == VehicleState.Running)
                        {
                            float holderEta = m_Runtime.EstimateRunningArrivalFrames(currentHolder, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                            if (m_ShouldHoldSpawnForNearestRunningCandidate(currentHolder, currentHolderState, holderEta, wps))
                            {
                                TryLogSpawnBlocked(line, lineTag, slot);
                                slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                continue;
                            }
                        }

                        float slotFramesAway = minsToSlot * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
                        if (useWorkbenchSchedule)
                            slotFramesAway = DispatchLeadMinutes(nowMin, slot) * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;

                        Entity bestVehicle = Entity.Null;
                        int bestTier = 99;
                        float bestEta = float.MaxValue;
                        float bestRemaining = -1f;
                        int bestPrevTarget = -1;
                        Entity nearestVehicle = Entity.Null;
                        VehicleState nearestState = VehicleState.Preparing;
                        float nearestEta = float.MaxValue;
                        string nearestReason = "none";

                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity vehicle = runtimeVehicles[i];
                            if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                                continue;
                            if (state == VehicleState.Retiring || m_Runtime.m_BVMisfire.Contains(vehicle))
                                continue;

                            int assignedTarget = -1;
                            if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin) && targetMin >= 0)
                            {
                                assignedTarget = targetMin;
                                if (targetMin != slot)
                                {
                                    if (IsCurrentOrRecentSlot(nowMin, targetMin))
                                        continue;
                                    int minsToAssigned = MinutesUntil(nowMin, targetMin);
                                    if (minsToAssigned <= minsToSlot)
                                        continue;
                                }
                            }

                            if (state == VehicleState.Running
                                && assignedTarget >= 0
                                && assignedTarget != slot
                                && IsCurrentOrRecentSlot(nowMin, assignedTarget)
                                && m_Runtime.IsBorderlineOriginArrivalCandidate(vehicle, wps))
                            {
                                continue;
                            }

                            if (m_Runtime.NeedsMaintenance(vehicle) || !m_Runtime.CanFinishNextLap(vehicle))
                                continue;

                            int tier = 99;
                            float eta = float.MaxValue;

                            if (state == VehicleState.Idle || state == VehicleState.Holding)
                            {
                                if (spawnOnlyScan)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = vehicle;
                                        nearestState = state;
                                        nearestEta = 0f;
                                        nearestReason = "outside-origin-hold-window";
                                    }
                                    continue;
                                }

                                int cachedWaypointIndex = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypoint) ? cachedWaypoint : -1;
                                if (cachedWaypointIndex != 0)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = vehicle;
                                        nearestState = state;
                                        nearestEta = 0f;
                                        nearestReason = "cachedWp=" + cachedWaypointIndex;
                                    }
                                    continue;
                                }

                                tier = 0;
                                eta = 0f;
                            }
                            else if (state == VehicleState.Running)
                            {
                                float etaFrames = m_Runtime.EstimateRunningArrivalFrames(vehicle, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                                if (etaFrames == float.MaxValue)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = vehicle;
                                        nearestState = state;
                                        nearestEta = float.MaxValue;
                                        nearestReason = "no-running-eta";
                                    }
                                    continue;
                                }

                                tier = 1;
                                eta = etaFrames;
                            }
                            else if (state == VehicleState.Preparing)
                            {
                                float etaFrames = m_Runtime.EstimatePreparingArrivalFrames(vehicle, line, wps, nowFrame, lineDurationFrames);
                                if (etaFrames == float.MaxValue)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = vehicle;
                                        nearestState = state;
                                        nearestEta = float.MaxValue;
                                        nearestReason = "no-preparing-eta";
                                    }
                                    continue;
                                }

                                tier = 1;
                                eta = etaFrames;
                            }
                            else
                            {
                                continue;
                            }

                            if (eta > slotFramesAway)
                            {
                                if (eta < nearestEta || nearestVehicle == Entity.Null)
                                {
                                    nearestVehicle = vehicle;
                                    nearestState = state;
                                    nearestEta = eta;
                                    nearestReason = "late-for-slot";
                                }
                                continue;
                            }

                            if (spawnOnlyScan)
                            {
                                float earliestHoldArrivalFrames = slotFramesAway
                                    - originHoldLimitMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
                                if (earliestHoldArrivalFrames > 0f && eta < earliestHoldArrivalFrames)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = vehicle;
                                        nearestState = state;
                                        nearestEta = eta;
                                        nearestReason = "before-origin-hold-window";
                                    }
                                    continue;
                                }
                            }

                            float remaining = m_Runtime.GetRemainingRange(vehicle);
                            bool better = tier < bestTier
                                || (tier == bestTier && eta < bestEta)
                                || (tier == bestTier && eta == bestEta && remaining > bestRemaining);
                            if (better)
                            {
                                bestVehicle = vehicle;
                                bestTier = tier;
                                bestEta = eta;
                                bestRemaining = remaining;
                                bestPrevTarget = assignedTarget;
                            }
                        }

                        if (currentHolder != Entity.Null && bestVehicle == currentHolder)
                        {
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        if (bestVehicle != Entity.Null)
                        {
                            VehicleState bestState = m_Runtime.m_VehicleView.GetState(bestVehicle);
                            m_Runtime.LogCrossLineCandidate(line, bestVehicle, bestState, slot, bestEta, bestPrevTarget);
                            if (bestState == VehicleState.Idle || bestState == VehicleState.Holding)
                            {
                                if (bestPrevTarget < 0)
                                {
                                    int holdingCount = 0;
                                    for (int i = 0; i < runtimeVehicles.Count; i++)
                                    {
                                        Entity vehicle = runtimeVehicles[i];
                                        if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                                            continue;
                                        if (state == VehicleState.Holding)
                                        {
                                            holdingCount++;
                                            continue;
                                        }
                                        if (state == VehicleState.Idle
                                            && m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin)
                                            && targetMin >= 0)
                                        {
                                            holdingCount++;
                                        }
                                    }

                                    int holdingCap = Math.Max(1, originHoldLimitMinutes / dispatchCycleMinutes);
                                    if (holdingCount >= holdingCap)
                                    {
                                        slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                        continue;
                                    }
                                }

                                m_SlotClaims.Add(new SlotClaim(bestVehicle, slot, currentHolder, commitHold: true, clearIdle: true));
                            }
                            else
                            {
                                m_SlotClaims.Add(new SlotClaim(bestVehicle, slot, currentHolder, commitHold: false, clearIdle: false));
                                m_Runtime.log.Info("[调度候选] " + lineTag + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                                    + " 选择车辆" + bestVehicle.Index
                                    + " state=" + bestState
                                    + " eta=" + (bestEta / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                                    + " prevTarget=" + (bestPrevTarget >= 0 ? DispatchRuntimeSystem.SlotStr(bestPrevTarget) : "-"));
                            }
                        }
                        else
                        {
                            bool hasIdleOrHoldingUnassigned = false;
                            for (int i = 0; i < runtimeVehicles.Count; i++)
                            {
                                Entity vehicle = runtimeVehicles[i];
                                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                                    continue;
                                if (state != VehicleState.Holding && state != VehicleState.Idle)
                                    continue;
                                if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin) && targetMin >= 0)
                                    continue;

                                hasIdleOrHoldingUnassigned = true;
                                break;
                            }

                            if (hasIdleOrHoldingUnassigned)
                            {
                                slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                continue;
                            }

                            int canMakeItCount = 0;
                            for (int i = 0; i < runtimeVehicles.Count; i++)
                            {
                                Entity vehicle = runtimeVehicles[i];
                                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                                    continue;
                                if (state != VehicleState.Preparing && state != VehicleState.Running)
                                    continue;
                                if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin) && targetMin >= 0)
                                    continue;

                                float etaFrames = state == VehicleState.Running
                                    ? m_Runtime.EstimateRunningArrivalFrames(vehicle, line, wps, nowFrame, lineDurationFrames, lineHasHistory)
                                    : m_Runtime.EstimatePreparingArrivalFrames(vehicle, line, wps, nowFrame, lineDurationFrames);
                                if (etaFrames == float.MaxValue)
                                    continue;

                                if (etaFrames <= slotFramesAway)
                                    canMakeItCount++;
                            }

                            if (canMakeItCount == 0 && !m_Runtime.m_SpawningLines.ContainsKey(line))
                            {
                                if (nearestVehicle != Entity.Null)
                                {
                                    string etaText = nearestEta == float.MaxValue
                                        ? "?"
                                        : (nearestEta / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟";
                                    TryLogScheduleDiagnostic(line, lineTag, slot, nearestVehicle, nearestState, etaText, nearestReason);
                                }

                                if (m_ShouldHoldSpawnForNearestRunningCandidate(nearestVehicle, nearestState, nearestEta, wps))
                                {
                                    TryLogSpawnBlocked(line, lineTag, slot);
                                    slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                    continue;
                                }
                                if (m_Runtime.HasInboundVehicleNearOrigin(line, wps, Entity.Null, DispatchRuntimeSystem.ORIGIN_CONGESTION_RADIUS_METERS))
                                {
                                    TryLogSpawnBlocked(line, lineTag, slot);
                                    slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                    continue;
                                }
                                if (m_HasBorderlineOriginArrivalCandidate(line, wps, slotFramesAway, lineDurationFrames, lineHasHistory))
                                {
                                    TryLogSpawnBlocked(line, lineTag, slot);
                                    slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                    continue;
                                }

                                float spawnLeadFrames = EstimateSpawnLeadFrames(line, lineDurationFrames);
                                float reachableWindowFrames = DispatchReachableWindowFrames(nowMin, slot);
                                if (spawnLeadFrames > reachableWindowFrames)
                                {
                                    TryLogSpawnLeadUnreachable(line, lineTag, slot, spawnLeadFrames, reachableWindowFrames);
                                    slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                    continue;
                                }

                                float spawnTriggerFrames = spawnLeadFrames
                                    + originHoldLimitMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
                                if (slotFramesAway > spawnTriggerFrames)
                                {
                                    slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                    continue;
                                }

                                if (lineDurationFrames > 0f)
                                {
                                    int theoreticalCount = (int)math.ceil(
                                        lineDurationFrames / (dispatchCycleMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE));
                                    int actualCountForCap = m_Runtime.CountActiveVehicles(line, rvBuffers);
                                    if (actualCountForCap >= theoreticalCount)
                                    {
                                        slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                        continue;
                                    }
                                }

                                int actualCount = m_Runtime.CountActiveVehicles(line, rvBuffers);
                                m_Runtime.m_SpawningLines[line] = actualCount + 1;
                                m_Runtime.m_LineSpawnRequestFrame[line] = nowFrame;
                                m_RecordLineSpawnTriggerSummary(line, nowMin, slot, actualCount);
                                m_Runtime.log.Info("[调度] " + lineTag + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                                    + " 无候选，触发产车+1 (当前=" + actualCount
                                    + " 圈时=" + (lineDurationFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "游戏分钟)");
                            }
                        }

                        slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                    }
                }
            }
            finally
            {
                lines.Dispose();
            }
        }

        public int NextSlotMin(int nowMin)
        {
            return ((nowMin / DispatchRuntimeSystem.SLOT_INTERVAL) + 1) * DispatchRuntimeSystem.SLOT_INTERVAL % 1440;
        }

        public int MinutesUntil(int nowMin, int targetMin)
        {
            int diff = targetMin - nowMin;
            if (diff <= 0)
                diff += 1440;
            return diff;
        }

        public bool IsTimeReached(int nowMin, int targetMin)
        {
            return ((nowMin - targetMin + 1440) % 1440) <= DispatchRuntimeSystem.SLOT_GRACE_MIN;
        }

        public int PreviousSlotMin(int nowMin)
        {
            return ((nowMin / DispatchRuntimeSystem.SLOT_INTERVAL) * DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
        }

        public bool IsCurrentOrRecentSlot(int nowMin, int targetMin)
        {
            return IsTimeReached(nowMin, targetMin) || CanLateDispatch(nowMin, targetMin);
        }

        public int OverdueMinutes(int nowMin, int targetMin)
        {
            return (nowMin - targetMin + 1440) % 1440;
        }

        public bool CanLateDispatch(int nowMin, int targetMin)
        {
            if (!LateDispatchEnabled())
                return false;

            int overdue = OverdueMinutes(nowMin, targetMin);
            int lateWindow = EffectiveLateDispatchWindowMinutes();
            return overdue > DispatchRuntimeSystem.SLOT_GRACE_MIN && overdue <= lateWindow;
        }

        public bool IsSoftExpired(int nowMin, int targetMin)
        {
            int overdue = OverdueMinutes(nowMin, targetMin);
            int releaseAfter = math.max(DispatchRuntimeSystem.SLOT_GRACE_MIN, EffectiveLateDispatchWindowMinutes());
            return overdue > releaseAfter && overdue <= DispatchRuntimeSystem.SLOT_INTERVAL;
        }

        public bool IsHardExpired(int nowMin, int targetMin)
        {
            int overdue = OverdueMinutes(nowMin, targetMin);
            return overdue > DispatchRuntimeSystem.SLOT_INTERVAL
                && overdue <= DispatchRuntimeSystem.SPAWN_LEAD_MIN + DispatchRuntimeSystem.SLOT_GRACE_MIN;
        }

        public bool IsExpired(int nowMin, int targetMin)
        {
            int overdue = OverdueMinutes(nowMin, targetMin);
            return overdue > DispatchRuntimeSystem.SLOT_GRACE_MIN
                && overdue <= DispatchRuntimeSystem.SPAWN_LEAD_MIN + DispatchRuntimeSystem.SLOT_GRACE_MIN;
        }

        public int DispatchLeadMinutes(int nowMin, int targetMin)
        {
            int overdue = OverdueMinutes(nowMin, targetMin);
            if (overdue <= DispatchRuntimeSystem.SLOT_GRACE_MIN)
                return 0;

            return MinutesUntil(nowMin, targetMin);
        }

        public float DispatchReachableWindowFrames(int nowMin, int targetMin)
        {
            return DispatchLeadMinutes(nowMin, targetMin) * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
        }

        public int PreviousScheduledTarget(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            int previous = -1;
            for (int i = 0; i < targets.Count; i++)
            {
                int target = targets[i];
                if (target <= nowMin)
                    previous = target;
                else
                    break;
            }

            return previous >= 0 ? previous : targets[targets.Count - 1];
        }

        public int NextScheduledTarget(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            int bestTarget = targets[0];
            int bestDistance = MinutesUntil(nowMin, bestTarget);
            for (int i = 1; i < targets.Count; i++)
            {
                int target = targets[i];
                int distance = MinutesUntil(nowMin, target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTarget = target;
                }
            }

            return bestTarget;
        }

        public int NextScheduledTargetIndex(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] >= nowMin)
                    return i;
            }

            return 0;
        }

        public int ScheduledHeadwayMinutes(IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count <= 1)
                return DispatchRuntimeSystem.SLOT_INTERVAL;

            int bestGap = 1440;
            for (int i = 0; i < targets.Count; i++)
            {
                int current = targets[i];
                int next = targets[(i + 1) % targets.Count];
                int gap = (next - current + 1440) % 1440;
                if (gap <= 0)
                    continue;
                if (gap < bestGap)
                    bestGap = gap;
            }

            return bestGap < 1440 ? bestGap : DispatchRuntimeSystem.SLOT_INTERVAL;
        }

        public int NextManagedTarget(Entity line, int nowMin)
        {
            if (line == Entity.Null || !m_IsDispatchRuntimeManagedLine(line))
                return -1;

            int[] appliedTargets = m_GetAppliedWorkbenchDepartureMinutes(line);
            return NextScheduledTarget(nowMin, appliedTargets);
        }

        public bool ShouldRetireWaitingVehicle(Entity line, int nowMin, int targetMin)
        {
            if (line == Entity.Null || !m_IsDispatchRuntimeManagedLine(line) || targetMin < 0)
                return false;
            if (IsCurrentOrRecentSlot(nowMin, targetMin))
                return false;

            return MinutesUntil(nowMin, targetMin) > m_GetWorkbenchOriginHoldLimitMinutes(line);
        }

        public bool ShouldProtectIdle(Entity line, Entity vehicle, int nowMin, int nextTargetMin = -1)
        {
            if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin) && targetMin >= 0)
            {
                if (CanLateDispatch(nowMin, targetMin))
                    return true;
                return MinutesUntil(nowMin, targetMin) <= DispatchRuntimeSystem.YIELD_PROTECT_MINUTES;
            }

            if (nextTargetMin >= 0)
                return MinutesUntil(nowMin, nextTargetMin) <= DispatchRuntimeSystem.YIELD_PROTECT_MINUTES;

            return MinutesUntil(nowMin, FallbackProtectTarget(line, nowMin)) <= DispatchRuntimeSystem.YIELD_PROTECT_MINUTES;
        }

        public int FallbackProtectTarget(Entity line, int nowMin)
        {
            if (line != Entity.Null && m_IsDispatchRuntimeManagedLine(line))
            {
                int nextManagedTarget = NextManagedTarget(line, nowMin);
                if (nextManagedTarget >= 0)
                    return nextManagedTarget;
            }

            return NextSlotMin(nowMin);
        }

        public bool IsTargetOccupied(Entity line, Entity vehicle, int targetMin)
        {
            if (line == Entity.Null || targetMin < 0)
                return false;

            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = rvs[i].m_Vehicle;
                if (other == vehicle || !m_Runtime.EntityManager.Exists(other))
                    continue;

                if (m_Runtime.m_VehicleView.TryGetSlot(other, out int currentSlot) && currentSlot == targetMin)
                    return true;

                if (m_Runtime.m_VehicleView.TryGetTarget(other, out int targetSlot)
                    && targetSlot == targetMin
                    && m_Runtime.m_VehicleView.TryGetState(other, out VehicleState state)
                    && (state == VehicleState.Preparing || state == VehicleState.Holding || state == VehicleState.Idle))
                {
                    return true;
                }
            }

            return false;
        }

        public float EstimateSpawnLeadFrames(Entity line, float lineDurationFrames)
        {
            float cachedFrames = m_ReadDispatchCache(line);
            if (cachedFrames > 0f)
                return cachedFrames;

            float estimateMinutes = 0f;
            if (lineDurationFrames > 0f)
                estimateMinutes = (lineDurationFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE) * 0.2f;
            if (estimateMinutes <= 0f)
                estimateMinutes = 6f;

            estimateMinutes = math.clamp(
                estimateMinutes,
                DispatchRuntimeSystem.DISPATCH_ESTIMATE_MIN_MINUTES,
                DispatchRuntimeSystem.DISPATCH_ESTIMATE_MAX_MINUTES);
            return estimateMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
        }

        public float SpawnTriggerBufferMinutes(float spawnLeadFrames)
        {
            float spawnLeadMinutes = spawnLeadFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
            return spawnLeadMinutes < DispatchRuntimeSystem.SPAWN_TRIGGER_BUFFER_THRESHOLD_MINUTES
                ? DispatchRuntimeSystem.SPAWN_TRIGGER_BUFFER_SHORT_MINUTES
                : DispatchRuntimeSystem.SPAWN_TRIGGER_BUFFER_LONG_MINUTES;
        }

        public bool TryAssignCurrentOrLateSlot(
            Entity line,
            Entity vehicle,
            int nowMin,
            string lineTag,
            string stateTag,
            out Entity releasedVehicle,
            out int lateSlot)
        {
            releasedVehicle = Entity.Null;
            lateSlot = -1;
            int previousSlot = PreviousSlotMin(nowMin);
            if (!IsCurrentOrRecentSlot(nowMin, previousSlot))
                return false;
            if (IsTargetOccupied(line, vehicle, previousSlot))
                return false;

            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = m_ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                if (other == vehicle || !m_Runtime.EntityManager.Exists(other))
                    continue;
                if (!m_Runtime.m_VehicleView.TryGetTarget(other, out int otherTarget) || otherTarget != previousSlot)
                    continue;

                if (m_Runtime.m_VehicleView.TryGetState(other, out VehicleState otherState)
                    && (otherState == VehicleState.Preparing || otherState == VehicleState.Idle || otherState == VehicleState.Holding))
                {
                    return false;
                }

                releasedVehicle = other;
                LogVehicleStateOnce(
                    m_Runtime.m_LateDispatchLogCache,
                    vehicle,
                    "LateDispatchTakeover|" + previousSlot + "|" + other.Index,
                    "[补发接管] " + lineTag + " 车辆" + vehicle.Index
                        + " 接管班次" + DispatchRuntimeSystem.SlotStr(previousSlot)
                        + " 释放车辆" + other.Index
                        + " state=" + (m_Runtime.m_VehicleView.TryGetState(other, out VehicleState releasedState) ? releasedState.ToString() : "?"));
            }

            lateSlot = previousSlot;
            if (CanLateDispatch(nowMin, previousSlot))
            {
                LogVehicleStateOnce(
                    m_Runtime.m_LateDispatchLogCache,
                    vehicle,
                    "LateDispatchCandidate|" + previousSlot + "|" + stateTag,
                    "[补发候选] " + lineTag + " 车辆" + vehicle.Index
                        + " state=" + stateTag
                        + " 候选补发班次" + DispatchRuntimeSystem.SlotStr(previousSlot)
                        + " 已过期" + OverdueMinutes(nowMin, previousSlot) + "分钟");
            }

            return true;
        }

        public bool TryAssignUpcomingTarget(
            Entity line,
            Entity vehicle,
            int nowMin,
            string lineTag,
            string stateTag,
            out int assignedTarget)
        {
            assignedTarget = -1;
            if (line == Entity.Null || vehicle == Entity.Null || !m_IsDispatchRuntimeManagedLine(line))
                return false;

            int nextTarget = NextManagedTarget(line, nowMin);
            if (nextTarget < 0 || IsCurrentOrRecentSlot(nowMin, nextTarget))
                return false;

            int waitMinutes = MinutesUntil(nowMin, nextTarget);
            if (waitMinutes > m_GetWorkbenchOriginHoldLimitMinutes(line))
                return false;
            if (IsTargetOccupied(line, vehicle, nextTarget))
                return false;

            assignedTarget = nextTarget;
            LogVehicleStateOnce(
                m_Runtime.m_LateDispatchLogCache,
                vehicle,
                "UpcomingTarget|" + nextTarget + "|" + stateTag,
                "[预分配] " + lineTag + " 车辆" + vehicle.Index
                    + " state=" + stateTag
                    + " 预分配未来班次" + DispatchRuntimeSystem.SlotStr(nextTarget)
                    + " 距今" + waitMinutes + "分钟");
            return true;
        }

        public bool TryAssignCurrentOrLateScheduledTarget(
            Entity line,
            Entity vehicle,
            int nowMin,
            string lineTag,
            string stateTag,
            IReadOnlyList<int> targets,
            out Entity releasedVehicle,
            out int lateTarget)
        {
            releasedVehicle = Entity.Null;
            lateTarget = -1;
            int previousTarget = PreviousScheduledTarget(nowMin, targets);
            if (previousTarget < 0 || !IsCurrentOrRecentSlot(nowMin, previousTarget))
                return false;
            if (IsTargetOccupied(line, vehicle, previousTarget))
                return false;

            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = m_ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                if (other == vehicle || !m_Runtime.EntityManager.Exists(other))
                    continue;
                if (!m_Runtime.m_VehicleView.TryGetTarget(other, out int otherTarget) || otherTarget != previousTarget)
                    continue;

                if (m_Runtime.m_VehicleView.TryGetState(other, out VehicleState otherState)
                    && (otherState == VehicleState.Preparing || otherState == VehicleState.Idle || otherState == VehicleState.Holding))
                {
                    return false;
                }

                releasedVehicle = other;
                LogVehicleStateOnce(
                    m_Runtime.m_LateDispatchLogCache,
                    vehicle,
                    "LateDispatchTakeover|" + previousTarget + "|" + other.Index,
                    "[补发接管] " + lineTag + " 车辆" + vehicle.Index
                        + " 接管班次" + DispatchRuntimeSystem.SlotStr(previousTarget)
                        + " 释放车辆" + other.Index
                        + " state=" + (m_Runtime.m_VehicleView.TryGetState(other, out VehicleState releasedState) ? releasedState.ToString() : "?"));
            }

            lateTarget = previousTarget;
            if (CanLateDispatch(nowMin, previousTarget))
            {
                LogVehicleStateOnce(
                    m_Runtime.m_LateDispatchLogCache,
                    vehicle,
                    "LateDispatchCandidate|" + previousTarget + "|" + stateTag,
                    "[补发候选] " + lineTag + " 车辆" + vehicle.Index
                        + " state=" + stateTag
                        + " 候选补发班次" + DispatchRuntimeSystem.SlotStr(previousTarget)
                        + " 已过期" + OverdueMinutes(nowMin, previousTarget) + "分钟");
            }

            return true;
        }

        private int EffectiveLateDispatchWindowMinutes()
        {
            return math.clamp(DispatchRuntimeSystem.LATE_DISPATCH_WINDOW_MINUTES, 0, DispatchRuntimeSystem.SLOT_INTERVAL);
        }

        private bool LateDispatchEnabled()
        {
            return EffectiveLateDispatchWindowMinutes() > 0;
        }

        private void TryLogSpawnBlocked(Entity line, string lineTag, int slot)
        {
            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (m_Runtime.m_LastSpawnBlockedLogFrame.TryGetValue(line, out uint lastFrame)
                && (nowFrame - lastFrame) < DispatchRuntimeSystem.SPAWN_BLOCKED_LOG_COOLDOWN_FRAMES)
            {
                return;
            }

            m_Runtime.m_LastSpawnBlockedLogFrame[line] = nowFrame;
            m_Runtime.log.Info("[SpawnBlocked] " + lineTag + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                + " 始发站附近已有回流车，跳过产车");
        }

        private void TryLogSpawnLeadUnreachable(
            Entity line,
            string lineTag,
            int slot,
            float spawnLeadFrames,
            float reachableWindowFrames)
        {
            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            ulong key = MakeLineSlotKey(line, slot) ^ 0x8000000000000000UL;
            if (m_Runtime.m_LastScheduleDiagnosticLogFrame.TryGetValue(key, out uint lastFrame)
                && (nowFrame - lastFrame) < DispatchRuntimeSystem.SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES)
            {
                return;
            }

            m_Runtime.m_LastScheduleDiagnosticLogFrame[key] = nowFrame;
            m_Runtime.log.Info("[SpawnLeadBlocked] " + lineTag
                + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                + " 出库ETA=" + (spawnLeadFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                + " 正点窗口剩余=" + (reachableWindowFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                + "，跳过产车");
        }

        private void TryLogScheduleDiagnostic(
            Entity line,
            string lineTag,
            int slot,
            Entity nearestVehicle,
            VehicleState nearestState,
            string etaText,
            string nearestReason)
        {
            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            ulong key = MakeLineSlotKey(line, slot);
            if (m_Runtime.m_LastScheduleDiagnosticLogFrame.TryGetValue(key, out uint lastFrame)
                && (nowFrame - lastFrame) < DispatchRuntimeSystem.SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES)
            {
                return;
            }

            m_Runtime.m_LastScheduleDiagnosticLogFrame[key] = nowFrame;
            m_Runtime.log.Info("[调度诊断] " + lineTag + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                + " 最近候选车辆" + nearestVehicle.Index
                + " state=" + nearestState
                + " eta=" + etaText
                + " reason=" + nearestReason);
        }

        private static ulong MakeLineSlotKey(Entity line, int slot)
        {
            return ((ulong)(uint)line.Index << 32) | (uint)(slot & 0xFFFF);
        }

        internal void LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
        {
            if (cache.TryGetValue(vehicle, out string previousKey) && previousKey == key)
                return;

            cache[vehicle] = key;
            m_Runtime.log.Info(message);
        }
    }
}
