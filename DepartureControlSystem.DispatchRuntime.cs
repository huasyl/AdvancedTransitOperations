using System;
using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private void DoRetire(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            EntityCommandBuffer ecb,
            string reason = "")
        {
            m_VehicleState[v] = VehicleState.Retiring;
            m_VehicleTargetMin[v] = -1;
            m_VehicleIdleStartFrame.Remove(v);
            m_VehiclePreparingStartFrame.Remove(v);
            m_VehicleDispatchRequestStartFrame.Remove(v);
            m_BVMisfire.Remove(v);
            m_BVMisfireStartFrame.Remove(v);
            ClearForcedMidStopClosingConsist(v);
            m_LaunchCooldownUntil.Remove(v);
            m_PreparingFixCooldownUntil.Remove(v);
            m_NearingTerminus.Remove(v);
            m_OriginArrivalCandidateSinceFrame.Remove(v);
            m_ForcedOriginReadyFrame.Remove(v);
            m_RetireFixCount[v] = 0;

            string lineTag = m_VehicleLine.TryGetValue(v, out Entity le) ? "线路" + le.Index : "线路?";
            SetUILabel(v, "回库中" + (reason.Length > 0 ? "(" + reason + ")" : ""));
            log.Info("[回库] " + lineTag + " 车辆" + v.Index
                + (reason.Length > 0 ? " 原因:" + reason : "") + " -> 车库");

            if (!EntityManager.HasComponent<Owner>(v))
            {
                log.Info("[回库] " + lineTag + " 车辆" + v.Index + " 无Owner，标记删除");
                ecb.AddComponent<Deleted>(v);
                return;
            }
            tgt.m_Target = EntityManager.GetComponentData<Owner>(v).m_Owner;
            pt.m_DepartureFrame = 0;
            pt.m_State &= ~PublicTransportFlags.Boarding;
            RepathVehicle(v, pt, tgt, ecb);
            m_RetireFixCooldownUntil[v] = m_SimulationSystem.frameIndex + RETIREFIX_REPATH_COOLDOWN_FRAMES;
        }

        private void LaunchVehicle(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            DynamicBuffer<RouteWaypoint> wps,
            EntityCommandBuffer ecb)
        {
            m_ForcedOriginBoardingGraceUntil.Remove(v);
            pt.m_State &= ~PublicTransportFlags.Boarding;
            pt.m_DepartureFrame = m_SimulationSystem.frameIndex - 1;
            if (TryApplyLaunchSegmentPath(v, ref pt, ref tgt, wps, ecb))
                return;

            tgt.m_Target = wps[1].m_Waypoint;
            RepathVehicle(v, pt, tgt, ecb);
        }

        private bool TryApplyLaunchSegmentPath(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport pt,
            ref Target tgt,
            DynamicBuffer<RouteWaypoint> wps,
            EntityCommandBuffer ecb)
        {
            if (wps.Length < 2)
                return false;

            Entity route = Entity.Null;
            if (m_VehicleLine.TryGetValue(vehicle, out Entity mappedLine) && mappedLine != Entity.Null)
            {
                route = mappedLine;
            }
            else if (EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            }

            if (route == Entity.Null
                || !EntityManager.Exists(route)
                || !EntityManager.HasBuffer<RouteSegment>(route))
            {
                return false;
            }

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(route, true);
            if (segments.Length == 0)
                return false;

            Entity firstSegment = segments[0].m_Segment;
            if (firstSegment == Entity.Null
                || !EntityManager.Exists(firstSegment)
                || !EntityManager.HasBuffer<PathElement>(firstSegment))
            {
                return false;
            }

            DynamicBuffer<PathElement> segmentPath = EntityManager.GetBuffer<PathElement>(firstSegment, true);
            if (segmentPath.Length == 0)
                return false;

            tgt.m_Target = wps[1].m_Waypoint;
            ecb.SetComponent(vehicle, tgt);
            ecb.SetComponent(vehicle, pt);

            if (EntityManager.HasComponent<PathOwner>(vehicle))
            {
                ecb.SetComponent(vehicle, new PathOwner(PathFlags.Updated));
            }

            DynamicBuffer<PathElement> targetPath = ecb.SetBuffer<PathElement>(vehicle);
            targetPath.Clear();
            for (int i = 0; i < segmentPath.Length; i++)
            {
                targetPath.Add(segmentPath[i]);
            }

            ecb.AddComponent<Updated>(vehicle);
            return true;
        }

        private void CommitVehicleDepartureState(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            EntityCommandBuffer ecb)
        {
            ecb.SetComponent(v, tgt);
            ecb.SetComponent(v, pt);
            ecb.AddComponent<Updated>(v);
        }

        private void EnsurePreparingRoute(
            Entity v,
            ref Game.Vehicles.PublicTransport pt,
            ref Target tgt,
            DynamicBuffer<RouteWaypoint> wps,
            int curWpIdx,
            bool boarding,
            EntityCommandBuffer ecb)
        {
            Entity stationA = wps[0].m_Waypoint;
            bool wrongTarget = tgt.m_Target != stationA;
            bool driftedToMidStop = boarding && curWpIdx > 0;
            if (!wrongTarget && !driftedToMidStop) return;
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (wrongTarget
                && !driftedToMidStop
                && IsFreshDispatchedPreparingVehicle(v, nowFrame))
                return;
            if (m_PreparingFixCooldownUntil.TryGetValue(v, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return;

            string lineTag = m_VehicleLine.TryGetValue(v, out Entity lineEnt)
                ? "线路" + lineEnt.Index : "线路?";
            string why = driftedToMidStop
                ? (curWpIdx >= 0 ? ("偏航到 wp=" + curWpIdx) : "偏航 boarding")
                : "目标不是始发站";

            m_VehiclePreparingStartFrame[v] = nowFrame;
            m_CachedWpIdx[v] = -1;
            m_LastBoarding[v] = false;
            m_BVMisfire.Remove(v);
            m_BVMisfireStartFrame.Remove(v);
            pt.m_State &= ~PublicTransportFlags.Boarding;
            pt.m_DepartureFrame = nowFrame + 9999;
            tgt.m_Target = stationA;
            RepathVehicle(v, pt, tgt, ecb);
            m_PreparingFixCooldownUntil[v] = nowFrame + PREPARINGFIX_REPATH_COOLDOWN_FRAMES;

            log.Info("[PreparingFix] " + lineTag + " 车辆" + v.Index
                + " " + why + "，重置去始发站 wp0=" + stationA.Index);
        }

        private void EnsureRetiringRoute(
            Entity v,
            ref Game.Vehicles.PublicTransport pt,
            ref Target tgt,
            EntityCommandBuffer ecb)
        {
            if (!EntityManager.HasComponent<Owner>(v)) return;

            Entity depot = EntityManager.GetComponentData<Owner>(v).m_Owner;
            bool wrongTarget = tgt.m_Target != depot;
            bool stillBoarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
            if (!wrongTarget && !stillBoarding) return;
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_RetireFixCooldownUntil.TryGetValue(v, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return;

            string lineTag = m_VehicleLine.TryGetValue(v, out Entity lineEnt)
                ? "线路" + lineEnt.Index : "线路?";

            pt.m_State &= ~PublicTransportFlags.Boarding;
            pt.m_DepartureFrame = 0;
            tgt.m_Target = depot;
            RepathVehicle(v, pt, tgt, ecb);
            m_RetireFixCooldownUntil[v] = nowFrame + RETIREFIX_REPATH_COOLDOWN_FRAMES;
            byte fixCount = m_RetireFixCount.TryGetValue(v, out byte oldCount)
                ? (byte)(oldCount + 1)
                : (byte)1;
            m_RetireFixCount[v] = fixCount;

            if (!m_LastRetireFixLogFrame.TryGetValue(v, out uint lastLogFrame)
                || (nowFrame - lastLogFrame) >= RETIREFIX_LOG_COOLDOWN_FRAMES)
            {
                m_LastRetireFixLogFrame[v] = nowFrame;
                log.Info("[RetireFix] " + lineTag + " 车辆" + v.Index
                    + " 回库目标异常，重新指向车库 depot=" + depot.Index);
            }

            if (fixCount >= RETIREFIX_DELETE_THRESHOLD)
            {
                log.Info("[RetireAbandon] " + lineTag + " 车辆" + v.Index
                    + " 连续回库修正" + fixCount + "次，视为失效运力并删除");
                ecb.AddComponent<Deleted>(v);
            }
        }

        private void RepathVehicle(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            EntityCommandBuffer ecb)
        {
            if (EntityManager.HasComponent<PathOwner>(v))
            {
                var po = EntityManager.GetComponentData<PathOwner>(v);
                po.m_State = PathFlags.Obsolete;
                po.m_ElementIndex = 0;
                ecb.SetComponent(v, po);
            }
            ecb.SetBuffer<PathElement>(v).Clear();
            ecb.SetComponent(v, tgt);
            ecb.SetComponent(v, pt);
            ecb.AddComponent<PathfindUpdated>(v);
            ecb.AddComponent<Updated>(v);
        }

        private void ForceRetireOne(EntityCommandBuffer ecb)
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            try
            {
                foreach (var line in lines)
                {
                    if (!rvBuffers.TryGetBuffer(line, out var rvs)) continue;
                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = rvs[i].m_Vehicle;
                        if (!EntityManager.Exists(v)) continue;
                        if (!m_VehicleState.TryGetValue(v, out var st)) continue;
                        if (st == VehicleState.Retiring) continue;
                        var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        var tgt = EntityManager.GetComponentData<Target>(v);
                        log.Info("[F7] 线路" + line.Index + " 强制回库车辆" + v.Index + " (状态=" + st + ")");
                        DoRetire(v, pt, tgt, ecb, "F7强制");
                        return;
                    }
                    break;
                }
            }
            finally { lines.Dispose(); }
        }

        private void PuppetMasterControl(int nowMin)
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            var modBuffers = GetBufferLookup<RouteModifier>(false);
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);

            try
            {
                var spawnKeys = m_SpawningLines.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < spawnKeys.Length; i++)
                {
                    if (!EntityManager.Exists(spawnKeys[i]))
                    {
                        log.Info("[PuppetMaster] 清理失效产车记录 线路" + spawnKeys[i].Index);
                        m_SpawningLines.Remove(spawnKeys[i]);
                        m_LineSpawnRequestFrame.Remove(spawnKeys[i]);
                        m_LastSpawnBlockedLogFrame.Remove(spawnKeys[i]);
                    }
                }
                spawnKeys.Dispose();

                var lineStableKeys = m_LineWaypointSignature.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < lineStableKeys.Length; i++)
                {
                    if (EntityManager.Exists(lineStableKeys[i])) continue;
                    m_LineWaypointSignature.Remove(lineStableKeys[i]);
                    m_LineStableSinceFrame.Remove(lineStableKeys[i]);
                    m_LineInitialAdopted.Remove(lineStableKeys[i]);
                    m_DiagnosedLines.Remove(lineStableKeys[i]);
                    InvalidateTrackModel(lineStableKeys[i]);
                    ClearLineTimeProfiles();
                }
                lineStableKeys.Dispose();

                foreach (var line in lines)
                {
                    if (!EntityManager.Exists(line)) continue;
                    if (!wpBuffers.TryGetBuffer(line, out var wps) || wps.Length < 2) continue;
                    if (!IsLineStable(line, wps)) continue;
                    if (!IsWorkbenchTimetableApplied(line)) continue;
                    if (!EntityManager.HasComponent<PrefabRef>(line)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                    if (!EntityManager.HasComponent<TransportLineData>(prefab)) continue;
                    if (!modBuffers.TryGetBuffer(line, out var mods)) continue;

                    float iDefault = EntityManager.GetComponentData<TransportLineData>(prefab).m_DefaultVehicleInterval;
                    float D = CalculateLineDuration(line);
                    if (D <= 0f) D = iDefault;
                    if (D <= 0f) continue;

                    int actualCount = CountActiveVehicles(line, rvBuffers);
                    int nTarget = actualCount;

                    if (m_SpawningLines.TryGetValue(line, out int spawnTarget))
                    {
                        if (actualCount >= spawnTarget)
                        {
                            m_SpawningLines.Remove(line);
                            m_LineSpawnRequestFrame.Remove(line);
                            log.Info("[PuppetMaster] 线路" + line.Index + " 产车完成 actualCount=" + actualCount);
                        }
                        else
                        {
                            nTarget = spawnTarget;
                        }
                    }

                    float targetInterval;
                    if (nTarget <= 0)
                    {
                        // Allow true zero-vehicle lines by stretching the native interval far beyond
                        // a normal lap, instead of forcing the game to keep one vehicle alive.
                        targetInterval = math.max(iDefault, D) * 64f;
                    }
                    else
                    {
                        targetInterval = D / nTarget;
                    }
                    float delta = targetInterval - iDefault;
                    InjectModifier(mods, delta);
                }
            }
            finally { lines.Dispose(); }
        }

        private void SchedulerTick(EntityCommandBuffer ecb, int nowMin)
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);

            try
            {
                foreach (var line in lines)
                {
                    if (!EntityManager.Exists(line)) continue;
                    if (!rvBuffers.TryGetBuffer(line, out var rvs)) continue;
                    if (!wpBuffers.TryGetBuffer(line, out var wps) || wps.Length < 2) continue;
                    if (!IsLineStable(line, wps)) continue;
                    bool useWorkbenchSchedule = IsWorkbenchTimetableApplied(line);
                    int[] appliedTargets = useWorkbenchSchedule
                        ? GetAppliedWorkbenchDepartureMinutes(line)
                        : null;
                    if (useWorkbenchSchedule && (appliedTargets == null || appliedTargets.Length == 0))
                        continue;
                    int originHoldLimitMinutes = useWorkbenchSchedule
                        ? GetWorkbenchOriginHoldLimitMinutes(line)
                        : SPAWN_LEAD_MIN;

                    uint nowFrame = m_SimulationSystem.frameIndex;
                    string lineTag = "线路" + line.Index;
                    float cachedLapFrames = ReadLineLapCache(line);

                    bool lineHasHistory = false;
                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v0 = rvs[i].m_Vehicle;
                        if (!EntityManager.Exists(v0)) continue;
                        if (m_VehicleLapFrames.TryGetValue(v0, out uint lf0) && lf0 > 0)
                        {
                            lineHasHistory = true;
                            break;
                        }
                    }
                    if (!lineHasHistory && cachedLapFrames > 0f)
                        lineHasHistory = true;

                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = rvs[i].m_Vehicle;
                        if (!EntityManager.Exists(v)) continue;
                        if (!m_VehicleState.TryGetValue(v, out var st)) continue;
                        if (st != VehicleState.Idle && st != VehicleState.Holding) continue;
                        if (!NeedsMaintenance(v) && CanFinishNextLap(v)) continue;
                        var pt2 = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        var tgt2 = EntityManager.GetComponentData<Target>(v);
                        DoRetire(v, pt2, tgt2, ecb, "在站维护/里程不足");
                    }

                    int slot = useWorkbenchSchedule && appliedTargets.Length > 0 ? appliedTargets[0] : NextSlotMin(nowMin);
                    int maxSlots = useWorkbenchSchedule
                        ? appliedTargets.Length
                        : SPAWN_LEAD_MIN / SLOT_INTERVAL + 1;
                    int dispatchCycleMinutes = useWorkbenchSchedule
                        ? GetScheduledHeadwayMinutes(appliedTargets)
                        : SLOT_INTERVAL;
                    int nextAppliedTargetIndex = useWorkbenchSchedule
                        ? GetNextScheduledTargetIndex(nowMin, appliedTargets)
                        : -1;
                    int previousAppliedTarget = useWorkbenchSchedule
                        ? GetPreviousScheduledTargetMin(nowMin, appliedTargets)
                        : -1;

                    float lineDurationFrames = 0f;
                    {
                        float maxLapFrames = 0f;
                        for (int i = 0; i < rvs.Length; i++)
                        {
                            Entity v0 = rvs[i].m_Vehicle;
                            if (!EntityManager.Exists(v0)) continue;
                            if (m_VehicleLapFrames.TryGetValue(v0, out uint lf0) && lf0 > maxLapFrames)
                                maxLapFrames = lf0;
                        }
                        if (maxLapFrames > 0)
                        {
                            lineDurationFrames = maxLapFrames;
                        }
                        else
                        {
                            if (cachedLapFrames > 0f)
                            {
                                lineDurationFrames = cachedLapFrames;
                                for (int i = 0; i < rvs.Length; i++)
                                {
                                    Entity v0 = rvs[i].m_Vehicle;
                                    if (!EntityManager.Exists(v0)) continue;
                                    if (!m_VehicleLapFrames.TryGetValue(v0, out uint lf0) || lf0 == 0)
                                        m_VehicleLapFrames[v0] = (uint)cachedLapFrames;
                                }
                            }
                            else
                            {
                                lineDurationFrames = CalculateLineDuration(line) * 60f;
                            }
                        }
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
                            ? GetDispatchLeadMinutes(nowMin, slot)
                            : MinutesUntil(nowMin, slot);
                        if (IsSlotExpired(nowMin, slot))
                        {
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }
                        if (minsToSlot > originHoldLimitMinutes)
                        {
                            if (useWorkbenchSchedule && s >= 0)
                                break;
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        Entity currentOccupier = Entity.Null;
                        for (int i = 0; i < rvs.Length; i++)
                        {
                            Entity v = rvs[i].m_Vehicle;
                            if (!EntityManager.Exists(v)) continue;
                            if (!m_VehicleCurrentSlot.TryGetValue(v, out int vcs) || vcs != slot) continue;
                            if (!m_VehicleState.TryGetValue(v, out var currentState)) continue;
                            if (currentState != VehicleState.Running) continue;
                            currentOccupier = v;
                            break;
                        }
                        if (currentOccupier != Entity.Null)
                        {
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        Entity currentHolder = Entity.Null;
                        int currentHolderTier = 99;
                        for (int i = 0; i < rvs.Length; i++)
                        {
                            Entity v = rvs[i].m_Vehicle;
                            if (!EntityManager.Exists(v)) continue;
                            if (!m_VehicleTargetMin.TryGetValue(v, out int vtm) || vtm != slot) continue;
                            currentHolder = v;
                            if (m_VehicleState.TryGetValue(v, out var hst) &&
                                (hst == VehicleState.Idle || hst == VehicleState.Holding))
                                currentHolderTier = 0;
                            else
                                currentHolderTier = 1;
                            break;
                        }
                        if (currentHolder != Entity.Null && currentHolderTier == 0)
                        {
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }
                        if (currentHolder != Entity.Null && currentHolderTier == 1
                            && m_VehicleState.TryGetValue(currentHolder, out var holderState)
                            && holderState == VehicleState.Running)
                        {
                            float holderEta = EstimateRunningArrivalFrames(currentHolder, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                            if (ShouldHoldSpawnForNearestRunningCandidate(currentHolder, holderState, holderEta, wps))
                            {
                                TryLogSpawnBlocked(line, lineTag, nowMin, slot);
                                slot = (slot + SLOT_INTERVAL) % 1440;
                                continue;
                            }
                        }

                        float slotFramesAway = minsToSlot * (float)SIM_FRAMES_PER_MINUTE;
                        if (useWorkbenchSchedule)
                            slotFramesAway = GetDispatchLeadMinutes(nowMin, slot) * (float)SIM_FRAMES_PER_MINUTE;

                        Entity bestVehicle = Entity.Null;
                        int bestTier = 99;
                        float bestETA = float.MaxValue;
                        float bestRemaining = -1f;
                        int bestPrevTarget = -1;
                        Entity nearestVehicle = Entity.Null;
                        VehicleState nearestState = VehicleState.Preparing;
                        float nearestETA = float.MaxValue;
                        string nearestReason = "none";

                        for (int i = 0; i < rvs.Length; i++)
                        {
                            Entity v = rvs[i].m_Vehicle;
                            if (!EntityManager.Exists(v)) continue;
                            if (!m_VehicleState.TryGetValue(v, out var st)) continue;
                            if (st == VehicleState.Retiring) continue;
                            if (m_BVMisfire.Contains(v)) continue;
                            int assignedTarget = -1;
                            if (m_VehicleTargetMin.TryGetValue(v, out int vtm) && vtm >= 0)
                            {
                                assignedTarget = vtm;
                                if (vtm != slot)
                                {
                                    if (IsCurrentOrRecentDispatchableSlot(nowMin, vtm))
                                        continue;
                                    int minsToAssigned = MinutesUntil(nowMin, vtm);
                                    if (minsToAssigned <= minsToSlot)
                                        continue;
                                }
                            }
                            if (st == VehicleState.Running
                                && assignedTarget >= 0
                                && assignedTarget != slot
                                && IsCurrentOrRecentDispatchableSlot(nowMin, assignedTarget)
                                && IsBorderlineOriginArrivalCandidate(v, wps))
                            {
                                continue;
                            }
                            if (NeedsMaintenance(v) || !CanFinishNextLap(v)) continue;

                            int tier = 99;
                            float eta = float.MaxValue;

                            if (st == VehicleState.Idle || st == VehicleState.Holding)
                            {
                                int cachedIdx = m_CachedWpIdx.TryGetValue(v, out int ci) ? ci : -1;
                                if (cachedIdx != 0)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = v;
                                        nearestState = st;
                                        nearestETA = 0f;
                                        nearestReason = "cachedWp=" + cachedIdx;
                                    }
                                    continue;
                                }
                                tier = 0;
                                eta = 0f;
                            }
                            else if (st == VehicleState.Running)
                            {
                                float etaFrames = EstimateRunningArrivalFrames(v, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                                if (etaFrames == float.MaxValue)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = v;
                                        nearestState = st;
                                        nearestETA = float.MaxValue;
                                        nearestReason = "no-running-eta";
                                    }
                                    continue;
                                }
                                tier = 1;
                                eta = etaFrames;
                            }
                            else if (st == VehicleState.Preparing)
                            {
                                float etaFrames = EstimatePreparingArrivalFrames(v, line, nowFrame, lineDurationFrames);
                                if (etaFrames == float.MaxValue)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = v;
                                        nearestState = st;
                                        nearestETA = float.MaxValue;
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
                                if (eta < nearestETA || nearestVehicle == Entity.Null)
                                {
                                    nearestVehicle = v;
                                    nearestState = st;
                                    nearestETA = eta;
                                    nearestReason = "late-for-slot";
                                }
                                continue;
                            }

                            float remaining = GetRemainingRange(v);
                            bool better = (tier < bestTier)
                                       || (tier == bestTier && eta < bestETA)
                                       || (tier == bestTier && eta == bestETA && remaining > bestRemaining);
                            if (better)
                            {
                                bestVehicle = v;
                                bestTier = tier;
                                bestETA = eta;
                                bestRemaining = remaining;
                                bestPrevTarget = assignedTarget;
                            }
                        }

                        if (currentHolder != Entity.Null && bestVehicle == currentHolder)
                        {
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        if (bestVehicle != Entity.Null)
                        {
                            var bst = m_VehicleState[bestVehicle];
                            if (bst == VehicleState.Idle || bst == VehicleState.Holding)
                            {
                                if (bestPrevTarget < 0)
                                {
                                    int holdingCount = 0;
                                    for (int i = 0; i < rvs.Length; i++)
                                    {
                                        Entity rv = rvs[i].m_Vehicle;
                                        if (!EntityManager.Exists(rv)) continue;
                                        if (!m_VehicleState.TryGetValue(rv, out var rst)) continue;
                                        if (rst == VehicleState.Holding) { holdingCount++; continue; }
                                        if (rst == VehicleState.Idle
                                            && m_VehicleTargetMin.TryGetValue(rv, out int rvm) && rvm >= 0)
                                            holdingCount++;
                                    }
                                    int holdingCap = Math.Max(1, originHoldLimitMinutes / dispatchCycleMinutes);
                                    if (holdingCount >= holdingCap)
                                    {
                                        slot = (slot + SLOT_INTERVAL) % 1440;
                                        continue;
                                    }
                                }
                                if (currentHolder != Entity.Null)
                                    m_VehicleTargetMin[currentHolder] = -1;
                                AssignSlot(bestVehicle, slot, ecb);
                                m_VehicleIdleStartFrame.Remove(bestVehicle);
                            }
                            else
                            {
                                if (currentHolder != Entity.Null)
                                    m_VehicleTargetMin[currentHolder] = -1;
                                m_VehicleTargetMin[bestVehicle] = slot;
                                log.Info("[调度候选] " + lineTag + " 班次" + SlotStr(slot)
                                    + " 选择车辆" + bestVehicle.Index
                                    + " state=" + bst
                                    + " eta=" + (bestETA / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                                    + " prevTarget=" + (bestPrevTarget >= 0 ? SlotStr(bestPrevTarget) : "-"));
                            }
                        }
                        else
                        {
                            bool hasIdleOrHoldingUnassigned = false;
                            for (int i = 0; i < rvs.Length; i++)
                            {
                                Entity v = rvs[i].m_Vehicle;
                                if (!EntityManager.Exists(v)) continue;
                                if (!m_VehicleState.TryGetValue(v, out var st2)) continue;
                                if (st2 != VehicleState.Holding && st2 != VehicleState.Idle) continue;
                                if (m_VehicleTargetMin.TryGetValue(v, out int vtm2) && vtm2 >= 0) continue;
                                hasIdleOrHoldingUnassigned = true;
                                break;
                            }
                            if (hasIdleOrHoldingUnassigned)
                            {
                                slot = (slot + SLOT_INTERVAL) % 1440;
                                continue;
                            }

                            int canMakeItCount = 0;
                            int inTransitCount = 0;

                            for (int i = 0; i < rvs.Length; i++)
                            {
                                Entity v = rvs[i].m_Vehicle;
                                if (!EntityManager.Exists(v)) continue;
                                if (!m_VehicleState.TryGetValue(v, out var st2)) continue;
                                if (st2 != VehicleState.Preparing && st2 != VehicleState.Running) continue;
                                inTransitCount++;
                                if (m_VehicleTargetMin.TryGetValue(v, out int vtm2) && vtm2 >= 0) continue;

                                float etaF = float.MaxValue;
                                if (st2 == VehicleState.Running)
                                {
                                    etaF = EstimateRunningArrivalFrames(v, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                                    if (etaF == float.MaxValue)
                                        continue;
                                }
                                else
                                {
                                    etaF = EstimatePreparingArrivalFrames(v, line, nowFrame, lineDurationFrames);
                                    if (etaF == float.MaxValue)
                                        continue;
                                }

                                if (etaF <= slotFramesAway)
                                    canMakeItCount++;
                            }

                            if (canMakeItCount == 0 && !m_SpawningLines.ContainsKey(line))
                            {
                                if (nearestVehicle != Entity.Null)
                                {
                                    string etaText = nearestETA == float.MaxValue
                                        ? "?"
                                        : (nearestETA / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟";
                                    TryLogScheduleDiagnostic(line, lineTag, slot, nearestVehicle, nearestState, etaText, nearestReason);
                                }
                                if (ShouldHoldSpawnForNearestRunningCandidate(nearestVehicle, nearestState, nearestETA, wps))
                                {
                                    TryLogSpawnBlocked(line, lineTag, nowMin, slot);
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }
                                if (HasInboundVehicleNearOrigin(line, wps, Entity.Null, ORIGIN_CONGESTION_RADIUS_METERS))
                                {
                                    TryLogSpawnBlocked(line, lineTag, nowMin, slot);
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }
                                if (HasBorderlineOriginArrivalCandidate(line, wps, slotFramesAway, lineDurationFrames, lineHasHistory))
                                {
                                    TryLogSpawnBlocked(line, lineTag, nowMin, slot);
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }

                                float spawnLeadFrames = EstimateSpawnLeadFrames(line, lineDurationFrames);
                                float spawnTriggerFrames = spawnLeadFrames
                                    + GetSpawnTriggerBufferMinutes(spawnLeadFrames) * (float)SIM_FRAMES_PER_MINUTE;
                                if (slotFramesAway > spawnTriggerFrames)
                                {
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }

                                if (lineDurationFrames > 0f)
                                {
                                    int theoreticalCount = (int)math.ceil(
                                        lineDurationFrames / (dispatchCycleMinutes * (float)SIM_FRAMES_PER_MINUTE));
                                    int actualCountForCap = CountActiveVehicles(line, rvBuffers);
                                    if (actualCountForCap >= theoreticalCount)
                                    {
                                        slot = (slot + SLOT_INTERVAL) % 1440;
                                        continue;
                                    }
                                }

                                int actualCount = CountActiveVehicles(line, rvBuffers);
                                m_SpawningLines[line] = actualCount + 1;
                                m_LineSpawnRequestFrame[line] = nowFrame;
                                RecordLineSpawnTriggerSummary(line, nowMin, slot, actualCount);
                                log.Info("[调度] " + lineTag + " 班次" + SlotStr(slot)
                                    + " 无候选，触发产车+1 (当前=" + actualCount
                                    + " 圈时=" + (lineDurationFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "游戏分钟)");
                            }
                        }

                        slot = (slot + SLOT_INTERVAL) % 1440;
                    }
                }
            }
            finally { lines.Dispose(); }
        }

        private void ForceConfiguredDepotTransportVehicleRequests()
        {
            if (m_TransportVehicleRequestQuery.IsEmptyIgnoreFilter)
                return;

            NativeArray<Entity> requestEntities = m_TransportVehicleRequestQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < requestEntities.Length; i++)
                {
                    Entity request = requestEntities[i];
                    if (!EntityManager.Exists(request) || EntityManager.HasComponent<Dispatched>(request))
                        continue;
                    if (!EntityManager.HasComponent<TransportVehicleRequest>(request)
                        || !EntityManager.HasComponent<ServiceRequest>(request))
                    {
                        continue;
                    }

                    ServiceRequest serviceRequest = EntityManager.GetComponentData<ServiceRequest>(request);
                    if ((serviceRequest.m_Flags & ServiceRequestFlags.Reversed) != 0)
                        continue;

                    TransportVehicleRequest vehicleRequest = EntityManager.GetComponentData<TransportVehicleRequest>(request);
                    Entity line = vehicleRequest.m_Route;
                    if (line == Entity.Null
                        || !EntityManager.Exists(line)
                        || !EntityManager.HasComponent<TransportLine>(line))
                    {
                        continue;
                    }

                    Entity configuredDepot = GetConfiguredAllowedDepot(line);
                    if (!IsConfiguredDepotCompatibleWithLine(configuredDepot, line))
                        continue;

                    if (!TryGetForcedTransportVehicleRequestDestination(line, out Entity destinationWaypoint))
                        continue;

                    PathInformation forcedPath = default;
                    forcedPath.m_Origin = configuredDepot;
                    forcedPath.m_Destination = destinationWaypoint;

                    if (EntityManager.HasComponent<PathInformation>(request))
                    {
                        PathInformation existingPath = EntityManager.GetComponentData<PathInformation>(request);
                        if (existingPath.m_Origin != forcedPath.m_Origin
                            || existingPath.m_Destination != forcedPath.m_Destination)
                        {
                            EntityManager.SetComponentData(request, forcedPath);
                        }
                    }
                    else
                    {
                        EntityManager.AddComponentData(request, forcedPath);
                    }

                    if (!EntityManager.HasBuffer<PathElement>(request))
                    {
                        EntityManager.AddBuffer<PathElement>(request);
                    }
                }
            }
            finally
            {
                if (requestEntities.IsCreated) requestEntities.Dispose();
            }
        }

        private bool IsConfiguredDepotCompatibleWithLine(Entity depot, Entity line)
        {
            if (depot == Entity.Null
                || line == Entity.Null
                || !EntityManager.Exists(depot)
                || !EntityManager.Exists(line)
                || !EntityManager.HasComponent<Game.Buildings.TransportDepot>(depot)
                || !EntityManager.HasComponent<PrefabRef>(depot)
                || !EntityManager.HasComponent<PrefabRef>(line))
            {
                return false;
            }

            Entity depotPrefab = EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
            Entity linePrefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (depotPrefab == Entity.Null
                || linePrefab == Entity.Null
                || !EntityManager.HasComponent<TransportDepotData>(depotPrefab)
                || !EntityManager.HasComponent<TransportLineData>(linePrefab))
            {
                return false;
            }

            TransportDepotData depotData = EntityManager.GetComponentData<TransportDepotData>(depotPrefab);
            TransportLineData lineData = EntityManager.GetComponentData<TransportLineData>(linePrefab);
            return depotData.m_TransportType == lineData.m_TransportType;
        }

        private bool TryGetForcedTransportVehicleRequestDestination(Entity line, out Entity destinationWaypoint)
        {
            destinationWaypoint = Entity.Null;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint == Entity.Null || !EntityManager.Exists(waypoint))
                    continue;

                destinationWaypoint = waypoint;
                return true;
            }

            return false;
        }

        private void RegisterNewVehicles(bool fullSweep)
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);
            try
            {
                foreach (var line in lines)
                {
                    if (!rvBuffers.TryGetBuffer(line, out var rvs)) continue;
                    if (!wpBuffers.TryGetBuffer(line, out var wps) || wps.Length < 2) continue;
                    if (!IsLineStable(line, wps)) continue;
                    if (!IsWorkbenchTimetableApplied(line)) continue;
                    bool adoptExistingVehicles = !m_LineInitialAdopted.Contains(line);
                    bool isHotLine = adoptExistingVehicles || m_SpawningLines.ContainsKey(line);
                    if (!fullSweep && !isHotLine) continue;

                    string lineTag = "线路" + line.Index;

                    if (!adoptExistingVehicles && !m_DiagnosedLines.Contains(line))
                    {
                        m_DiagnosedLines.Add(line);
                        LogLineTrackChainDiagnostics(line);
                        string lineName = m_NameSystem.GetRenderedLabelName(line);
                        log.Info("[诊断] " + lineTag + " (" + lineName + ") waypoint数=" + wps.Length);
                    }

                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = rvs[i].m_Vehicle;
                        if (!EntityManager.Exists(v)) continue;
                        if (m_VehicleState.ContainsKey(v)) continue;

                        var pt0 = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        bool boarding0 = (pt0.m_State & PublicTransportFlags.Boarding) != 0;

                        int initWpIdx = boarding0 ? ComputeWpIndex(v, wps) : -1;
                        bool atA0 = (initWpIdx == 0);

                        string initReason;
                        var initState = InferInitialVehicleState(v, wps, pt0, boarding0, initWpIdx, adoptExistingVehicles, out initReason);
                        m_VehicleState[v] = initState;
                        m_VehicleTargetMin[v] = -1;
                        m_VehicleLapDistance[v] = -1f;
                        if (initState == VehicleState.Preparing)
                            m_VehiclePreparingStartFrame[v] = m_SimulationSystem.frameIndex;
                        else
                            m_VehiclePreparingStartFrame.Remove(v);
                        if (!adoptExistingVehicles
                            && m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
                        {
                            m_VehicleDispatchRequestStartFrame[v] = spawnRequestFrame;
                            m_LineSpawnRequestFrame.Remove(line);
                        }
                        else
                        {
                            m_VehicleDispatchRequestStartFrame.Remove(v);
                        }
                        m_LastBoarding[v] = boarding0;
                        m_CachedWpIdx[v] = initWpIdx;
                        m_VehicleLine[v] = line;
                        m_UICache.Remove(v);
                        ClearVehicleProgressSuspect(v, "register-reset");
                        if (initReason == "boarding-midway")
                            MarkVehicleProgressSuspect(v, initReason);

                        if (boarding0 && initWpIdx < 0)
                        {
                            ObserveBvMisfireCandidate(
                                v,
                                "线路" + line.Index,
                                "register",
                                "boarding-without-waypoint",
                                m_SimulationSystem.frameIndex);
                        }

                        bool preferOriginHolding = initState == VehicleState.Holding
                            && (initReason == "at-origin"
                                || initReason == "boarding-origin-fallback"
                                || initReason.StartsWith("route-progress-origin-fallback"));
                        bool restored = TryRestoreVehicleState(v, line, !preferOriginHolding);
                        if (!restored && initState == VehicleState.Running)
                            restored = RestoreRunningContextFromProgress(v, line, wps, initReason);
                        VehicleState finalState = m_VehicleState[v];
                        int finalTarget = m_VehicleTargetMin.TryGetValue(v, out int ft) ? ft : -1;
                        if (finalState == VehicleState.Holding)
                            TryRecordPreparingArrivalSample(v, line, m_SimulationSystem.frameIndex);

                        if (finalState == VehicleState.Running)
                            SetUILabel(v, "运行中" + (finalTarget >= 0 ? " " + SlotStr(finalTarget) : ""));
                        else if (finalState == VehicleState.Holding)
                            SetUILabel(v, finalTarget >= 0 ? "候车 " + SlotStr(finalTarget) : "候车 等待调度");
                        else
                            SetUILabel(v, atA0 ? "候车 等待调度" : "前往始发站");

                        log.Info("[注册] " + lineTag + " 车辆" + v.Index
                            + " 初始:" + initState + " 最终:" + finalState
                            + (restored ? "(缓存恢复)" : "")
                            + " targetMin=" + finalTarget
                            + " initReason=" + initReason
                            + " depot=" + DescribeVehicleOwnerDepot(v));
                        if (!adoptExistingVehicles)
                        {
                            log.Info("[OfficialSpawnResult] line=" + line.Index
                                + " vehicle=" + v.Index
                                + " state=" + finalState
                                + " targetMin=" + finalTarget
                                + " initReason=" + initReason
                                + " depot=" + DescribeVehicleOwnerDepot(v));
                        }
                        if (!adoptExistingVehicles)
                            RecordLineVehicleRegisterSummary(line, (int)(m_TimeSystem.normalizedTime * 1440f) % 1440, v, finalState);
                    }
                    if (adoptExistingVehicles)
                        m_LineInitialAdopted.Add(line);
                }
            }
            finally { lines.Dispose(); }
        }

        private void DriveStateMachine(EntityCommandBuffer ecb, int nowMin)
        {
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);
            var publicTransportLookup = GetComponentLookup<Game.Vehicles.PublicTransport>(true);
            var targetLookup = GetComponentLookup<Target>(true);
            var currentRouteLookup = GetComponentLookup<CurrentRoute>(true);
            var vehicles = m_VehicleQuery.ToEntityArray(Allocator.Temp);

            try
            {
                foreach (var v in vehicles)
                {
                    if (!EntityManager.Exists(v)) continue;
                    Entity line = ResolveVehicleLine(v);
                    if (!IsWorkbenchTimetableApplied(line)) continue;
                    if (!m_VehicleState.TryGetValue(v, out var state)) continue;
                    if (!publicTransportLookup.HasComponent(v)
                        || !targetLookup.HasComponent(v)
                        || !currentRouteLookup.HasComponent(v))
                    {
                        continue;
                    }

                    var pt = publicTransportLookup[v];
                    var tgt = targetLookup[v];
                    var cr = currentRouteLookup[v];
                    Entity routeEnt = cr.m_Route;

                    if (!wpBuffers.TryGetBuffer(routeEnt, out var wps) || wps.Length < 2) continue;
                    int waypointCount = wps.Length;

                    bool boarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
                    int targetMin = m_VehicleTargetMin.TryGetValue(v, out int tm) ? tm : -1;
                    uint nowFrame = m_SimulationSystem.frameIndex;
                    bool suppressForcedMidStopBoardingGhost = state == VehicleState.Running
                        && boarding
                        && IsSuppressedForcedMidStopBoardingGhost(v, tgt, wps, nowFrame, out _);
                    if (suppressForcedMidStopBoardingGhost)
                    {
                        boarding = false;
                        m_BVMisfire.Remove(v);
                        m_BVMisfireStartFrame.Remove(v);
                    }

                    Entity lineEnt = line;
                    string lineTag = "线路" + line.Index;
                    bool allowOriginHoldingBoardingGhost = state == VehicleState.Holding
                        && targetMin >= 0
                        && GetDistanceToOriginMeters(v, wps) <= ORIGIN_FORCE_IDLE_RADIUS_METERS;

                    if (!IsBvMisfireEnforcementEnabled() && m_BVMisfire.Contains(v))
                    {
                        m_BVMisfire.Remove(v);
                        m_BVMisfireStartFrame.Remove(v);
                    }

                    if (m_BVMisfire.Contains(v))
                    {
                        if (allowOriginHoldingBoardingGhost)
                        {
                            m_BVMisfire.Remove(v);
                            m_BVMisfireStartFrame.Remove(v);
                            m_LastBoarding[v] = false;
                            m_CachedWpIdx[v] = 0;
                            boarding = false;
                        }
                    }

                    if (m_BVMisfire.Contains(v))
                    {
                        if (m_BVMisfireStartFrame.TryGetValue(v, out uint misfireStart)
                            && (nowFrame - misfireStart) > BV_MISFIRE_TIMEOUT)
                        {
                            if (targetMin >= 0)
                            {
                                log.Info("[BVMisfire] " + lineTag + " 车辆" + v.Index
                                    + " 超时，释放班次" + SlotStr(targetMin) + " 并回库");
                                m_VehicleTargetMin[v] = -1;
                            }
                            else
                            {
                                log.Info("[BVMisfire] " + lineTag + " 车辆" + v.Index + " 超时，回库");
                            }
                            m_BVMisfire.Remove(v);
                            m_BVMisfireStartFrame.Remove(v);
                            DoRetire(v, pt, tgt, ecb, "BVMisfire超时");
                            continue;
                        }
                        string misfireLabel = m_ForcedMidStopBoardingGraceUntil.TryGetValue(v, out uint forcedDepartGraceUntil)
                            && nowFrame < forcedDepartGraceUntil
                            ? "停站超时协助中 #" + v.Index
                            : "寻路异常 #" + v.Index;
                        SetUILabel(v, misfireLabel);
                        continue;
                    }

                    bool inCooldown = m_LaunchCooldownUntil.TryGetValue(v, out uint cooldownUntil)
                        && nowFrame < cooldownUntil;

                    bool lastBoarding = m_LastBoarding.TryGetValue(v, out bool lb) ? lb : false;
                    bool boardingChanged = !inCooldown && (boarding != lastBoarding);
                    int curWpIdx;
                    int previousCachedWpIdx = m_CachedWpIdx.TryGetValue(v, out int prevCached) ? prevCached : -1;

                    if (boardingChanged && state != VehicleState.Idle)
                    {
                        if (!boarding)
                        {
                            bool suppressBypassDepartureBounce = false;
                            if (state == VehicleState.Running && previousCachedWpIdx > 0)
                            {
                                TryGetCadencedBypassHoldDecision(
                                    v,
                                    lineEnt,
                                    wps,
                                    previousCachedWpIdx,
                                    nowFrame,
                                    out bool shouldHoldBypass,
                                    out _,
                                    out bool canClearAfterExit);
                                suppressBypassDepartureBounce = shouldHoldBypass && !canClearAfterExit;
                            }

                            if (suppressBypassDepartureBounce)
                            {
                                curWpIdx = previousCachedWpIdx;
                                m_CachedWpIdx[v] = previousCachedWpIdx;
                                // Keep the station context latched while bypass hold is still active;
                                // a transient boarding=false pulse should not unlock a station-side hold.
                                m_LastBoarding[v] = true;
                            }
                            else
                            {
                                int liveDepartureWpIdx = ComputeWpIndex(v, wps);
                                bool stillAtPreviousStop = previousCachedWpIdx >= 0
                                    && liveDepartureWpIdx == previousCachedWpIdx;
                                if (stillAtPreviousStop)
                                {
                                    curWpIdx = previousCachedWpIdx;
                                    m_CachedWpIdx[v] = previousCachedWpIdx;
                                    m_LastBoarding[v] = true;
                                }
                                else
                                {
                                    TryRecordObservedStopDwellOnBoardingEnd(v, lineEnt, previousCachedWpIdx, nowFrame);
                                    RecordWorkbenchRealtimeStopEvent(v, lineEnt, wps, false, -1, previousCachedWpIdx);
                                    if (previousCachedWpIdx >= 0)
                                    {
                                        Entity departedStop = GetStationBuildingForWaypoint(wps, previousCachedWpIdx);
                                        Entity departedStopEntity = ResolveWorkbenchStopEntity(wps[previousCachedWpIdx].m_Waypoint);
                                        string departedStopName = ResolveWorkbenchEntityName(departedStop);
                                        if (string.IsNullOrWhiteSpace(departedStopName))
                                        {
                                            departedStopName = "stop#" + departedStop.Index;
                                        }

                                        int nextWaypointIndex = previousCachedWpIdx + 1 < waypointCount
                                            ? previousCachedWpIdx + 1
                                            : -1;
                                        Entity nextStop = nextWaypointIndex >= 0
                                            ? GetStationBuildingForWaypoint(wps, nextWaypointIndex)
                                            : Entity.Null;
                                        string nextStopName = nextStop != Entity.Null
                                            ? ResolveWorkbenchEntityName(nextStop)
                                            : string.Empty;
                                        if (nextStop != Entity.Null && string.IsNullOrWhiteSpace(nextStopName))
                                        {
                                            nextStopName = "stop#" + nextStop.Index;
                                        }

                                        string departureKey = SlotStr((int)(nowFrame / (uint)SIM_FRAMES_PER_MINUTE) % 1440)
                                            + "|wp=" + previousCachedWpIdx.ToString()
                                            + "|next=" + nextWaypointIndex.ToString();
                                        LogVehicleStateOnce(
                                            m_DepartureObserveLogCache,
                                            v,
                                            departureKey,
                                            "[离站观察] " + lineTag
                                            + " 车辆" + v.Index
                                            + " 从\"" + departedStopName + "\"离站"
                                            + (nextWaypointIndex >= 0 ? " next=\"" + nextStopName + "\"" : " next=\"-\"")
                                            + " state=" + state.ToString());
                                        TryLogBoardingDepartureAudit(
                                            v,
                                            lineTag,
                                            departedStopEntity,
                                            departedStopName,
                                            nowFrame);
                                    }
                                    TryClearVehicleProgressSuspectOnStableDeparture(v, previousCachedWpIdx);
                                    curWpIdx = -1;
                                    m_CachedWpIdx[v] = -1;
                                    m_LastBoarding[v] = false;
                                    m_BVMisfire.Remove(v);
                                    m_BVMisfireStartFrame.Remove(v);
                                    ClearForcedMidStopClosingConsist(v);
                                    m_StopDwellStartFrame.Remove(v);
                                    m_StopDwellSessions.Remove(v);
                                }
                            }
                        }
                        else
                        {
                            curWpIdx = ComputeWpIndex(v, wps);
                            m_CachedWpIdx[v] = curWpIdx;

                            if (curWpIdx >= 0)
                            {
                                BeginObservedStopDwellSession(v, lineEnt, curWpIdx, nowFrame);
                                RecordWorkbenchRealtimeStopEvent(v, lineEnt, wps, true, curWpIdx, previousCachedWpIdx);
                                m_LastBoarding[v] = true;
                                NoteVehicleProgressSuspectRecoveryBoarding(v, curWpIdx);
                                m_BVMisfire.Remove(v);
                                m_BVMisfireStartFrame.Remove(v);
                                ClearForcedMidStopClosingConsist(v);
                            }
                            else
                            {
                                m_LastBoarding[v] = true;
                                ObserveBvMisfireCandidate(
                                    v,
                                    lineTag,
                                    "boarding-change",
                                    "boarding-without-waypoint",
                                    nowFrame);
                            }
                        }

                        if (state == VehicleState.Running)
                        {
                            if (curWpIdx >= 0)
                            {
                                if (curWpIdx == waypointCount - 1)
                                    m_NearingTerminus.Add(v);
                            }
                            else if (boarding)
                            {
                                log.Info("[boarding变化] " + lineTag + " 车辆" + v.Index + " BV误写，标记misfire");
                            }
                        }
                    }
                    else
                    {
                        curWpIdx = m_CachedWpIdx.TryGetValue(v, out int ci) ? ci : -1;
                    }

                    if (state == VehicleState.Preparing)
                    {
                        ClearBypassYieldState(v);
                        int liveWpIdx = ComputeWpIndex(v, wps);
                        if (liveWpIdx >= 0 && liveWpIdx != curWpIdx)
                        {
                            curWpIdx = liveWpIdx;
                            m_CachedWpIdx[v] = liveWpIdx;
                        }
                    }

                    if (inCooldown)
                        m_LastBoarding[v] = boarding;
                    if (state == VehicleState.Idle)
                        m_LastBoarding[v] = boarding;

                    if (state != VehicleState.Running)
                    {
                        if (m_VehicleTraversalSliceSessions.TryGetValue(v, out VehicleTraversalSliceSession droppedSession))
                            RecordTraversalSliceLapDebugDropped(v, droppedSession.SliceIndex);
                        m_VehicleTraversalSliceSessions.Remove(v);
                    }

                    bool atA = state == VehicleState.Preparing
                        ? HasPreparingVehicleReachedOrigin(v, wps, boarding, curWpIdx)
                        : (curWpIdx == 0);
                    bool midStopBoarding = state == VehicleState.Running
                        && boarding
                        && curWpIdx > 0
                        && curWpIdx < waypointCount - 1;
                    uint midStopDwellSinceFrame = 0;
                    uint midStopDwellDeadlineFrame = 0;
                    int maxStationDwellMinutes = 0;
                    bool midStopDwellTimedOut = state == VehicleState.Running
                        && ShouldForceMidStopDwellTimeout(
                            v,
                            lineEnt,
                            curWpIdx,
                            boarding,
                            nowFrame,
                            waypointCount,
                            out midStopDwellSinceFrame,
                            out midStopDwellDeadlineFrame,
                            out maxStationDwellMinutes);
                    string vTag = " #" + v.Index;

                    switch (state)
                    {
                        case VehicleState.Preparing:
                            if (targetMin >= 0 && IsSlotSoftExpired(nowMin, targetMin) && !CanLateDispatchSlot(nowMin, targetMin))
                            {
                                int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
                                LogVehicleStateOnce(
                                    m_PreparingSlotLogCache,
                                    v,
                                    "PreparingSlot|" + targetMin + "|" + overdue,
                                    "[PreparingSlot] " + lineTag + " 车辆" + v.Index
                                        + " 班次" + SlotStr(targetMin) + " 已过期(" + overdue + "分钟)，释放重新调度");
                                m_VehicleTargetMin[v] = -1;
                                targetMin = -1;
                            }

                            if (atA)
                            {
                                if (targetMin < 0 && TryAssignUpcomingScheduledTargetToWaitingVehicle(
                                    routeEnt,
                                    v,
                                    nowMin,
                                    lineTag,
                                    "Preparing",
                                    ecb,
                                    out int preparingAssignedTarget))
                                {
                                    targetMin = preparingAssignedTarget;
                                }

                                if (ShouldRetireWaitingVehicleForFarFutureTarget(routeEnt, nowMin, targetMin))
                                {
                                    DoRetire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMin, targetMin));
                                    break;
                                }
                                m_VehicleState[v] = VehicleState.Holding;
                                TryRecordPreparingArrivalSample(v, lineEnt, nowFrame);
                                RecordLineHoldingSummary(lineEnt, nowMin, v, targetMin);
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                if (targetMin >= 0)
                                {
                                    SetUILabel(v, "候车 " + SlotStr(targetMin) + vTag);
                                    log.Info("[Preparing->Holding] " + lineTag + " 车辆" + v.Index + " 到站，预分配 " + SlotStr(targetMin));
                                }
                                else
                                {
                                    SetUILabel(v, "候车 等待调度" + vTag);
                                    log.Info("[Preparing->Holding] " + lineTag + " 车辆" + v.Index + " 到站，等待调度");
                                }
                            }
                            else
                            {
                                EnsurePreparingRoute(v, ref pt, ref tgt, wps, curWpIdx, boarding, ecb);
                                SetUILabel(v, "前往始发站" + (targetMin >= 0 ? " " + SlotStr(targetMin) : "") + vTag);
                            }
                            break;

                        case VehicleState.Holding:
                            if (!atA)
                            {
                                if (IsWaitingForcedOriginDwell(v, nowFrame))
                                {
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, targetMin >= 0 ? "候车 " + SlotStr(targetMin) + vTag : "候车 等待调度" + vTag);
                                    break;
                                }
                                m_VehicleState[v] = VehicleState.Running;
                                m_VehiclePreparingStartFrame.Remove(v);
                                m_ForcedOriginReadyFrame.Remove(v);
                                RecordLapStart(v, "Holding异常离站");
                                SetUILabel(v, "运行中(异常)" + vTag);
                                log.Info("[异常] " + lineTag + " 车辆" + v.Index + " Holding 时意外离站");
                                break;
                            }
                            if (targetMin < 0)
                            {
                                int lateSlot = -1;
                                int[] appliedTargets = GetAppliedWorkbenchDepartureMinutes(routeEnt);
                                bool assigned = appliedTargets.Length > 0
                                    ? TryAssignCurrentOrLateScheduledTargetToWaitingVehicle(
                                        routeEnt,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Holding",
                                        appliedTargets,
                                        out lateSlot)
                                    : TryAssignCurrentOrLateSlotToWaitingVehicle(
                                        routeEnt,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Holding",
                                        out lateSlot);
                                if (assigned)
                                {
                                    targetMin = lateSlot;
                                }
                                else if (TryAssignUpcomingScheduledTargetToWaitingVehicle(
                                    routeEnt,
                                    v,
                                    nowMin,
                                    lineTag,
                                    "Holding",
                                    ecb,
                                    out int upcomingTarget))
                                {
                                    targetMin = upcomingTarget;
                                }
                                else
                                {
                                    m_VehicleState[v] = VehicleState.Idle;
                                    if (!m_VehicleIdleStartFrame.ContainsKey(v))
                                        m_VehicleIdleStartFrame[v] = nowFrame;
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, "等待调度" + vTag);
                                    break;
                                }
                            }

                            if (ShouldRetireWaitingVehicleForFarFutureTarget(routeEnt, nowMin, targetMin))
                            {
                                DoRetire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMin, targetMin));
                                ClearBypassYieldState(v);
                                break;
                            }

                            if (IsTimeReached(nowMin, targetMin) || CanLateDispatchSlot(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v, "始发候车不参与待避");
                                if (IsDispatchTargetAlreadyOccupied(routeEnt, v, targetMin))
                                {
                                    m_VehicleTargetMin[v] = -1;
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, "候车 等待调度" + vTag);
                                    LogVehicleStateOnce(
                                        m_HoldingSkipLogCache,
                                        v,
                                        "HoldingSkip|" + targetMin,
                                        "[HoldingSkip] " + lineTag + " 车辆" + v.Index
                                            + " 班次" + SlotStr(targetMin) + " 已被其他车辆占用，释放重调度");
                                    break;
                                }

                                if (IsWaitingForcedOriginDwell(v, nowFrame))
                                {
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, CanLateDispatchSlot(nowMin, targetMin)
                                        ? "候车 补发 " + SlotStr(targetMin) + vTag
                                        : "候车 " + SlotStr(targetMin) + vTag);
                                    break;
                                }
                                if (boarding)
                                {
                                    bool shouldRefreshOriginAssist = !m_ForcedOriginBoardingGraceUntil.TryGetValue(v, out uint originBoardingGraceUntil)
                                        || nowFrame >= originBoardingGraceUntil;
                                    int scannedPassengers = 0;
                                    int readiedPassengers = 0;
                                    BoardingCloseAssistStats assistStats = default;
                                    if (shouldRefreshOriginAssist)
                                    {
                                        PrepareVehicleForOriginalBoardingClose(
                                            v,
                                            ref pt,
                                            ecb,
                                            out scannedPassengers,
                                            out readiedPassengers,
                                            out assistStats);
                                        ecb.SetComponent(v, pt);
                                        m_ForcedOriginBoardingGraceUntil[v] = nowFrame + FORCED_MIDSTOP_BV_GRACE_FRAMES;
                                        log.Info("[始发发车协助] " + lineTag + " 车辆" + v.Index
                                            + " 班次" + SlotStr(targetMin)
                                            + " scannedPassengers=" + scannedPassengers
                                            + " readiedPassengers=" + readiedPassengers
                                            + " " + FormatBoardingCloseAssistStats(assistStats)
                                            + " wp=" + curWpIdx);
                                    }
                                    SetUILabel(v, "结束上客 " + SlotStr(targetMin) + vTag);
                                    break;
                                }
                                bool isLateDispatch = CanLateDispatchSlot(nowMin, targetMin);
                                int overdue = isLateDispatch ? GetSlotOverdueMinutes(nowMin, targetMin) : 0;
                                LaunchVehicle(v, pt, tgt, wps, ecb);
                                m_VehicleState[v] = VehicleState.Running;
                                m_JustLaunched.Add(v);
                                m_VehicleLastLaunchFrame[v] = nowFrame;
                                RecordLapStart(v, isLateDispatch ? "补发" : "计划发车");
                                m_LastBoarding[v] = false;
                                m_CachedWpIdx[v] = -1;
                                m_BVMisfire.Remove(v);
                                m_BVMisfireStartFrame.Remove(v);
                                m_LaunchCooldownUntil[v] = nowFrame + LAUNCH_COOLDOWN_FRAMES;
                                m_VehicleCurrentSlot[v] = targetMin;
                                m_VehicleTargetMin[v] = -1;
                                m_OriginArrivalCandidateSinceFrame.Remove(v);
                                m_ForcedOriginReadyFrame.Remove(v);
                                BeginWorkbenchRealtimeTripAtLaunch(v, lineEnt, wps);
                                SetUILabel(v, (isLateDispatch ? "运行中 补发 " : "运行中 ") + SlotStr(targetMin) + vTag);
                                if (isLateDispatch)
                                {
                                    LogVehicleStateOnce(
                                        m_LateDispatchLogCache,
                                        v,
                                        "LateDispatchLaunch|" + targetMin,
                                        "[补发] " + lineTag + " 车辆" + v.Index
                                            + " 于 " + SlotStr(nowMin) + " 补发（班次 " + SlotStr(targetMin) + "）"
                                            + " 已过期" + overdue + "分钟"
                                            + " 冷却至帧" + (nowFrame + LAUNCH_COOLDOWN_FRAMES));
                                }
                                else
                                {
                                    log.Info("[发车] " + lineTag + " 车辆" + v.Index
                                        + " 于 " + SlotStr(nowMin) + " 发车（班次 " + SlotStr(targetMin) + "）"
                                        + " 冷却至帧" + (nowFrame + LAUNCH_COOLDOWN_FRAMES));
                                }
                            }
                            else if (IsSlotHardExpired(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v);
                                int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
                                log.Info("[Holding] " + lineTag + " 车辆" + v.Index
                                    + " 班次" + SlotStr(targetMin) + " 大幅过期(" + overdue + "分钟)，直接回库");
                                DoRetire(v, pt, tgt, ecb, "班次大幅过期" + overdue + "分钟");
                            }
                            else if (IsSlotSoftExpired(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v);
                                int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
                                log.Info("[Holding] " + lineTag + " 车辆" + v.Index
                                    + " 班次" + SlotStr(targetMin) + " 已过期(" + overdue + "分钟)，释放重新调度");
                                m_VehicleTargetMin[v] = -1;
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                SetUILabel(v, "候车 等待调度" + vTag);
                            }
                            else
                            {
                                ClearBypassYieldState(v);
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                SetUILabel(v,
                                    CanLateDispatchSlot(nowMin, targetMin)
                                        ? "候车 补发 " + SlotStr(targetMin) + vTag
                                        : "候车 " + SlotStr(targetMin) + vTag);
                            }
                            break;

                        case VehicleState.Running:
                            if (IsAppliedWorkbenchExpressLine(lineEnt))
                                UpdateVehicleTraversalSliceObservation(v, lineEnt, wps, nowFrame);

                            int bypassControlWaypointIndex = curWpIdx >= 0 ? curWpIdx : previousCachedWpIdx;
                            bool runningShouldHoldBypass = false;
                            bool runningCanClearAfterExit = true;
                            Entity runningBypassBlocker = Entity.Null;
                            bool runningBypassLatched = m_BypassYieldBlocker.ContainsKey(v);
                            if (bypassControlWaypointIndex > 0
                                && (boarding || runningBypassLatched))
                            {
                                TryGetCadencedBypassHoldDecision(
                                    v,
                                    routeEnt,
                                    wps,
                                    bypassControlWaypointIndex,
                                    nowFrame,
                                    out runningShouldHoldBypass,
                                    out runningBypassBlocker,
                                    out runningCanClearAfterExit);
                            }

                            if (midStopDwellTimedOut && !runningShouldHoldBypass)
                            {
                                bool shouldRefreshTimeoutAssist = !m_ForcedMidStopBoardingGraceUntil.TryGetValue(v, out uint timeoutAssistGraceUntil)
                                    || nowFrame >= timeoutAssistGraceUntil;
                                int scannedPassengers = 0;
                                int readiedPassengers = 0;
                                BoardingCloseAssistStats assistStats = default;
                                if (shouldRefreshTimeoutAssist)
                                {
                                    PrepareVehicleForOriginalBoardingClose(
                                        v,
                                        ref pt,
                                        ecb,
                                        out scannedPassengers,
                                        out readiedPassengers,
                                        out assistStats);
                                    Entity assistStop = Entity.Null;
                                    if (tgt.m_Target != Entity.Null && EntityManager.HasComponent<Connected>(tgt.m_Target))
                                        assistStop = EntityManager.GetComponentData<Connected>(tgt.m_Target).m_Connected;
                                    if (assistStop == Entity.Null && curWpIdx >= 0 && curWpIdx < waypointCount)
                                        assistStop = ResolveWorkbenchStopEntity(wps[curWpIdx].m_Waypoint);
                                    RememberBoardingAssistSnapshot(v, assistStop, nowFrame);
                                    ecb.SetComponent(v, pt);
                                    MarkForcedMidStopClosingConsist(
                                        v,
                                        nowFrame + FORCED_MIDSTOP_BV_GRACE_FRAMES,
                                        nowFrame + FORCED_MIDSTOP_HARD_CLOSE_FRAMES);
                                    string timeoutLogKey = midStopDwellSinceFrame.ToString();
                                    LogVehicleStateOnce(
                                        m_MidStopTimeoutLogCache,
                                        v,
                                        timeoutLogKey,
                                        "[停站超时] " + lineTag + " 车辆" + v.Index
                                            + " 停站超时" + maxStationDwellMinutes + "分钟"
                                            + " sinceFrame=" + midStopDwellSinceFrame
                                            + " deadlineFrame=" + midStopDwellDeadlineFrame
                                            + " curWpIdx=" + curWpIdx
                                            + " nextTargetWp=" + (curWpIdx + 1 < waypointCount ? (curWpIdx + 1).ToString() : "-")
                                            + " scannedPassengers=" + scannedPassengers
                                            + " readiedPassengers=" + readiedPassengers
                                            + " " + FormatBoardingCloseAssistStats(assistStats));
                                }
                                ClearBypassYieldState(v);
                                SetUILabel(v, "停站超时" + vTag);
                                if (ENABLE_MIDSTOP_TIMEOUT_GATE_LOGS && !shouldRefreshTimeoutAssist)
                                {
                                    Entity currentStop = Entity.Null;
                                    Entity boardingVehicle = Entity.Null;
                                    if (tgt.m_Target != Entity.Null
                                        && EntityManager.HasComponent<Connected>(tgt.m_Target))
                                    {
                                        currentStop = EntityManager.GetComponentData<Connected>(tgt.m_Target).m_Connected;
                                        if (currentStop != Entity.Null
                                            && EntityManager.HasComponent<BoardingVehicle>(currentStop))
                                        {
                                            boardingVehicle = EntityManager.GetComponentData<BoardingVehicle>(currentStop).m_Vehicle;
                                        }
                                    }

                                    string assistGateKey = "timeout-assist-gate|"
                                        + timeoutAssistGraceUntil.ToString()
                                        + "|stop=" + currentStop.Index.ToString()
                                        + "|bv=" + boardingVehicle.Index.ToString()
                                        + "|dep=" + pt.m_DepartureFrame.ToString()
                                        + "|min=" + pt.m_MinWaitingDistance.ToString("F1")
                                        + "|max=" + pt.m_MaxBoardingDistance.ToString("F1");
                                    LogVehicleStateOnce(
                                        m_BvMisfireObserveLogCache,
                                        v,
                                        assistGateKey,
                                        "[停站超时门槛] " + lineTag + " 车辆" + v.Index
                                        + " simulationFrame=" + nowFrame
                                        + " departureFrame=" + pt.m_DepartureFrame
                                        + " minWaitingDistance=" + pt.m_MinWaitingDistance
                                        + " maxBoardingDistance=" + pt.m_MaxBoardingDistance
                                        + " stop=" + currentStop.Index
                                        + " stopBoardingVehicle=" + boardingVehicle.Index
                                        + " stopBoardingVehicleIsSelf=" + (boardingVehicle == v));
                                }
                                break;
                            }

                            if (bypassControlWaypointIndex > 0
                                && runningShouldHoldBypass)
                            {
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                SetBypassYieldState(v, runningBypassBlocker, lineTag, "运行中");
                                SetUILabel(v, "待避快车" + vTag);
                                break;
                            }

                            string runningReleaseReason = null;
                            if (runningBypassLatched && !runningShouldHoldBypass)
                            {
                                runningReleaseReason = runningCanClearAfterExit
                                    ? "已越过当前待避站出口"
                                    : "待避条件消失";
                            }
                            if (runningBypassLatched && !runningShouldHoldBypass)
                            {
                                ClearBypassYieldState(v, runningReleaseReason);
                            }
                            bool shouldEvaluateOriginSettle = !inCooldown
                                && ShouldEvaluateRunningOriginSettleCheck(
                                    v,
                                    wps,
                                    atA,
                                    boarding,
                                    lastBoarding,
                                    targetMin);
                            bool settleAtOrigin = shouldEvaluateOriginSettle
                                && ShouldSettleRunningAtOrigin(
                                    v,
                                    wps,
                                    nowFrame,
                                    atA,
                                    boarding,
                                    lastBoarding,
                                    targetMin);
                            bool forcedAtOrigin = settleAtOrigin && !atA;
                            if ((atA || forcedAtOrigin) && !inCooldown)
                            {
                                bool hasLapStartOdo = m_VehicleLapStartOdometer.TryGetValue(v, out float ls);
                                bool hasLapStartFrame = m_VehicleLapStartFrame.TryGetValue(v, out uint lapStartFrame);
                                bool lapStartValid = hasLapStartOdo && !float.IsNaN(ls) && !float.IsInfinity(ls) && ls >= 0f;
                                bool brokenRecoveredRunning = hasLapStartFrame && !lapStartValid;
                                float lapStart = hasLapStartOdo ? ls : -1f;
                                float nowOdo = EntityManager.HasComponent<Odometer>(v)
                                    ? EntityManager.GetComponentData<Odometer>(v).m_Distance : -1f;
                                const float LAP_MOVED_MIN = 500f;
                                bool hasMoved = (nowOdo >= 0f && lapStartValid && (nowOdo - lapStart) > LAP_MOVED_MIN);
                                float ld = 0f;
                                m_VehicleLapDistance.TryGetValue(v, out ld);
                                if (brokenRecoveredRunning)
                                {
                                    m_VehicleState[v] = VehicleState.Idle;
                                    m_VehicleCurrentSlot.Remove(v);
                                    m_VehicleLapStartFrame.Remove(v);
                                    m_VehicleLapFrames.Remove(v);
                                    m_RestoredRunning.Remove(v);
                                    m_NearingTerminus.Remove(v);
                                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                                    m_ForcedOriginReadyFrame.Remove(v);
                                    m_CachedWpIdx[v] = 0;
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, targetMin >= 0 ? "候车 " + SlotStr(targetMin) + vTag : "等待调度" + vTag);
                                    log.Info("[恢复兜底] " + lineTag + " 车辆" + v.Index
                                        + " Running圈起点无效，回站后转Idle"
                                        + " lapStartFrame=" + lapStartFrame
                                        + " lapStartValid=" + lapStartValid
                                        + " lapStartRaw=" + (hasLapStartOdo ? ls.ToString("F1") : "?")
                                        + " target=" + (targetMin >= 0 ? SlotStr(targetMin) : "-")
                                        + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?"));
                                    break;
                                }
                                if (!hasMoved)
                                {
                                    if (settleAtOrigin)
                                    {
                                        uint originSinceFrame = m_OriginArrivalCandidateSinceFrame.TryGetValue(v, out uint sinceFrame)
                                            ? sinceFrame
                                            : nowFrame;
                                        bool keepAssignedTarget = targetMin >= 0 && IsCurrentOrRecentDispatchableSlot(nowMin, targetMin);
                                        bool recoverToHolding = keepAssignedTarget;

                                        m_VehicleState[v] = recoverToHolding ? VehicleState.Holding : VehicleState.Idle;
                                        if (!recoverToHolding)
                                        {
                                            m_VehicleTargetMin[v] = -1;
                                            m_VehicleIdleStartFrame[v] = nowFrame;
                                        }
                                        else
                                        {
                                            m_VehicleIdleStartFrame.Remove(v);
                                        }

                                        m_VehicleCurrentSlot.Remove(v);
                                        m_VehicleLastLaunchFrame.Remove(v);
                                        m_LaunchCooldownUntil.Remove(v);
                                        m_NearingTerminus.Remove(v);
                                        m_OriginArrivalCandidateSinceFrame.Remove(v);
                                        m_ForcedOriginReadyFrame.Remove(v);
                                        m_CachedWpIdx[v] = 0;
                                        pt.m_DepartureFrame = nowFrame + 9999;
                                        ecb.SetComponent(v, pt);

                                        if (recoverToHolding)
                                        {
                                            bool isLateRecoveredTarget = CanLateDispatchSlot(nowMin, targetMin);
                                            SetUILabel(v, (isLateRecoveredTarget ? "候车 补发 " : "候车 ") + SlotStr(targetMin) + vTag);
                                            log.Info("[Running->Holding兜底] " + lineTag + " 车辆" + v.Index
                                                + " 到达始发站后长时间静止，回收为候车"
                                                + " target=" + SlotStr(targetMin)
                                                + " waitedFrames=" + (nowFrame - originSinceFrame)
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " curWpIdx=" + curWpIdx
                                                + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                        }
                                        else
                                        {
                                            SetUILabel(v, "等待调度" + vTag);
                                            log.Info("[Running->Idle兜底] " + lineTag + " 车辆" + v.Index
                                                + " 到达始发站后长时间静止，回收为Idle"
                                                + " waitedFrames=" + (nowFrame - originSinceFrame)
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " curWpIdx=" + curWpIdx
                                                + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                        }
                                        break;
                                    }

                                    if (!settleAtOrigin)
                                        m_OriginArrivalCandidateSinceFrame.Remove(v);
                                    pt.m_DepartureFrame = midStopBoarding && midStopDwellDeadlineFrame > 0
                                        ? midStopDwellDeadlineFrame
                                        : nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    string curSlot1 = m_VehicleCurrentSlot.TryGetValue(v, out int cs1) ? SlotStr(cs1) : "?";
                                    string nxtSlot1 = targetMin >= 0 ? ("->" + SlotStr(targetMin)) : "";
                                    SetUILabel(v, "运行中" + curSlot1 + nxtSlot1 + vTag);
                                    if (nowFrame % 1800 == 0)
                                    {
                                        uint lastLaunchFrame = m_VehicleLastLaunchFrame.TryGetValue(v, out uint llf) ? llf : 0;
                                        uint lapStartFrameDbg = m_VehicleLapStartFrame.TryGetValue(v, out uint lsfDbg) ? lsfDbg : 0;
                                        string curSlotDbg = m_VehicleCurrentSlot.TryGetValue(v, out int csDbg) ? SlotStr(csDbg) : "?";
                                        string targetSlotDbg = targetMin >= 0 ? SlotStr(targetMin) : "-";
                                        int cachedWpDbg = m_CachedWpIdx.TryGetValue(v, out int cwDbg) ? cwDbg : -1;
                                        log.Info("[心跳-卡站] " + lineTag + " 车辆" + v.Index
                                            + " atA=true hasMoved=false"
                                            + " traveled=" + (nowOdo >= 0f && lapStart >= 0f
                                                ? ((nowOdo - lapStart) / 1000f).ToString("F2") + "km" : "?")
                                            + " threshold=" + (LAP_MOVED_MIN / 1000f).ToString("F2") + "km"
                                            + " lapDist=" + (ld > 0f ? (ld / 1000f).ToString("F2") + "km" : "未知")
                                            + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?")
                                            + " lapStart=" + (lapStartValid ? lapStart.ToString("F1") : "?")
                                            + " lapStartValid=" + lapStartValid
                                            + " lapStartRaw=" + (hasLapStartOdo ? ls.ToString("F1") : "?")
                                            + " lapStartFrame=" + (lapStartFrameDbg > 0 ? lapStartFrameDbg.ToString() : "?")
                                            + " lastLaunchFrame=" + (lastLaunchFrame > 0 ? lastLaunchFrame.ToString() : "?")
                                            + " sinceLaunch=" + (lastLaunchFrame > 0 ? (nowFrame - lastLaunchFrame).ToString() : "?")
                                            + " curSlot=" + curSlotDbg
                                            + " targetSlot=" + targetSlotDbg
                                            + " cachedWp=" + cachedWpDbg
                                            + " curWpIdx=" + curWpIdx
                                            + " boarding=" + boarding
                                            + " lastBoarding=" + lastBoarding);
                                    }
                                    break;
                                }
                                FinalizeVehicleTraversalSliceObservation(v, nowFrame);
                                UpdateLapStats(v);
                                m_VehicleState[v] = VehicleState.Idle;
                                if (targetMin >= 0)
                                {
                                    if (IsCurrentOrRecentDispatchableSlot(nowMin, targetMin))
                                        m_VehicleTargetMin[v] = targetMin;
                                    else
                                        m_VehicleTargetMin[v] = -1;
                                }
                                else
                                {
                                    m_VehicleTargetMin[v] = -1;
                                }
                                m_CachedWpIdx[v] = 0;
                                m_NearingTerminus.Remove(v);
                                m_OriginArrivalCandidateSinceFrame.Remove(v);
                                if (forcedAtOrigin)
                                    m_ForcedOriginReadyFrame[v] = nowFrame + FORCED_ORIGIN_MIN_DWELL_FRAMES;
                                else
                                    m_ForcedOriginReadyFrame.Remove(v);
                                SetUILabel(v, "等待调度" + vTag);
                                log.Info("[Running->Idle] " + lineTag + " 车辆" + v.Index
                                    + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?")
                                    + " lapStart=" + (lapStartValid ? lapStart.ToString("F1") : "?")
                                    + " curWpIdx=" + curWpIdx
                                    + (targetMin >= 0 && IsCurrentOrRecentDispatchableSlot(nowMin, targetMin)
                                        ? " keptTarget=" + SlotStr(targetMin)
                                        : "")
                                    + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                            }
                            else
                            {
                                if (!m_VehicleLapStartOdometer.ContainsKey(v) && !inCooldown)
                                    RecordLapStart(v, "Running缺少圈起点自愈");
                                string curSlot2 = m_VehicleCurrentSlot.TryGetValue(v, out int cs2) ? SlotStr(cs2) : "?";
                                string nxtSlot2 = targetMin >= 0 ? ("->" + SlotStr(targetMin)) : "";
                                SetUILabel(v, "运行中" + curSlot2 + nxtSlot2 + vTag);
                            }
                            break;

                        case VehicleState.Idle:
                            ClearBypassYieldState(v);
                            if (!atA)
                            {
                                m_VehicleState[v] = VehicleState.Running;
                                m_VehiclePreparingStartFrame.Remove(v);
                                m_ForcedOriginReadyFrame.Remove(v);
                                RecordLapStart(v, "Idle异常离站");
                                m_VehicleIdleStartFrame.Remove(v);
                                SetUILabel(v, "运行中(异常离站)" + vTag);
                                log.Info("[异常] " + lineTag + " 车辆" + v.Index + " Idle 时意外离站");
                                break;
                            }

                            if (targetMin < 0)
                            {
                                int[] appliedTargets = GetAppliedWorkbenchDepartureMinutes(routeEnt);
                                int lateTarget = -1;
                                bool assignedLateTarget = appliedTargets.Length > 0
                                    ? TryAssignCurrentOrLateScheduledTargetToWaitingVehicle(
                                        routeEnt,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Idle",
                                        appliedTargets,
                                        out lateTarget)
                                    : TryAssignCurrentOrLateSlotToWaitingVehicle(
                                        routeEnt,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Idle",
                                        out lateTarget);
                                if (assignedLateTarget)
                                    targetMin = lateTarget;
                            }

                            if (HasInboundVehicleNearOrigin(routeEnt, wps, v, ORIGIN_CONGESTION_RADIUS_METERS, includePreparingVehicles: false))
                            {
                                if (ShouldProtectIdleFromYield(routeEnt, v, nowMin))
                                {
                                    if (m_VehicleTargetMin.TryGetValue(v, out int ptm) && ptm >= 0 && CanLateDispatchSlot(nowMin, ptm))
                                    {
                                        LogVehicleStateOnce(
                                            m_YieldSkipLogCache,
                                            v,
                                            "YieldSkipLate|" + ptm,
                                            "[YieldSkip] " + lineTag + " 车辆" + v.Index
                                                + " 班次" + SlotStr(ptm)
                                                + " 已过期" + GetSlotOverdueMinutes(nowMin, ptm) + "分钟，保留补发");
                                    }
                                    else
                                    {
                                        int protectTarget = m_VehicleTargetMin.TryGetValue(v, out int ptm2) && ptm2 >= 0
                                            ? ptm2
                                            : GetFallbackProtectTarget(routeEnt, nowMin);
                                        LogVehicleStateOnce(
                                            m_YieldSkipLogCache,
                                            v,
                                            "YieldSkipProtect|" + protectTarget,
                                            "[YieldSkip] " + lineTag + " 车辆" + v.Index
                                                + " 最近班次" + SlotStr(protectTarget)
                                                + " 仅剩" + MinutesUntil(nowMin, protectTarget) + "分钟，保留待避");
                                    }
                                    break;
                                }
                                log.Info("[Yield] " + lineTag + " 车辆" + v.Index + " 始发站有回流车压队，回库疏解");
                                DoRetire(v, pt, tgt, ecb, "始发站压队疏解");
                                break;
                            }

                            if (targetMin >= 0)
                            {
                                if (ShouldRetireWaitingVehicleForFarFutureTarget(routeEnt, nowMin, targetMin))
                                {
                                    DoRetire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMin, targetMin));
                                    break;
                                }
                                m_VehicleState[v] = VehicleState.Holding;
                                m_VehicleIdleStartFrame.Remove(v);
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                bool isLateTarget = CanLateDispatchSlot(nowMin, targetMin);
                                SetUILabel(v,
                                    (isLateTarget ? "候车 补发 " : "候车 ") + SlotStr(targetMin) + vTag);
                                LogVehicleStateOnce(
                                    isLateTarget ? m_LateDispatchLogCache : m_HoldingSkipLogCache,
                                    v,
                                    (isLateTarget ? "LateDispatchClaim|" : "IdleHoldingAssign|") + targetMin,
                                    (isLateTarget ? "[补发认领] " : "[Idle->Holding] ")
                                        + lineTag + " 车辆" + v.Index
                                        + (isLateTarget
                                            ? " 认领补发班次" + SlotStr(targetMin) + " 于 " + SlotStr(nowMin)
                                            : " 进入候车班次" + SlotStr(targetMin)));
                                break;
                            }

                            if (!m_VehicleIdleStartFrame.ContainsKey(v))
                                m_VehicleIdleStartFrame[v] = nowFrame;

                            if (m_VehicleIdleStartFrame.TryGetValue(v, out uint idleStart))
                            {
                                float idleMin = (nowFrame - idleStart) / (float)SIM_FRAMES_PER_MINUTE;
                                if (idleMin > IDLE_TIMEOUT_MIN)
                                {
                                    m_VehicleIdleStartFrame.Remove(v);
                                    DoRetire(v, pt, tgt, ecb, "闲置" + idleMin.ToString("F1") + "分钟");
                                    break;
                                }
                            }

                            pt.m_DepartureFrame = nowFrame + 9999;
                            ecb.SetComponent(v, pt);
                            SetUILabel(v, "等待调度" + vTag);
                            break;

                        case VehicleState.Retiring:
                            ClearBypassYieldState(v);
                            SetUILabel(v, "回库中" + vTag);
                            EnsureRetiringRoute(v, ref pt, ref tgt, ecb);
                            break;
                    }
                }

                var deadKeys = new NativeList<Entity>(Allocator.Temp);
                foreach (var kv in m_VehicleState)
                {
                    if (!EntityManager.Exists(kv.Key)) deadKeys.Add(kv.Key);
                }
                foreach (var dead in deadKeys)
                {
                    m_VehicleState.Remove(dead);
                    m_VehicleTargetMin.Remove(dead);
                    m_VehicleLapStartOdometer.Remove(dead);
                    m_VehicleLapDistance.Remove(dead);
                    m_VehicleLapStartFrame.Remove(dead);
                    m_VehicleLapFrames.Remove(dead);
                    m_VehicleIdleStartFrame.Remove(dead);
                    m_VehiclePreparingStartFrame.Remove(dead);
                    m_VehicleDispatchRequestStartFrame.Remove(dead);
                    m_VehicleCurrentSlot.Remove(dead);
                    m_VehicleLastLaunchFrame.Remove(dead);
                    m_UICache.Remove(dead);
                    m_LastBoarding.Remove(dead);
                    m_CachedWpIdx.Remove(dead);
                    m_WaypointIndexFrameSnapshots.Remove(dead);
                    m_RouteProgressFrameSnapshots.Remove(dead);
                    m_BVMisfire.Remove(dead);
                    m_BVMisfireStartFrame.Remove(dead);
                    m_BypassControlScopeCache.Remove(dead);
                    m_BypassHoldCadenceSnapshots.Remove(dead);
                    m_BypassConflictEpisodes.Remove(dead);
                    ClearVehicleProgressSuspect(dead, "vehicle-removed");
                    ClearForcedMidStopClosingConsist(dead);
                    m_VehicleLine.Remove(dead);
                    m_LaunchCooldownUntil.Remove(dead);
                    m_LastRetireFixLogFrame.Remove(dead);
                    m_RetireFixCooldownUntil.Remove(dead);
                    m_PreparingFixCooldownUntil.Remove(dead);
                    m_RetireFixCount.Remove(dead);
                    m_OriginArrivalCandidateSinceFrame.Remove(dead);
                    m_ForcedOriginReadyFrame.Remove(dead);
                    m_ForcedOriginBoardingGraceUntil.Remove(dead);
                    m_StopDwellStartFrame.Remove(dead);
                    m_BypassYieldBlocker.Remove(dead);
                    m_VehicleTraversalSliceSessions.Remove(dead);
                    ClearVehicleTraversalSliceLapDebug(dead);
                    m_BvWaypointMismatchLogCache.Remove(dead);
                    m_BvTrackAnchorRecoveryLogCache.Remove(dead);
                    m_BvWaypointMismatchLastLogFrame.Remove(dead);
                    m_BypassQueuedLocalOverrideLogCache.Remove(dead);
                    m_LastBoardingAssistSnapshots.Remove(dead);
                    List<Entity> episodeReleaseKeys = null;
                    foreach (KeyValuePair<Entity, BypassConflictEpisode> entry in m_BypassConflictEpisodes)
                    {
                        if (entry.Value.BlockerVehicle != dead)
                            continue;

                        episodeReleaseKeys ??= new List<Entity>();
                        episodeReleaseKeys.Add(entry.Key);
                    }

                    if (episodeReleaseKeys != null)
                    {
                        for (int i = 0; i < episodeReleaseKeys.Count; i++)
                            m_BypassConflictEpisodes.Remove(episodeReleaseKeys[i]);
                    }
                    log.Info("[清理] 车辆" + dead.Index + " 消失");
                }
                if (deadKeys.Length > 0)
                    m_WaypointIndexFrameSnapshots.Clear();
                if (deadKeys.Length > 0)
                    m_RouteProgressFrameSnapshots.Clear();
                if (deadKeys.Length > 0)
                    m_LineRunningVehicleFrameSnapshots.Clear();
                deadKeys.Dispose();
            }
            finally { vehicles.Dispose(); }
        }

        private int ComputeWpIndex(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            bool hit = TryGetWaypointIndexCurrentFrame(v, wps, out int cachedWaypointIndex);
            if (hit)
                return cachedWaypointIndex;

            int computedWaypointIndex = ComputeWpIndexUncached(v, wps);
            if (computedWaypointIndex >= 0)
            {
                Entity route = ResolveVehicleLine(v);
                bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v)
                    && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v).m_State & PublicTransportFlags.Boarding) != 0;
                m_WaypointIndexFrameSnapshots[v] = new WaypointIndexFrameSnapshot(
                    m_SimulationSystem.frameIndex,
                    route,
                    boarding,
                    computedWaypointIndex);
            }
            else
            {
                m_WaypointIndexFrameSnapshots.Remove(v);
            }

            return computedWaypointIndex;
        }

        private bool TryGetWaypointIndexCurrentFrame(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints, out int waypointIndex)
        {
            waypointIndex = -1;
            if (vehicle == Entity.Null
                || !m_WaypointIndexFrameSnapshots.TryGetValue(vehicle, out WaypointIndexFrameSnapshot snapshot))
            {
                return false;
            }

            if (snapshot.Frame != m_SimulationSystem.frameIndex)
                return false;

            Entity route = ResolveVehicleLine(vehicle);
            if (snapshot.Route != route)
                return false;

            bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;
            if (snapshot.Boarding != boarding)
                return false;

            waypointIndex = snapshot.WaypointIndex;
            return true;
        }

        private int ComputeWpIndexUncached(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            if (!EntityManager.HasComponent<Game.Objects.Transform>(v)) return -1;
            float3 vPos = EntityManager.GetComponentData<Game.Objects.Transform>(v).m_Position;
            bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v).m_State & PublicTransportFlags.Boarding) != 0;
            int targetWaypointIndex = -1;
            if (EntityManager.HasComponent<Target>(v))
            {
                Entity targetWaypoint = EntityManager.GetComponentData<Target>(v).m_Target;
                if (EntityManager.HasComponent<Waypoint>(targetWaypoint))
                    targetWaypointIndex = EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index;
            }

            int bvWi = -1;
            float bvDist = float.MaxValue;
            int closestWi = -1;
            float closestDist = AT_STOP_MAX_DIST;

            for (int wi = 0; wi < wps.Length; wi++)
            {
                Entity wp = wps[wi].m_Waypoint;
                Entity stop = EntityManager.HasComponent<Connected>(wp)
                    ? EntityManager.GetComponentData<Connected>(wp).m_Connected : Entity.Null;
                if (stop == Entity.Null) continue;
                if (!EntityManager.HasComponent<Game.Objects.Transform>(stop)) continue;

                float3 sPos = EntityManager.GetComponentData<Game.Objects.Transform>(stop).m_Position;
                float dist = math.distance(vPos, sPos);

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestWi = wi;
                }

                if (!EntityManager.HasComponent<BoardingVehicle>(stop)) continue;
                if (EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != v) continue;
                bvWi = wi;
                bvDist = dist;
            }

            if (bvWi >= 0 && bvDist <= AT_STOP_MAX_DIST) return bvWi;

            bool allowTrackWaypointAnchoring = ENABLE_TRACK_WAYPOINT_ANCHORING && m_VehicleState.ContainsKey(v);

            if (bvWi >= 0)
            {
                if (EntityManager.HasComponent<Target>(v))
                {
                    Target target = EntityManager.GetComponentData<Target>(v);
                    if (IsSuppressedForcedMidStopBoardingGhost(v, target, wps, m_SimulationSystem.frameIndex, out _))
                        return closestWi >= 0 ? closestWi : -1;
                }

                if (allowTrackWaypointAnchoring
                    && TryResolveWaypointIndexByTrackCursor(v, wps, targetWaypointIndex, bvWi, closestWi, out int anchoredWaypointIndex, out string anchorDetail))
                {
                    LogVehicleStateOnce(
                        m_BvTrackAnchorRecoveryLogCache,
                        v,
                        "bv-mismatch|" + anchorDetail,
                        "[定位接管] 车辆" + v.Index + " BV误写后按track锚定 wp[" + anchoredWaypointIndex + "] " + anchorDetail);
                    return anchoredWaypointIndex;
                }

                uint nowFrame = m_SimulationSystem.frameIndex;
                string mismatchKey = "wps[" + bvWi + "]|closest[" + closestWi + "]";
                if (ShouldEmitVehicleLogWithCooldown(
                        m_BvWaypointMismatchLogCache,
                        m_BvWaypointMismatchLastLogFrame,
                        v,
                        mismatchKey,
                        nowFrame,
                        BV_WAYPOINT_MISMATCH_LOG_COOLDOWN_FRAMES))
                {
                    log.Info("[定位] 车辆" + v.Index
                        + " BV误写 wps[" + bvWi + "] dist=" + bvDist.ToString("F0") + "m > " + AT_STOP_MAX_DIST + "m");
                }
                return -1;
            }

            if (allowTrackWaypointAnchoring
                && boarding
                && TryResolveWaypointIndexByTrackCursor(v, wps, targetWaypointIndex, -1, closestWi, out int boardingWaypointIndex, out string boardingAnchorDetail))
            {
                LogVehicleStateOnce(
                    m_BvTrackAnchorRecoveryLogCache,
                    v,
                    "boarding-track|" + boardingAnchorDetail,
                    "[定位接管] 车辆" + v.Index + " boarding无站位按track锚定 wp[" + boardingWaypointIndex + "] " + boardingAnchorDetail);
                return boardingWaypointIndex;
            }

            if (closestWi >= 0)
                return closestWi;

            return -1;
        }

        private bool TryResolveWaypointIndexByTrackCursor(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            int closestWaypointIndex,
            out int waypointIndex,
            out string detail)
        {
            waypointIndex = -1;
            detail = string.Empty;
            Entity line = ResolveVehicleLine(vehicle);
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            var candidateIndices = new HashSet<int>();
            void AddCandidate(int index)
            {
                if (index >= 0 && index < waypoints.Length)
                    candidateIndices.Add(index);
            }

            AddCandidate(targetWaypointIndex);
            AddCandidate(boardingWaypointIndex);
            AddCandidate(closestWaypointIndex);
            if (cursor.SegmentIndex >= 0)
            {
                AddCandidate(cursor.SegmentIndex);
                AddCandidate(cursor.SegmentIndex + 1);
                AddCandidate(cursor.SegmentIndex - 1);
                AddCandidate(cursor.SegmentIndex + 2);
            }

            int bestWaypointIndex = -1;
            int bestWindowStart = -1;
            int bestWindowEndExclusive = -1;
            int bestDistance = int.MaxValue;
            const int anchorSlackAtoms = 6;

            foreach (int candidateIndex in candidateIndices)
            {
                if (!TryGetWaypointTraversalAtomWindow(chain, candidateIndex, cursor.AtomCursorIndex, out int windowStart, out int windowEndExclusive))
                    continue;

                int expandedStart = math.max(0, windowStart - anchorSlackAtoms);
                int expandedEndExclusive = math.min(chain.TrackAtoms.Count, windowEndExclusive + anchorSlackAtoms);
                if (cursor.AtomCursorIndex < expandedStart || cursor.AtomCursorIndex >= expandedEndExclusive)
                    continue;

                int distance = cursor.AtomCursorIndex < windowStart
                    ? windowStart - cursor.AtomCursorIndex
                    : cursor.AtomCursorIndex >= windowEndExclusive
                        ? cursor.AtomCursorIndex - (windowEndExclusive - 1)
                        : 0;
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestWaypointIndex = candidateIndex;
                bestWindowStart = windowStart;
                bestWindowEndExclusive = windowEndExclusive;
            }

            if (bestWaypointIndex < 0)
                return false;

            waypointIndex = bestWaypointIndex;
            detail = "atom=" + cursor.AtomCursorIndex
                + " seg=" + cursor.SegmentIndex
                + " targetWp=" + targetWaypointIndex
                + " bvWp=" + boardingWaypointIndex
                + " closestWp=" + closestWaypointIndex
                + " window=" + bestWindowStart + ".." + bestWindowEndExclusive;
            return true;
        }

        private bool TryGetWaypointTraversalAtomWindow(
            LineTrackChain chain,
            int waypointIndex,
            int referenceAtomIndex,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            startAtomIndex = -1;
            endAtomIndexExclusive = -1;
            if (chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.Events == null
                || waypointIndex < 0)
            {
                return false;
            }

            int bestDistance = int.MaxValue;
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                if (traversalEvent.WaypointIndex != waypointIndex
                    || (traversalEvent.Kind != TraversalEventKind.Stop && traversalEvent.Kind != TraversalEventKind.Pass))
                {
                    continue;
                }

                int candidateStart = traversalEvent.StartAtomIndex;
                int candidateEndExclusive = math.max(candidateStart + 1, traversalEvent.EndAtomIndexExclusive);
                int candidateDistance = referenceAtomIndex < candidateStart
                    ? candidateStart - referenceAtomIndex
                    : referenceAtomIndex >= candidateEndExclusive
                        ? referenceAtomIndex - (candidateEndExclusive - 1)
                        : 0;
                if (candidateDistance >= bestDistance)
                    continue;

                bestDistance = candidateDistance;
                startAtomIndex = candidateStart;
                endAtomIndexExclusive = candidateEndExclusive;
            }

            return startAtomIndex >= 0 && endAtomIndexExclusive > startAtomIndex;
        }

        private bool IsSuppressedForcedMidStopBoardingGhost(
            Entity vehicle,
            Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out int targetWaypointIndex)
        {
            targetWaypointIndex = -1;
            if (vehicle == Entity.Null
                || !m_ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out uint graceUntil))
            {
                return false;
            }

            if (nowFrame >= graceUntil)
            {
                m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
                return false;
            }

            if (!EntityManager.HasComponent<Waypoint>(target.m_Target))
                return false;

            targetWaypointIndex = EntityManager.GetComponentData<Waypoint>(target.m_Target).m_Index;
            if (targetWaypointIndex < 0 || targetWaypointIndex >= waypoints.Length)
                return false;

            Entity targetStop = GetConnectedStopForWaypoint(waypoints[targetWaypointIndex].m_Waypoint);
            if (targetStop == Entity.Null
                || !EntityManager.HasComponent<BoardingVehicle>(targetStop)
                || EntityManager.GetComponentData<BoardingVehicle>(targetStop).m_Vehicle != vehicle
                || !EntityManager.HasComponent<Game.Objects.Transform>(targetStop)
                || !EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                return false;
            }

            float3 vehiclePosition = EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
            float3 stopPosition = EntityManager.GetComponentData<Game.Objects.Transform>(targetStop).m_Position;
            return math.distance(vehiclePosition, stopPosition) > AT_STOP_MAX_DIST;
        }

        private Entity GetConnectedStopForWaypoint(Entity waypoint)
        {
            if (waypoint == Entity.Null || !EntityManager.HasComponent<Connected>(waypoint))
                return Entity.Null;

            return EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
        }

        private static void InjectModifier(DynamicBuffer<RouteModifier> mods, float delta)
        {
            int idx = (int)RouteModifierType.VehicleInterval;
            while (mods.Length <= idx)
                mods.Add(new RouteModifier { m_Delta = float2.zero });
            var m = mods[idx];
            m.m_Delta = new float2(delta, 0f);
            mods[idx] = m;
        }

        private float CalculateLineDuration(Entity line)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line)) return 0f;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab)) return 0f;
            float stopDuration = EntityManager.GetComponentData<TransportLineData>(prefab).m_StopDuration;

            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);
            var segBuffers = GetBufferLookup<RouteSegment>(true);
            var pathInfoLookup = GetComponentLookup<PathInformation>(true);
            var vehicleTimingLookup = GetComponentLookup<VehicleTiming>(true);

            if (!wpBuffers.TryGetBuffer(line, out var waypoints)) return 0f;
            if (!segBuffers.TryGetBuffer(line, out var segments)) return 0f;
            if (waypoints.Length == 0 || segments.Length == 0) return 0f;

            int firstWaypoint = 0;
            for (int w = 0; w < waypoints.Length; w++)
            {
                if (vehicleTimingLookup.HasComponent(waypoints[w].m_Waypoint))
                {
                    firstWaypoint = w;
                    break;
                }
            }

            float duration = 0f;
            for (int i = 0; i < waypoints.Length; i++)
            {
                int wi = (firstWaypoint + i) % waypoints.Length;
                int wi1 = (wi + 1) % waypoints.Length;
                Entity seg = segments[wi].m_Segment;
                if (pathInfoLookup.TryGetComponent(seg, out var pathInfo))
                    duration += pathInfo.m_Duration;
                if (vehicleTimingLookup.HasComponent(waypoints[wi1].m_Waypoint))
                    duration += stopDuration;
            }
            return duration;
        }

        private void AssignSlot(Entity v, int slot, EntityCommandBuffer ecb)
        {
            m_VehicleTargetMin[v] = slot;
            var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
            pt.m_DepartureFrame = m_SimulationSystem.frameIndex + 9999;
            ecb.SetComponent(v, pt);
            SetUILabel(v, "候车 " + SlotStr(slot));
        }

        private void TryLogSpawnBlocked(Entity line, string lineTag, int nowMin, int slot)
        {
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_LastSpawnBlockedLogFrame.TryGetValue(line, out uint lastFrame)
                && (nowFrame - lastFrame) < SPAWN_BLOCKED_LOG_COOLDOWN_FRAMES)
                return;

            m_LastSpawnBlockedLogFrame[line] = nowFrame;
            log.Info("[SpawnBlocked] " + lineTag + " 班次" + SlotStr(slot)
                + " 始发站附近已有回流车，跳过产车");
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
            uint nowFrame = m_SimulationSystem.frameIndex;
            ulong key = MakeLineSlotKey(line, slot);
            if (m_LastScheduleDiagnosticLogFrame.TryGetValue(key, out uint lastFrame)
                && (nowFrame - lastFrame) < SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES)
                return;

            m_LastScheduleDiagnosticLogFrame[key] = nowFrame;
            log.Info("[调度诊断] " + lineTag + " 班次" + SlotStr(slot)
                + " 最近候选车辆" + nearestVehicle.Index
                + " state=" + nearestState
                + " eta=" + etaText
                + " reason=" + nearestReason);
        }

        private static ulong MakeLineSlotKey(Entity line, int slot)
        {
            return ((ulong)(uint)line.Index << 32) | (uint)(slot & 0xFFFF);
        }

        private bool HasInboundVehicleNearOrigin(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            Entity ignoreVehicle,
            float radiusMeters,
            bool includePreparingVehicles = true)
        {
            Entity stationA = wps[0].m_Waypoint;
            Entity stopA = EntityManager.HasComponent<Connected>(stationA)
                ? EntityManager.GetComponentData<Connected>(stationA).m_Connected
                : Entity.Null;
            if (stopA == Entity.Null || !EntityManager.HasComponent<Game.Objects.Transform>(stopA))
                return false;

            float3 stationPos = EntityManager.GetComponentData<Game.Objects.Transform>(stopA).m_Position;
            float radiusSq = radiusMeters * radiusMeters;
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity nearV = rvs[i].m_Vehicle;
                if (nearV == ignoreVehicle) continue;
                if (!EntityManager.Exists(nearV)) continue;
                if (!m_VehicleState.TryGetValue(nearV, out var nearState)) continue;
                bool isPreparing = nearState == VehicleState.Preparing;
                if (isPreparing && !includePreparingVehicles) continue;
                bool isTaggedInbound = m_NearingTerminus.Contains(nearV);
                if (!isPreparing && !isTaggedInbound) continue;
                if (!EntityManager.HasComponent<Game.Objects.Transform>(nearV)) continue;

                float3 nearPos = EntityManager.GetComponentData<Game.Objects.Transform>(nearV).m_Position;
                float3 delta = nearPos - stationPos;
                float distSq = math.lengthsq(delta);
                if (distSq <= radiusSq)
                    return true;
            }

            return false;
        }

        private bool IsFreshDispatchedPreparingVehicle(Entity vehicle, uint nowFrame)
        {
            if (vehicle == Entity.Null
                || !m_VehicleDispatchRequestStartFrame.TryGetValue(vehicle, out uint dispatchStartFrame))
                return false;

            return nowFrame >= dispatchStartFrame
                && (nowFrame - dispatchStartFrame) <= PREPARING_ROUTE_FIX_GRACE_FRAMES;
        }

        private int CountActiveVehicles(Entity line, BufferLookup<RouteVehicle> rvBuffers)
        {
            int count = 0;
            if (!rvBuffers.TryGetBuffer(line, out var rvs)) return 0;
            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = rvs[i].m_Vehicle;
                if (!EntityManager.Exists(v)) continue;
                if (m_VehicleState.TryGetValue(v, out var st) && st == VehicleState.Retiring) continue;
                count++;
            }
            return count;
        }

        private float GetRemainingRange(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v)) return float.MaxValue;
            if (!EntityManager.HasComponent<PrefabRef>(v)) return float.MaxValue;
            float cur = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity pref = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(pref)) return float.MaxValue;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(pref).m_MaintenanceRange;
            return (range > 0f) ? (range - cur) : float.MaxValue;
        }

        private bool NeedsMaintenance(Entity v)
        {
            var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
            if ((pt.m_State & PublicTransportFlags.RequiresMaintenance) != 0) return true;
            if (!EntityManager.HasComponent<Odometer>(v) || !EntityManager.HasComponent<PrefabRef>(v)) return false;
            float dist = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return false;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            return range > 0f && (dist / range) >= MAINTENANCE_THRESHOLD;
        }

        private bool CanFinishNextLap(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v) || !EntityManager.HasComponent<PrefabRef>(v)) return true;
            float current = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return true;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            if (range <= 0f) return true;
            float remaining = range - current;
            if (m_VehicleLapDistance.TryGetValue(v, out float lapDist) && lapDist > 0f)
                return remaining >= lapDist;
            return (current / range) < MAINTENANCE_THRESHOLD;
        }
    }
}
