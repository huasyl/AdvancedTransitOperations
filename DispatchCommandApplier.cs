using System;
using System.Collections.Generic;
using Game;
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
    internal sealed class RetireHandoffWatchRecord
    {
        public uint RequestedFrame;
        public uint LastWriteFrame;
        public byte AttemptCount;
        public uint SoftAckFrame;
        public uint HardAckFrame;
        public Entity LastObservedTarget;
        public string ReasonCode = string.Empty;
        public bool HasIntervention;
        public bool HardAckStallLogged;
        public uint LastTraceFrame;
        public uint LastDispatchGuardLogFrame;
        public uint LastParkingDiagLogFrame;
        public uint LastPreCommitLogFrame;
        public uint LastEndReachedRepairLogFrame;
        public uint LastRedispatchBlockedLogFrame;
        public string LastTraceKey = string.Empty;
    }

    internal sealed class DispatchCommandApplier
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Dictionary<Entity, RetireHandoffWatchRecord> m_RetireHandoffWatch =
            new Dictionary<Entity, RetireHandoffWatchRecord>();
        private readonly Dictionary<Entity, List<string>> m_RetireShadowHistory = new Dictionary<Entity, List<string>>();
        private readonly Dictionary<Entity, string> m_RetireShadowLastSnapshot = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_RetireShadowLastFrame = new Dictionary<Entity, uint>();

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;
        private SimulationSystem Simulation => m_Runtime.m_SimulationSystem;

        public DispatchCommandApplier(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal void AssignSlot(Entity vehicle, int slot, EntityCommandBuffer ecb)
        {
            m_Runtime.m_RuntimeController.Target(vehicle, slot);
            Game.Vehicles.PublicTransport publicTransport =
                EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            publicTransport.m_DepartureFrame = Simulation.frameIndex + 9999;
            CommitPublicTransport(vehicle, publicTransport, ecb);
            m_Runtime.SetUILabel(vehicle, "候车 " + DispatchRuntimeSystem.SlotStr(slot));
        }

        internal void CommitPublicTransport(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            EntityCommandBuffer ecb)
        {
            ecb.SetComponent(vehicle, publicTransport);
        }

        internal void HoldDeparture(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            publicTransport.m_DepartureFrame = nowFrame + 9999;
            CommitPublicTransport(vehicle, publicTransport, ecb);
        }

        internal void ForceDepart(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            ForceOfficialBoardingClose(ref publicTransport, nowFrame);
            CommitPublicTransport(vehicle, publicTransport, ecb);
        }

        internal void ApplyDepotRequestPath(Entity request, PathInformation forcedPath)
        {
            if (EntityManager.HasComponent<PathInformation>(request))
                EntityManager.SetComponentData(request, forcedPath);
            else
                EntityManager.AddComponentData(request, forcedPath);

            if (!EntityManager.HasBuffer<PathElement>(request))
                EntityManager.AddBuffer<PathElement>(request);
        }

        internal void Retire(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            Target target,
            EntityCommandBuffer ecb,
            string reason = "")
        {
            vehicle = m_Runtime.ResolveRuntimeControllerVehicle(vehicle);
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            ResetRetireShadowSnapshots(vehicle);
            m_Runtime.m_RuntimeController.Retire(vehicle);
            m_Runtime.m_BVMisfire.Remove(vehicle);
            m_Runtime.m_BVMisfireStartFrame.Remove(vehicle);
            m_Runtime.ClearForcedMidStopClosingConsist(vehicle);
            m_Runtime.m_PreparingFixCooldownUntil.Remove(vehicle);
            m_Runtime.ClearAssistLaunchPending(vehicle);
            m_Runtime.ClearBroadcastRuntimeState(vehicle);
            m_Runtime.m_RetireFixCount[vehicle] = 0;

            string lineTag = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity line) ? "线路" + line.Index : "线路?";
            m_Runtime.SetUILabel(vehicle, "回库中" + (reason.Length > 0 ? "(" + reason + ")" : ""));
            log.Info("[回库] " + lineTag + " 车辆" + vehicle.Index
                + (reason.Length > 0 ? " 原因:" + reason : "") + " -> 车库");
            RecordRetireShadowSnapshot(vehicle, "retire-request");
            m_RetireHandoffWatch[vehicle] = new RetireHandoffWatchRecord
            {
                RequestedFrame = Simulation.frameIndex,
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
                LastPreCommitLogFrame = 0,
                LastEndReachedRepairLogFrame = 0,
                LastRedispatchBlockedLogFrame = 0,
                LastTraceKey = string.Empty
            };
        }

        internal void Launch(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            EntityCommandBuffer ecb)
        {
            m_Runtime.ClearAssistLaunchPending(vehicle);
            m_Runtime.m_RuntimeController.ClearBoardingGrace(vehicle);
            publicTransport.m_State &= ~PublicTransportFlags.Boarding;
            publicTransport.m_DepartureFrame = Simulation.frameIndex - 1;
            if (TryApplyLaunchSegmentPath(vehicle, ref publicTransport, ref target, waypoints, ecb))
                return;

            target.m_Target = waypoints[1].m_Waypoint;
            Repath(vehicle, publicTransport, target, ecb);
        }

        internal void EnsurePreparingRoute(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            ref Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            bool boarding,
            EntityCommandBuffer ecb)
        {
            Entity stationA = waypoints[0].m_Waypoint;
            bool wrongTarget = target.m_Target != stationA;
            bool driftedToMidStop = boarding && currentWaypointIndex > 0;
            if (!wrongTarget && !driftedToMidStop)
                return;

            uint nowFrame = Simulation.frameIndex;
            if (wrongTarget
                && !driftedToMidStop
                && m_Runtime.IsFreshDispatchedPreparingVehicle(vehicle, nowFrame))
            {
                return;
            }
            if (m_Runtime.m_PreparingFixCooldownUntil.TryGetValue(vehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return;

            string lineTag = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity lineEnt)
                ? "线路" + lineEnt.Index
                : "线路?";
            string why = driftedToMidStop
                ? (currentWaypointIndex >= 0 ? ("偏航到 wp=" + currentWaypointIndex) : "偏航 boarding")
                : "目标不是始发站";

            m_Runtime.m_RuntimeController.SetPreparing(vehicle, nowFrame);
            m_Runtime.m_CachedWpIdx[vehicle] = -1;
            m_Runtime.m_LastBoarding[vehicle] = false;
            m_Runtime.m_BVMisfire.Remove(vehicle);
            m_Runtime.m_BVMisfireStartFrame.Remove(vehicle);
            publicTransport.m_State &= ~PublicTransportFlags.Boarding;
            publicTransport.m_DepartureFrame = nowFrame + 9999;
            target.m_Target = stationA;
            Repath(vehicle, publicTransport, target, ecb);
            m_Runtime.m_PreparingFixCooldownUntil[vehicle] =
                nowFrame + DispatchRuntimeSystem.PREPARINGFIX_REPATH_COOLDOWN_FRAMES;

            log.Info("[PreparingFix] " + lineTag + " 车辆" + vehicle.Index
                + " " + why + "，重置去始发站 wp0=" + stationA.Index);
        }

        internal void Repath(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            Target target,
            EntityCommandBuffer ecb)
        {
            if (EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
                pathOwner.m_State = PathFlags.Obsolete;
                pathOwner.m_ElementIndex = 0;
                ecb.SetComponent(vehicle, pathOwner);
            }

            ecb.SetBuffer<PathElement>(vehicle).Clear();
            ecb.SetComponent(vehicle, target);
            ecb.SetComponent(vehicle, publicTransport);
            ecb.AddComponent<PathfindUpdated>(vehicle);
            ecb.AddComponent<Updated>(vehicle);
        }

        internal void ForceRetireOne(EntityCommandBuffer ecb)
        {
            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            try
            {
                foreach (Entity line in lines)
                {
                    if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                        continue;

                    HashSet<Entity> seenVehicles = new HashSet<Entity>();
                    for (int i = 0; i < routeVehicles.Length; i++)
                    {
                        Entity vehicle = m_Runtime.ResolveRuntimeControllerVehicle(routeVehicles[i].m_Vehicle);
                        if (!EntityManager.Exists(vehicle) || !seenVehicles.Add(vehicle))
                            continue;
                        if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state) || state == VehicleState.Retiring)
                            continue;

                        Game.Vehicles.PublicTransport publicTransport =
                            EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                        Target target = EntityManager.GetComponentData<Target>(vehicle);
                        log.Info("[F7] 线路" + line.Index + " 强制回库车辆" + vehicle.Index + " (状态=" + state + ")");
                        Retire(vehicle, publicTransport, target, ecb, "F7强制");
                        return;
                    }
                    break;
                }
            }
            finally
            {
                lines.Dispose();
            }
        }

        internal void GuardRetireHandoffInputs(uint nowFrame)
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
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState runtimeState)
                    || runtimeState != VehicleState.Retiring)
                {
                    continue;
                }

                Entity ownerDepot = EntityManager.HasComponent<Owner>(vehicle)
                    ? EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                    : Entity.Null;
                if (ownerDepot == Entity.Null || !EntityManager.HasComponent<Target>(vehicle))
                    continue;

                int serviceDispatchCount = 0;
                int publicRequestCount = 0;
                int cargoRequestCount = 0;
                bool clearedDispatch = false;
                bool changedState = false;
                bool changedTarget = false;
                bool clampedDeparture = false;
                bool publicWasReturning = false;
                bool cargoWasReturning = false;
                bool wasBoarding = false;

                Target target = EntityManager.GetComponentData<Target>(vehicle);
                Entity headVehicle = ResolveRetireHandoffHeadVehicle(vehicle);
                bool targetWasRouteWaypoint = target.m_Target != Entity.Null
                    && EntityManager.Exists(target.m_Target)
                    && IsRouteWaypointLikeTarget(vehicle, target.m_Target);
                bool alreadyDepotTarget = IsRetireHandoffDepotSemanticEntity(target.m_Target, ownerDepot);
                bool alreadyDepotPath = EntityHasDepotPathDestination(vehicle, ownerDepot)
                    || (headVehicle != vehicle && EntityHasDepotPathDestination(headVehicle, ownerDepot));
                bool alreadyParkingPath = EntityHasParkingNavigationLane(vehicle)
                    || (headVehicle != vehicle && EntityHasParkingNavigationLane(headVehicle));
                PathFlags pathState = 0;
                if (EntityManager.HasComponent<PathOwner>(vehicle))
                    pathState = EntityManager.GetComponentData<PathOwner>(vehicle).m_State;
                bool alreadyPathfindActive = (pathState & (PathFlags.Pending | PathFlags.Updated)) != 0;

                TryRepairRetireHandoffEndReached(
                    vehicle,
                    headVehicle,
                    targetWasRouteWaypoint,
                    pathState,
                    nowFrame,
                    watch,
                    out _);

                bool pathEndReached = HasTrainLaneFlags(vehicle, TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached)
                    || (headVehicle != vehicle && HasTrainLaneFlags(headVehicle, TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached));
                bool handoffBoundaryReady = IsRetireHandoffRouteBoundaryReady(
                    vehicle,
                    headVehicle,
                    pathEndReached,
                    out string handoffBoundary);

                if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                {
                    Game.Vehicles.PublicTransport publicSnapshot =
                        EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                    publicWasReturning = (publicSnapshot.m_State & PublicTransportFlags.Returning) != 0;
                    wasBoarding |= (publicSnapshot.m_State & PublicTransportFlags.Boarding) != 0;
                }
                if (EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle))
                {
                    Game.Vehicles.CargoTransport cargoSnapshot =
                        EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle);
                    cargoWasReturning = (cargoSnapshot.m_State & CargoTransportFlags.Returning) != 0;
                    wasBoarding |= (cargoSnapshot.m_State & CargoTransportFlags.Boarding) != 0;
                }
                bool officialDepotReturning = (publicWasReturning || cargoWasReturning) && alreadyDepotTarget;

                bool accelerateOfficialBoardingClose = wasBoarding && handoffBoundaryReady && targetWasRouteWaypoint;
                uint officialBoardingCloseFrame = nowFrame > DispatchRuntimeSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                    ? nowFrame - DispatchRuntimeSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                    : 1;

                if (EntityManager.HasBuffer<ServiceDispatch>(vehicle))
                {
                    DynamicBuffer<ServiceDispatch> dispatchBuffer = EntityManager.GetBuffer<ServiceDispatch>(vehicle);
                    serviceDispatchCount = dispatchBuffer.Length;
                    if (dispatchBuffer.Length > 0)
                    {
                        dispatchBuffer.Clear();
                        clearedDispatch = true;
                    }
                }

                bool publicReturning = false;
                bool cargoReturning = false;
                if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                {
                    Game.Vehicles.PublicTransport publicTransport =
                        EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                    publicRequestCount = publicTransport.m_RequestCount;
                    PublicTransportFlags oldState = publicTransport.m_State;
                    bool publicClampedDeparture = false;
                    publicTransport.m_RequestCount = 0;
                    if (!officialDepotReturning)
                    {
                        publicTransport.m_State &= ~(PublicTransportFlags.Returning
                            | PublicTransportFlags.EnRoute
                            | PublicTransportFlags.Refueling
                            | PublicTransportFlags.AbandonRoute);
                        if ((oldState & PublicTransportFlags.Boarding) != 0
                            && publicTransport.m_DepartureFrame > nowFrame)
                        {
                            publicTransport.m_DepartureFrame = nowFrame;
                            publicClampedDeparture = true;
                            clampedDeparture = true;
                        }
                        if (accelerateOfficialBoardingClose && (oldState & PublicTransportFlags.Boarding) != 0)
                        {
                            publicTransport.m_DepartureFrame = officialBoardingCloseFrame;
                            publicTransport.m_MinWaitingDistance = float.MaxValue;
                            publicTransport.m_MaxBoardingDistance = float.MaxValue;
                            publicClampedDeparture = true;
                            clampedDeparture = true;
                        }
                    }
                    publicReturning = true;
                    if (publicRequestCount != 0 || publicTransport.m_State != oldState || publicClampedDeparture)
                    {
                        EntityManager.SetComponentData(vehicle, publicTransport);
                        changedState = true;
                    }
                }

                if (EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle))
                {
                    Game.Vehicles.CargoTransport cargoTransport =
                        EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle);
                    cargoRequestCount = cargoTransport.m_RequestCount;
                    CargoTransportFlags oldState = cargoTransport.m_State;
                    bool cargoClampedDeparture = false;
                    cargoTransport.m_RequestCount = 0;
                    if (!officialDepotReturning)
                    {
                        cargoTransport.m_State &= ~(CargoTransportFlags.Returning
                            | CargoTransportFlags.EnRoute
                            | CargoTransportFlags.Refueling
                            | CargoTransportFlags.AbandonRoute);
                        if ((oldState & CargoTransportFlags.Boarding) != 0
                            && cargoTransport.m_DepartureFrame > nowFrame)
                        {
                            cargoTransport.m_DepartureFrame = nowFrame;
                            cargoClampedDeparture = true;
                            clampedDeparture = true;
                        }
                        if (accelerateOfficialBoardingClose && (oldState & CargoTransportFlags.Boarding) != 0)
                        {
                            cargoTransport.m_DepartureFrame = officialBoardingCloseFrame;
                            cargoClampedDeparture = true;
                            clampedDeparture = true;
                        }
                    }
                    cargoReturning = true;
                    if (cargoRequestCount != 0 || cargoTransport.m_State != oldState || cargoClampedDeparture)
                    {
                        EntityManager.SetComponentData(vehicle, cargoTransport);
                        changedState = true;
                    }
                }

                if (!officialDepotReturning && !wasBoarding && handoffBoundaryReady && targetWasRouteWaypoint)
                {
                    target.m_Target = ownerDepot;
                    EntityManager.SetComponentData(vehicle, target);
                    changedTarget = true;
                }

                bool changed = clearedDispatch || changedState || changedTarget || clampedDeparture;
                if (!changed)
                    continue;

                string lineTag = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity lineEntity)
                    ? "线路" + lineEntity.Index
                    : "线路?";

                bool guardCooled = watch.LastDispatchGuardLogFrame == 0 || nowFrame - watch.LastDispatchGuardLogFrame >= 180;
                if (clearedDispatch && guardCooled)
                {
                    watch.LastDispatchGuardLogFrame = nowFrame;
                    log.Info("[RetireHandoffGuard] " + lineTag + " 车辆" + vehicle.Index
                        + " 清理未停稳回库车dispatch输入"
                        + " serviceDispatch=" + serviceDispatchCount
                        + " publicReq=" + publicRequestCount
                        + " cargoReq=" + cargoRequestCount);
                }

                bool redispatchBlocked = officialDepotReturning
                    && (serviceDispatchCount > 0 || publicRequestCount > 0 || cargoRequestCount > 0);
                bool redispatchCooled = watch.LastRedispatchBlockedLogFrame == 0
                    || nowFrame - watch.LastRedispatchBlockedLogFrame >= 180;
                if (redispatchBlocked && redispatchCooled)
                {
                    watch.LastRedispatchBlockedLogFrame = nowFrame;
                    log.Info("[RetireHandoffGuard] " + lineTag + " 车辆" + vehicle.Index
                        + " 清理官方回库车再派发输入"
                        + " redispatchBlocked=1"
                        + " serviceDispatch=" + serviceDispatchCount
                        + " publicReq=" + publicRequestCount
                        + " cargoReq=" + cargoRequestCount
                        + " target=" + DescribeRetireShadowEntity(target.m_Target)
                        + " targetKind=" + DescribeRetireShadowTargetKind(target.m_Target)
                        + " path=" + pathState
                        + " reason=" + watch.ReasonCode);
                }

                bool preCommitCooled = watch.LastPreCommitLogFrame == 0 || nowFrame - watch.LastPreCommitLogFrame >= 180;
                if (preCommitCooled)
                {
                    watch.LastPreCommitLogFrame = nowFrame;
                    log.Info("[RetireHandoffArmVanillaReturn] " + lineTag + " 车辆" + vehicle.Index
                        + " owner=" + DescribeRetireShadowEntity(ownerDepot)
                        + " target=" + DescribeRetireShadowEntity(target.m_Target)
                        + " targetKind=" + DescribeRetireShadowTargetKind(target.m_Target)
                        + " serviceDispatch=" + serviceDispatchCount
                        + " publicReq=" + publicRequestCount
                        + " cargoReq=" + cargoRequestCount
                        + " hadPublic=" + (publicReturning ? "1" : "0")
                        + " hadCargo=" + (cargoReturning ? "1" : "0")
                        + " publicWasReturning=" + (publicWasReturning ? "1" : "0")
                        + " cargoWasReturning=" + (cargoWasReturning ? "1" : "0")
                        + " wasBoarding=" + (wasBoarding ? "1" : "0")
                        + " pathEndReached=" + (pathEndReached ? "1" : "0")
                        + " boundaryReady=" + (handoffBoundaryReady ? "1" : "0")
                        + " boundary=" + handoffBoundary
                        + " targetWasRouteWaypoint=" + (targetWasRouteWaypoint ? "1" : "0")
                        + " changedState=" + (changedState ? "1" : "0")
                        + " changedTarget=" + (changedTarget ? "1" : "0")
                        + " clampedDeparture=" + (clampedDeparture ? "1" : "0")
                        + " acceleratedClose=" + (accelerateOfficialBoardingClose ? "1" : "0")
                        + " path=" + pathState
                        + " reason=" + watch.ReasonCode);
                }
            }
        }

        internal void TickRetireHandoffWatch(EntityCommandBuffer ecb, uint nowFrame)
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

                bool hasRuntimeState = m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState runtimeState);
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

                if (EntityManager.HasComponent<Deleted>(vehicle) || EntityManager.HasComponent<ParkedTrain>(vehicle))
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

                string lineTag = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity lineEntity)
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
                Game.Vehicles.PublicTransport publicTransport =
                    EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
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

                bool maxAgeReached = nowFrame - watch.RequestedFrame >= DispatchRuntimeSystem.RETIRE_HANDOFF_MAX_AGE_FRAMES;
                bool routeRegression = waypointLikeTarget;
                bool lostDepotSemantics = watch.HardAckFrame > 0
                    && !targetDepotSemantic
                    && !pathDepotSemantic
                    && !returning
                    && !parking;
                bool hardAckStalled = watch.HardAckFrame > 0
                    && !depotSemanticRepathWindow
                    && !parking
                    && (nowFrame - watch.HardAckFrame) >= DispatchRuntimeSystem.RETIRE_HANDOFF_MAX_AGE_FRAMES;

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

                    if (ShouldRetryRetireHandoff(watch, nowFrame))
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

                bool maxAttemptsReached = watch.AttemptCount >= DispatchRuntimeSystem.RETIRE_HANDOFF_MAX_ATTEMPTS;
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

                if (!ShouldRetryRetireHandoff(watch, nowFrame))
                    continue;

                QueueRetireHandoffWrite(vehicle, ownerDepot, ecb, watch, nowFrame, lineTag);
            }

            if (removals == null)
                return;

            for (int i = 0; i < removals.Count; i++)
                m_RetireHandoffWatch.Remove(removals[i]);
        }

        internal void ReleaseCompletedRetireHandoffs()
        {
            NativeList<Entity> handedOffKeys = new NativeList<Entity>(Allocator.Temp);
            foreach (var kv in m_Runtime.m_VehicleRuntime.State)
            {
                Entity vehicle = kv.Key;
                if (kv.Value != VehicleState.Retiring)
                    continue;
                if (!EntityManager.Exists(vehicle))
                    continue;

                RecordRetireShadowSnapshot(vehicle, "retiring");
                if (!EntityManager.HasComponent<Deleted>(vehicle)
                    && !EntityManager.HasComponent<ParkedTrain>(vehicle))
                {
                    continue;
                }

                RecordRetireShadowSnapshot(
                    vehicle,
                    EntityManager.HasComponent<ParkedTrain>(vehicle) ? "parked" : "deleted-marked");

                if (EntityManager.HasComponent<ParkedTrain>(vehicle))
                {
                    if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                    {
                        Game.Vehicles.PublicTransport publicTransport =
                            EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                        publicTransport.m_State &= ~PublicTransportFlags.Disabled;
                        EntityManager.SetComponentData(vehicle, publicTransport);
                    }

                    if (EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle))
                    {
                        Game.Vehicles.CargoTransport cargoTransport =
                            EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle);
                        cargoTransport.m_State &= ~CargoTransportFlags.Disabled;
                        EntityManager.SetComponentData(vehicle, cargoTransport);
                    }
                }

                handedOffKeys.Add(vehicle);
            }

            for (int i = 0; i < handedOffKeys.Length; i++)
            {
                Entity handedOff = handedOffKeys[i];
                ReleaseRuntimeOwnershipAfterRetireHandoff(
                    handedOff,
                    EntityManager.HasComponent<ParkedTrain>(handedOff)
                        ? "retire-handoff-parked"
                        : "retire-handoff-deleted");
            }

            handedOffKeys.Dispose();
        }

        internal void RemoveRetireHandoff(Entity vehicle)
        {
            m_RetireHandoffWatch.Remove(vehicle);
        }

        internal void ClearRetireHandoffState()
        {
            m_RetireHandoffWatch.Clear();
            m_RetireShadowHistory.Clear();
            m_RetireShadowLastSnapshot.Clear();
            m_RetireShadowLastFrame.Clear();
        }

        internal string DescribeRetireShadowTargetKind(Entity entity)
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

        internal static string DescribeRetireShadowEntity(Entity entity)
        {
            return entity == Entity.Null ? "-" : entity.Index.ToString();
        }

        private static void ForceOfficialBoardingClose(ref Game.Vehicles.PublicTransport publicTransport, uint nowFrame)
        {
            publicTransport.m_DepartureFrame = nowFrame > DispatchRuntimeSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                ? nowFrame - DispatchRuntimeSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                : 1;
            publicTransport.m_MinWaitingDistance = float.MaxValue;
            publicTransport.m_MaxBoardingDistance = float.MaxValue;
        }

        private bool TryApplyLaunchSegmentPath(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            ref Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            EntityCommandBuffer ecb)
        {
            if (waypoints.Length < 2)
                return false;

            Entity route = Entity.Null;
            if (m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mappedLine) && mappedLine != Entity.Null)
            {
                route = mappedLine;
            }
            else if (EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            }

            if (route == Entity.Null || !EntityManager.Exists(route) || !EntityManager.HasBuffer<RouteSegment>(route))
                return false;

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

            target.m_Target = waypoints[1].m_Waypoint;
            CommitVehicleDepartureState(vehicle, publicTransport, target, ecb);

            if (EntityManager.HasComponent<PathOwner>(vehicle))
                ecb.SetComponent(vehicle, new PathOwner(PathFlags.Updated));

            DynamicBuffer<PathElement> targetPath = ecb.SetBuffer<PathElement>(vehicle);
            targetPath.Clear();
            for (int i = 0; i < segmentPath.Length; i++)
                targetPath.Add(segmentPath[i]);

            ecb.AddComponent<Updated>(vehicle);
            return true;
        }

        private void CommitVehicleDepartureState(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            Target target,
            EntityCommandBuffer ecb)
        {
            ecb.SetComponent(vehicle, target);
            ecb.SetComponent(vehicle, publicTransport);
            ecb.AddComponent<Updated>(vehicle);
        }

        internal void ReleaseRuntimeOwnershipAfterRetireHandoff(Entity vehicle, string reason)
        {
            m_RetireHandoffWatch.Remove(vehicle);
            m_Runtime.ClearBroadcastRuntimeState(vehicle);
            m_Runtime.m_VehicleRegistry.Remove(vehicle);
            m_Runtime.m_LapObservations.Remove(vehicle);
            m_Runtime.m_LastBoarding.Remove(vehicle);
            m_Runtime.m_CachedWpIdx.Remove(vehicle);
            m_Runtime.m_TrackProjector.Remove(vehicle);
            m_Runtime.m_UICache.Remove(vehicle);
            m_Runtime.m_LastRetireFixLogFrame.Remove(vehicle);
            m_Runtime.m_RetireFixCooldownUntil.Remove(vehicle);
            m_Runtime.m_PreparingFixCooldownUntil.Remove(vehicle);
            m_Runtime.m_RetireFixCount.Remove(vehicle);
            m_Runtime.m_StopDwell.Remove(vehicle);
            m_Runtime.m_BVMisfire.Remove(vehicle);
            m_Runtime.m_BVMisfireStartFrame.Remove(vehicle);
            m_Runtime.ClearBypassYieldState(vehicle, reason);
            m_Runtime.ClearVehicleProgressSuspect(vehicle, reason);
            FlushRetireShadowSnapshots(vehicle, reason);
            ResetRetireShadowSnapshots(vehicle);
        }

        internal void ResetRetireShadowSnapshots(Entity vehicle)
        {
            m_RetireShadowHistory.Remove(vehicle);
            m_RetireShadowLastSnapshot.Remove(vehicle);
            m_RetireShadowLastFrame.Remove(vehicle);
        }

        internal void FlushRetireShadowSnapshots(Entity vehicle, string reason)
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

        internal void RecordRetireShadowSnapshot(Entity vehicle, string phase)
        {
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            string snapshot = BuildRetireShadowSnapshot(vehicle, phase);
            uint nowFrame = Simulation.frameIndex;
            bool shouldSample = true;
            string lastSnapshot = string.Empty;
            uint lastFrame = 0;
            if (phase == "retiring"
                && m_RetireShadowLastSnapshot.TryGetValue(vehicle, out lastSnapshot)
                && lastSnapshot == snapshot
                && m_RetireShadowLastFrame.TryGetValue(vehicle, out lastFrame)
                && (nowFrame - lastFrame) < DispatchRuntimeSystem.RETIRE_SHADOW_SAMPLE_INTERVAL_FRAMES)
            {
                shouldSample = false;
            }

            if (!shouldSample)
                return;

            m_RetireShadowLastSnapshot[vehicle] = snapshot;
            m_RetireShadowLastFrame[vehicle] = nowFrame;

            if (!m_RetireShadowHistory.TryGetValue(vehicle, out List<string> history) || history == null)
            {
                history = new List<string>(DispatchRuntimeSystem.RETIRE_SHADOW_HISTORY_LIMIT);
                m_RetireShadowHistory[vehicle] = history;
            }

            if (history.Count >= DispatchRuntimeSystem.RETIRE_SHADOW_HISTORY_LIMIT)
                history.RemoveAt(0);
            history.Add(snapshot);
        }

        private string BuildRetireShadowSnapshot(Entity vehicle, string phase)
        {
            string state = m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState runtimeState)
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
            int pathLen = EntityManager.HasBuffer<PathElement>(vehicle) ? EntityManager.GetBuffer<PathElement>(vehicle, true).Length : -1;
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

            int navLen = EntityManager.HasBuffer<TrainNavigationLane>(vehicle) ? EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true).Length : -1;
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
            int layoutLen = EntityManager.HasBuffer<LayoutElement>(vehicle) ? EntityManager.GetBuffer<LayoutElement>(vehicle, true).Length : -1;
            Entity headVehicle = vehicle;
            if (layoutLen > 0)
                headVehicle = EntityManager.GetBuffer<LayoutElement>(vehicle, true)[0].m_Vehicle;

            int headNavLen = EntityManager.HasBuffer<TrainNavigationLane>(headVehicle) ? EntityManager.GetBuffer<TrainNavigationLane>(headVehicle, true).Length : -1;
            string headLastNavFlags = "-";
            Entity headLastNavLane = Entity.Null;
            if (headNavLen > 0)
            {
                DynamicBuffer<TrainNavigationLane> headNavigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(headVehicle, true);
                headLastNavFlags = headNavigationLanes[headNavLen - 1].m_Flags.ToString();
                headLastNavLane = headNavigationLanes[headNavLen - 1].m_Lane;
            }

            int headPathLen = EntityManager.HasBuffer<PathElement>(headVehicle) ? EntityManager.GetBuffer<PathElement>(headVehicle, true).Length : -1;
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

            return "frame=" + Simulation.frameIndex
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
            if (parked) return "parked";
            if (deleted) return "deleted-marked";
            if (targetEntity == Entity.Null || !EntityManager.Exists(targetEntity)) return "target-invalid";
            if ((pathFlags & PathFlags.Stuck) != 0 || (headPathFlags & PathFlags.Stuck) != 0) return "path-stuck";
            if ((pathFlags & PathFlags.Failed) != 0 || (headPathFlags & PathFlags.Failed) != 0) return "path-failed";
            if (navLen == 0 && headNavLen > 0) return "root-nav-missing-head-nav-present";
            if (navLen == 0 && (HasFlagText(frontLaneFlags, "EndOfPath") || HasFlagText(headFrontFlags, "EndOfPath")))
                return "end-of-path-without-nav";
            if (navLen > 0 && HasFlagText(navLastFlags, "ParkingSpace") && !parked) return "parking-space-not-parked";
            if (headNavLen > 0 && HasFlagText(headNavLastFlags, "ParkingSpace") && !parked) return "head-parking-space-not-parked";
            if (navLen > 0 && !HasFlagText(navLastFlags, "ParkingSpace") && HasFlagText(frontLaneFlags, "EndOfPath"))
                return "end-of-path-non-parking";
            if (navLen == 0 && pathLen >= 0 && pathElementIndex >= pathLen) return "path-consumed-no-nav";
            if (headNavLen == 0 && headPathLen >= 0 && headPathLen > 0 && targetEntity == ownerDepot)
                return "head-path-consumed-no-nav";
            return "unknown";
        }

        private bool ShouldRetryRetireHandoff(RetireHandoffWatchRecord watch, uint nowFrame)
        {
            if (watch.AttemptCount == 0)
                return true;
            return nowFrame - watch.LastWriteFrame >= DispatchRuntimeSystem.RETIRE_HANDOFF_RETRY_INTERVAL_FRAMES;
        }

        private bool IsRetireHandoffSoftAck(Entity vehicle, Entity targetEntity, Entity ownerDepot)
        {
            if (targetEntity != Entity.Null && targetEntity == ownerDepot)
                return true;
            if (IsRetireHandoffHardAck(vehicle, ownerDepot))
                return true;
            if (targetEntity != Entity.Null && EntityManager.Exists(targetEntity) && !IsRouteWaypointLikeTarget(vehicle, targetEntity))
                return true;
            if (targetEntity != Entity.Null && targetEntity == ownerDepot && EntityManager.HasComponent<PathOwner>(vehicle))
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
            if (entity == Entity.Null || !EntityManager.Exists(entity) || !EntityManager.HasBuffer<TrainNavigationLane>(entity))
                return false;
            DynamicBuffer<TrainNavigationLane> lanes = EntityManager.GetBuffer<TrainNavigationLane>(entity, true);
            return lanes.Length > 0 && (lanes[lanes.Length - 1].m_Flags & TrainLaneFlags.ParkingSpace) != 0;
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
            bool cooled = watch.LastParkingDiagLogFrame == 0 || nowFrame - watch.LastParkingDiagLogFrame >= 180;
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
            int pathLen = EntityManager.HasBuffer<PathElement>(entity) ? EntityManager.GetBuffer<PathElement>(entity, true).Length : -1;
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
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) || !EntityManager.HasBuffer<LayoutElement>(vehicle))
                return vehicle;
            DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            return layout.Length == 0 ? vehicle : layout[0].m_Vehicle;
        }

        private bool TryRepairRetireHandoffEndReached(
            Entity vehicle,
            Entity headVehicle,
            bool targetWasRouteWaypoint,
            PathFlags pathState,
            uint nowFrame,
            RetireHandoffWatchRecord watch,
            out string boundary)
        {
            boundary = "not-repaired";
            if (!targetWasRouteWaypoint
                || (pathState & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Updated | PathFlags.Stuck | PathFlags.Failed)) != 0
                || headVehicle == Entity.Null
                || !EntityManager.Exists(headVehicle)
                || !EntityManager.HasComponent<TrainCurrentLane>(headVehicle)
                || !EntityManager.HasComponent<TrainNavigation>(headVehicle))
            {
                return false;
            }

            TrainNavigation navigation = EntityManager.GetComponentData<TrainNavigation>(headVehicle);
            if (!(navigation.m_Speed < 0.1f))
            {
                boundary = "speed-not-stopped";
                return false;
            }

            TrainCurrentLane currentLane = EntityManager.GetComponentData<TrainCurrentLane>(headVehicle);
            TrainLaneFlags beforeFlags = currentLane.m_Front.m_LaneFlags;
            TrainLaneFlags movedFlags = 0;
            int navLenBefore = EntityManager.HasBuffer<TrainNavigationLane>(vehicle) ? EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true).Length : -1;
            int navConsumed = 0;

            if ((beforeFlags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached))
                == (TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached))
            {
                boundary = "already-path-end-reached";
                return false;
            }

            if ((beforeFlags & TrainLaneFlags.EndOfPath) != 0)
            {
                boundary = "front-end-of-path";
            }
            else if (TryMoveRetireHandoffNavigationEndToFront(vehicle, ref currentLane, out movedFlags, out navConsumed))
            {
                boundary = "nav-end-marker";
            }
            else if (HasConsumedPathWithoutNavigation(vehicle)
                || (headVehicle != vehicle && HasConsumedPathWithoutNavigation(headVehicle)))
            {
                currentLane.m_Front.m_LaneFlags |= TrainLaneFlags.EndOfPath;
                boundary = "path-consumed-no-nav";
            }
            else
            {
                return false;
            }

            if ((currentLane.m_Front.m_LaneFlags & TrainLaneFlags.EndOfPath) == 0)
                return false;

            currentLane.m_Front.m_LaneFlags |= TrainLaneFlags.EndReached;
            EntityManager.SetComponentData(headVehicle, currentLane);

            bool cooled = watch.LastEndReachedRepairLogFrame == 0 || nowFrame - watch.LastEndReachedRepairLogFrame >= 180;
            if (cooled)
            {
                watch.LastEndReachedRepairLogFrame = nowFrame;
                log.Info("[RetireHandoffEndReachedRepair] 车辆" + vehicle.Index
                    + " head=" + DescribeRetireShadowEntity(headVehicle)
                    + " frontBefore=" + beforeFlags
                    + " frontAfter=" + currentLane.m_Front.m_LaneFlags
                    + " movedFlags=" + movedFlags
                    + " navLenBefore=" + navLenBefore
                    + " navConsumed=" + navConsumed
                    + " speed=" + navigation.m_Speed
                    + " targetKind=" + (EntityManager.HasComponent<Target>(vehicle)
                        ? DescribeRetireShadowTargetKind(EntityManager.GetComponentData<Target>(vehicle).m_Target)
                        : "-")
                    + " pathState=" + pathState
                    + " boundary=" + boundary
                    + " reason=" + watch.ReasonCode);
            }

            return true;
        }

        private bool TryMoveRetireHandoffNavigationEndToFront(
            Entity vehicle,
            ref TrainCurrentLane currentLane,
            out TrainLaneFlags movedFlags,
            out int navConsumed)
        {
            movedFlags = 0;
            navConsumed = 0;
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) || !EntityManager.HasBuffer<TrainNavigationLane>(vehicle))
                return false;

            DynamicBuffer<TrainNavigationLane> navigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(vehicle);
            for (int i = 0; i < navigationLanes.Length; i++)
            {
                TrainLaneFlags flags = navigationLanes[i].m_Flags;
                if ((flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return)) == 0)
                    continue;

                movedFlags = flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return);
                currentLane.m_Front.m_LaneFlags |= movedFlags;
                navConsumed = i + 1;
                navigationLanes.RemoveRange(0, navConsumed);
                return (currentLane.m_Front.m_LaneFlags & TrainLaneFlags.EndOfPath) != 0;
            }
            return false;
        }

        private bool IsRetireHandoffRouteBoundaryReady(
            Entity vehicle,
            Entity headVehicle,
            bool strictPathEndReached,
            out string boundary)
        {
            if (strictPathEndReached || HasTrainLaneFlags(headVehicle, TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached))
            {
                boundary = "path-end-reached";
                return true;
            }

            bool vehicleEndOfPath = HasTrainLaneFlags(vehicle, TrainLaneFlags.EndOfPath);
            bool headEndOfPath = headVehicle != vehicle && HasTrainLaneFlags(headVehicle, TrainLaneFlags.EndOfPath);
            if ((vehicleEndOfPath || headEndOfPath)
                && HasNoTrainNavigation(vehicle)
                && (headVehicle == vehicle || HasNoTrainNavigation(headVehicle)))
            {
                boundary = "end-of-path-without-nav";
                return true;
            }
            if (HasConsumedPathWithoutNavigation(vehicle)
                || (headVehicle != vehicle && HasConsumedPathWithoutNavigation(headVehicle)))
            {
                boundary = "path-consumed-no-nav";
                return true;
            }

            boundary = "not-ready";
            return false;
        }

        private bool HasTrainLaneFlags(Entity entity, TrainLaneFlags flags)
        {
            return entity != Entity.Null
                && EntityManager.Exists(entity)
                && EntityManager.HasComponent<TrainCurrentLane>(entity)
                && (EntityManager.GetComponentData<TrainCurrentLane>(entity).m_Front.m_LaneFlags & flags) == flags;
        }

        private bool HasNoTrainNavigation(Entity entity)
        {
            return entity != Entity.Null
                && EntityManager.Exists(entity)
                && EntityManager.HasBuffer<TrainNavigationLane>(entity)
                && EntityManager.GetBuffer<TrainNavigationLane>(entity, true).Length == 0;
        }

        private bool HasConsumedPathWithoutNavigation(Entity entity)
        {
            if (entity == Entity.Null
                || !EntityManager.Exists(entity)
                || !HasNoTrainNavigation(entity)
                || !EntityManager.HasBuffer<PathElement>(entity)
                || !EntityManager.HasComponent<PathOwner>(entity))
            {
                return false;
            }

            DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(entity, true);
            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(entity);
            return path.Length >= 0 && pathOwner.m_ElementIndex >= path.Length;
        }

        private bool IsRetireHandoffDepotSemanticEntity(Entity entity, Entity ownerDepot)
        {
            if (entity == Entity.Null || ownerDepot == Entity.Null || !EntityManager.Exists(entity))
                return false;
            if (entity == ownerDepot)
                return true;
            return m_Runtime.CanonicalizeTransportDepotEntity(entity) == ownerDepot;
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
            string pathFlags = EntityManager.HasComponent<PathOwner>(vehicle)
                ? EntityManager.GetComponentData<PathOwner>(vehicle).m_State.ToString()
                : "-";
            bool pathfindUpdated = EntityManager.HasComponent<PathfindUpdated>(vehicle);
            string navLastFlags = "-";
            if (EntityManager.HasBuffer<TrainNavigationLane>(vehicle))
            {
                DynamicBuffer<TrainNavigationLane> lanes = EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true);
                if (lanes.Length > 0)
                    navLastFlags = lanes[lanes.Length - 1].m_Flags.ToString();
            }
            string frontFlags = EntityManager.HasComponent<TrainCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle).m_Front.m_LaneFlags.ToString()
                : "-";
            Entity headVehicle = ResolveRetireHandoffHeadVehicle(vehicle);
            string headNavLastFlags = "-";
            if (headVehicle != Entity.Null && EntityManager.HasBuffer<TrainNavigationLane>(headVehicle))
            {
                DynamicBuffer<TrainNavigationLane> lanes = EntityManager.GetBuffer<TrainNavigationLane>(headVehicle, true);
                if (lanes.Length > 0)
                    headNavLastFlags = lanes[lanes.Length - 1].m_Flags.ToString();
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
                || (nowFrame - watch.LastTraceFrame) >= DispatchRuntimeSystem.RETIRE_HANDOFF_TRACE_COOLDOWN_FRAMES;
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
            if (targetEntity == Entity.Null || !EntityManager.Exists(targetEntity) || !EntityManager.HasComponent<Waypoint>(targetEntity))
                return false;
            if (!EntityManager.HasComponent<CurrentRoute>(vehicle))
                return true;
            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            if (route == Entity.Null || !EntityManager.Exists(route) || !EntityManager.HasBuffer<RouteWaypoint>(route))
                return true;
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
            watch.LastWriteFrame = nowFrame;
            watch.AttemptCount = (byte)(watch.AttemptCount + 1);
            RecordRetireShadowSnapshot(vehicle, "handoff-precommit-requested");
            if (watch.HasIntervention || watch.AttemptCount > 2)
            {
                log.Info("[RetireHandoffRetry] " + lineTag + " 车辆" + vehicle.Index
                    + " attempt=" + watch.AttemptCount
                    + " target=depot#" + ownerDepot.Index
                    + " mode=precommit"
                    + " reason=" + watch.ReasonCode);
            }
        }

        private static bool HasFlagText(string text, string flag)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf(flag, StringComparison.Ordinal) >= 0;
        }
    }
}
