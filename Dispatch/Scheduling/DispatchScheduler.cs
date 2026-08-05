using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Routes;
using RapidTransitMod.Core;
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

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Func<Entity, bool> m_Managed;
        private readonly Func<Entity, int[]> m_Times;
        private readonly Func<Entity, int> m_Hold;
        private readonly Func<Entity, float> m_ReadDispatchCache;
        private readonly Func<Entity, float> m_ReadLineLapCache;
        private readonly Func<Entity, Entity> m_ResolveRuntimeControllerVehicle;
        private readonly Func<Entity, DynamicBuffer<RouteWaypoint>, bool> m_IsLineStable;
        private readonly Func<Entity, VehicleState, float, DynamicBuffer<RouteWaypoint>, ClockSnapshot, bool> m_ShouldHoldSpawnForNearestRunningCandidate;
        private readonly Func<Entity, DynamicBuffer<RouteWaypoint>, float, float, bool, ClockSnapshot, bool> m_HasBorderlineOriginArrivalCandidate;
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
            ModRuntimeHostSystem runtime,
            Func<Entity, bool> managed,
            Func<Entity, int[]> times,
            Func<Entity, int> hold,
            Func<Entity, float> readDispatchCache,
            Func<Entity, float> readLineLapCache,
            Func<Entity, Entity> resolveRuntimeControllerVehicle,
            Func<Entity, DynamicBuffer<RouteWaypoint>, bool> isLineStable,
            Func<Entity, VehicleState, float, DynamicBuffer<RouteWaypoint>, ClockSnapshot, bool> shouldHoldSpawnForNearestRunningCandidate,
            Func<Entity, DynamicBuffer<RouteWaypoint>, float, float, bool, ClockSnapshot, bool> hasBorderlineOriginArrivalCandidate,
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

        public void Tick(ClockSnapshot clockSnapshot, IReadOnlyList<Entity> lines)
        {
            int nowMinute = clockSnapshot.NowMinute;
            m_Runtime.m_SpawnLeadTheory?.Tick();
            m_SlotClaims.Clear();
            m_RetireDecisions.Clear();
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);

            if (lines == null)
                return;

            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                Entity line = lines[lineIndex];
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
                    if (useManagedTimes)
                        m_Runtime.m_SpawnLeadTheory?.Ensure(line, wps);

                    int originHoldLimitMinutes = useManagedTimes
                        ? m_Hold(line)
                        : ModRuntimeHostSystem.SPAWN_LEAD_MINUTES;

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

                    int slotMinute = useManagedTimes && appliedTargets.Length > 0 ? appliedTargets[0] : NextSlotMin(nowMinute);
                    int maxSlots = useManagedTimes
                        ? appliedTargets.Length
                        : ModRuntimeHostSystem.SPAWN_LEAD_MINUTES / ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES + 1;
                    int dispatchCycleMinutes = useManagedTimes
                        ? ScheduleTargets.Headway(appliedTargets)
                        : ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES;
                    int nextAppliedTargetIndex = useManagedTimes
                        ? ScheduleTargets.NextIndex(nowMinute, appliedTargets)
                        : -1;
                    int previousAppliedTarget = useManagedTimes
                        ? ScheduleTargets.Previous(nowMinute, appliedTargets)
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

                    uint originHoldLimitFrames = clockSnapshot.ToFramesCeil(originHoldLimitMinutes);
                    float dispatchScanLimitFrames = originHoldLimitFrames;
                    if (useManagedTimes)
                    {
                        float scanSpawnLeadFrames = m_Policy.SpawnLead(line, lineDurationFrames);
                        uint scanRoundingFrames = math.max(1u, clockSnapshot.ToFramesCeil(1d)) - 1u;
                        dispatchScanLimitFrames = math.max(
                            originHoldLimitFrames,
                            scanSpawnLeadFrames + originHoldLimitFrames + scanRoundingFrames);
                    }

                    for (int s = useManagedTimes ? -1 : 0; s < maxSlots; s++)
                    {
                        if (useManagedTimes)
                        {
                            if (s < 0)
                            {
                                slotMinute = previousAppliedTarget;
                                if (slotMinute < 0)
                                    continue;
                            }
                            else
                            {
                                int slotIndex = (nextAppliedTargetIndex + s) % appliedTargets.Length;
                                slotMinute = appliedTargets[slotIndex];
                                if (slotMinute == previousAppliedTarget)
                                    continue;
                            }
                        }

                        int minutesToSlot = useManagedTimes
                            ? ScheduleClock.Lead(nowMinute, slotMinute)
                            : ScheduleClock.MinutesUntil(nowMinute, slotMinute);
                        float slotFramesAway = clockSnapshot.ToFramesCeil(minutesToSlot);
                        if (ScheduleClock.Expired(nowMinute, slotMinute))
                        {
                            slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                            continue;
                        }
                        if (slotFramesAway > dispatchScanLimitFrames)
                        {
                            if (useManagedTimes && s >= 0)
                                break;
                            slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                            continue;
                        }

                        bool spawnOnlyScan = useManagedTimes && slotFramesAway > originHoldLimitFrames;

                        Entity currentOccupier = Entity.Null;
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity vehicle = runtimeVehicles[i];
                            if (!m_Runtime.m_VehicleView.TryGetSlot(vehicle, out int currentSlotMinute) || currentSlotMinute != slotMinute)
                                continue;
                            if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState currentState) || currentState != VehicleState.Running)
                                continue;

                            currentOccupier = vehicle;
                            break;
                        }

                        if (currentOccupier != Entity.Null)
                        {
                            slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                            continue;
                        }

                        Entity currentHolder = Entity.Null;
                        int currentHolderTier = 99;
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity vehicle = runtimeVehicles[i];
                            if (!m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute) || targetMinute != slotMinute)
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
                                m_LogDispatchSlotHeld(line, slotMinute, currentHolder, line, wps, nowMinute, nowFrame, "idle-or-holding-holder");
                            slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                            continue;
                        }

                        if (currentHolder != Entity.Null
                            && currentHolderTier == 1
                            && m_Runtime.m_VehicleView.TryGetState(currentHolder, out VehicleState currentHolderState)
                            && currentHolderState == VehicleState.Running)
                        {
                            float holderEtaFrames = m_Runtime.m_LineTimes.Run(currentHolder, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                            if (m_ShouldHoldSpawnForNearestRunningCandidate(
                                currentHolder,
                                currentHolderState,
                                holderEtaFrames,
                                wps,
                                clockSnapshot))
                            {
                                TryLogSpawnBlocked(line, lineTag, slotMinute);
                                slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                                continue;
                            }
                        }

                        LineTick tick = new LineTick(
                            line,
                            wps,
                            runtimeVehicles,
                            nowMinute,
                            nowFrame,
                            appliedTargets,
                            originHoldLimitMinutes,
                            lineDurationFrames,
                            lineHasHistory);
                        VehiclePick.Result pick = m_Pick.Pick(
                            tick,
                            clockSnapshot,
                            slotMinute,
                            minutesToSlot,
                            spawnOnlyScan,
                            slotFramesAway);
                        SlotPlan.Status slotStatus = m_SlotPlan.Build(
                            tick,
                            pick,
                            slotMinute,
                            currentHolder,
                            lineTag,
                            dispatchCycleMinutes,
                            m_SlotClaims);
                        if (slotStatus == SlotPlan.Status.Skip)
                        {
                            slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
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
                                if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute) && targetMinute >= 0)
                                    continue;

                                hasIdleOrHoldingUnassigned = true;
                                break;
                            }

                            if (hasIdleOrHoldingUnassigned)
                            {
                                slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
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
                                if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute) && targetMinute >= 0)
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
                                    string etaText = pick.NearestEtaFrames == float.MaxValue
                                        ? "?"
                                        : clockSnapshot.ToMinutes(pick.NearestEtaFrames).ToString("F1") + "分钟";
                                    TryLogScheduleDiagnostic(line, lineTag, slotMinute, pick.NearVehicle, pick.NearState, etaText, pick.NearReason);
                                }

                                if (m_ShouldHoldSpawnForNearestRunningCandidate(
                                    pick.NearVehicle,
                                    pick.NearState,
                                    pick.NearestEtaFrames,
                                    wps,
                                    clockSnapshot))
                                {
                                    TryLogSpawnBlocked(line, lineTag, slotMinute);
                                    slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                                    continue;
                                }
                                if (m_Runtime.m_LineProfile.HasInboundNearOrigin(line, wps, Entity.Null, ModRuntimeHostSystem.ORIGIN_CONGESTION_RADIUS_METERS))
                                {
                                    TryLogSpawnBlocked(line, lineTag, slotMinute);
                                    slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                                    continue;
                                }
                                if (m_HasBorderlineOriginArrivalCandidate(
                                    line,
                                    wps,
                                    slotFramesAway,
                                    lineDurationFrames,
                                    lineHasHistory,
                                    clockSnapshot))
                                {
                                    TryLogSpawnBlocked(line, lineTag, slotMinute);
                                    slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                                    continue;
                                }

                                float spawnLeadFrames = m_Policy.SpawnLead(line, lineDurationFrames);
                                float reachableWindowFrames = ScheduleClock.ReachFrames(clockSnapshot, slotMinute);
                                if (spawnLeadFrames > reachableWindowFrames)
                                {
                                    TryLogSpawnLeadUnreachable(
                                        line,
                                        lineTag,
                                        slotMinute,
                                        spawnLeadFrames,
                                        reachableWindowFrames,
                                        clockSnapshot);
                                    slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                                    continue;
                                }

                                float spawnTriggerFrames = spawnLeadFrames
                                    + originHoldLimitFrames;
                                if (slotFramesAway > spawnTriggerFrames)
                                {
                                    slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                                    continue;
                                }

                                int actualCount = m_Runtime.m_LineVehicles.Count(line, rvBuffers);
                                m_Runtime.m_SpawningLines[line] = actualCount + 1;
                                m_Runtime.m_LineSpawnRequestFrame[line] = nowFrame;
                                string spawnIntent = m_Runtime.m_SpawnIntentTrace.Create(
                                    line,
                                    slotMinute,
                                    nowFrame,
                                    spawnLeadFrames,
                                    m_Policy.SpawnLeadSource(line),
                                    originHoldLimitMinutes,
                                    actualCount,
                                    pick.NearVehicle,
                                    pick.NearState,
                                    pick.NearestEtaFrames,
                                    pick.NearReason);
                                m_RecordLineSpawnTriggerSummary(line, nowMinute, slotMinute, actualCount);
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.log.Info("[调度] " + lineTag + " 班次" + ModRuntimeHostSystem.SlotStr(slotMinute)
                                        + " 无候选，触发产车+1 (当前=" + actualCount
                                        + " 圈时=" + clockSnapshot.ToMinutes(lineDurationFrames).ToString("F1") + "游戏分钟)"
                                        + spawnIntent);
                                }
                            }
                        }

                        slotMinute = (slotMinute + ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
                    }
            }

            m_Runtime.m_SpawnLeadTheory?.Tick();
        }

        public int NextSlotMin(int nowMinute)
        {
            return ScheduleClock.NextSlot(nowMinute);
        }

        public int PreviousSlotMin(int nowMinute)
        {
            return ScheduleClock.PreviousSlot(nowMinute);
        }

        public bool IsCurrentOrRecentSlot(int nowMinute, int targetMinute)
        {
            return ScheduleClock.CurrentOrRecent(nowMinute, targetMinute);
        }

        public int DispatchLeadMinutes(int nowMinute, int targetMinute)
        {
            return ScheduleClock.Lead(nowMinute, targetMinute);
        }

        public float DispatchReachableWindowFrames(ClockSnapshot clockSnapshot, int targetMinute)
        {
            return ScheduleClock.ReachFrames(clockSnapshot, targetMinute);
        }

        public int NextManagedTarget(Entity line, int nowMinute)
        {
            if (line == Entity.Null || !m_Managed(line))
                return -1;

            int[] appliedTargets = m_Times(line);
            return ScheduleTargets.Next(nowMinute, appliedTargets);
        }

        private void TryLogSpawnBlocked(Entity line, string lineTag, int slotMinute)
        {
            if (!RtLog.VerboseEnabled)
                return;

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (m_Runtime.m_LastSpawnBlockedLogFrame.TryGetValue(line, out uint lastFrame)
                && (nowFrame - lastFrame) < ModRuntimeHostSystem.SPAWN_BLOCKED_LOG_COOLDOWN_FRAMES)
            {
                return;
            }

            m_Runtime.m_LastSpawnBlockedLogFrame[line] = nowFrame;
            m_Runtime.log.Info("[SpawnBlocked] " + lineTag + " 班次" + ModRuntimeHostSystem.SlotStr(slotMinute)
                + " 始发站附近已有回流车，跳过产车");
        }

        private void TryLogSpawnLeadUnreachable(
            Entity line,
            string lineTag,
            int slotMinute,
            float spawnLeadFrames,
            float reachableWindowFrames,
            ClockSnapshot clockSnapshot)
        {
            if (!RtLog.VerboseEnabled)
                return;

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            ulong key = MakeLineSlotKey(line, slotMinute) ^ 0x8000000000000000UL;
            if (m_Runtime.m_LastScheduleDiagnosticLogFrame.TryGetValue(key, out uint lastFrame)
                && (nowFrame - lastFrame) < ModRuntimeHostSystem.SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES)
            {
                return;
            }

            m_Runtime.m_LastScheduleDiagnosticLogFrame[key] = nowFrame;
            m_Runtime.log.Info("[SpawnLeadBlocked] " + lineTag
                + " 班次" + ModRuntimeHostSystem.SlotStr(slotMinute)
                + " 出库ETA=" + clockSnapshot.ToMinutes(spawnLeadFrames).ToString("F1") + "分钟"
                + " 正点窗口剩余=" + clockSnapshot.ToMinutes(reachableWindowFrames).ToString("F1") + "分钟"
                + "，跳过产车");
        }

        private void TryLogScheduleDiagnostic(
            Entity line,
            string lineTag,
            int slotMinute,
            Entity nearestVehicle,
            VehicleState nearestState,
            string etaText,
            string nearestReason)
        {
            if (!RtLog.VerboseEnabled)
                return;

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            ulong key = MakeLineSlotKey(line, slotMinute);
            if (m_Runtime.m_LastScheduleDiagnosticLogFrame.TryGetValue(key, out uint lastFrame)
                && (nowFrame - lastFrame) < ModRuntimeHostSystem.SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES)
            {
                return;
            }

            m_Runtime.m_LastScheduleDiagnosticLogFrame[key] = nowFrame;
            m_Runtime.log.Info("[调度诊断] " + lineTag + " 班次" + ModRuntimeHostSystem.SlotStr(slotMinute)
                + " 最近候选车辆" + nearestVehicle.Index
                + " state=" + nearestState
                + " eta=" + etaText
                + " reason=" + nearestReason);
        }

        private static ulong MakeLineSlotKey(Entity line, int slotMinute)
        {
            return ((ulong)(uint)line.Index << 32) | (uint)(slotMinute & 0xFFFF);
        }
    }
}
