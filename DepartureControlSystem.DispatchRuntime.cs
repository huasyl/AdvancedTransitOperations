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
    public partial class DispatchRuntimeSystem
    {
        internal void ClearForcedMidStopClosingConsist(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_MidStopTimeoutLogCache.Remove(vehicle);
        }

        private void ArmAssistLaunchPending(Entity vehicle, Entity line, int targetMin)
        {
            if (vehicle == Entity.Null || line == Entity.Null || targetMin < 0)
                return;

            m_AssistLaunchPendingByVehicle[vehicle] = new AssistLaunchPendingRecord(line, targetMin);
        }

        internal void ClearAssistLaunchPending(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_AssistLaunchPendingByVehicle.Remove(vehicle);
        }

        private bool TryGetAssistLaunchPending(
            Entity vehicle,
            Entity line,
            int targetMin,
            out AssistLaunchPendingRecord pending)
        {
            if (vehicle != Entity.Null
                && m_AssistLaunchPendingByVehicle.TryGetValue(vehicle, out pending)
                && pending.Line == line
                && pending.TargetMin >= 0
                && targetMin == pending.TargetMin)
            {
                return true;
            }

            pending = default;
            return false;
        }

        private string BuildTrainHeadLaunchDiagnostic(
            Entity vehicle,
            bool hasCurrentLaunchSnapshot,
            TrainHeadSnapshot currentLaunchSnapshot)
        {
            if (!m_LastLaunchHeadSnapshots.TryGetValue(vehicle, out TrainHeadSnapshot previousLaunchSnapshot))
            {
                return hasCurrentLaunchSnapshot
                    ? " headCheck=no-prev-launch launchHead=" + FormatTrainHeadSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                        + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchWp=" + currentLaunchSnapshot.WaypointIndex
                    : " headCheck=no-prev-launch launchHead=capture-failed";
            }

            if (!m_LastBoardingHeadSnapshots.TryGetValue(vehicle, out TrainHeadSnapshot boardingSnapshot))
            {
                return hasCurrentLaunchSnapshot
                    ? " headCheck=no-boarding prevHead=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
                        + " prevRev=" + (previousLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchHead=" + FormatTrainHeadSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                        + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchWp=" + currentLaunchSnapshot.WaypointIndex
                    : " headCheck=no-boarding prevHead=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
                        + " prevRev=" + (previousLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchHead=capture-failed";
            }

            if (boardingSnapshot.Frame <= previousLaunchSnapshot.Frame)
            {
                return " headCheck=stale"
                    + " prevLaunchFrame=" + previousLaunchSnapshot.Frame
                    + " boardFrame=" + boardingSnapshot.Frame
                    + (hasCurrentLaunchSnapshot
                        ? " launchFrame=" + currentLaunchSnapshot.Frame
                        : string.Empty);
            }

            bool turned =
                previousLaunchSnapshot.HeadVehicle != boardingSnapshot.HeadVehicle
                || previousLaunchSnapshot.Reversed != boardingSnapshot.Reversed
                || previousLaunchSnapshot.FrontLane != boardingSnapshot.FrontLane
                || previousLaunchSnapshot.RearLane != boardingSnapshot.RearLane;

            string diagnostic = " headCheck=" + (turned ? "turned" : "same")
                + " prevHead=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
                + " boardHead=" + FormatTrainHeadSnapshotEntity(boardingSnapshot.HeadVehicle)
                + " prevRev=" + (previousLaunchSnapshot.Reversed ? "1" : "0")
                + " boardRev=" + (boardingSnapshot.Reversed ? "1" : "0")
                + " prevFront=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.FrontLane)
                + " prevRear=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.RearLane)
                + " boardFront=" + FormatTrainHeadSnapshotEntity(boardingSnapshot.FrontLane)
                + " boardRear=" + FormatTrainHeadSnapshotEntity(boardingSnapshot.RearLane)
                + " boardWp=" + boardingSnapshot.WaypointIndex;

            if (hasCurrentLaunchSnapshot)
            {
                diagnostic += " launchHead=" + FormatTrainHeadSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                    + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                    + " launchWp=" + currentLaunchSnapshot.WaypointIndex;
            }
            else
            {
                diagnostic += " launchHead=capture-failed";
            }

            return diagnostic;
        }

        internal void GuardRetireHandoffDispatchInputs(uint nowFrame)
        {
            m_CommandApplier.GuardRetireHandoffInputs(nowFrame);
        }

        internal void LogRouteVehicleOwnerMismatch(Entity observedLine, Entity vehicle, string phase)
        {
            if (observedLine == Entity.Null || vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            Entity mappedLine = m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            bool mappedMismatch = mappedLine != Entity.Null && mappedLine != observedLine;
            bool routeMismatch = currentRoute != Entity.Null && currentRoute != observedLine;
            if (!mappedMismatch && !routeMismatch)
                return;

            VehicleState state = m_VehicleView.TryGetState(vehicle, out VehicleState runtimeState)
                ? runtimeState
                : default;
            int targetMin = m_VehicleView.TryGetTarget(vehicle, out int assignedTarget)
                ? assignedTarget
                : -1;
            Entity targetEntity = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            string key = phase
                + "|observed=" + observedLine.Index
                + "|mapped=" + mappedLine.Index
                + "|route=" + currentRoute.Index
                + "|target=" + targetEntity.Index
                + "|state=" + state;

            LogVehicleStateOnce(
                m_RouteVehicleOwnerMismatchLogCache,
                vehicle,
                key,
                "[RouteVehicleOwnerMismatch] line=" + observedLine.Index
                    + " vehicle=" + vehicle.Index
                    + " phase=" + phase
                    + " " + BuildVehicleOwnershipDiagnostic(observedLine, vehicle, state, targetMin, phase));
        }

        internal void LogCrossLineCandidate(
            Entity observedLine,
            Entity vehicle,
            VehicleState state,
            int slot,
            float etaFrames,
            int previousTarget)
        {
            if (observedLine == Entity.Null || vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            Entity mappedLine = m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            bool mappedMismatch = mappedLine != Entity.Null && mappedLine != observedLine;
            bool routeMismatch = currentRoute != Entity.Null && currentRoute != observedLine;
            if (!mappedMismatch && !routeMismatch)
                return;

            int targetMin = m_VehicleView.TryGetTarget(vehicle, out int assignedTarget)
                ? assignedTarget
                : -1;
            Entity targetEntity = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            string key = "candidate"
                + "|observed=" + observedLine.Index
                + "|slot=" + slot
                + "|mapped=" + mappedLine.Index
                + "|route=" + currentRoute.Index
                + "|target=" + targetEntity.Index
                + "|state=" + state;

            LogVehicleStateOnce(
                m_CrossLineCandidateLogCache,
                vehicle,
                key,
                "[CrossLineCandidate] line=" + observedLine.Index
                    + " slot=" + SlotStr(slot)
                    + " vehicle=" + vehicle.Index
                    + " state=" + state
                    + " eta=" + (etaFrames == float.MaxValue ? "?" : (etaFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟")
                    + " prevTarget=" + (previousTarget >= 0 ? SlotStr(previousTarget) : "-")
                    + " " + BuildVehicleOwnershipDiagnostic(observedLine, vehicle, state, targetMin, "candidate"));
        }

        private void LogPreparingTargetDrift(
            Entity line,
            Entity vehicle,
            Entity route,
            Entity originWaypoint,
            Entity target,
            int targetMin,
            int curWpIdx,
            bool boarding,
            bool atOrigin)
        {
            if (line == Entity.Null || vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;
            if (target != Entity.Null && target == originWaypoint)
                return;

            Entity targetDepot = CanonicalizeTransportDepotEntity(target);
            string key = "target=" + target.Index
                + "|route=" + route.Index
                + "|targetDepot=" + targetDepot.Index
                + "|wp=" + curWpIdx
                + "|boarding=" + (boarding ? "1" : "0")
                + "|atA=" + (atOrigin ? "1" : "0");

            LogVehicleStateOnce(
                m_PreparingTargetDriftLogCache,
                vehicle,
                key,
                "[PreparingTargetDrift] line=" + line.Index
                    + " vehicle=" + vehicle.Index
                    + " originWp=" + DispatchCommandApplier.DescribeRetireShadowEntity(originWaypoint)
                    + " curWp=" + curWpIdx
                    + " boarding=" + (boarding ? "1" : "0")
                    + " atA=" + (atOrigin ? "1" : "0")
                    + " " + BuildVehicleOwnershipDiagnostic(line, vehicle, VehicleState.Preparing, targetMin, "preparing"));
        }

        private string BuildVehicleOwnershipDiagnostic(
            Entity observedLine,
            Entity vehicle,
            VehicleState state,
            int targetMin,
            string phase)
        {
            Entity mappedLine = m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            Entity owner = EntityManager.HasComponent<Owner>(vehicle)
                ? EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                : Entity.Null;
            Entity ownerDepot = CanonicalizeTransportDepotEntity(owner);
            Entity target = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            Entity targetDepot = CanonicalizeTransportDepotEntity(target);
            Entity pathDestination = EntityManager.HasComponent<PathInformation>(vehicle)
                ? EntityManager.GetComponentData<PathInformation>(vehicle).m_Destination
                : Entity.Null;
            Entity pathDestinationDepot = CanonicalizeTransportDepotEntity(pathDestination);
            string publicState = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State.ToString()
                : "-";
            string pathState = EntityManager.HasComponent<PathInformation>(vehicle)
                ? EntityManager.GetComponentData<PathInformation>(vehicle).m_State.ToString()
                : "-";
            int cachedWp = m_CachedWpIdx.TryGetValue(vehicle, out int cached)
                ? cached
                : -1;
            uint preparingAge = m_VehicleView.TryGetPreparing(vehicle, out uint prepStart)
                ? m_SimulationSystem.frameIndex - prepStart
                : 0;

            return "phase=" + phase
                + " observedLine=" + DispatchCommandApplier.DescribeRetireShadowEntity(observedLine)
                + " mappedLine=" + DispatchCommandApplier.DescribeRetireShadowEntity(mappedLine)
                + " currentRoute=" + DispatchCommandApplier.DescribeRetireShadowEntity(currentRoute)
                + " state=" + state
                + " targetMin=" + (targetMin >= 0 ? SlotStr(targetMin) : "-")
                + " cachedWp=" + cachedWp
                + " preparingAgeFrames=" + preparingAge
                + " owner=" + DispatchCommandApplier.DescribeRetireShadowEntity(owner)
                + " ownerDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(ownerDepot)
                + " target=" + DispatchCommandApplier.DescribeRetireShadowEntity(target)
                + " targetKind=" + m_CommandApplier.DescribeRetireShadowTargetKind(target)
                + " targetExists=" + ((target != Entity.Null && EntityManager.Exists(target)) ? "1" : "0")
                + " targetDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(targetDepot)
                + " pathDest=" + DispatchCommandApplier.DescribeRetireShadowEntity(pathDestination)
                + " pathDestDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(pathDestinationDepot)
                + " pathState=" + pathState
                + " ptState=" + publicState
                + " deleted=" + (EntityManager.HasComponent<Deleted>(vehicle) ? "1" : "0")
                + " parked=" + (EntityManager.HasComponent<ParkedTrain>(vehicle) ? "1" : "0");
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
                    ApplyPuppetMasterControlForLine(line, rvBuffers, modBuffers, wpBuffers);
                }
            }
            finally { lines.Dispose(); }
        }

        private void ApplyPuppetMasterControlForLine(
            Entity line,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteModifier> modBuffers,
            BufferLookup<RouteWaypoint> wpBuffers)
        {
            if (!EntityManager.Exists(line)) return;
            if (!wpBuffers.TryGetBuffer(line, out var wps) || wps.Length < 2) return;
            if (!IsLineStable(line, wps)) return;
            if (!IsDispatchRuntimeManagedLine(line)) return;
            if (!EntityManager.HasComponent<PrefabRef>(line)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab)) return;
            if (!modBuffers.TryGetBuffer(line, out var mods)) return;

            float iDefault = EntityManager.GetComponentData<TransportLineData>(prefab).m_DefaultVehicleInterval;
            float D = CalculateLineDuration(line);
            if (D <= 0f) D = iDefault;
            if (D <= 0f) return;

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

        private void ApplyCleanupTargetReductionForLine(
            Entity line,
            int removedCount,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteModifier> modBuffers,
            BufferLookup<RouteWaypoint> wpBuffers)
        {
            if (line == Entity.Null || !EntityManager.Exists(line) || removedCount <= 0)
                return;

            int actualCount = CountActiveVehicles(line, rvBuffers);
            if (m_SpawningLines.TryGetValue(line, out int spawnTarget))
            {
                int newSpawnTarget = math.max(0, spawnTarget - removedCount);
                if (newSpawnTarget <= actualCount)
                {
                    m_SpawningLines.Remove(line);
                    m_LineSpawnRequestFrame.Remove(line);
                    log.Info("[CleanupTargetAdjust] 线路" + line.Index
                        + " 清理" + removedCount + "辆"
                        + " spawnTarget=" + spawnTarget + " -> -"
                        + " actualCount=" + actualCount);
                }
                else if (newSpawnTarget != spawnTarget)
                {
                    m_SpawningLines[line] = newSpawnTarget;
                    log.Info("[CleanupTargetAdjust] 线路" + line.Index
                        + " 清理" + removedCount + "辆"
                        + " spawnTarget=" + spawnTarget + " -> " + newSpawnTarget
                        + " actualCount=" + actualCount);
                }
            }

            ApplyPuppetMasterControlForLine(line, rvBuffers, modBuffers, wpBuffers);
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

                    m_CommandApplier.ApplyDepotRequestPath(request, forcedPath);
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
                    if (!IsDispatchRuntimeManagedLine(line)) continue;
                    bool adoptExistingVehicles = !m_LineInitialAdopted.Contains(line);
                    bool isHotLine = adoptExistingVehicles || m_SpawningLines.ContainsKey(line);
                    if (!fullSweep && !isHotLine) continue;

                    string lineTag = "线路" + line.Index;
                    HashSet<Entity> seenVehicles = new HashSet<Entity>();

                    if (!adoptExistingVehicles && !m_DiagnosedLines.Contains(line))
                    {
                        m_DiagnosedLines.Add(line);
                        LogLineTrackChainDiagnostics(line);
                        string lineName = ResolveWorkbenchEntityName(line);
                        log.Info("[诊断] " + lineTag + " (" + lineName + ") waypoint数=" + wps.Length);
                    }

                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                        if (!EntityManager.Exists(v)) continue;
                        if (!seenVehicles.Add(v)) continue;
                        if (m_VehicleView.Contains(v)) continue;

                        var pt0 = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        bool boarding0 = (pt0.m_State & PublicTransportFlags.Boarding) != 0;
                        if ((pt0.m_State & PublicTransportFlags.Returning) != 0)
                            continue;

                        int initWpIdx = boarding0 ? ComputeWpIndex(v, wps) : -1;
                        bool atA0 = (initWpIdx == 0);

                        string initReason;
                        var initState = InferInitialVehicleState(v, wps, pt0, boarding0, initWpIdx, adoptExistingVehicles, out initReason);
                        uint? dispatchFrame = null;
                        if (!adoptExistingVehicles
                            && m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
                        {
                            dispatchFrame = spawnRequestFrame;
                            m_LineSpawnRequestFrame.Remove(line);
                        }
                        m_RuntimeController.Adopt(v, line, initState, m_SimulationSystem.frameIndex, dispatchFrame);
                        m_LapObservations.SetDistance(v, -1f);
                        m_LastBoarding[v] = boarding0;
                        m_CachedWpIdx[v] = initWpIdx;
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
                        VehicleState finalState = m_VehicleView.GetState(v);
                        int finalTarget = m_VehicleView.TryGetTarget(v, out int ft) ? ft : -1;
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
                        LogVehicleStateOnce(
                            m_RouteVehicleOwnerMismatchLogCache,
                            v,
                            "register-detail|line=" + line.Index
                                + "|state=" + finalState
                                + "|target=" + (EntityManager.HasComponent<Target>(v) ? EntityManager.GetComponentData<Target>(v).m_Target.Index : -1)
                                + "|route=" + (EntityManager.HasComponent<CurrentRoute>(v) ? EntityManager.GetComponentData<CurrentRoute>(v).m_Route.Index : -1),
                            "[RegisterDetail] " + lineTag + " 车辆" + v.Index
                                + " " + BuildVehicleOwnershipDiagnostic(line, v, finalState, finalTarget, "register")
                                + " initReason=" + initReason
                                + " restored=" + (restored ? "1" : "0")
                                + " atA0=" + (atA0 ? "1" : "0")
                                + " initWp=" + initWpIdx);
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
                HashSet<Entity> seenVehicles = new HashSet<Entity>();
                foreach (var rawVehicle in vehicles)
                {
                    Entity v = ResolveRuntimeControllerVehicle(rawVehicle);
                    if (v == Entity.Null || !EntityManager.Exists(v)) continue;
                    if (!seenVehicles.Add(v)) continue;
                    if (!EntityManager.Exists(v)) continue;
                    Entity line = ResolveVehicleLine(v);
                    if (!IsDispatchRuntimeManagedLine(line)) continue;
                    if (!m_VehicleView.TryGetState(v, out var state)) continue;
                    int targetMin = m_VehicleView.TryGetTarget(v, out int tm) ? tm : -1;
                    if (!publicTransportLookup.HasComponent(v)
                        || !targetLookup.HasComponent(v)
                        || !currentRouteLookup.HasComponent(v))
                    {
                        if (targetMin >= 0 || state == VehicleState.Holding || state == VehicleState.Preparing)
                        {
                            LogVehicleStateOnce(
                                m_OriginDispatchTraceLogCache,
                                v,
                                "runtime-skip-core|state=" + state
                                    + "|target=" + targetMin
                                    + "|pt=" + (publicTransportLookup.HasComponent(v) ? "1" : "0")
                                    + "|tgt=" + (targetLookup.HasComponent(v) ? "1" : "0")
                                    + "|route=" + (currentRouteLookup.HasComponent(v) ? "1" : "0"),
                                "[OriginDispatchTrace] reason=runtime-skip-core line=" + line.Index
                                    + " vehicle=" + v.Index
                                    + " state=" + state
                                    + " target=" + FormatDispatchTraceSlot(targetMin)
                                    + " hasPublicTransport=" + (publicTransportLookup.HasComponent(v) ? "1" : "0")
                                    + " hasTarget=" + (targetLookup.HasComponent(v) ? "1" : "0")
                                    + " hasCurrentRoute=" + (currentRouteLookup.HasComponent(v) ? "1" : "0"));
                        }
                        continue;
                    }

                    var pt = publicTransportLookup[v];
                    var tgt = targetLookup[v];
                    var cr = currentRouteLookup[v];
                    Entity routeEnt = cr.m_Route;

                    if (!wpBuffers.TryGetBuffer(routeEnt, out var wps) || wps.Length < 2)
                    {
                        if (targetMin >= 0 || state == VehicleState.Holding || state == VehicleState.Preparing)
                        {
                            LogVehicleStateOnce(
                                m_OriginDispatchTraceLogCache,
                                v,
                                "runtime-skip-wps|route=" + routeEnt.Index,
                                "[OriginDispatchTrace] reason=runtime-skip-wps line=" + line.Index
                                    + " route=" + routeEnt.Index
                                    + " vehicle=" + v.Index
                                    + " state=" + state
                                    + " target=" + FormatDispatchTraceSlot(targetMin)
                                    + " hasWpBuffer=" + (wpBuffers.TryGetBuffer(routeEnt, out _) ? "1" : "0"));
                        }
                        continue;
                    }
                    int waypointCount = wps.Length;

                    bool boarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
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
                    if (state == VehicleState.Retiring)
                    {
                        SetUILabel(v, "回库中 #" + v.Index);
                        continue;
                    }

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
                                m_RuntimeController.ReleaseTarget(v);
                            }
                            else
                            {
                                log.Info("[BVMisfire] " + lineTag + " 车辆" + v.Index + " 超时，回库");
                            }
                            m_BVMisfire.Remove(v);
                            m_BVMisfireStartFrame.Remove(v);
                            m_CommandApplier.Retire(v, pt, tgt, ecb, "BVMisfire超时");
                            continue;
                        }
                        int misfireCurWpIdx = m_CachedWpIdx.TryGetValue(v, out int misfireCachedWpIdx) ? misfireCachedWpIdx : -1;
                        bool misfireAtA = state == VehicleState.Preparing
                            ? HasPreparingVehicleReachedOrigin(v, wps, boarding, misfireCurWpIdx)
                            : (misfireCurWpIdx == 0);
                        LogOriginDispatchTrace(
                            "bv-misfire-latched",
                            v,
                            lineEnt,
                            routeEnt,
                            wps,
                            state,
                            targetMin,
                            nowMin,
                            misfireCurWpIdx,
                            misfireAtA,
                            boarding,
                            m_LastBoarding.TryGetValue(v, out bool misfireLastBoarding) && misfireLastBoarding,
                            nowFrame,
                            "misfireAgeFrames=" + (m_BVMisfireStartFrame.TryGetValue(v, out uint loggedMisfireStart) ? (nowFrame - loggedMisfireStart).ToString() : "?"));
                        string misfireLabel = m_ForcedMidStopBoardingGraceUntil.TryGetValue(v, out uint forcedDepartGraceUntil)
                            && nowFrame < forcedDepartGraceUntil
                            ? "停站超时协助中 #" + v.Index
                            : "寻路异常 #" + v.Index;
                        SetUILabel(v, misfireLabel);
                        continue;
                    }

                    bool inCooldown = m_VehicleView.TryGetCooldown(v, out uint cooldownUntil)
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
                            bool departureGateLatched = false;
                            Entity departureGateLatchedBlocker = Entity.Null;
                            bool departureGateShouldHold = false;
                            bool departureGateCanClearAfterExit = true;
                            Entity departureGateBlocker = Entity.Null;
                            if (state == VehicleState.Running && previousCachedWpIdx > 0)
                            {
                                BypassDecisionResult departureGateDecision = m_BypassDecision.Evaluate(
                                    v,
                                    lineEnt,
                                    wps,
                                    previousCachedWpIdx,
                                    nowFrame);
                                departureGateLatched = departureGateDecision.HadLatchedYield;
                                departureGateLatchedBlocker = departureGateDecision.LatchedBlocker;
                                departureGateShouldHold = departureGateDecision.ShouldHold;
                                departureGateCanClearAfterExit = departureGateDecision.CanClearAfterExit;
                                departureGateBlocker = m_BypassDecision.FindBlocker(departureGateDecision);
                                suppressBypassDepartureBounce = departureGateShouldHold && !departureGateCanClearAfterExit;
                            }

                            if (suppressBypassDepartureBounce)
                            {
                                LogVehicleStateOnce(
                                    m_BypassDepartureGateLogCache,
                                    v,
                                    "gate|suppress|" + previousCachedWpIdx + "|" + departureGateLatched + "|" + departureGateShouldHold + "|" + departureGateCanClearAfterExit,
                                    "[待避离站门] vehicle=" + v.Index
                                        + " line=" + lineEnt.Index
                                        + " state=" + state
                                        + " prevWp=" + previousCachedWpIdx
                                        + " liveWp=-"
                                        + " boarding=" + boarding
                                        + " lastBoarding=" + lastBoarding
                                        + " latched=" + departureGateLatched
                                        + " latchedBlocker=" + departureGateLatchedBlocker.Index
                                        + " shouldHold=" + departureGateShouldHold
                                        + " blocker=" + departureGateBlocker.Index
                                        + " canClear=" + departureGateCanClearAfterExit
                                        + " depFrame=" + pt.m_DepartureFrame
                                        + " action=suppress");
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
                                    if (state == VehicleState.Running && previousCachedWpIdx > 0 && (departureGateLatched || departureGateShouldHold))
                                    {
                                        LogVehicleStateOnce(
                                            m_BypassDepartureGateLogCache,
                                            v,
                                            "gate|still|" + previousCachedWpIdx + "|" + liveDepartureWpIdx + "|" + departureGateLatched + "|" + departureGateShouldHold + "|" + departureGateCanClearAfterExit,
                                            "[待避离站门] vehicle=" + v.Index
                                                + " line=" + lineEnt.Index
                                                + " state=" + state
                                                + " prevWp=" + previousCachedWpIdx
                                                + " liveWp=" + liveDepartureWpIdx
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " latched=" + departureGateLatched
                                                + " latchedBlocker=" + departureGateLatchedBlocker.Index
                                                + " shouldHold=" + departureGateShouldHold
                                                + " blocker=" + departureGateBlocker.Index
                                                + " canClear=" + departureGateCanClearAfterExit
                                                + " depFrame=" + pt.m_DepartureFrame
                                                + " action=still-at-stop");
                                    }
                                    curWpIdx = previousCachedWpIdx;
                                    m_CachedWpIdx[v] = previousCachedWpIdx;
                                    m_LastBoarding[v] = true;
                                }
                                else
                                {
                                    if (state == VehicleState.Running && previousCachedWpIdx > 0 && (departureGateLatched || departureGateShouldHold))
                                    {
                                        LogVehicleStateOnce(
                                            m_BypassDepartureGateLogCache,
                                            v,
                                            "gate|depart|" + previousCachedWpIdx + "|" + liveDepartureWpIdx + "|" + departureGateLatched + "|" + departureGateShouldHold + "|" + departureGateCanClearAfterExit,
                                            "[待避离站门] vehicle=" + v.Index
                                                + " line=" + lineEnt.Index
                                                + " state=" + state
                                                + " prevWp=" + previousCachedWpIdx
                                                + " liveWp=" + liveDepartureWpIdx
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " latched=" + departureGateLatched
                                                + " latchedBlocker=" + departureGateLatchedBlocker.Index
                                                + " shouldHold=" + departureGateShouldHold
                                                + " blocker=" + departureGateBlocker.Index
                                                + " canClear=" + departureGateCanClearAfterExit
                                                + " depFrame=" + pt.m_DepartureFrame
                                                + " action=depart-log");
                                    }
                                    TryRecordObservedStopDwellOnBoardingEnd(v, lineEnt, previousCachedWpIdx, nowFrame);
                                    RecordWorkbenchRealtimeStopEvent(v, lineEnt, wps, false, -1, previousCachedWpIdx);
                                    ArmBroadcastLeaveStationTrigger(v, lineEnt, wps, previousCachedWpIdx);
                                    if (state == VehicleState.Running && previousCachedWpIdx >= 0)
                                    {
                                        StopRef departedStop = ResolveStop(
                                            wps[previousCachedWpIdx].m_Waypoint,
                                            GetLatestStop(v));
                                        Entity departedStopEntity = departedStop.Ent;
                                        Entity departedStopBuilding = departedStop.Kind == ResolvedStopKind.Building
                                            ? departedStop.Ent
                                            : GetStationBuildingForWaypoint(wps, previousCachedWpIdx);
                                        string departedStopName = departedStop.Kind == ResolvedStopKind.Building
                                            ? ResolveWorkbenchEntityName(departedStopEntity)
                                            : ResolveWorkbenchEntityName(departedStopBuilding);
                                        if (string.IsNullOrWhiteSpace(departedStopName))
                                        {
                                            departedStopName = "stop#" + departedStopEntity.Index;
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
                                    }
                                    TryClearVehicleProgressSuspectOnStableDeparture(v, previousCachedWpIdx);
                                    curWpIdx = -1;
                                    m_CachedWpIdx[v] = -1;
                                    m_LastBoarding[v] = false;
                                    m_BVMisfire.Remove(v);
                                    m_BVMisfireStartFrame.Remove(v);
                                    ClearForcedMidStopClosingConsist(v);
                                    m_StopDwell.Remove(v);
                                }
                            }
                        }
                        else
                        {
                            curWpIdx = ComputeWpIndex(v, wps);
                            m_CachedWpIdx[v] = curWpIdx;

                            if (curWpIdx >= 0)
                            {
                                if (TryCaptureTrainHeadSnapshot(v, curWpIdx, out TrainHeadSnapshot boardingHeadSnapshot))
                                    m_LastBoardingHeadSnapshots[v] = boardingHeadSnapshot;
                                else
                                    m_LastBoardingHeadSnapshots.Remove(v);
                                BeginObservedStopDwellSession(v, lineEnt, curWpIdx, nowFrame);
                                RecordWorkbenchRealtimeStopEvent(v, lineEnt, wps, true, curWpIdx, previousCachedWpIdx);
                                HandleBroadcastStopAndOpenTrigger(v, lineEnt, wps, curWpIdx);
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
                                    m_RuntimeController.MarkInbound(v);
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
                        if (m_TraversalSlices.Sessions.TryGetValue(v, out VehicleTraversalSliceSession droppedSession))
                            RecordTraversalSliceLapDebugDropped(v, droppedSession.SliceIndex);
                        m_TraversalSlices.Sessions.Remove(v);
                    }

                    bool atA = state == VehicleState.Preparing
                        ? HasPreparingVehicleReachedOrigin(v, wps, boarding, curWpIdx)
                        : (curWpIdx == 0);
                    bool broadcastOriginWaitBusy = atA
                        || boarding
                        || m_VehicleRuntime.ForcedOriginReadyFrame.ContainsKey(v)
                        || (state != VehicleState.Preparing
                            && m_CachedWpIdx.TryGetValue(v, out int broadcastCachedWpIdx)
                            && broadcastCachedWpIdx == 0);
                    if (state == VehicleState.Preparing)
                        LogPreparingTargetDrift(lineEnt, v, routeEnt, wps[0].m_Waypoint, tgt.m_Target, targetMin, curWpIdx, boarding, atA);
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
                    bool hasEnabledPlatformAnnouncements = LineHasEnabledBroadcastPlatformAnnouncements(routeEnt);

                    switch (state)
                    {
                        case VehicleState.Preparing:
                            if (hasEnabledPlatformAnnouncements)
                            {
                                float preparingApproachEtaFrames = atA
                                    ? 0f
                                    : EstimatePreparingArrivalFrames(v, routeEnt, wps, nowFrame, ReadLineLapCache(routeEnt));
                                UpdateBroadcastPlatformOriginBusyWatch(
                                    routeEnt,
                                    wps,
                                    atA || preparingApproachEtaFrames <= BroadcastPlatformPreparingApproachLeadMinutes * (float)SIM_FRAMES_PER_MINUTE);
                                if (LineHasEnabledBroadcastPlatformApproachAnnouncements(routeEnt))
                                {
                                    UpdateBroadcastPreparingPlatformApproachWatch(
                                        v,
                                        routeEnt,
                                        wps,
                                        atA,
                                        preparingApproachEtaFrames);
                                }
                            }

                            if (targetMin >= 0 && m_DispatchScheduler.IsSoftExpired(nowMin, targetMin) && !m_DispatchScheduler.CanLateDispatch(nowMin, targetMin))
                            {
                                int overdue = m_DispatchScheduler.OverdueMinutes(nowMin, targetMin);
                                LogVehicleStateOnce(
                                    m_PreparingSlotLogCache,
                                    v,
                                    "PreparingSlot|" + targetMin + "|" + overdue,
                                    "[PreparingSlot] " + lineTag + " 车辆" + v.Index
                                        + " 班次" + SlotStr(targetMin) + " 已过期(" + overdue + "分钟)，释放重新调度");
                                m_RuntimeController.ReleaseTarget(v);
                                targetMin = -1;
                            }

                            if (atA)
                            {
                                if (targetMin < 0 && m_DispatchScheduler.TryAssignUpcomingTarget(
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

                                if (m_DispatchScheduler.ShouldRetireWaitingVehicle(routeEnt, nowMin, targetMin))
                                {
                                    m_CommandApplier.Retire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMin, targetMin));
                                    break;
                                }
                                m_RuntimeController.Hold(v, nowFrame + PREPARING_ORIGIN_SETTLE_FRAMES);
                                TryRecordPreparingArrivalSample(v, lineEnt, nowFrame);
                                RecordLineHoldingSummary(lineEnt, nowMin, v, targetMin);
                                if (targetMin >= 0)
                                {
                                    RecordRuntimeObservationTargetBound(routeEnt, v, targetMin, nowFrame, "preparing-holding-assign");
                                }
                                m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
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
                                m_CommandApplier.EnsurePreparingRoute(v, ref pt, ref tgt, wps, curWpIdx, boarding, ecb);
                                SetUILabel(v, "前往始发站" + (targetMin >= 0 ? " " + SlotStr(targetMin) : "") + vTag);
                            }
                            break;

                        case VehicleState.Holding:
                            if (hasEnabledPlatformAnnouncements)
                            {
                                UpdateBroadcastPlatformOriginBusyWatch(routeEnt, wps, broadcastOriginWaitBusy);
                            }

                            if (!atA)
                            {
                                if (TryGetAssistLaunchPending(v, routeEnt, targetMin, out AssistLaunchPendingRecord assistPending))
                                {
                                    int assistedTargetMin = assistPending.TargetMin;
                                    bool isLateAssistLaunch = m_DispatchScheduler.CanLateDispatch(nowMin, assistedTargetMin);
                                    m_RuntimeController.Launch(v, assistedTargetMin, nowFrame, nowFrame + LAUNCH_COOLDOWN_FRAMES);
                                    RequestLineOrderedRuntimeForceRefresh(routeEnt, "origin-assist-launch-sync");
                                    m_JustLaunched.Add(v);
                                    RecordLapStart(v, isLateAssistLaunch ? "协助补发确认" : "协助发车确认");
                                    m_LastBoarding[v] = false;
                                    m_CachedWpIdx[v] = -1;
                                    m_BVMisfire.Remove(v);
                                    m_BVMisfireStartFrame.Remove(v);
                                    RecordRuntimeObservationLaunch(routeEnt, v, assistedTargetMin, nowMin, nowFrame, isLateAssistLaunch);
                                    ClearAssistLaunchPending(v);
                                    pt.m_DepartureFrame = nowFrame > 0 ? nowFrame - 1 : 0;
                                    pt.m_State &= ~PublicTransportFlags.Boarding;
                                    m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                                    SetUILabel(v, (isLateAssistLaunch ? "运行中 补发 " : "运行中 ") + SlotStr(assistedTargetMin) + vTag);
                                    log.Info("[AssistLaunchSync] " + lineTag + " 车辆" + v.Index
                                        + " 在始发发车协助后已离站，补记班次" + SlotStr(assistedTargetMin)
                                        + " 于 " + SlotStr(nowMin)
                                        + (isLateAssistLaunch ? " late=1" : " late=0"));
                                    break;
                                }
                                if (IsWaitingForcedOriginDwell(v, nowFrame))
                                {
                                    LogOriginDispatchTrace(
                                        "holding-not-at-origin-forced-dwell",
                                        v,
                                        lineEnt,
                                        routeEnt,
                                        wps,
                                        state,
                                        targetMin,
                                        nowMin,
                                        curWpIdx,
                                        atA,
                                        boarding,
                                        lastBoarding,
                                        nowFrame);
                                    m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                    SetUILabel(v, targetMin >= 0 ? "候车 " + SlotStr(targetMin) + vTag : "候车 等待调度" + vTag);
                                    break;
                                }
                                LogOriginDispatchTrace(
                                    "holding-not-at-origin-abnormal-running",
                                    v,
                                    lineEnt,
                                    routeEnt,
                                    wps,
                                    state,
                                    targetMin,
                                    nowMin,
                                    curWpIdx,
                                    atA,
                                    boarding,
                                    lastBoarding,
                                    nowFrame,
                                    "assistPending=0");
                                m_RuntimeController.Run(v);
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
                                    ? m_DispatchScheduler.TryAssignCurrentOrLateScheduledTarget(
                                        routeEnt,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Holding",
                                        appliedTargets,
                                        out lateSlot)
                                    : m_DispatchScheduler.TryAssignCurrentOrLateSlot(
                                        routeEnt,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Holding",
                                        out lateSlot);
                                if (assigned)
                                {
                                    targetMin = lateSlot;
                                    RecordRuntimeObservationTargetBound(routeEnt, v, targetMin, nowFrame, "holding-assigned");
                                }
                                else if (m_DispatchScheduler.TryAssignUpcomingTarget(
                                    routeEnt,
                                    v,
                                    nowMin,
                                    lineTag,
                                    "Holding",
                                    ecb,
                                    out int upcomingTarget))
                                {
                                    targetMin = upcomingTarget;
                                    RecordRuntimeObservationTargetBound(routeEnt, v, targetMin, nowFrame, "holding-upcoming-assigned");
                                }
                                else
                                {
                                    LogOriginDispatchTrace(
                                        "holding-no-target-demote-idle",
                                        v,
                                        lineEnt,
                                        routeEnt,
                                        wps,
                                        state,
                                        targetMin,
                                        nowMin,
                                        curWpIdx,
                                        atA,
                                        boarding,
                                        lastBoarding,
                                        nowFrame);
                                    m_RuntimeController.RecoverToIdle(v, nowFrame);
                                    m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                    SetUILabel(v, "等待调度" + vTag);
                                    break;
                                }
                            }

                            if (m_DispatchScheduler.ShouldRetireWaitingVehicle(routeEnt, nowMin, targetMin))
                            {
                                LogOriginDispatchTrace(
                                    "holding-far-future-retire",
                                    v,
                                    lineEnt,
                                    routeEnt,
                                    wps,
                                    state,
                                    targetMin,
                                    nowMin,
                                    curWpIdx,
                                    atA,
                                    boarding,
                                    lastBoarding,
                                    nowFrame);
                                m_CommandApplier.Retire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMin, targetMin));
                                ClearBypassYieldState(v);
                                break;
                            }

                            if (m_DispatchScheduler.IsTimeReached(nowMin, targetMin) || m_DispatchScheduler.CanLateDispatch(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v, "始发候车不参与待避");
                                if (m_DispatchScheduler.IsTargetOccupied(routeEnt, v, targetMin))
                                {
                                    LogOriginDispatchTrace(
                                        "holding-occupied-release",
                                        v,
                                        lineEnt,
                                        routeEnt,
                                        wps,
                                        state,
                                        targetMin,
                                        nowMin,
                                        curWpIdx,
                                        atA,
                                        boarding,
                                        lastBoarding,
                                        nowFrame);
                                    m_RuntimeController.ReleaseTarget(v);
                                    m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
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
                                    LogOriginDispatchTrace(
                                        "holding-time-reached-forced-dwell",
                                        v,
                                        lineEnt,
                                        routeEnt,
                                        wps,
                                        state,
                                        targetMin,
                                        nowMin,
                                        curWpIdx,
                                        atA,
                                        boarding,
                                        lastBoarding,
                                        nowFrame);
                                    m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                    SetUILabel(v, m_DispatchScheduler.CanLateDispatch(nowMin, targetMin)
                                        ? "候车 补发 " + SlotStr(targetMin) + vTag
                                        : "候车 " + SlotStr(targetMin) + vTag);
                                    break;
                                }
                                if (boarding)
                                {
                                    bool shouldRefreshOriginAssist = !m_VehicleView.TryGetBoardingGrace(v, out uint originBoardingGraceUntil)
                                        || nowFrame >= originBoardingGraceUntil;
                                    if (shouldRefreshOriginAssist)
                                    {
                                        m_CommandApplier.ForceDepart(v, ref pt, nowFrame, ecb);
                                        m_RuntimeController.SetBoardingGrace(v, nowFrame + FORCED_MIDSTOP_BV_GRACE_FRAMES);
                                        log.Info("[始发发车协助] " + lineTag + " 车辆" + v.Index
                                            + " 班次" + SlotStr(targetMin)
                                            + " wp=" + curWpIdx);
                                    }
                                    ArmAssistLaunchPending(v, routeEnt, targetMin);
                                    LogOriginDispatchTrace(
                                        "holding-boarding-assist-pending",
                                        v,
                                        lineEnt,
                                        routeEnt,
                                        wps,
                                        state,
                                        targetMin,
                                        nowMin,
                                        curWpIdx,
                                        atA,
                                        boarding,
                                        lastBoarding,
                                        nowFrame,
                                        "assistRefreshed=" + (shouldRefreshOriginAssist ? "1" : "0"));
                                    SetUILabel(v, "结束上客 " + SlotStr(targetMin) + vTag);
                                    break;
                                }
                                bool isLateDispatch = m_DispatchScheduler.CanLateDispatch(nowMin, targetMin);
                                int overdue = isLateDispatch ? m_DispatchScheduler.OverdueMinutes(nowMin, targetMin) : 0;
                                bool hasLaunchHeadSnapshot = TryCaptureTrainHeadSnapshot(v, curWpIdx, out TrainHeadSnapshot currentLaunchHeadSnapshot);
                                string headDiagnostic = BuildTrainHeadLaunchDiagnostic(v, hasLaunchHeadSnapshot, currentLaunchHeadSnapshot);
                                m_CommandApplier.Launch(v, pt, tgt, wps, ecb);
                                m_RuntimeController.Launch(v, targetMin, nowFrame, nowFrame + LAUNCH_COOLDOWN_FRAMES);
                                RequestLineOrderedRuntimeForceRefresh(routeEnt, "origin-launch");
                                m_JustLaunched.Add(v);
                                if (hasLaunchHeadSnapshot)
                                    m_LastLaunchHeadSnapshots[v] = currentLaunchHeadSnapshot;
                                else
                                    m_LastLaunchHeadSnapshots.Remove(v);
                                RecordLapStart(v, isLateDispatch ? "补发" : "计划发车");
                                m_LastBoarding[v] = false;
                                m_CachedWpIdx[v] = -1;
                                m_BVMisfire.Remove(v);
                                m_BVMisfireStartFrame.Remove(v);
                                RecordRuntimeObservationLaunch(routeEnt, v, targetMin, nowMin, nowFrame, isLateDispatch);
                                BeginWorkbenchRealtimeTripAtLaunch(v, lineEnt, wps);
                                log.Info("[LaunchHeadCheck] " + lineTag + " vehicle" + v.Index + headDiagnostic);
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
                            else if (m_DispatchScheduler.IsHardExpired(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v);
                                int overdue = m_DispatchScheduler.OverdueMinutes(nowMin, targetMin);
                                log.Info("[Holding] " + lineTag + " 车辆" + v.Index
                                    + " 班次" + SlotStr(targetMin) + " 大幅过期(" + overdue + "分钟)，直接回库");
                                m_CommandApplier.Retire(v, pt, tgt, ecb, "班次大幅过期" + overdue + "分钟");
                            }
                            else if (m_DispatchScheduler.IsSoftExpired(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v);
                                int overdue = m_DispatchScheduler.OverdueMinutes(nowMin, targetMin);
                                log.Info("[Holding] " + lineTag + " 车辆" + v.Index
                                    + " 班次" + SlotStr(targetMin) + " 已过期(" + overdue + "分钟)，释放重新调度");
                                m_RuntimeController.ReleaseTarget(v);
                                m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                SetUILabel(v, "候车 等待调度" + vTag);
                            }
                            else
                            {
                                LogOriginDispatchTrace(
                                    "holding-waiting-window",
                                    v,
                                    lineEnt,
                                    routeEnt,
                                    wps,
                                    state,
                                    targetMin,
                                    nowMin,
                                    curWpIdx,
                                    atA,
                                    boarding,
                                    lastBoarding,
                                    nowFrame);
                                ClearBypassYieldState(v);
                                m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                SetUILabel(v,
                                    m_DispatchScheduler.CanLateDispatch(nowMin, targetMin)
                                        ? "候车 补发 " + SlotStr(targetMin) + vTag
                                        : "候车 " + SlotStr(targetMin) + vTag);
                            }
                            break;

                        case VehicleState.Running:
                            UpdateVehicleTraversalSliceObservation(v, lineEnt, wps, nowFrame);

                            bool shouldBroadcastForTrackedVehicle = ShouldBroadcastForTrackedVehicle(v);
                            bool hasPlatformApproachWatch = LineHasEnabledBroadcastPlatformApproachAnnouncements(routeEnt);
                            bool needsBroadcastRuntimeContext = hasEnabledPlatformAnnouncements || shouldBroadcastForTrackedVehicle;
                            BroadcastVehicleRuntimeFrameContext broadcastRuntimeContext = default;
                            bool hasBroadcastRuntimeContext = needsBroadcastRuntimeContext
                                && TryBuildBroadcastVehicleRuntimeFrameContext(
                                    v,
                                    routeEnt,
                                    wps,
                                    curWpIdx,
                                    out broadcastRuntimeContext);
                            UpdateBroadcastPlatformBusyWatch(
                                routeEnt,
                                wps,
                                hasEnabledPlatformAnnouncements && hasBroadcastRuntimeContext,
                                boarding,
                                broadcastRuntimeContext);
                            UpdateBroadcastPlatformApproachWatch(
                                v,
                                routeEnt,
                                wps,
                                hasPlatformApproachWatch && hasBroadcastRuntimeContext,
                                broadcastRuntimeContext);
                            TickBroadcastProgressTriggers(
                                v,
                                routeEnt,
                                wps,
                                boarding,
                                shouldBroadcastForTrackedVehicle,
                                hasBroadcastRuntimeContext,
                                broadcastRuntimeContext);

                            int bypassControlWaypointIndex = curWpIdx >= 0 ? curWpIdx : previousCachedWpIdx;
                            BypassControlResult runningBypass = m_BypassControl.Update(
                                v,
                                routeEnt,
                                wps,
                                bypassControlWaypointIndex,
                                boarding,
                                nowFrame);
                            bool runningShouldHoldBypass = runningBypass.ShouldHold;
                            bool runningCanClearAfterExit = runningBypass.CanClearAfterExit;
                            Entity runningBypassBlocker = runningBypass.Blocker;
                            bool runningBypassLatched = runningBypass.HadLatchedYield;

                            if (runningBypassLatched || runningShouldHoldBypass)
                            {
                                string holdFrameAction = runningShouldHoldBypass
                                    ? "hold"
                                    : (midStopDwellTimedOut ? "timeout-close" : "release");
                                LogVehicleStateOnce(
                                    m_BypassHoldFrameLogCache,
                                    v,
                                    "frame|" + bypassControlWaypointIndex
                                        + "|" + boarding
                                        + "|" + runningBypassLatched
                                        + "|" + runningShouldHoldBypass
                                        + "|" + runningCanClearAfterExit
                                        + "|" + midStopDwellTimedOut
                                        + "|" + runningBypassBlocker.Index
                                        + "|" + holdFrameAction,
                                    "[待避压车帧] vehicle=" + v.Index
                                        + " line=" + routeEnt.Index
                                        + " wp=" + bypassControlWaypointIndex
                                        + " boarding=" + boarding
                                        + " latched=" + runningBypassLatched
                                        + " shouldHold=" + runningShouldHoldBypass
                                        + " blocker=" + runningBypassBlocker.Index
                                        + " canClear=" + runningCanClearAfterExit
                                        + " midTimeout=" + midStopDwellTimedOut
                                        + " depBefore=" + pt.m_DepartureFrame
                                        + " frame=" + nowFrame
                                        + " action=" + holdFrameAction);
                            }

                            if (midStopDwellTimedOut && !runningShouldHoldBypass)
                            {
                                bool shouldRefreshTimeoutAssist = !m_ForcedMidStopBoardingGraceUntil.TryGetValue(v, out uint timeoutAssistGraceUntil)
                                    || nowFrame >= timeoutAssistGraceUntil;
                                if (shouldRefreshTimeoutAssist)
                                {
                                    m_CommandApplier.ForceDepart(v, ref pt, nowFrame, ecb);
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
                                            + " nextTargetWp=" + (curWpIdx + 1 < waypointCount ? (curWpIdx + 1).ToString() : "-"));
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
                                m_BypassControl.Hold(
                                    runningBypass,
                                    ref pt,
                                    ecb,
                                    wps,
                                    lineTag,
                                    nowFrame);
                                SetUILabel(v, "待避快车" + vTag);
                                break;
                            }

                            if (runningBypass.ShouldRelease)
                            {
                                m_BypassControl.Release(runningBypass);
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
                                bool hasLapStartOdo = m_LapObservations.TryStart(v, out float ls);
                                bool hasLapStartFrame = m_LapObservations.TryStartFrame(v, out uint lapStartFrame);
                                bool lapStartValid = hasLapStartOdo && !float.IsNaN(ls) && !float.IsInfinity(ls) && ls >= 0f;
                                bool brokenRecoveredRunning = hasLapStartFrame && !lapStartValid;
                                float lapStart = hasLapStartOdo ? ls : -1f;
                                float nowOdo = EntityManager.HasComponent<Odometer>(v)
                                    ? EntityManager.GetComponentData<Odometer>(v).m_Distance : -1f;
                                const float LAP_MOVED_MIN = 500f;
                                bool hasMoved = (nowOdo >= 0f && lapStartValid && (nowOdo - lapStart) > LAP_MOVED_MIN);
                                float ld = 0f;
                                m_LapObservations.TryDistance(v, out ld);
                                if (brokenRecoveredRunning)
                                {
                                    m_RuntimeController.ArriveIdle(v);
                                    m_RuntimeController.ClearReady(v);
                                    RequestLineOrderedRuntimeForceRefresh(lineEnt, "origin-return-recovered-idle");
                                    m_LapObservations.StartFrame.Remove(v);
                                    m_LapObservations.Frames.Remove(v);
                                    m_LapObservations.RestoredRunning.Remove(v);
                                    m_CachedWpIdx[v] = 0;
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    m_CommandApplier.CommitPublicTransport(v, pt, ecb);
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
                                        uint originSinceFrame = m_VehicleView.TryGetOrigin(v, out uint sinceFrame)
                                            ? sinceFrame
                                            : nowFrame;
                                        bool keepAssignedTarget = targetMin >= 0 && m_DispatchScheduler.IsCurrentOrRecentSlot(nowMin, targetMin);
                                        bool recoverToHolding = keepAssignedTarget;

                                        if (recoverToHolding)
                                            m_RuntimeController.RecoverToHolding(v);
                                        else
                                            m_RuntimeController.RecoverToIdle(v, nowFrame);
                                        RequestLineOrderedRuntimeForceRefresh(
                                            lineEnt,
                                            recoverToHolding ? "origin-return-holding" : "origin-return-idle");
                                        m_CachedWpIdx[v] = 0;
                                        pt.m_DepartureFrame = nowFrame + 9999;
                                        m_CommandApplier.CommitPublicTransport(v, pt, ecb);

                                        if (recoverToHolding)
                                        {
                                            bool isLateRecoveredTarget = m_DispatchScheduler.CanLateDispatch(nowMin, targetMin);
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

                                    pt.m_DepartureFrame = midStopBoarding && midStopDwellDeadlineFrame > 0
                                        ? midStopDwellDeadlineFrame
                                        : nowFrame + 9999;
                                    m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                                    string curSlot1 = m_VehicleView.TryGetSlot(v, out int cs1) ? SlotStr(cs1) : "?";
                                    string nxtSlot1 = targetMin >= 0 ? ("->" + SlotStr(targetMin)) : "";
                                    SetUILabel(v, "运行中" + curSlot1 + nxtSlot1 + vTag);
                                    if (nowFrame % 1800 == 0)
                                    {
                                        uint lastLaunchFrame = m_VehicleView.TryGetLaunch(v, out uint llf) ? llf : 0;
                                        uint lapStartFrameDbg = m_LapObservations.TryStartFrame(v, out uint lsfDbg) ? lsfDbg : 0;
                                        string curSlotDbg = m_VehicleView.TryGetSlot(v, out int csDbg) ? SlotStr(csDbg) : "?";
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
                                m_RuntimeController.ArriveIdle(v);
                                RequestLineOrderedRuntimeForceRefresh(lineEnt, "origin-return-idle");
                                if (targetMin >= 0)
                                {
                                    if (m_DispatchScheduler.IsCurrentOrRecentSlot(nowMin, targetMin))
                                        m_RuntimeController.Target(v, targetMin);
                                    else
                                        m_RuntimeController.ReleaseTarget(v);
                                }
                                else
                                {
                                    m_RuntimeController.ReleaseTarget(v);
                                }
                                m_CachedWpIdx[v] = 0;
                                m_RuntimeController.ClearInbound(v);
                                m_RuntimeController.ClearOriginCandidate(v);
                                if (forcedAtOrigin)
                                    m_RuntimeController.SetReady(v, nowFrame + FORCED_ORIGIN_MIN_DWELL_FRAMES);
                                else
                                    m_RuntimeController.ClearReady(v);
                                SetUILabel(v, "等待调度" + vTag);
                                log.Info("[Running->Idle] " + lineTag + " 车辆" + v.Index
                                    + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?")
                                    + " lapStart=" + (lapStartValid ? lapStart.ToString("F1") : "?")
                                    + " curWpIdx=" + curWpIdx
                                    + (targetMin >= 0 && m_DispatchScheduler.IsCurrentOrRecentSlot(nowMin, targetMin)
                                        ? " keptTarget=" + SlotStr(targetMin)
                                        : "")
                                    + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                            }
                            else
                            {
                                if (!m_LapObservations.StartOdometer.ContainsKey(v) && !inCooldown)
                                    RecordLapStart(v, "Running缺少圈起点自愈");
                                string curSlot2 = m_VehicleView.TryGetSlot(v, out int cs2) ? SlotStr(cs2) : "?";
                                string nxtSlot2 = targetMin >= 0 ? ("->" + SlotStr(targetMin)) : "";
                                SetUILabel(v, "运行中" + curSlot2 + nxtSlot2 + vTag);
                            }
                            break;

                        case VehicleState.Idle:
                            ClearBypassYieldState(v);
                            if (hasEnabledPlatformAnnouncements)
                            {
                                UpdateBroadcastPlatformOriginBusyWatch(routeEnt, wps, broadcastOriginWaitBusy);
                            }

                            if (!atA)
                            {
                                m_RuntimeController.Run(v);
                                RecordLapStart(v, "Idle异常离站");
                                SetUILabel(v, "运行中(异常离站)" + vTag);
                                log.Info("[异常] " + lineTag + " 车辆" + v.Index + " Idle 时意外离站");
                                break;
                            }

                            if (targetMin < 0)
                            {
                                int[] appliedTargets = GetAppliedWorkbenchDepartureMinutes(routeEnt);
                                int lateTarget = -1;
                                bool assignedLateTarget = appliedTargets.Length > 0
                                    ? m_DispatchScheduler.TryAssignCurrentOrLateScheduledTarget(
                                        routeEnt,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Idle",
                                        appliedTargets,
                                        out lateTarget)
                                    : m_DispatchScheduler.TryAssignCurrentOrLateSlot(
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
                                if (m_DispatchScheduler.ShouldProtectIdle(routeEnt, v, nowMin))
                                {
                                    if (m_VehicleView.TryGetTarget(v, out int ptm) && ptm >= 0 && m_DispatchScheduler.CanLateDispatch(nowMin, ptm))
                                    {
                                        LogVehicleStateOnce(
                                            m_YieldSkipLogCache,
                                            v,
                                            "YieldSkipLate|" + ptm,
                                            "[YieldSkip] " + lineTag + " 车辆" + v.Index
                                                + " 班次" + SlotStr(ptm)
                                                + " 已过期" + m_DispatchScheduler.OverdueMinutes(nowMin, ptm) + "分钟，保留补发");
                                    }
                                    else
                                    {
                                        int protectTarget = m_VehicleView.TryGetTarget(v, out int ptm2) && ptm2 >= 0
                                            ? ptm2
                                            : m_DispatchScheduler.FallbackProtectTarget(routeEnt, nowMin);
                                        LogVehicleStateOnce(
                                            m_YieldSkipLogCache,
                                            v,
                                            "YieldSkipProtect|" + protectTarget,
                                            "[YieldSkip] " + lineTag + " 车辆" + v.Index
                                                + " 最近班次" + SlotStr(protectTarget)
                                                + " 仅剩" + m_DispatchScheduler.MinutesUntil(nowMin, protectTarget) + "分钟，保留待避");
                                    }
                                    break;
                                }
                                log.Info("[Yield] " + lineTag + " 车辆" + v.Index + " 始发站有回流车压队，回库疏解");
                                m_CommandApplier.Retire(v, pt, tgt, ecb, "始发站压队疏解");
                                break;
                            }

                            if (targetMin >= 0)
                            {
                                if (m_DispatchScheduler.ShouldRetireWaitingVehicle(routeEnt, nowMin, targetMin))
                                {
                                    m_CommandApplier.Retire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMin, targetMin));
                                    break;
                                }
                                m_RuntimeController.HoldFromIdle(v);
                                pt.m_DepartureFrame = nowFrame + 9999;
                                m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                                bool isLateTarget = m_DispatchScheduler.CanLateDispatch(nowMin, targetMin);
                                RecordRuntimeObservationTargetBound(routeEnt, v, targetMin, nowFrame, isLateTarget ? "idle-late-claim" : "idle-holding-assign");
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

                            if (!m_VehicleRuntime.IdleStartFrame.ContainsKey(v))
                                m_RuntimeController.SetIdle(v, nowFrame);

                            if (m_VehicleView.TryGetIdle(v, out uint idleStart))
                            {
                                float idleMin = (nowFrame - idleStart) / (float)SIM_FRAMES_PER_MINUTE;
                                if (idleMin > IDLE_TIMEOUT_MIN)
                                {
                                    m_VehicleRegistry.ClearIdle(v);
                                    m_CommandApplier.Retire(v, pt, tgt, ecb, "闲置" + idleMin.ToString("F1") + "分钟");
                                    break;
                                }
                            }

                            pt.m_DepartureFrame = nowFrame + 9999;
                            m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                            SetUILabel(v, "等待调度" + vTag);
                            break;

                        case VehicleState.Retiring:
                            SetUILabel(v, "回库中" + vTag);
                            break;
                    }
                }

                TickBroadcastRuntime(m_SimulationSystem.frameIndex);
                m_CommandApplier.ReleaseCompletedRetireHandoffs();

                var deadKeys = new NativeList<Entity>(Allocator.Temp);
                foreach (var kv in m_VehicleRuntime.State)
                {
                    if (!EntityManager.Exists(kv.Key)) deadKeys.Add(kv.Key);
                }
                Dictionary<Entity, int> removedCountByLine = null;
                foreach (var dead in deadKeys)
                {
                    VehicleState deadState = m_VehicleView.TryGetState(dead, out VehicleState removedState)
                        ? removedState
                        : default;
                    Entity mappedLine = m_VehicleView.TryGetLine(dead, out Entity removedLine)
                        ? removedLine
                        : Entity.Null;
                    if (deadState == VehicleState.Preparing)
                    {
                        int removedTargetMin = m_VehicleView.TryGetTarget(dead, out int removedTarget)
                            ? removedTarget
                            : -1;
                        int removedCachedWp = m_CachedWpIdx.TryGetValue(dead, out int removedWp)
                            ? removedWp
                            : -1;
                        uint removedPrepAge = m_VehicleView.TryGetPreparing(dead, out uint removedPrepStart)
                            ? m_SimulationSystem.frameIndex - removedPrepStart
                            : 0;
                        log.Info("[PreparingRemoved] 车辆" + dead.Index
                            + " line=" + DispatchCommandApplier.DescribeRetireShadowEntity(mappedLine)
                            + " targetMin=" + (removedTargetMin >= 0 ? SlotStr(removedTargetMin) : "-")
                            + " cachedWp=" + removedCachedWp
                            + " prepAgeFrames=" + removedPrepAge);
                    }
                    ClearBroadcastRuntimeState(dead);
                    m_CommandApplier.FlushRetireShadowSnapshots(dead, "entity-removed");
                    m_CommandApplier.ResetRetireShadowSnapshots(dead);
                    m_VehicleRegistry.Remove(dead);
                    m_LapObservations.Remove(dead);
                    m_UICache.Remove(dead);
                    m_LastBoarding.Remove(dead);
                    m_CachedWpIdx.Remove(dead);
                    m_TrackProjector.Remove(dead);
                    m_WaypointIndexFrameSnapshots.Remove(dead);
                    m_RouteProgressFrameSnapshots.Remove(dead);
                    m_BVMisfire.Remove(dead);
                    m_BVMisfireStartFrame.Remove(dead);
                    m_BypassDecision.Remove(dead);
                    ClearVehicleProgressSuspect(dead, "vehicle-removed");
                    ClearForcedMidStopClosingConsist(dead);
                    m_LastRetireFixLogFrame.Remove(dead);
                    m_RetireFixCooldownUntil.Remove(dead);
                    m_CommandApplier.RemoveRetireHandoff(dead);
                    m_PreparingFixCooldownUntil.Remove(dead);
                    m_RetireFixCount.Remove(dead);
                    m_AssistLaunchPendingByVehicle.Remove(dead);
                    m_StopDwell.Remove(dead);
                    m_TraversalSlices.Remove(dead);
                    ClearVehicleTraversalSliceLapDebug(dead);
                    m_BvWaypointMismatchLogCache.Remove(dead);
                    m_BvTrackAnchorRecoveryLogCache.Remove(dead);
                    m_OriginDispatchTraceLogCache.Remove(dead);
                    m_OriginDispatchTraceLastLogFrameCache.Remove(dead);
                    m_PreparingTargetDriftLogCache.Remove(dead);
                    m_CrossLineCandidateLogCache.Remove(dead);
                    m_RouteVehicleOwnerMismatchLogCache.Remove(dead);
                    m_BvWaypointMismatchLastLogFrame.Remove(dead);
                    m_BypassQueuedLocalOverrideLogCache.Remove(dead);
                    m_LastLaunchHeadSnapshots.Remove(dead);
                    m_LastBoardingHeadSnapshots.Remove(dead);
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
                    if (mappedLine != Entity.Null && deadState != VehicleState.Retiring)
                    {
                        removedCountByLine ??= new Dictionary<Entity, int>();
                        removedCountByLine[mappedLine] = removedCountByLine.TryGetValue(mappedLine, out int removedCount)
                            ? removedCount + 1
                            : 1;
                    }
                    log.Info("[清理] 车辆" + dead.Index + " 消失");
                }
                if (removedCountByLine != null && removedCountByLine.Count > 0)
                {
                    var cleanupRouteVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
                    var cleanupModifierBuffers = GetBufferLookup<RouteModifier>(false);
                    var cleanupWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
                    foreach (KeyValuePair<Entity, int> removedEntry in removedCountByLine)
                    {
                        ApplyCleanupTargetReductionForLine(
                            removedEntry.Key,
                            removedEntry.Value,
                            cleanupRouteVehicleBuffers,
                            cleanupModifierBuffers,
                            cleanupWaypointBuffers);
                    }
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
            Entity route = ResolveVehicleLine(v);
            bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v).m_State & PublicTransportFlags.Boarding) != 0;
            m_WaypointIndexFrameSnapshots[v] = new WaypointIndexFrameSnapshot(
                m_SimulationSystem.frameIndex,
                route,
                boarding,
                computedWaypointIndex);

            return computedWaypointIndex;
        }

        internal bool IsRuntimeReadyForOriginArrivingRepair()
        {
            return m_SystemReady;
        }

        internal bool TryGetRuntimeVehicleState(Entity vehicle, out VehicleState state)
        {
            state = default;
            return m_VehicleRuntime.State.IsCreated && m_VehicleView.TryGetState(vehicle, out state);
        }

        internal int ComputeWaypointIndexForOriginArrivingRepair(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints)
        {
            return ComputeWpIndex(vehicle, waypoints);
        }

        internal bool TryGetOriginArrivalRouteProgressForOriginArrivingRepair(
            Entity vehicle,
            out int nextWaypointIndex,
            out float segmentPosition)
        {
            if (!TryGetRouteProgress(vehicle, out nextWaypointIndex, out segmentPosition))
                return false;

            return nextWaypointIndex == 0 && segmentPosition >= ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS;
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

        private bool TryGetLineWaypointIndexLookup(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out LineWaypointIndexLookup lookup)
        {
            lookup = null;
            if (line == Entity.Null || waypoints.Length == 0)
                return false;

            ulong signature = ComputeLineWaypointSignature(waypoints);
            if (!m_LineWaypointIndexLookups.TryGetValue(line, out lookup)
                || lookup == null
                || lookup.Signature != signature)
            {
                lookup = new LineWaypointIndexLookup
                {
                    Signature = signature
                };

                for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
                {
                    Entity waypoint = waypoints[waypointIndex].m_Waypoint;
                    if (waypoint == Entity.Null || !EntityManager.Exists(waypoint))
                        continue;

                    lookup.WaypointIndexByWaypoint[waypoint] = waypointIndex;
                    if (EntityManager.HasComponent<Connected>(waypoint))
                    {
                        Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                        if (stop != Entity.Null)
                            lookup.WaypointIndexByStop[stop] = waypointIndex;
                    }
                }

                m_LineWaypointIndexLookups[line] = lookup;
            }

            return true;
        }

        private int ComputeWpIndexUncached(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            Entity line = ResolveVehicleLine(v);
            bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v).m_State & PublicTransportFlags.Boarding) != 0;
            int targetWaypointIndex = -1;
            LineWaypointIndexLookup lookup = null;
            if (line != Entity.Null)
                TryGetLineWaypointIndexLookup(line, wps, out lookup);
            if (EntityManager.HasComponent<Target>(v))
            {
                Entity targetWaypoint = EntityManager.GetComponentData<Target>(v).m_Target;
                if (lookup != null
                    && targetWaypoint != Entity.Null
                    && lookup.WaypointIndexByWaypoint.TryGetValue(targetWaypoint, out int indexedTargetWaypointIndex))
                {
                    targetWaypointIndex = indexedTargetWaypointIndex;
                }
                else if (EntityManager.HasComponent<Waypoint>(targetWaypoint))
                    targetWaypointIndex = EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index;
            }

            int bvWi = -1;
            if (lookup != null && boarding)
            {
                foreach (KeyValuePair<Entity, int> entry in lookup.WaypointIndexByStop)
                {
                    Entity stop = entry.Key;
                    if (!EntityManager.Exists(stop)
                        || !EntityManager.HasComponent<BoardingVehicle>(stop)
                        || EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != v)
                    {
                        continue;
                    }

                    bvWi = entry.Value;
                    break;
                }
            }
            else
            {
                for (int wi = 0; wi < wps.Length; wi++)
                {
                    Entity wp = wps[wi].m_Waypoint;
                    Entity stop = EntityManager.HasComponent<Connected>(wp)
                        ? EntityManager.GetComponentData<Connected>(wp).m_Connected : Entity.Null;
                    if (stop == Entity.Null) continue;

                    if (!EntityManager.HasComponent<BoardingVehicle>(stop)) continue;
                    if (EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != v) continue;
                    bvWi = wi;
                    break;
                }
            }

            bool allowTrackWaypointAnchoring = ENABLE_TRACK_WAYPOINT_ANCHORING
                && m_VehicleView.TryGetState(v, out VehicleState trackState)
                && trackState != VehicleState.Retiring;

            if (allowTrackWaypointAnchoring
                && TryResolveWaypointIndexByTrackCursor(v, wps, targetWaypointIndex, bvWi, -1, out int anchoredWaypointIndex, out string anchorDetail, out string anchorStableKey))
            {
                LogVehicleStateOnce(
                    m_BvTrackAnchorRecoveryLogCache,
                    v,
                    "track-anchor|" + anchorStableKey,
                    "[定位接管] 车辆" + v.Index + " 按track锚定 wp[" + anchoredWaypointIndex + "] " + anchorDetail);
                return anchoredWaypointIndex;
            }

            // Fallback when track cursor is temporarily unavailable:
            // trust explicit boarding-stop ownership only, never world-distance nearest-stop.
            if (boarding && bvWi >= 0)
                return bvWi;

            return -1;
        }

        private bool TryResolveWaypointIndexByTrackCursor(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            int closestWaypointIndex,
            out int waypointIndex,
            out string detail,
            out string stableKey)
        {
            waypointIndex = -1;
            detail = string.Empty;
            stableKey = string.Empty;
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
            const int anchorSlackAtoms = 3;

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
            stableKey = "wp=" + bestWaypointIndex
                + " seg=" + cursor.SegmentIndex
                + " targetWp=" + targetWaypointIndex
                + " bvWp=" + boardingWaypointIndex
                + " closestWp=" + closestWaypointIndex
                + " window=" + bestWindowStart + ".." + bestWindowEndExclusive;
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

        private enum CursorAtomWindowRelation : byte
        {
            Unknown = 0,
            Before = 1,
            Inside = 2,
            After = 3,
        }

        private static CursorAtomWindowRelation CompareCursorToAtomWindow(
            int cursorAtomIndex,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (cursorAtomIndex < 0 || startAtomIndex < 0 || endAtomIndexExclusive <= startAtomIndex)
                return CursorAtomWindowRelation.Unknown;

            if (cursorAtomIndex < startAtomIndex)
                return CursorAtomWindowRelation.Before;

            if (cursorAtomIndex >= endAtomIndexExclusive)
                return CursorAtomWindowRelation.After;

            return CursorAtomWindowRelation.Inside;
        }

        private bool TryGetCursorWaypointWindowRelation(
            LineTrackChain chain,
            int waypointIndex,
            int cursorAtomIndex,
            out CursorAtomWindowRelation relation,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            relation = CursorAtomWindowRelation.Unknown;
            startAtomIndex = -1;
            endAtomIndexExclusive = -1;
            if (!TryGetWaypointTraversalAtomWindow(
                    chain,
                    waypointIndex,
                    cursorAtomIndex,
                    out startAtomIndex,
                    out endAtomIndexExclusive))
            {
                return false;
            }

            relation = CompareCursorToAtomWindow(cursorAtomIndex, startAtomIndex, endAtomIndexExclusive);
            return relation != CursorAtomWindowRelation.Unknown;
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
            if (waypoint == Entity.Null
                || !EntityManager.Exists(waypoint)
                || !EntityManager.HasComponent<Connected>(waypoint))
                return Entity.Null;

            Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            return connected != Entity.Null && EntityManager.Exists(connected)
                ? connected
                : Entity.Null;
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

        internal float CalculateLineDuration(Entity line)
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

        internal bool HasInboundVehicleNearOrigin(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            Entity ignoreVehicle,
            float radiusMeters,
            bool includePreparingVehicles = true)
        {
            Entity stationA = wps[0].m_Waypoint;
            Entity stopA = stationA != Entity.Null
                && EntityManager.Exists(stationA)
                && EntityManager.HasComponent<Connected>(stationA)
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
                Entity nearV = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                if (nearV == ignoreVehicle) continue;
                if (!EntityManager.Exists(nearV)) continue;
                if (!m_VehicleView.TryGetState(nearV, out var nearState)) continue;
                bool isPreparing = nearState == VehicleState.Preparing;
                if (isPreparing && !includePreparingVehicles) continue;
                bool isTaggedInbound = m_VehicleView.IsInbound(nearV);
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

        internal bool IsFreshDispatchedPreparingVehicle(Entity vehicle, uint nowFrame)
        {
            if (vehicle == Entity.Null
                || !m_VehicleView.TryGetDispatch(vehicle, out uint dispatchStartFrame))
                return false;

            return nowFrame >= dispatchStartFrame
                && (nowFrame - dispatchStartFrame) <= PREPARING_ROUTE_FIX_GRACE_FRAMES;
        }

        internal int CountActiveVehicles(Entity line, BufferLookup<RouteVehicle> rvBuffers)
        {
            int count = 0;
            if (!rvBuffers.TryGetBuffer(line, out var rvs)) return 0;
            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                if (!EntityManager.Exists(v)) continue;
                if (m_VehicleView.TryGetState(v, out var st) && st == VehicleState.Retiring) continue;
                count++;
            }
            return count;
        }

        internal float GetRemainingRange(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v)) return float.MaxValue;
            if (!EntityManager.HasComponent<PrefabRef>(v)) return float.MaxValue;
            float cur = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity pref = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(pref)) return float.MaxValue;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(pref).m_MaintenanceRange;
            return (range > 0f) ? (range - cur) : float.MaxValue;
        }

        internal bool NeedsMaintenance(Entity v)
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

        internal bool CanFinishNextLap(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v) || !EntityManager.HasComponent<PrefabRef>(v)) return true;
            float current = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return true;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            if (range <= 0f) return true;
            float remaining = range - current;
            if (m_LapObservations.TryDistance(v, out float lapDist) && lapDist > 0f)
                return remaining >= lapDist;
            return (current / range) < MAINTENANCE_THRESHOLD;
        }
    }
}
