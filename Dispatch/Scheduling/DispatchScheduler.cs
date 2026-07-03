using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Routes;
using RapidTransitMod.Dispatch.Scheduling;
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
        private readonly Func<Entity, bool> m_Managed;
        private readonly Func<Entity, int[]> m_Times;
        private readonly Func<Entity, int> m_Hold;
        private readonly Func<Entity, float> m_ReadDispatchCache;
        private readonly Func<Entity, float> m_ReadLineLapCache;
        private readonly Func<Entity, Entity> m_ResolveRuntimeControllerVehicle;
        private readonly Func<Entity, DynamicBuffer<RouteWaypoint>, bool> m_IsLineStable;
        private readonly Func<Entity, VehicleState, float, DynamicBuffer<RouteWaypoint>, bool> m_ShouldHoldSpawnForNearestRunningCandidate;
        private readonly Func<Entity, DynamicBuffer<RouteWaypoint>, float, float, bool, bool> m_HasBorderlineOriginArrivalCandidate;
        private readonly Action<Entity, int, Entity, Entity, DynamicBuffer<RouteWaypoint>, int, uint, string> m_LogDispatchSlotHeld;
        private readonly Action<Entity, int, int, int> m_RecordLineSpawnTriggerSummary;
        private readonly SchedulePolicy m_Policy;
        private readonly VehiclePick m_Pick;
        private readonly SlotPlan m_SlotPlan;
        private readonly List<SlotClaim> m_SlotClaims = new List<SlotClaim>();
        private readonly List<RetireDecision> m_RetireDecisions = new List<RetireDecision>();

        internal IReadOnlyList<SlotClaim> SlotClaims => m_SlotClaims;
        internal IReadOnlyList<RetireDecision> RetireDecisions => m_RetireDecisions;
        internal SchedulePolicy Policy => m_Policy;
        internal SlotPlan Plan => m_SlotPlan;

        public DispatchScheduler(
            DispatchRuntimeSystem runtime,
            Func<Entity, bool> managed,
            Func<Entity, int[]> times,
            Func<Entity, int> hold,
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
            m_Managed = managed;
            m_Times = times;
            m_Hold = hold;
            m_ReadDispatchCache = readDispatchCache;
            m_ReadLineLapCache = readLineLapCache;
            m_ResolveRuntimeControllerVehicle = resolveRuntimeControllerVehicle;
            m_IsLineStable = isLineStable;
            m_ShouldHoldSpawnForNearestRunningCandidate = shouldHoldSpawnForNearestRunningCandidate;
            m_HasBorderlineOriginArrivalCandidate = hasBorderlineOriginArrivalCandidate;
            m_LogDispatchSlotHeld = logDispatchSlotHeld;
            m_RecordLineSpawnTriggerSummary = recordLineSpawnTriggerSummary;
            m_Policy = new SchedulePolicy(runtime, managed, times, hold, readDispatchCache);
            m_Pick = new VehiclePick(runtime);
            m_SlotPlan = new SlotPlan(runtime, m_Policy, managed, times, hold, resolveRuntimeControllerVehicle);
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
                    if (!DispatchLineEligibility.IsDispatchTransportLine(m_Runtime.EntityManager, line))
                        continue;
                    if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
                        continue;
                    if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> wps) || wps.Length < 2)
                        continue;
                    if (!DispatchLineEligibility.ComputeDispatchSupport(
                            m_Runtime.EntityManager,
                            line,
                            waypoint => m_Runtime.m_Resolve.Stop(waypoint)).Supported)
                        continue;
                    if (!m_IsLineStable(line, wps))
                        continue;

                    bool useManagedTimes = m_Managed(line);
                    int[] appliedTargets = useManagedTimes
                        ? m_Times(line)
                        : null;
                    if (useManagedTimes && (appliedTargets == null || appliedTargets.Length == 0))
                        continue;

                    int originHoldLimitMinutes = useManagedTimes
                        ? m_Hold(line)
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
                        m_Runtime.m_RuntimeLog.RouteOwnerMismatch(line, runtimeVehicle, "schedule-buffer");
                    }

                    bool lineHasHistory = false;
                    for (int i = 0; i < runtimeVehicles.Count; i++)
                    {
                        Entity vehicle = runtimeVehicles[i];
                        if (m_Runtime.m_Observation.TryLapFrames(vehicle, out uint lapFrames) && lapFrames > 0)
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
                        if (!m_Runtime.m_LineRange.Needs(vehicle) && m_Runtime.m_LineRange.CanFinish(vehicle))
                            continue;

                        m_RetireDecisions.Add(new RetireDecision(vehicle, "在站维护/里程不足"));
                    }

                    int slot = useManagedTimes && appliedTargets.Length > 0 ? appliedTargets[0] : NextSlotMin(nowMin);
                    int maxSlots = useManagedTimes
                        ? appliedTargets.Length
                        : DispatchRuntimeSystem.SPAWN_LEAD_MIN / DispatchRuntimeSystem.SLOT_INTERVAL + 1;
                    int dispatchCycleMinutes = useManagedTimes
                        ? ScheduleTargets.Headway(appliedTargets)
                        : DispatchRuntimeSystem.SLOT_INTERVAL;
                    int nextAppliedTargetIndex = useManagedTimes
                        ? ScheduleTargets.NextIndex(nowMin, appliedTargets)
                        : -1;
                    int previousAppliedTarget = useManagedTimes
                        ? ScheduleTargets.Previous(nowMin, appliedTargets)
                        : -1;

                    float lineDurationFrames = 0f;
                    {
                        float maxLapFrames = 0f;
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity vehicle = runtimeVehicles[i];
                            if (m_Runtime.m_Observation.TryLapFrames(vehicle, out uint lapFrames) && lapFrames > maxLapFrames)
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
                                if (!m_Runtime.m_Observation.TryLapFrames(vehicle, out uint lapFrames) || lapFrames == 0)
                                    m_Runtime.m_ObsPersist.SetLapFrames(vehicle, (uint)cachedLapFrames);
                            }
                        }
                        else
                        {
                            lineDurationFrames = m_Runtime.m_LineTimes.Duration(line) * 60f;
                        }
                    }

                    int dispatchScanLimitMinutes = originHoldLimitMinutes;
                    if (useManagedTimes)
                    {
                        float scanSpawnLeadFrames = m_Policy.SpawnLead(line, lineDurationFrames);
                        float scanSpawnTriggerFrames = scanSpawnLeadFrames
                            + originHoldLimitMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
                        dispatchScanLimitMinutes = math.max(
                            originHoldLimitMinutes,
                            (int)math.ceil(scanSpawnTriggerFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE));
                    }

                    for (int s = useManagedTimes ? -1 : 0; s < maxSlots; s++)
                    {
                        if (useManagedTimes)
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

                        int minsToSlot = useManagedTimes
                            ? ScheduleClock.Lead(nowMin, slot)
                            : ScheduleClock.MinutesUntil(nowMin, slot);
                        if (ScheduleClock.Expired(nowMin, slot))
                        {
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }
                        if (minsToSlot > dispatchScanLimitMinutes)
                        {
                            if (useManagedTimes && s >= 0)
                                break;
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        bool spawnOnlyScan = useManagedTimes && minsToSlot > originHoldLimitMinutes;

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
                            if (RtLog.VerboseEnabled)
                                m_LogDispatchSlotHeld(line, slot, currentHolder, line, wps, nowMin, nowFrame, "idle-or-holding-holder");
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        if (currentHolder != Entity.Null
                            && currentHolderTier == 1
                            && m_Runtime.m_VehicleView.TryGetState(currentHolder, out VehicleState currentHolderState)
                            && currentHolderState == VehicleState.Running)
                        {
                            float holderEta = m_Runtime.m_LineTimes.Run(currentHolder, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                            if (m_ShouldHoldSpawnForNearestRunningCandidate(currentHolder, currentHolderState, holderEta, wps))
                            {
                                TryLogSpawnBlocked(line, lineTag, slot);
                                slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                continue;
                            }
                        }

                        float slotFramesAway = minsToSlot * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
                        if (useManagedTimes)
                            slotFramesAway = ScheduleClock.Lead(nowMin, slot) * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;

                        LineTick tick = new LineTick(
                            line,
                            wps,
                            runtimeVehicles,
                            nowMin,
                            nowFrame,
                            appliedTargets,
                            originHoldLimitMinutes,
                            lineDurationFrames,
                            lineHasHistory);
                        VehiclePick.Result pick = m_Pick.Pick(tick, slot, minsToSlot, spawnOnlyScan, slotFramesAway);
                        SlotPlan.Status slotStatus = m_SlotPlan.Build(
                            tick,
                            pick,
                            slot,
                            currentHolder,
                            lineTag,
                            dispatchCycleMinutes,
                            m_SlotClaims);
                        if (slotStatus == SlotPlan.Status.Skip)
                        {
                            slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        if (slotStatus == SlotPlan.Status.None)
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
                                    ? m_Runtime.m_LineTimes.Run(vehicle, line, wps, nowFrame, lineDurationFrames, lineHasHistory)
                                    : m_Runtime.m_LineTimes.Prep(vehicle, line, wps, lineDurationFrames);
                                if (etaFrames == float.MaxValue)
                                    continue;

                                if (etaFrames <= slotFramesAway)
                                    canMakeItCount++;
                            }

                            if (canMakeItCount == 0 && !m_Runtime.m_SpawningLines.ContainsKey(line))
                            {
                                if (RtLog.VerboseEnabled && pick.NearVehicle != Entity.Null)
                                {
                                    string etaText = pick.NearEta == float.MaxValue
                                        ? "?"
                                        : (pick.NearEta / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟";
                                    TryLogScheduleDiagnostic(line, lineTag, slot, pick.NearVehicle, pick.NearState, etaText, pick.NearReason);
                                }

                                if (m_ShouldHoldSpawnForNearestRunningCandidate(pick.NearVehicle, pick.NearState, pick.NearEta, wps))
                                {
                                    TryLogSpawnBlocked(line, lineTag, slot);
                                    slot = (slot + DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
                                    continue;
                                }
                                if (m_Runtime.m_LineProfile.HasInboundNearOrigin(line, wps, Entity.Null, DispatchRuntimeSystem.ORIGIN_CONGESTION_RADIUS_METERS))
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

                                float spawnLeadFrames = m_Policy.SpawnLead(line, lineDurationFrames);
                                float reachableWindowFrames = ScheduleClock.ReachFrames(nowMin, slot);
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

                                int actualCount = m_Runtime.m_LineVehicles.Count(line, rvBuffers);
                                m_Runtime.m_SpawningLines[line] = actualCount + 1;
                                m_Runtime.m_LineSpawnRequestFrame[line] = nowFrame;
                                m_RecordLineSpawnTriggerSummary(line, nowMin, slot, actualCount);
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.log.Info("[调度] " + lineTag + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                                        + " 无候选，触发产车+1 (当前=" + actualCount
                                        + " 圈时=" + (lineDurationFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "游戏分钟)");
                                }
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
            return ScheduleClock.NextSlot(nowMin);
        }

        public int PreviousSlotMin(int nowMin)
        {
            return ScheduleClock.PreviousSlot(nowMin);
        }

        public bool IsCurrentOrRecentSlot(int nowMin, int targetMin)
        {
            return ScheduleClock.CurrentOrRecent(nowMin, targetMin);
        }

        public int DispatchLeadMinutes(int nowMin, int targetMin)
        {
            return ScheduleClock.Lead(nowMin, targetMin);
        }

        public float DispatchReachableWindowFrames(int nowMin, int targetMin)
        {
            return ScheduleClock.ReachFrames(nowMin, targetMin);
        }

        public int NextManagedTarget(Entity line, int nowMin)
        {
            if (line == Entity.Null || !m_Managed(line))
                return -1;

            int[] appliedTargets = m_Times(line);
            return ScheduleTargets.Next(nowMin, appliedTargets);
        }

        private void TryLogSpawnBlocked(Entity line, string lineTag, int slot)
        {
            if (!RtLog.VerboseEnabled)
                return;

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
            if (!RtLog.VerboseEnabled)
                return;

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
            if (!RtLog.VerboseEnabled)
                return;

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
    }
}
