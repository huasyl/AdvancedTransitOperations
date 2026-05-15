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
            v = ResolveRuntimeControllerVehicle(v);
            if (v == Entity.Null || !EntityManager.Exists(v))
                return;

            ResetRetireShadowSnapshots(v);
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
            ClearAssistLaunchPending(v);
            ClearBroadcastRuntimeState(v);
            m_RetireFixCount[v] = 0;

            string lineTag = m_VehicleLine.TryGetValue(v, out Entity le) ? "线路" + le.Index : "线路?";
            SetUILabel(v, "回库中" + (reason.Length > 0 ? "(" + reason + ")" : ""));
            log.Info("[回库] " + lineTag + " 车辆" + v.Index
                + (reason.Length > 0 ? " 原因:" + reason : "") + " -> 车库");
            RecordRetireShadowSnapshot(v, "retire-request");
            m_RetireHandoffWatch[v] = new RetireHandoffWatchRecord
            {
                RequestedFrame = m_SimulationSystem.frameIndex,
                LastWriteFrame = 0,
                AttemptCount = 0,
                SoftAckFrame = 0,
                HardAckFrame = 0,
                LastObservedTarget = Entity.Null,
                ReasonCode = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason,
                HasIntervention = false,
                HardAckStallLogged = false,
                LastTraceFrame = 0,
                LastDispatchGuardLogFrame = 0,
                LastParkingDiagLogFrame = 0,
                LastTraceKey = string.Empty
            };
        }

        private void LaunchVehicle(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            DynamicBuffer<RouteWaypoint> wps,
            EntityCommandBuffer ecb)
        {
            ClearAssistLaunchPending(v);
            m_ForcedOriginBoardingGraceUntil.Remove(v);
            pt.m_State &= ~PublicTransportFlags.Boarding;
            pt.m_DepartureFrame = m_SimulationSystem.frameIndex - 1;
            if (TryApplyLaunchSegmentPath(v, ref pt, ref tgt, wps, ecb))
                return;

            tgt.m_Target = wps[1].m_Waypoint;
            RepathVehicle(v, pt, tgt, ecb);
        }

        private void ArmAssistLaunchPending(Entity vehicle, Entity line, int targetMin)
        {
            if (vehicle == Entity.Null || line == Entity.Null || targetMin < 0)
                return;

            m_AssistLaunchPendingByVehicle[vehicle] = new AssistLaunchPendingRecord(line, targetMin);
        }

        private void ClearAssistLaunchPending(Entity vehicle)
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
                    HashSet<Entity> seenVehicles = new HashSet<Entity>();
                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                        if (!EntityManager.Exists(v)) continue;
                        if (!seenVehicles.Add(v)) continue;
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

        private void ReleaseRuntimeOwnershipAfterRetireHandoff(Entity vehicle, string reason)
        {
            m_RetireHandoffWatch.Remove(vehicle);
            ClearBroadcastRuntimeState(vehicle);
            m_VehicleState.Remove(vehicle);
            m_VehicleTargetMin.Remove(vehicle);
            m_VehicleLapDistance.Remove(vehicle);
            m_VehicleIdleStartFrame.Remove(vehicle);
            m_VehiclePreparingStartFrame.Remove(vehicle);
            m_VehicleDispatchRequestStartFrame.Remove(vehicle);
            m_VehicleCurrentSlot.Remove(vehicle);
            m_VehicleLastLaunchFrame.Remove(vehicle);
            m_LastBoarding.Remove(vehicle);
            m_CachedWpIdx.Remove(vehicle);
            m_VehicleLine.Remove(vehicle);
            m_UICache.Remove(vehicle);
            m_LastRetireFixLogFrame.Remove(vehicle);
            m_RetireFixCooldownUntil.Remove(vehicle);
            m_PreparingFixCooldownUntil.Remove(vehicle);
            m_RetireFixCount.Remove(vehicle);
            m_NearingTerminus.Remove(vehicle);
            m_OriginArrivalCandidateSinceFrame.Remove(vehicle);
            m_ForcedOriginReadyFrame.Remove(vehicle);
            m_ForcedOriginBoardingGraceUntil.Remove(vehicle);
            m_StopDwellStartFrame.Remove(vehicle);
            m_StopDwellSessions.Remove(vehicle);
            m_BVMisfire.Remove(vehicle);
            m_BVMisfireStartFrame.Remove(vehicle);
            ClearBypassYieldState(vehicle, reason);
            ClearVehicleProgressSuspect(vehicle, reason);
            FlushRetireShadowSnapshots(vehicle, reason);
            ResetRetireShadowSnapshots(vehicle);
        }

        private void ResetRetireShadowSnapshots(Entity vehicle)
        {
            m_RetireShadowHistory.Remove(vehicle);
            m_RetireShadowLastSnapshot.Remove(vehicle);
            m_RetireShadowLastFrame.Remove(vehicle);
        }

        private void FlushRetireShadowSnapshots(Entity vehicle, string reason)
        {
            if (!m_RetireShadowHistory.TryGetValue(vehicle, out List<string> history) || history == null || history.Count == 0)
                return;

            for (int i = 0; i < history.Count; i++)
            {
                log.Info("[RetireShadow] 车辆" + vehicle.Index
                    + " reason=" + reason
                    + " step=" + (i + 1) + "/" + history.Count
                    + " " + history[i]);
            }
        }

        private void RecordRetireShadowSnapshot(Entity vehicle, string phase)
        {
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            string snapshot = BuildRetireShadowSnapshot(vehicle, phase);
            uint nowFrame = m_SimulationSystem.frameIndex;
            bool shouldSample = true;
            if (phase == "retiring"
                && m_RetireShadowLastSnapshot.TryGetValue(vehicle, out string lastSnapshot)
                && lastSnapshot == snapshot
                && m_RetireShadowLastFrame.TryGetValue(vehicle, out uint lastFrame)
                && (nowFrame - lastFrame) < RETIRE_SHADOW_SAMPLE_INTERVAL_FRAMES)
            {
                shouldSample = false;
            }

            if (!shouldSample)
                return;

            m_RetireShadowLastSnapshot[vehicle] = snapshot;
            m_RetireShadowLastFrame[vehicle] = nowFrame;

            if (!m_RetireShadowHistory.TryGetValue(vehicle, out List<string> history) || history == null)
            {
                history = new List<string>(RETIRE_SHADOW_HISTORY_LIMIT);
                m_RetireShadowHistory[vehicle] = history;
            }

            if (history.Count >= RETIRE_SHADOW_HISTORY_LIMIT)
                history.RemoveAt(0);
            history.Add(snapshot);
        }

        private string BuildRetireShadowSnapshot(Entity vehicle, string phase)
        {
            string state = m_VehicleState.TryGetValue(vehicle, out VehicleState runtimeState)
                ? runtimeState.ToString()
                : "-";
            Entity controllerEntity = EntityManager.HasComponent<Controller>(vehicle)
                ? EntityManager.GetComponentData<Controller>(vehicle).m_Controller
                : Entity.Null;
            Entity ownerDepot = EntityManager.HasComponent<Owner>(vehicle)
                ? EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                : Entity.Null;
            Entity targetEntity = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            string publicFlags = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State.ToString()
                : "-";
            string cargoFlags = EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle).m_State.ToString()
                : "-";

            int pathLen = EntityManager.HasBuffer<PathElement>(vehicle)
                ? EntityManager.GetBuffer<PathElement>(vehicle, true).Length
                : -1;
            string pathFlags = "-";
            int pathElementIndex = -1;
            PathFlags pathFlagBits = 0;
            if (EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
                pathFlags = pathOwner.m_State.ToString();
                pathElementIndex = pathOwner.m_ElementIndex;
                pathFlagBits = pathOwner.m_State;
            }

            int navLen = EntityManager.HasBuffer<TrainNavigationLane>(vehicle)
                ? EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true).Length
                : -1;
            string lastNavFlags = "-";
            Entity lastNavLane = Entity.Null;
            if (navLen > 0)
            {
                DynamicBuffer<TrainNavigationLane> navigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true);
                lastNavFlags = navigationLanes[navLen - 1].m_Flags.ToString();
                lastNavLane = navigationLanes[navLen - 1].m_Lane;
            }

            string frontLaneFlags = EntityManager.HasComponent<TrainCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle).m_Front.m_LaneFlags.ToString()
                : "-";
            Entity frontLane = EntityManager.HasComponent<TrainCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle).m_Front.m_Lane
                : Entity.Null;
            Entity rearLane = EntityManager.HasComponent<TrainCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle).m_Rear.m_Lane
                : Entity.Null;
            int layoutLen = EntityManager.HasBuffer<LayoutElement>(vehicle)
                ? EntityManager.GetBuffer<LayoutElement>(vehicle, true).Length
                : -1;
            Entity headVehicle = vehicle;
            if (layoutLen > 0)
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                headVehicle = layout[0].m_Vehicle;
            }

            int headNavLen = EntityManager.HasBuffer<TrainNavigationLane>(headVehicle)
                ? EntityManager.GetBuffer<TrainNavigationLane>(headVehicle, true).Length
                : -1;
            string headLastNavFlags = "-";
            Entity headLastNavLane = Entity.Null;
            if (headNavLen > 0)
            {
                DynamicBuffer<TrainNavigationLane> headNavigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(headVehicle, true);
                headLastNavFlags = headNavigationLanes[headNavLen - 1].m_Flags.ToString();
                headLastNavLane = headNavigationLanes[headNavLen - 1].m_Lane;
            }
            int headPathLen = EntityManager.HasBuffer<PathElement>(headVehicle)
                ? EntityManager.GetBuffer<PathElement>(headVehicle, true).Length
                : -1;
            string headFrontFlags = EntityManager.HasComponent<TrainCurrentLane>(headVehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(headVehicle).m_Front.m_LaneFlags.ToString()
                : "-";
            Entity headFrontLane = EntityManager.HasComponent<TrainCurrentLane>(headVehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(headVehicle).m_Front.m_Lane
                : Entity.Null;
            Entity headRearLane = EntityManager.HasComponent<TrainCurrentLane>(headVehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(headVehicle).m_Rear.m_Lane
                : Entity.Null;
            string headPathFlags = "-";
            PathFlags headPathFlagBits = 0;
            if (EntityManager.HasComponent<PathOwner>(headVehicle))
            {
                PathOwner headPathOwner = EntityManager.GetComponentData<PathOwner>(headVehicle);
                headPathFlags = headPathOwner.m_State.ToString();
                headPathFlagBits = headPathOwner.m_State;
            }

            Entity pathInfoDest = EntityManager.HasComponent<PathInformation>(vehicle)
                ? EntityManager.GetComponentData<PathInformation>(vehicle).m_Destination
                : Entity.Null;
            string pathInfoState = EntityManager.HasComponent<PathInformation>(vehicle)
                ? EntityManager.GetComponentData<PathInformation>(vehicle).m_State.ToString()
                : "-";
            Entity headPathInfoDest = EntityManager.HasComponent<PathInformation>(headVehicle)
                ? EntityManager.GetComponentData<PathInformation>(headVehicle).m_Destination
                : Entity.Null;
            string headPathInfoState = EntityManager.HasComponent<PathInformation>(headVehicle)
                ? EntityManager.GetComponentData<PathInformation>(headVehicle).m_State.ToString()
                : "-";

            string ownerDepotFlags = EntityManager.HasComponent<Game.Buildings.TransportDepot>(ownerDepot)
                ? EntityManager.GetComponentData<Game.Buildings.TransportDepot>(ownerDepot).m_Flags.ToString()
                : "-";
            int ownerDepotAvailable = EntityManager.HasComponent<Game.Buildings.TransportDepot>(ownerDepot)
                ? EntityManager.GetComponentData<Game.Buildings.TransportDepot>(ownerDepot).m_AvailableVehicles
                : -1;
            bool hasHandoffWatch = m_RetireHandoffWatch.TryGetValue(vehicle, out RetireHandoffWatchRecord handoffWatch);

            string targetKind = DescribeRetireShadowTargetKind(targetEntity);
            string guess = ClassifyRetireShadowGuess(
                targetEntity,
                ownerDepot,
                pathFlagBits,
                headPathFlagBits,
                navLen,
                headNavLen,
                lastNavFlags,
                headLastNavFlags,
                frontLaneFlags,
                headFrontFlags,
                pathLen,
                pathElementIndex,
                headPathLen,
                EntityManager.HasComponent<ParkedTrain>(vehicle),
                EntityManager.HasComponent<Deleted>(vehicle));

            return "frame=" + m_SimulationSystem.frameIndex
                + " phase=" + phase
                + " state=" + state
                + " pending=" + (hasHandoffWatch ? "1" : "0")
                + " attempt=" + (hasHandoffWatch ? handoffWatch.AttemptCount.ToString() : "-")
                + " softAck=" + (hasHandoffWatch && handoffWatch.SoftAckFrame > 0 ? handoffWatch.SoftAckFrame.ToString() : "-")
                + " hardAck=" + (hasHandoffWatch && handoffWatch.HardAckFrame > 0 ? handoffWatch.HardAckFrame.ToString() : "-")
                + " lastWrite=" + (hasHandoffWatch && handoffWatch.LastWriteFrame > 0 ? handoffWatch.LastWriteFrame.ToString() : "-")
                + " guess=" + guess
                + " ctrl=" + DescribeRetireShadowEntity(controllerEntity)
                + " owner=" + DescribeRetireShadowEntity(ownerDepot)
                + " ownerFlags=" + ownerDepotFlags
                + " ownerAvail=" + ownerDepotAvailable
                + " target=" + DescribeRetireShadowEntity(targetEntity)
                + " targetKind=" + targetKind
                + " targetExists=" + ((targetEntity != Entity.Null && EntityManager.Exists(targetEntity)) ? "1" : "0")
                + " targetIsOwner=" + ((targetEntity != Entity.Null && targetEntity == ownerDepot) ? "1" : "0")
                + " route=" + DescribeRetireShadowEntity(currentRoute)
                + " deleted=" + (EntityManager.HasComponent<Deleted>(vehicle) ? "1" : "0")
                + " parked=" + (EntityManager.HasComponent<ParkedTrain>(vehicle) ? "1" : "0")
                + " pfu=" + (EntityManager.HasComponent<PathfindUpdated>(vehicle) ? "1" : "0")
                + " upd=" + (EntityManager.HasComponent<Updated>(vehicle) ? "1" : "0")
                + " pt=" + publicFlags
                + " cargo=" + cargoFlags
                + " path=" + pathFlags
                + " pathLen=" + pathLen
                + " pathIdx=" + pathElementIndex
                + " piDest=" + DescribeRetireShadowEntity(pathInfoDest)
                + " piState=" + pathInfoState
                + " navLen=" + navLen
                + " navLane=" + DescribeRetireShadowEntity(lastNavLane)
                + " navLast=" + lastNavFlags
                + " front=" + frontLaneFlags
                + " frontLane=" + DescribeRetireShadowEntity(frontLane)
                + " rearLane=" + DescribeRetireShadowEntity(rearLane)
                + " layout=" + layoutLen
                + " head=" + DescribeRetireShadowEntity(headVehicle)
                + " headSelf=" + (headVehicle == vehicle ? "1" : "0")
                + " headPfu=" + (EntityManager.HasComponent<PathfindUpdated>(headVehicle) ? "1" : "0")
                + " headUpd=" + (EntityManager.HasComponent<Updated>(headVehicle) ? "1" : "0")
                + " headNav=" + headNavLen
                + " headNavLane=" + DescribeRetireShadowEntity(headLastNavLane)
                + " headNavLast=" + headLastNavFlags
                + " headPath=" + headPathLen
                + " headPathFlags=" + headPathFlags
                + " headPiDest=" + DescribeRetireShadowEntity(headPathInfoDest)
                + " headPiState=" + headPathInfoState
                + " headFront=" + headFrontFlags
                + " headFrontLane=" + DescribeRetireShadowEntity(headFrontLane)
                + " headRearLane=" + DescribeRetireShadowEntity(headRearLane);
        }

        private static string DescribeRetireShadowEntity(Entity entity)
        {
            return entity == Entity.Null ? "-" : entity.Index.ToString();
        }

        private string DescribeRetireShadowTargetKind(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return "-";
            if (EntityManager.HasComponent<Game.Buildings.TransportDepot>(entity))
                return "depot";
            if (EntityManager.HasComponent<Waypoint>(entity))
                return "waypoint";
            if (EntityManager.HasComponent<Game.Objects.SpawnLocation>(entity))
                return "spawn";
            if (EntityManager.HasComponent<Connected>(entity))
                return "connected";
            return "other";
        }

        private static bool HasFlagText(string text, string flag)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf(flag, StringComparison.Ordinal) >= 0;
        }

        private string ClassifyRetireShadowGuess(
            Entity targetEntity,
            Entity ownerDepot,
            PathFlags pathFlags,
            PathFlags headPathFlags,
            int navLen,
            int headNavLen,
            string navLastFlags,
            string headNavLastFlags,
            string frontLaneFlags,
            string headFrontFlags,
            int pathLen,
            int pathElementIndex,
            int headPathLen,
            bool parked,
            bool deleted)
        {
            if (parked)
                return "parked";
            if (deleted)
                return "deleted-marked";
            if (targetEntity == Entity.Null || !EntityManager.Exists(targetEntity))
                return "target-invalid";
            if ((pathFlags & PathFlags.Stuck) != 0 || (headPathFlags & PathFlags.Stuck) != 0)
                return "path-stuck";
            if ((pathFlags & PathFlags.Failed) != 0 || (headPathFlags & PathFlags.Failed) != 0)
                return "path-failed";
            if (navLen == 0 && headNavLen > 0)
                return "root-nav-missing-head-nav-present";
            if (navLen == 0
                && (HasFlagText(frontLaneFlags, "EndOfPath") || HasFlagText(headFrontFlags, "EndOfPath")))
                return "end-of-path-without-nav";
            if (navLen > 0 && HasFlagText(navLastFlags, "ParkingSpace") && !parked)
                return "parking-space-not-parked";
            if (headNavLen > 0 && HasFlagText(headNavLastFlags, "ParkingSpace") && !parked)
                return "head-parking-space-not-parked";
            if (navLen > 0
                && !HasFlagText(navLastFlags, "ParkingSpace")
                && HasFlagText(frontLaneFlags, "EndOfPath"))
                return "end-of-path-non-parking";
            if (navLen == 0 && pathLen >= 0 && pathElementIndex >= pathLen)
                return "path-consumed-no-nav";
            if (headNavLen == 0 && headPathLen >= 0 && headPathLen > 0 && targetEntity == ownerDepot)
                return "head-path-consumed-no-nav";
            return "unknown";
        }

        internal void GuardRetireHandoffDispatchInputs(uint nowFrame)
        {
            if (m_RetireHandoffWatch.Count == 0)
                return;

            List<Entity> watchedVehicles = new List<Entity>(m_RetireHandoffWatch.Keys);
            foreach (Entity vehicle in watchedVehicles)
            {
                if (!m_RetireHandoffWatch.TryGetValue(vehicle, out RetireHandoffWatchRecord watch))
                    continue;
                if (vehicle == Entity.Null
                    || !EntityManager.Exists(vehicle)
                    || EntityManager.HasComponent<Deleted>(vehicle)
                    || EntityManager.HasComponent<ParkedTrain>(vehicle))
                {
                    continue;
                }
                if (!m_VehicleState.TryGetValue(vehicle, out VehicleState runtimeState)
                    || runtimeState != VehicleState.Retiring)
                {
                    continue;
                }

                int serviceDispatchCount = 0;
                int publicRequestCount = 0;
                int cargoRequestCount = 0;
                bool cleared = false;

                if (EntityManager.HasBuffer<ServiceDispatch>(vehicle))
                {
                    DynamicBuffer<ServiceDispatch> dispatchBuffer = EntityManager.GetBuffer<ServiceDispatch>(vehicle);
                    serviceDispatchCount = dispatchBuffer.Length;
                    if (dispatchBuffer.Length > 0)
                    {
                        dispatchBuffer.Clear();
                        cleared = true;
                    }
                }

                if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                {
                    Game.Vehicles.PublicTransport publicTransport =
                        EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                    publicRequestCount = publicTransport.m_RequestCount;
                    if (publicTransport.m_RequestCount != 0)
                    {
                        publicTransport.m_RequestCount = 0;
                        EntityManager.SetComponentData(vehicle, publicTransport);
                        cleared = true;
                    }
                }

                if (EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle))
                {
                    Game.Vehicles.CargoTransport cargoTransport =
                        EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle);
                    cargoRequestCount = cargoTransport.m_RequestCount;
                    if (cargoTransport.m_RequestCount != 0)
                    {
                        cargoTransport.m_RequestCount = 0;
                        EntityManager.SetComponentData(vehicle, cargoTransport);
                        cleared = true;
                    }
                }

                if (!cleared)
                    continue;

                bool cooled = watch.LastDispatchGuardLogFrame == 0
                    || nowFrame - watch.LastDispatchGuardLogFrame >= 180;
                if (!cooled)
                    continue;

                watch.LastDispatchGuardLogFrame = nowFrame;
                string lineTag = m_VehicleLine.TryGetValue(vehicle, out Entity lineEntity)
                    ? "线路" + lineEntity.Index
                    : "线路?";
                log.Info("[RetireHandoffGuard] " + lineTag + " 车辆" + vehicle.Index
                    + " 清理未停稳回库车dispatch输入"
                    + " serviceDispatch=" + serviceDispatchCount
                    + " publicReq=" + publicRequestCount
                    + " cargoReq=" + cargoRequestCount);
            }
        }

        private void TickRetireHandoffWatch(EntityCommandBuffer ecb, uint nowFrame)
        {
            if (m_RetireHandoffWatch.Count == 0)
                return;

            List<Entity> watchedVehicles = new List<Entity>(m_RetireHandoffWatch.Keys);
            List<Entity> removals = null;
            foreach (Entity vehicle in watchedVehicles)
            {
                if (!m_RetireHandoffWatch.TryGetValue(vehicle, out RetireHandoffWatchRecord watch))
                    continue;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                {
                    FlushRetireShadowSnapshots(vehicle, "entity-removed");
                    ResetRetireShadowSnapshots(vehicle);
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                bool hasRuntimeState = m_VehicleState.TryGetValue(vehicle, out VehicleState runtimeState);
                if (!hasRuntimeState || runtimeState != VehicleState.Retiring)
                {
                    RecordRetireShadowSnapshot(vehicle, "handoff-abort-runtime-state");
                    log.Info("[RetireHandoffAbort] 车辆" + vehicle.Index
                        + " runtime state=" + (hasRuntimeState ? runtimeState.ToString() : "-")
                        + "，停止回库watch");
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                if (EntityManager.HasComponent<Deleted>(vehicle)
                    || EntityManager.HasComponent<ParkedTrain>(vehicle))
                {
                    RecordRetireShadowSnapshot(
                        vehicle,
                        EntityManager.HasComponent<ParkedTrain>(vehicle) ? "parked" : "deleted-marked");
                    ReleaseRuntimeOwnershipAfterRetireHandoff(
                        vehicle,
                        EntityManager.HasComponent<ParkedTrain>(vehicle)
                            ? "retire-handoff-parked"
                            : "retire-handoff-deleted");
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                string lineTag = m_VehicleLine.TryGetValue(vehicle, out Entity lineEntity)
                    ? "线路" + lineEntity.Index
                    : "线路?";

                if (!EntityManager.HasComponent<Owner>(vehicle)
                    || !EntityManager.HasComponent<Target>(vehicle)
                    || !EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                {
                    RecordRetireShadowSnapshot(vehicle, "handoff-abort-missing-components");
                    log.Info("[RetireHandoffAbort] " + lineTag + " 车辆" + vehicle.Index
                        + " 缺少Owner/Target/PublicTransport，停止回库watch，保留RT Retiring ownership");
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                Entity ownerDepot = EntityManager.GetComponentData<Owner>(vehicle).m_Owner;
                Target target = EntityManager.GetComponentData<Target>(vehicle);
                Entity targetEntity = target.m_Target;
                watch.LastObservedTarget = targetEntity;
                Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                    ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                    : Entity.Null;
                Entity headVehicle = ResolveRetireHandoffHeadVehicle(vehicle);
                Entity pathInfoDestination = EntityManager.HasComponent<PathInformation>(vehicle)
                    ? EntityManager.GetComponentData<PathInformation>(vehicle).m_Destination
                    : Entity.Null;
                Entity headPathInfoDestination = EntityManager.HasComponent<PathInformation>(headVehicle)
                    ? EntityManager.GetComponentData<PathInformation>(headVehicle).m_Destination
                    : Entity.Null;
                Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                bool returning = (publicTransport.m_State & PublicTransportFlags.Returning) != 0;
                bool parking = EntityHasParkingNavigationLane(vehicle)
                    || (headVehicle != vehicle && EntityHasParkingNavigationLane(headVehicle));
                bool waypointLikeTarget = targetEntity != Entity.Null
                    && EntityManager.Exists(targetEntity)
                    && IsRouteWaypointLikeTarget(vehicle, targetEntity);
                bool targetDepotSemantic = IsRetireHandoffDepotSemanticEntity(targetEntity, ownerDepot);
                bool pathDepotSemantic = EntityHasDepotPathDestination(vehicle, ownerDepot)
                    || (headVehicle != vehicle && EntityHasDepotPathDestination(headVehicle, ownerDepot));
                PathFlags currentPathState = EntityManager.HasComponent<PathOwner>(vehicle)
                    ? EntityManager.GetComponentData<PathOwner>(vehicle).m_State
                    : 0;
                bool depotSemanticRepathWindow = watch.HardAckFrame > 0
                    && targetDepotSemantic
                    && pathDepotSemantic
                    && returning
                    && (currentPathState & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Updated)) != 0;

                bool softAck = IsRetireHandoffSoftAck(vehicle, targetEntity, ownerDepot);
                bool hardAck = IsRetireHandoffHardAck(vehicle, ownerDepot);

                MaybeLogRetireHandoffTrace(
                    vehicle,
                    lineTag,
                    watch,
                    nowFrame,
                    currentRoute,
                    targetEntity,
                    ownerDepot,
                    pathInfoDestination,
                    headPathInfoDestination,
                    softAck,
                    hardAck,
                    returning,
                    parking,
                    "sample",
                    force: false);
                MaybeLogRetireParkingDiagnostic(
                    vehicle,
                    lineTag,
                    watch,
                    nowFrame,
                    currentRoute,
                    targetEntity,
                    ownerDepot,
                    pathInfoDestination,
                    headPathInfoDestination,
                    returning,
                    parking);

                if (softAck && watch.SoftAckFrame == 0)
                {
                    watch.SoftAckFrame = nowFrame;
                    RecordRetireShadowSnapshot(vehicle, "handoff-soft-ack");
                    if (watch.HasIntervention)
                    {
                        log.Info("[RetireHandoffAck] " + lineTag + " 车辆" + vehicle.Index
                            + " soft target=" + DescribeRetireShadowEntity(targetEntity)
                            + " attempt=" + watch.AttemptCount);
                    }
                }

                if (hardAck && watch.HardAckFrame == 0)
                {
                    watch.HardAckFrame = nowFrame;
                    watch.HardAckStallLogged = false;
                    RecordRetireShadowSnapshot(vehicle, "handoff-hard-ack");
                    MaybeLogRetireHandoffTrace(
                        vehicle,
                        lineTag,
                        watch,
                        nowFrame,
                        currentRoute,
                        targetEntity,
                        ownerDepot,
                        pathInfoDestination,
                        headPathInfoDestination,
                        softAck,
                        hardAck,
                        returning,
                        parking,
                        "hard-ack",
                        force: true);
                    if (watch.HasIntervention)
                    {
                        log.Info("[RetireHandoffAck] " + lineTag + " 车辆" + vehicle.Index
                            + " hard target=" + DescribeRetireShadowEntity(targetEntity)
                            + " attempt=" + watch.AttemptCount);
                    }
                }

                bool maxAgeReached = nowFrame - watch.RequestedFrame >= RETIRE_HANDOFF_MAX_AGE_FRAMES;
                bool routeRegression = waypointLikeTarget;
                bool lostDepotSemantics = watch.HardAckFrame > 0
                    && !targetDepotSemantic
                    && !pathDepotSemantic
                    && !returning
                    && !parking;
                bool hardAckStalled = watch.HardAckFrame > 0
                    && !depotSemanticRepathWindow
                    && !parking
                    && (nowFrame - watch.HardAckFrame) >= RETIRE_HANDOFF_MAX_AGE_FRAMES;

                if (watch.HardAckFrame > 0 && (routeRegression || lostDepotSemantics))
                {
                    watch.HasIntervention = true;
                    RecordRetireShadowSnapshot(vehicle, "handoff-hard-ack-regressed");
                    MaybeLogRetireHandoffTrace(
                        vehicle,
                        lineTag,
                        watch,
                        nowFrame,
                        currentRoute,
                        targetEntity,
                        ownerDepot,
                        pathInfoDestination,
                        headPathInfoDestination,
                        softAck,
                        hardAck,
                        returning,
                        parking,
                        routeRegression ? "route-regressed" : "depot-semantics-lost",
                        force: true);
                    QueueRetireHandoffWrite(vehicle, ownerDepot, ecb, watch, nowFrame, lineTag);
                    watch.RequestedFrame = nowFrame;
                    watch.SoftAckFrame = 0;
                    watch.HardAckFrame = 0;
                    watch.HardAckStallLogged = false;
                    continue;
                }

                if (softAck && !hardAck && maxAgeReached)
                {
                    RecordRetireShadowSnapshot(vehicle, "handoff-soft-ack-stagnant-retry");
                    MaybeLogRetireHandoffTrace(
                        vehicle,
                        lineTag,
                        watch,
                        nowFrame,
                        currentRoute,
                        targetEntity,
                        ownerDepot,
                        pathInfoDestination,
                        headPathInfoDestination,
                        softAck,
                        hardAck,
                        returning,
                        parking,
                        "soft-stagnant-retry",
                        force: true);
                    log.Info("[RetireHandoffRetry] " + lineTag + " 车辆" + vehicle.Index
                        + " soft ack后未进入hard ack，超时重投"
                        + " attempts=" + watch.AttemptCount
                        + " ageFrames=" + (nowFrame - watch.RequestedFrame)
                        + " target=" + DescribeRetireShadowEntity(targetEntity)
                        + " reason=" + watch.ReasonCode);
                    watch.HasIntervention = true;
                    QueueRetireHandoffWrite(vehicle, ownerDepot, ecb, watch, nowFrame, lineTag);
                    watch.RequestedFrame = nowFrame;
                    watch.SoftAckFrame = 0;
                    watch.HardAckFrame = 0;
                    watch.HardAckStallLogged = false;
                    continue;
                }

                if (hardAckStalled)
                {
                    uint hardAckAgeFrames = nowFrame - watch.HardAckFrame;
                    if (!watch.HardAckStallLogged)
                    {
                        watch.HardAckStallLogged = true;
                        watch.HasIntervention = true;
                        RecordRetireShadowSnapshot(vehicle, "handoff-hard-ack-stalled");
                        MaybeLogRetireHandoffTrace(
                            vehicle,
                            lineTag,
                            watch,
                            nowFrame,
                            currentRoute,
                            targetEntity,
                            ownerDepot,
                            pathInfoDestination,
                            headPathInfoDestination,
                            softAck,
                            hardAck,
                            returning,
                            parking,
                            "hard-stalled",
                            force: true);
                        log.Info("[RetireHandoffStall] " + lineTag + " 车辆" + vehicle.Index
                            + " hard ack后长期未收口"
                            + " hardAckAgeFrames=" + hardAckAgeFrames
                            + " attempts=" + watch.AttemptCount
                            + " target=" + DescribeRetireShadowEntity(targetEntity)
                            + " targetKind=" + DescribeRetireShadowTargetKind(targetEntity)
                            + " targetExists=" + ((targetEntity != Entity.Null && EntityManager.Exists(targetEntity)) ? "1" : "0")
                            + " owner=" + DescribeRetireShadowEntity(ownerDepot)
                            + " route=" + DescribeRetireShadowEntity(currentRoute)
                            + " returning=" + (returning ? "1" : "0")
                            + " ptState=" + publicTransport.m_State
                            + " piDest=" + DescribeRetireShadowEntity(pathInfoDestination)
                            + " headPiDest=" + DescribeRetireShadowEntity(headPathInfoDestination)
                            + " parking=" + (parking ? "1" : "0")
                            + " headParking=" + ((headVehicle != vehicle && EntityHasParkingNavigationLane(headVehicle)) ? "1" : "0")
                            + " reason=" + watch.ReasonCode);
                    }

                    if (ShouldRetryRetireHandoff(watch, nowFrame, vehicle, targetEntity, ownerDepot))
                    {
                        QueueRetireHandoffWrite(vehicle, ownerDepot, ecb, watch, nowFrame, lineTag);
                        watch.RequestedFrame = nowFrame;
                        watch.SoftAckFrame = 0;
                        watch.HardAckFrame = 0;
                        watch.HardAckStallLogged = false;
                    }
                    continue;
                }

                if (softAck || hardAck)
                    continue;

                bool maxAttemptsReached = watch.AttemptCount >= RETIRE_HANDOFF_MAX_ATTEMPTS;
                if (maxAttemptsReached || maxAgeReached)
                {
                    RecordRetireShadowSnapshot(vehicle, "handoff-abort-timeout");
                    MaybeLogRetireHandoffTrace(
                        vehicle,
                        lineTag,
                        watch,
                        nowFrame,
                        currentRoute,
                        targetEntity,
                        ownerDepot,
                        pathInfoDestination,
                        headPathInfoDestination,
                        softAck,
                        hardAck,
                        returning,
                        parking,
                        "abort-timeout",
                        force: true);
                    log.Info("[RetireHandoffAbort] " + lineTag + " 车辆" + vehicle.Index
                        + " 回库交接未被vanilla接住，停止重投"
                        + " attempts=" + watch.AttemptCount
                        + " ageFrames=" + (nowFrame - watch.RequestedFrame)
                        + " target=" + DescribeRetireShadowEntity(targetEntity)
                        + " reason=" + watch.ReasonCode);
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                if (!ShouldRetryRetireHandoff(watch, nowFrame, vehicle, targetEntity, ownerDepot))
                    continue;

                QueueRetireHandoffWrite(vehicle, ownerDepot, ecb, watch, nowFrame, lineTag);
            }

            if (removals == null)
                return;

            for (int i = 0; i < removals.Count; i++)
                m_RetireHandoffWatch.Remove(removals[i]);
        }

        private bool ShouldRetryRetireHandoff(
            RetireHandoffWatchRecord watch,
            uint nowFrame,
            Entity vehicle,
            Entity targetEntity,
            Entity ownerDepot)
        {
            if (watch.AttemptCount == 0)
                return true;
            if (nowFrame > watch.LastWriteFrame
                && targetEntity != Entity.Null
                && targetEntity != ownerDepot
                && EntityManager.Exists(targetEntity)
                && IsRouteWaypointLikeTarget(vehicle, targetEntity))
            {
                return true;
            }
            return nowFrame - watch.LastWriteFrame >= RETIRE_HANDOFF_RETRY_INTERVAL_FRAMES;
        }

        private bool IsRetireHandoffSoftAck(Entity vehicle, Entity targetEntity, Entity ownerDepot)
        {
            if (targetEntity != Entity.Null && targetEntity == ownerDepot)
                return true;
            if (IsRetireHandoffHardAck(vehicle, ownerDepot))
                return true;
            if (targetEntity != Entity.Null
                && EntityManager.Exists(targetEntity)
                && !IsRouteWaypointLikeTarget(vehicle, targetEntity))
            {
                return true;
            }
            if (targetEntity != Entity.Null
                && targetEntity == ownerDepot
                && EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
                if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Obsolete)) != 0)
                    return true;
            }
            return false;
        }

        private bool IsRetireHandoffHardAck(Entity vehicle, Entity ownerDepot)
        {
            if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0)
                    return true;
            }
            if (EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle))
            {
                Game.Vehicles.CargoTransport cargoTransport = EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle);
                if ((cargoTransport.m_State & CargoTransportFlags.Returning) != 0)
                    return true;
            }
            if (EntityHasDepotPathDestination(vehicle, ownerDepot))
                return true;
            Entity headVehicle = ResolveRetireHandoffHeadVehicle(vehicle);
            if (headVehicle != vehicle && EntityHasDepotPathDestination(headVehicle, ownerDepot))
                return true;
            return EntityHasParkingNavigationLane(vehicle)
                || (headVehicle != vehicle && EntityHasParkingNavigationLane(headVehicle));
        }

        private bool EntityHasDepotPathDestination(Entity entity, Entity ownerDepot)
        {
            return entity != Entity.Null
                && EntityManager.Exists(entity)
                && ownerDepot != Entity.Null
                && EntityManager.HasComponent<PathInformation>(entity)
                && IsRetireHandoffDepotSemanticEntity(
                    EntityManager.GetComponentData<PathInformation>(entity).m_Destination,
                    ownerDepot);
        }

        private bool EntityHasParkingNavigationLane(Entity entity)
        {
            if (entity == Entity.Null
                || !EntityManager.Exists(entity)
                || !EntityManager.HasBuffer<TrainNavigationLane>(entity))
            {
                return false;
            }

            DynamicBuffer<TrainNavigationLane> lanes = EntityManager.GetBuffer<TrainNavigationLane>(entity, true);
            return lanes.Length > 0
                && (lanes[lanes.Length - 1].m_Flags & TrainLaneFlags.ParkingSpace) != 0;
        }

        private void MaybeLogRetireParkingDiagnostic(
            Entity vehicle,
            string lineTag,
            RetireHandoffWatchRecord watch,
            uint nowFrame,
            Entity currentRoute,
            Entity targetEntity,
            Entity ownerDepot,
            Entity pathInfoDestination,
            Entity headPathInfoDestination,
            bool returning,
            bool parking)
        {
            if (!parking)
                return;

            bool cooled = watch.LastParkingDiagLogFrame == 0
                || nowFrame - watch.LastParkingDiagLogFrame >= 180;
            if (!cooled)
                return;

            watch.LastParkingDiagLogFrame = nowFrame;
            Entity headVehicle = ResolveRetireHandoffHeadVehicle(vehicle);
            log.Info("[RetireParkingDiag] " + lineTag + " 车辆" + vehicle.Index
                + " attempt=" + watch.AttemptCount
                + " target=" + DescribeRetireShadowEntity(targetEntity)
                + " targetKind=" + DescribeRetireShadowTargetKind(targetEntity)
                + " owner=" + DescribeRetireShadowEntity(ownerDepot)
                + " route=" + DescribeRetireShadowEntity(currentRoute)
                + " returning=" + (returning ? "1" : "0")
                + " piDest=" + DescribeRetireShadowEntity(pathInfoDestination)
                + " headPiDest=" + DescribeRetireShadowEntity(headPathInfoDestination)
                + " ctrl{" + FormatRetireParkingEntityDiagnostic(vehicle) + "}"
                + " head{" + FormatRetireParkingEntityDiagnostic(headVehicle) + "}");
        }

        private string FormatRetireParkingEntityDiagnostic(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return "entity=-";

            return "entity=" + DescribeRetireShadowEntity(entity)
                + " path=" + FormatRetireParkingPathDiagnostic(entity)
                + " nav=" + FormatRetireParkingNavigationDiagnostic(entity)
                + " front=" + FormatRetireParkingFrontFlags(entity);
        }

        private string FormatRetireParkingPathDiagnostic(Entity entity)
        {
            if (!EntityManager.HasComponent<PathOwner>(entity))
                return "-";

            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(entity);
            int pathLen = EntityManager.HasBuffer<PathElement>(entity)
                ? EntityManager.GetBuffer<PathElement>(entity, true).Length
                : -1;
            string lastTarget = "-";
            string lastFlags = "-";
            if (pathLen > 0)
            {
                PathElement last = EntityManager.GetBuffer<PathElement>(entity, true)[pathLen - 1];
                lastTarget = DescribeRetireShadowEntity(last.m_Target);
                lastFlags = last.m_Flags.ToString();
            }

            return "state=" + pathOwner.m_State
                + " idx=" + pathOwner.m_ElementIndex
                + " len=" + pathLen
                + " last=" + lastTarget
                + " lastFlags=" + lastFlags;
        }

        private string FormatRetireParkingNavigationDiagnostic(Entity entity)
        {
            if (!EntityManager.HasBuffer<TrainNavigationLane>(entity))
                return "-";

            DynamicBuffer<TrainNavigationLane> lanes = EntityManager.GetBuffer<TrainNavigationLane>(entity, true);
            if (lanes.Length == 0)
                return "len=0";

            TrainNavigationLane last = lanes[lanes.Length - 1];
            string result = "len=" + lanes.Length
                + " lastLane=" + DescribeRetireShadowEntity(last.m_Lane)
                + " lastFlags=" + last.m_Flags;
            if (!EntityManager.HasComponent<Game.Objects.SpawnLocation>(last.m_Lane))
                return result + " spawn=0";

            Game.Objects.SpawnLocation spawnLocation =
                EntityManager.GetComponentData<Game.Objects.SpawnLocation>(last.m_Lane);
            bool parkedVehicle = (spawnLocation.m_Flags & Game.Objects.SpawnLocationFlags.ParkedVehicle) != 0;
            return result
                + " spawn=1"
                + " spawnFlags=" + spawnLocation.m_Flags
                + " spawnParked=" + (parkedVehicle ? "1" : "0")
                + " group=" + spawnLocation.m_GroupIndex
                + " conn1=" + DescribeRetireShadowEntity(spawnLocation.m_ConnectedLane1)
                + " conn2=" + DescribeRetireShadowEntity(spawnLocation.m_ConnectedLane2);
        }

        private string FormatRetireParkingFrontFlags(Entity entity)
        {
            return entity != Entity.Null
                && EntityManager.Exists(entity)
                && EntityManager.HasComponent<TrainCurrentLane>(entity)
                    ? EntityManager.GetComponentData<TrainCurrentLane>(entity).m_Front.m_LaneFlags.ToString()
                    : "-";
        }

        private Entity ResolveRetireHandoffHeadVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                return vehicle;
            }

            DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            if (layout.Length == 0)
                return vehicle;
            return layout[0].m_Vehicle;
        }

        private bool IsRetireHandoffDepotSemanticEntity(Entity entity, Entity ownerDepot)
        {
            if (entity == Entity.Null
                || ownerDepot == Entity.Null
                || !EntityManager.Exists(entity))
            {
                return false;
            }

            if (entity == ownerDepot)
                return true;

            return CanonicalizeTransportDepotEntity(entity) == ownerDepot;
        }

        private void MaybeLogRetireHandoffTrace(
            Entity vehicle,
            string lineTag,
            RetireHandoffWatchRecord watch,
            uint nowFrame,
            Entity currentRoute,
            Entity targetEntity,
            Entity ownerDepot,
            Entity pathInfoDestination,
            Entity headPathInfoDestination,
            bool softAck,
            bool hardAck,
            bool returning,
            bool parking,
            string reason,
            bool force)
        {
            string pathFlags = "-";
            if (EntityManager.HasComponent<PathOwner>(vehicle))
                pathFlags = EntityManager.GetComponentData<PathOwner>(vehicle).m_State.ToString();

            bool pathfindUpdated = EntityManager.HasComponent<PathfindUpdated>(vehicle);

            string navLastFlags = "-";
            if (EntityManager.HasBuffer<TrainNavigationLane>(vehicle))
            {
                DynamicBuffer<TrainNavigationLane> navigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true);
                if (navigationLanes.Length > 0)
                    navLastFlags = navigationLanes[navigationLanes.Length - 1].m_Flags.ToString();
            }

            string frontFlags = EntityManager.HasComponent<TrainCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle).m_Front.m_LaneFlags.ToString()
                : "-";

            Entity headVehicle = ResolveRetireHandoffHeadVehicle(vehicle);
            string headNavLastFlags = "-";
            if (headVehicle != Entity.Null && EntityManager.HasBuffer<TrainNavigationLane>(headVehicle))
            {
                DynamicBuffer<TrainNavigationLane> headNavigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(headVehicle, true);
                if (headNavigationLanes.Length > 0)
                    headNavLastFlags = headNavigationLanes[headNavigationLanes.Length - 1].m_Flags.ToString();
            }

            string headFrontFlags = headVehicle != Entity.Null && EntityManager.HasComponent<TrainCurrentLane>(headVehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(headVehicle).m_Front.m_LaneFlags.ToString()
                : "-";

            bool targetDepotSemantic = IsRetireHandoffDepotSemanticEntity(targetEntity, ownerDepot);
            bool pathDepotSemantic = IsRetireHandoffDepotSemanticEntity(pathInfoDestination, ownerDepot)
                || IsRetireHandoffDepotSemanticEntity(headPathInfoDestination, ownerDepot);
            string key = "target=" + DescribeRetireShadowEntity(targetEntity)
                + "|targetKind=" + DescribeRetireShadowTargetKind(targetEntity)
                + "|targetDepot=" + (targetDepotSemantic ? "1" : "0")
                + "|route=" + DescribeRetireShadowEntity(currentRoute)
                + "|piDest=" + DescribeRetireShadowEntity(pathInfoDestination)
                + "|headPiDest=" + DescribeRetireShadowEntity(headPathInfoDestination)
                + "|piDepot=" + (pathDepotSemantic ? "1" : "0")
                + "|returning=" + (returning ? "1" : "0")
                + "|parking=" + (parking ? "1" : "0")
                + "|path=" + pathFlags
                + "|pfu=" + (pathfindUpdated ? "1" : "0")
                + "|navLast=" + navLastFlags
                + "|front=" + frontFlags
                + "|headNavLast=" + headNavLastFlags
                + "|headFront=" + headFrontFlags
                + "|soft=" + (softAck ? "1" : "0")
                + "|hard=" + (hardAck ? "1" : "0")
                + "|attempt=" + watch.AttemptCount;

            bool changed = !string.Equals(watch.LastTraceKey, key, StringComparison.Ordinal);
            bool cooled = watch.LastTraceFrame == 0
                || (nowFrame - watch.LastTraceFrame) >= RETIRE_HANDOFF_TRACE_COOLDOWN_FRAMES;
            if (!force && !changed && !cooled)
                return;

            if (!force && !changed)
                reason = "cooldown";

            log.Info("[RetireHandoffTrace] " + lineTag + " 车辆" + vehicle.Index
                + " reason=" + reason
                + " attempt=" + watch.AttemptCount
                + " target=" + DescribeRetireShadowEntity(targetEntity)
                + " targetKind=" + DescribeRetireShadowTargetKind(targetEntity)
                + " targetDepot=" + (targetDepotSemantic ? "1" : "0")
                + " route=" + DescribeRetireShadowEntity(currentRoute)
                + " piDest=" + DescribeRetireShadowEntity(pathInfoDestination)
                + " headPiDest=" + DescribeRetireShadowEntity(headPathInfoDestination)
                + " piDepot=" + (pathDepotSemantic ? "1" : "0")
                + " returning=" + (returning ? "1" : "0")
                + " parking=" + (parking ? "1" : "0")
                + " path=" + pathFlags
                + " pfu=" + (pathfindUpdated ? "1" : "0")
                + " navLast=" + navLastFlags
                + " front=" + frontFlags
                + " headNavLast=" + headNavLastFlags
                + " headFront=" + headFrontFlags
                + " softAck=" + (softAck ? "1" : "0")
                + " hardAck=" + (hardAck ? "1" : "0"));

            watch.LastTraceFrame = nowFrame;
            watch.LastTraceKey = key;
        }

        private bool IsRouteWaypointLikeTarget(Entity vehicle, Entity targetEntity)
        {
            if (targetEntity == Entity.Null
                || !EntityManager.Exists(targetEntity)
                || !EntityManager.HasComponent<Waypoint>(targetEntity))
            {
                return false;
            }

            if (!EntityManager.HasComponent<CurrentRoute>(vehicle))
                return true;

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            if (route == Entity.Null
                || !EntityManager.Exists(route)
                || !EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                return true;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(route, true);
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i].m_Waypoint == targetEntity)
                    return true;
            }

            return false;
        }

        private void QueueRetireHandoffWrite(
            Entity vehicle,
            Entity ownerDepot,
            EntityCommandBuffer ecb,
            RetireHandoffWatchRecord watch,
            uint nowFrame,
            string lineTag)
        {
            if (EntityManager.HasBuffer<ServiceDispatch>(vehicle))
            {
                DynamicBuffer<ServiceDispatch> dispatchBuffer = ecb.SetBuffer<ServiceDispatch>(vehicle);
                dispatchBuffer.Clear();
            }

            if (EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle))
            {
                Game.Vehicles.CargoTransport cargoTransport = EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle);
                cargoTransport.m_RequestCount = 0;
                cargoTransport.m_State &= ~(CargoTransportFlags.EnRoute
                    | CargoTransportFlags.Refueling
                    | CargoTransportFlags.AbandonRoute);
                ecb.SetComponent(vehicle, cargoTransport);
            }

            if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                publicTransport.m_RequestCount = 0;
                publicTransport.m_State &= ~(PublicTransportFlags.EnRoute
                    | PublicTransportFlags.Refueling
                    | PublicTransportFlags.AbandonRoute);
                ecb.SetComponent(vehicle, publicTransport);
            }

            if (EntityManager.HasComponent<Train>(vehicle))
            {
                Train train = EntityManager.GetComponentData<Train>(vehicle);
                train.m_Flags &= ~Game.Vehicles.TrainFlags.IgnoreParkedVehicle;
                ecb.SetComponent(vehicle, train);
            }

            Target target = EntityManager.GetComponentData<Target>(vehicle);
            target.m_Target = ownerDepot;

            if (EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
                pathOwner.m_State &= ~PathFlags.Failed;
                pathOwner.m_State |= PathFlags.Obsolete;
                ecb.SetComponent(vehicle, pathOwner);
            }

            ecb.SetComponent(vehicle, target);
            if (!EntityManager.HasComponent<PathfindUpdated>(vehicle))
                ecb.AddComponent<PathfindUpdated>(vehicle);
            if (!EntityManager.HasComponent<Updated>(vehicle))
                ecb.AddComponent<Updated>(vehicle);

            watch.LastWriteFrame = nowFrame;
            watch.AttemptCount = (byte)(watch.AttemptCount + 1);
            RecordRetireShadowSnapshot(vehicle, "handoff-queued");
            if (watch.HasIntervention || watch.AttemptCount > 2)
            {
                log.Info("[RetireHandoffRetry] " + lineTag + " 车辆" + vehicle.Index
                    + " attempt=" + watch.AttemptCount
                    + " target=depot#" + ownerDepot.Index
                    + " reason=" + watch.ReasonCode);
            }
        }

        private void LogRouteVehicleOwnerMismatch(Entity observedLine, Entity vehicle, string phase)
        {
            if (observedLine == Entity.Null || vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            Entity mappedLine = m_VehicleLine.TryGetValue(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            bool mappedMismatch = mappedLine != Entity.Null && mappedLine != observedLine;
            bool routeMismatch = currentRoute != Entity.Null && currentRoute != observedLine;
            if (!mappedMismatch && !routeMismatch)
                return;

            VehicleState state = m_VehicleState.TryGetValue(vehicle, out VehicleState runtimeState)
                ? runtimeState
                : default;
            int targetMin = m_VehicleTargetMin.TryGetValue(vehicle, out int assignedTarget)
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

        private void LogCrossLineCandidate(
            Entity observedLine,
            Entity vehicle,
            VehicleState state,
            int slot,
            float etaFrames,
            int previousTarget)
        {
            if (observedLine == Entity.Null || vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            Entity mappedLine = m_VehicleLine.TryGetValue(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            bool mappedMismatch = mappedLine != Entity.Null && mappedLine != observedLine;
            bool routeMismatch = currentRoute != Entity.Null && currentRoute != observedLine;
            if (!mappedMismatch && !routeMismatch)
                return;

            int targetMin = m_VehicleTargetMin.TryGetValue(vehicle, out int assignedTarget)
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
                    + " originWp=" + DescribeRetireShadowEntity(originWaypoint)
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
            Entity mappedLine = m_VehicleLine.TryGetValue(vehicle, out Entity mapped)
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
            uint preparingAge = m_VehiclePreparingStartFrame.TryGetValue(vehicle, out uint prepStart)
                ? m_SimulationSystem.frameIndex - prepStart
                : 0;

            return "phase=" + phase
                + " observedLine=" + DescribeRetireShadowEntity(observedLine)
                + " mappedLine=" + DescribeRetireShadowEntity(mappedLine)
                + " currentRoute=" + DescribeRetireShadowEntity(currentRoute)
                + " state=" + state
                + " targetMin=" + (targetMin >= 0 ? SlotStr(targetMin) : "-")
                + " cachedWp=" + cachedWp
                + " preparingAgeFrames=" + preparingAge
                + " owner=" + DescribeRetireShadowEntity(owner)
                + " ownerDepot=" + DescribeRetireShadowEntity(ownerDepot)
                + " target=" + DescribeRetireShadowEntity(target)
                + " targetKind=" + DescribeRetireShadowTargetKind(target)
                + " targetExists=" + ((target != Entity.Null && EntityManager.Exists(target)) ? "1" : "0")
                + " targetDepot=" + DescribeRetireShadowEntity(targetDepot)
                + " pathDest=" + DescribeRetireShadowEntity(pathDestination)
                + " pathDestDepot=" + DescribeRetireShadowEntity(pathDestinationDepot)
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
            if (!IsWorkbenchTimetableApplied(line)) return;
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
                    List<Entity> runtimeVehicles = new List<Entity>(rvs.Length);
                    HashSet<Entity> seenRuntimeVehicles = new HashSet<Entity>();
                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity runtimeVehicle = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                        if (runtimeVehicle == Entity.Null || !EntityManager.Exists(runtimeVehicle))
                            continue;
                        if (!seenRuntimeVehicles.Add(runtimeVehicle))
                            continue;
                        runtimeVehicles.Add(runtimeVehicle);
                        LogRouteVehicleOwnerMismatch(line, runtimeVehicle, "schedule-buffer");
                    }

                    bool lineHasHistory = false;
                    for (int i = 0; i < runtimeVehicles.Count; i++)
                    {
                        Entity v0 = runtimeVehicles[i];
                        if (m_VehicleLapFrames.TryGetValue(v0, out uint lf0) && lf0 > 0)
                        {
                            lineHasHistory = true;
                            break;
                        }
                    }
                    if (!lineHasHistory && cachedLapFrames > 0f)
                        lineHasHistory = true;

                    for (int i = 0; i < runtimeVehicles.Count; i++)
                    {
                        Entity v = runtimeVehicles[i];
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
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity v0 = runtimeVehicles[i];
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
                                for (int i = 0; i < runtimeVehicles.Count; i++)
                                {
                                    Entity v0 = runtimeVehicles[i];
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

                    int dispatchScanLimitMinutes = originHoldLimitMinutes;
                    if (useWorkbenchSchedule)
                    {
                        float scanSpawnLeadFrames = EstimateSpawnLeadFrames(line, lineDurationFrames);
                        float scanSpawnTriggerFrames = scanSpawnLeadFrames
                            + originHoldLimitMinutes * (float)SIM_FRAMES_PER_MINUTE;
                        dispatchScanLimitMinutes = math.max(
                            originHoldLimitMinutes,
                            (int)math.ceil(scanSpawnTriggerFrames / (float)SIM_FRAMES_PER_MINUTE));
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
                        if (minsToSlot > dispatchScanLimitMinutes)
                        {
                            if (useWorkbenchSchedule && s >= 0)
                                break;
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }
                        bool spawnOnlyScan = useWorkbenchSchedule && minsToSlot > originHoldLimitMinutes;

                        Entity currentOccupier = Entity.Null;
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity v = runtimeVehicles[i];
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
                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity v = runtimeVehicles[i];
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

                        for (int i = 0; i < runtimeVehicles.Count; i++)
                        {
                            Entity v = runtimeVehicles[i];
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
                                if (spawnOnlyScan)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = v;
                                        nearestState = st;
                                        nearestETA = 0f;
                                        nearestReason = "outside-origin-hold-window";
                                    }
                                    continue;
                                }
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
                                float etaFrames = EstimatePreparingArrivalFrames(v, line, wps, nowFrame, lineDurationFrames);
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
                            if (spawnOnlyScan)
                            {
                                float earliestHoldArrivalFrames = slotFramesAway
                                    - originHoldLimitMinutes * (float)SIM_FRAMES_PER_MINUTE;
                                if (earliestHoldArrivalFrames > 0f && eta < earliestHoldArrivalFrames)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = v;
                                        nearestState = st;
                                        nearestETA = eta;
                                        nearestReason = "before-origin-hold-window";
                                    }
                                    continue;
                                }
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
                            LogCrossLineCandidate(line, bestVehicle, bst, slot, bestETA, bestPrevTarget);
                            if (bst == VehicleState.Idle || bst == VehicleState.Holding)
                            {
                                if (bestPrevTarget < 0)
                                {
                                    int holdingCount = 0;
                                    for (int i = 0; i < runtimeVehicles.Count; i++)
                                    {
                                        Entity rv = runtimeVehicles[i];
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
                            for (int i = 0; i < runtimeVehicles.Count; i++)
                            {
                                Entity v = runtimeVehicles[i];
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

                            for (int i = 0; i < runtimeVehicles.Count; i++)
                            {
                                Entity v = runtimeVehicles[i];
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
                                    etaF = EstimatePreparingArrivalFrames(v, line, wps, nowFrame, lineDurationFrames);
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
                                float reachableWindowFrames = GetDispatchReachableWindowFrames(nowMin, slot);
                                if (spawnLeadFrames > reachableWindowFrames)
                                {
                                    TryLogSpawnLeadUnreachable(line, lineTag, nowMin, slot, spawnLeadFrames, reachableWindowFrames);
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }

                                float spawnTriggerFrames = spawnLeadFrames
                                    + originHoldLimitMinutes * (float)SIM_FRAMES_PER_MINUTE;
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
                        if (m_VehicleState.ContainsKey(v)) continue;

                        var pt0 = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        bool boarding0 = (pt0.m_State & PublicTransportFlags.Boarding) != 0;
                        if ((pt0.m_State & PublicTransportFlags.Returning) != 0)
                            continue;

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
                                    ArmBroadcastLeaveStationTrigger(v, lineEnt, wps, previousCachedWpIdx);
                                    if (state == VehicleState.Running && previousCachedWpIdx >= 0)
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
                    bool broadcastOriginWaitBusy = atA
                        || boarding
                        || m_ForcedOriginReadyFrame.ContainsKey(v)
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
                                m_ForcedOriginReadyFrame[v] = nowFrame + PREPARING_ORIGIN_SETTLE_FRAMES;
                                TryRecordPreparingArrivalSample(v, lineEnt, nowFrame);
                                RecordLineHoldingSummary(lineEnt, nowMin, v, targetMin);
                                if (targetMin >= 0)
                                {
                                    RecordRuntimeObservationTargetBound(routeEnt, v, targetMin, nowFrame, "preparing-holding-assign");
                                }
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
                            if (hasEnabledPlatformAnnouncements)
                            {
                                UpdateBroadcastPlatformOriginBusyWatch(routeEnt, wps, broadcastOriginWaitBusy);
                            }

                            if (!atA)
                            {
                                if (TryGetAssistLaunchPending(v, routeEnt, targetMin, out AssistLaunchPendingRecord assistPending))
                                {
                                    int assistedTargetMin = assistPending.TargetMin;
                                    bool isLateAssistLaunch = CanLateDispatchSlot(nowMin, assistedTargetMin);
                                    m_VehicleState[v] = VehicleState.Running;
                                    m_VehiclePreparingStartFrame.Remove(v);
                                    m_ForcedOriginReadyFrame.Remove(v);
                                    RequestLineOrderedRuntimeForceRefresh(routeEnt, "origin-assist-launch-sync");
                                    m_JustLaunched.Add(v);
                                    m_VehicleLastLaunchFrame[v] = nowFrame;
                                    RecordLapStart(v, isLateAssistLaunch ? "协助补发确认" : "协助发车确认");
                                    m_LastBoarding[v] = false;
                                    m_CachedWpIdx[v] = -1;
                                    m_BVMisfire.Remove(v);
                                    m_BVMisfireStartFrame.Remove(v);
                                    m_LaunchCooldownUntil[v] = nowFrame + LAUNCH_COOLDOWN_FRAMES;
                                    m_VehicleCurrentSlot[v] = assistedTargetMin;
                                    m_VehicleTargetMin[v] = -1;
                                    RecordRuntimeObservationLaunch(routeEnt, v, assistedTargetMin, nowMin, nowFrame, isLateAssistLaunch);
                                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                                    m_ForcedOriginReadyFrame.Remove(v);
                                    ClearAssistLaunchPending(v);
                                    pt.m_DepartureFrame = nowFrame > 0 ? nowFrame - 1 : 0;
                                    pt.m_State &= ~PublicTransportFlags.Boarding;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, (isLateAssistLaunch ? "运行中 补发 " : "运行中 ") + SlotStr(assistedTargetMin) + vTag);
                                    log.Info("[AssistLaunchSync] " + lineTag + " 车辆" + v.Index
                                        + " 在始发发车协助后已离站，补记班次" + SlotStr(assistedTargetMin)
                                        + " 于 " + SlotStr(nowMin)
                                        + (isLateAssistLaunch ? " late=1" : " late=0"));
                                    break;
                                }
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
                                    RecordRuntimeObservationTargetBound(routeEnt, v, targetMin, nowFrame, "holding-assigned");
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
                                    RecordRuntimeObservationTargetBound(routeEnt, v, targetMin, nowFrame, "holding-upcoming-assigned");
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
                                    ArmAssistLaunchPending(v, routeEnt, targetMin);
                                    SetUILabel(v, "结束上客 " + SlotStr(targetMin) + vTag);
                                    break;
                                }
                                bool isLateDispatch = CanLateDispatchSlot(nowMin, targetMin);
                                int overdue = isLateDispatch ? GetSlotOverdueMinutes(nowMin, targetMin) : 0;
                                bool hasLaunchHeadSnapshot = TryCaptureTrainHeadSnapshot(v, curWpIdx, out TrainHeadSnapshot currentLaunchHeadSnapshot);
                                string headDiagnostic = BuildTrainHeadLaunchDiagnostic(v, hasLaunchHeadSnapshot, currentLaunchHeadSnapshot);
                                LaunchVehicle(v, pt, tgt, wps, ecb);
                                m_VehicleState[v] = VehicleState.Running;
                                RequestLineOrderedRuntimeForceRefresh(routeEnt, "origin-launch");
                                m_JustLaunched.Add(v);
                                m_VehicleLastLaunchFrame[v] = nowFrame;
                                if (hasLaunchHeadSnapshot)
                                    m_LastLaunchHeadSnapshots[v] = currentLaunchHeadSnapshot;
                                else
                                    m_LastLaunchHeadSnapshots.Remove(v);
                                RecordLapStart(v, isLateDispatch ? "补发" : "计划发车");
                                m_LastBoarding[v] = false;
                                m_CachedWpIdx[v] = -1;
                                m_BVMisfire.Remove(v);
                                m_BVMisfireStartFrame.Remove(v);
                                m_LaunchCooldownUntil[v] = nowFrame + LAUNCH_COOLDOWN_FRAMES;
                                m_VehicleCurrentSlot[v] = targetMin;
                                m_VehicleTargetMin[v] = -1;
                                RecordRuntimeObservationLaunch(routeEnt, v, targetMin, nowMin, nowFrame, isLateDispatch);
                                m_OriginArrivalCandidateSinceFrame.Remove(v);
                                m_ForcedOriginReadyFrame.Remove(v);
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
                                Entity bypassHoldStation = bypassControlWaypointIndex >= 0 && bypassControlWaypointIndex < wps.Length
                                    ? ResolveWorkbenchStopEntity(wps[bypassControlWaypointIndex].m_Waypoint)
                                    : Entity.Null;
                                SetBypassYieldState(v, runningBypassBlocker, lineTag, "运行中", bypassHoldStation, bypassControlWaypointIndex);
                                HandleBroadcastBypassWaitingTrigger(v, routeEnt, wps, bypassControlWaypointIndex);
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
                                    RequestLineOrderedRuntimeForceRefresh(lineEnt, "origin-return-recovered-idle");
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
                                        RequestLineOrderedRuntimeForceRefresh(
                                            lineEnt,
                                            recoverToHolding ? "origin-return-holding" : "origin-return-idle");
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
                                RequestLineOrderedRuntimeForceRefresh(lineEnt, "origin-return-idle");
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
                            if (hasEnabledPlatformAnnouncements)
                            {
                                UpdateBroadcastPlatformOriginBusyWatch(routeEnt, wps, broadcastOriginWaitBusy);
                            }

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
                            SetUILabel(v, "回库中" + vTag);
                            break;
                    }
                }

                TickBroadcastRuntime(m_SimulationSystem.frameIndex);
                var handedOffKeys = new NativeList<Entity>(Allocator.Temp);
                foreach (var kv in m_VehicleState)
                {
                    Entity vehicle = kv.Key;
                    if (kv.Value != VehicleState.Retiring)
                        continue;
                    if (!EntityManager.Exists(vehicle))
                        continue;
                    RecordRetireShadowSnapshot(vehicle, "retiring");
                    if (EntityManager.HasComponent<Deleted>(vehicle)
                        || EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        RecordRetireShadowSnapshot(
                            vehicle,
                            EntityManager.HasComponent<ParkedTrain>(vehicle) ? "parked" : "deleted-marked");
                        if (EntityManager.HasComponent<ParkedTrain>(vehicle))
                        {
                            if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                            {
                                Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                                publicTransport.m_State &= ~PublicTransportFlags.Disabled;
                                EntityManager.SetComponentData(vehicle, publicTransport);
                            }
                            if (EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle))
                            {
                                Game.Vehicles.CargoTransport cargoTransport = EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle);
                                cargoTransport.m_State &= ~CargoTransportFlags.Disabled;
                                EntityManager.SetComponentData(vehicle, cargoTransport);
                            }
                        }
                        handedOffKeys.Add(vehicle);
                    }
                }
                foreach (var handedOff in handedOffKeys)
                {
                    ReleaseRuntimeOwnershipAfterRetireHandoff(
                        handedOff,
                        EntityManager.HasComponent<ParkedTrain>(handedOff)
                            ? "retire-handoff-parked"
                            : "retire-handoff-deleted");
                }
                handedOffKeys.Dispose();

                var deadKeys = new NativeList<Entity>(Allocator.Temp);
                foreach (var kv in m_VehicleState)
                {
                    if (!EntityManager.Exists(kv.Key)) deadKeys.Add(kv.Key);
                }
                Dictionary<Entity, int> removedCountByLine = null;
                foreach (var dead in deadKeys)
                {
                    VehicleState deadState = m_VehicleState.TryGetValue(dead, out VehicleState removedState)
                        ? removedState
                        : default;
                    Entity mappedLine = m_VehicleLine.TryGetValue(dead, out Entity removedLine)
                        ? removedLine
                        : Entity.Null;
                    if (deadState == VehicleState.Preparing)
                    {
                        int removedTargetMin = m_VehicleTargetMin.TryGetValue(dead, out int removedTarget)
                            ? removedTarget
                            : -1;
                        int removedCachedWp = m_CachedWpIdx.TryGetValue(dead, out int removedWp)
                            ? removedWp
                            : -1;
                        uint removedPrepAge = m_VehiclePreparingStartFrame.TryGetValue(dead, out uint removedPrepStart)
                            ? m_SimulationSystem.frameIndex - removedPrepStart
                            : 0;
                        log.Info("[PreparingRemoved] 车辆" + dead.Index
                            + " line=" + DescribeRetireShadowEntity(mappedLine)
                            + " targetMin=" + (removedTargetMin >= 0 ? SlotStr(removedTargetMin) : "-")
                            + " cachedWp=" + removedCachedWp
                            + " prepAgeFrames=" + removedPrepAge);
                    }
                    ClearBroadcastRuntimeState(dead);
                    FlushRetireShadowSnapshots(dead, "entity-removed");
                    ResetRetireShadowSnapshots(dead);
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
                    m_RetireHandoffWatch.Remove(dead);
                    m_PreparingFixCooldownUntil.Remove(dead);
                    m_RetireFixCount.Remove(dead);
                    m_OriginArrivalCandidateSinceFrame.Remove(dead);
                    m_ForcedOriginReadyFrame.Remove(dead);
                    m_ForcedOriginBoardingGraceUntil.Remove(dead);
                    m_AssistLaunchPendingByVehicle.Remove(dead);
                    m_StopDwellStartFrame.Remove(dead);
                    m_BypassYieldBlocker.Remove(dead);
                    m_VehicleTraversalSliceSessions.Remove(dead);
                    m_VehicleTraversalSliceLastSampleFrame.Remove(dead);
                    m_VehicleTraversalSliceSamplingPlans.Remove(dead);
                    ClearVehicleTraversalSliceLapDebug(dead);
                    m_BvWaypointMismatchLogCache.Remove(dead);
                    m_BvTrackAnchorRecoveryLogCache.Remove(dead);
                    m_PreparingTargetDriftLogCache.Remove(dead);
                    m_CrossLineCandidateLogCache.Remove(dead);
                    m_RouteVehicleOwnerMismatchLogCache.Remove(dead);
                    m_BvWaypointMismatchLastLogFrame.Remove(dead);
                    m_BypassQueuedLocalOverrideLogCache.Remove(dead);
                    m_LastLaunchHeadSnapshots.Remove(dead);
                    m_LastBoardingHeadSnapshots.Remove(dead);
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
            return m_VehicleState.IsCreated && m_VehicleState.TryGetValue(vehicle, out state);
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
                && m_VehicleState.TryGetValue(v, out VehicleState trackState)
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

        private void TryLogSpawnLeadUnreachable(
            Entity line,
            string lineTag,
            int nowMin,
            int slot,
            float spawnLeadFrames,
            float reachableWindowFrames)
        {
            uint nowFrame = m_SimulationSystem.frameIndex;
            ulong key = MakeLineSlotKey(line, slot) ^ 0x8000000000000000UL;
            if (m_LastScheduleDiagnosticLogFrame.TryGetValue(key, out uint lastFrame)
                && (nowFrame - lastFrame) < SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES)
                return;

            m_LastScheduleDiagnosticLogFrame[key] = nowFrame;
            log.Info("[SpawnLeadBlocked] " + lineTag
                + " 班次" + SlotStr(slot)
                + " 出库ETA=" + (spawnLeadFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                + " 正点窗口剩余=" + (reachableWindowFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
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
                Entity nearV = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
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
                Entity v = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
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
