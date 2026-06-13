using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Commands
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
        public string LastTraceGateKey = string.Empty;
        public string LastTraceKey = string.Empty;
    }

    internal sealed class RetireHandoff
    {
        // Real handoff order stays:
        // Retire(...) request -> RetireHandoffDispatchGuardSystem before vanilla train AI ->
        // vanilla return/path setup -> controller ReleaseCompletedRetireHandoffs() ->
        // controller tail TickRetireHandoffWatch(...).
        private readonly RetireHost m_RetireHost;
        private readonly Dictionary<Entity, RetireHandoffWatchRecord> m_RetireHandoffWatch =
            new Dictionary<Entity, RetireHandoffWatchRecord>();
        private readonly Dictionary<Entity, List<string>> m_RetireShadowHistory =
            new Dictionary<Entity, List<string>>();
        private readonly Dictionary<Entity, string> m_RetireShadowLastSnapshot =
            new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_RetireShadowLastFrame =
            new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> m_RetireShadowLastRetiringFrame =
            new Dictionary<Entity, uint>();
        private readonly List<Entity> m_WatchKeysScratch = new List<Entity>();

        private readonly struct RetireGuardInputSnapshot
        {
            public readonly Entity OwnerDepot;
            public readonly Target Target;
            public readonly Entity HeadVehicle;
            public readonly PathFlags PathState;
            public readonly bool HasVehicleTrainCurrentLane;
            public readonly TrainCurrentLane VehicleTrainCurrentLane;
            public readonly bool HasHeadTrainCurrentLane;
            public readonly TrainCurrentLane HeadTrainCurrentLane;
            public readonly bool HasHeadTrainNavigation;
            public readonly TrainNavigation HeadTrainNavigation;
            public readonly bool HasPublicTransport;
            public readonly PublicTransport PublicTransport;
            public readonly bool HasCargoTransport;
            public readonly CargoTransport CargoTransport;

            public RetireGuardInputSnapshot(
                Entity ownerDepot,
                Target target,
                Entity headVehicle,
                PathFlags pathState,
                bool hasVehicleTrainCurrentLane,
                TrainCurrentLane vehicleTrainCurrentLane,
                bool hasHeadTrainCurrentLane,
                TrainCurrentLane headTrainCurrentLane,
                bool hasHeadTrainNavigation,
                TrainNavigation headTrainNavigation,
                bool hasPublicTransport,
                PublicTransport publicTransport,
                bool hasCargoTransport,
                CargoTransport cargoTransport)
            {
                OwnerDepot = ownerDepot;
                Target = target;
                HeadVehicle = headVehicle;
                PathState = pathState;
                HasVehicleTrainCurrentLane = hasVehicleTrainCurrentLane;
                VehicleTrainCurrentLane = vehicleTrainCurrentLane;
                HasHeadTrainCurrentLane = hasHeadTrainCurrentLane;
                HeadTrainCurrentLane = headTrainCurrentLane;
                HasHeadTrainNavigation = hasHeadTrainNavigation;
                HeadTrainNavigation = headTrainNavigation;
                HasPublicTransport = hasPublicTransport;
                PublicTransport = publicTransport;
                HasCargoTransport = hasCargoTransport;
                CargoTransport = cargoTransport;
            }
        }

        private EntityManager EntityManager => m_RetireHost.EntityManager;
        private TimedLogger Log => m_RetireHost.Log;

        public RetireHandoff(RetireHost retireHost)
        {
            m_RetireHost = retireHost;
        }

        public void Retire(
            Entity vehicle,
            PublicTransport publicTransport,
            Target target,
            EntityCommandBuffer ecb,
            string reason = "")
        {
            vehicle = m_RetireHost.ResolveVehicle(vehicle);
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            ResetShadow(vehicle);
            m_RetireHost.RetireRuntimeVehicle(vehicle);
            m_RetireHost.ClearRetireRequestState(vehicle);

            string lineTag = m_RetireHost.TryVehicleLine(vehicle, out Entity line)
                ? "线路" + line.Index
                : "线路?";
            m_RetireHost.SetRetireLabel(vehicle, reason);
            Log.Info("[回库] " + lineTag + " 车辆" + vehicle.Index
                + (reason.Length > 0 ? " 原因:" + reason : "") + " -> 车库");
            RecordShadow(vehicle, "retire-request");
            PutWatch(vehicle, new RetireHandoffWatchRecord
            {
                RequestedFrame = m_RetireHost.Frame,
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
            });
        }

        public void ForceRetireOne(EntityCommandBuffer ecb)
        {
            NativeArray<Entity> lines = m_RetireHost.LineEntities(Allocator.Temp);
            BufferLookup<RouteVehicle> routeVehicleBuffers = m_RetireHost.RouteVehicles(true);
            try
            {
                foreach (Entity line in lines)
                {
                    if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                        continue;

                    HashSet<Entity> seenVehicles = new HashSet<Entity>();
                    for (int i = 0; i < routeVehicles.Length; i++)
                    {
                        Entity vehicle = m_RetireHost.ResolveVehicle(routeVehicles[i].m_Vehicle);
                        if (!EntityManager.Exists(vehicle) || !seenVehicles.Add(vehicle))
                            continue;
                        if (!m_RetireHost.TryVehicleState(vehicle, out VehicleState state) || state == VehicleState.Retiring)
                            continue;

                        PublicTransport publicTransport = EntityManager.GetComponentData<PublicTransport>(vehicle);
                        Target target = EntityManager.GetComponentData<Target>(vehicle);
                        Log.Info("[F7] 线路" + line.Index + " 强制回库车辆" + vehicle.Index + " (状态=" + state + ")");
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

        public void GuardRetireHandoffInputs(uint nowFrame)
        {
            if (WatchCount == 0)
                return;

            List<Entity> watchedVehicles = WatchKeys();
            foreach (Entity vehicle in watchedVehicles)
            {
                if (!TryGetWatch(vehicle, out RetireHandoffWatchRecord watch))
                    continue;
                if (vehicle == Entity.Null
                    || !EntityManager.Exists(vehicle)
                    || EntityManager.HasComponent<Deleted>(vehicle)
                    || EntityManager.HasComponent<ParkedTrain>(vehicle))
                {
                    continue;
                }
                if (!m_RetireHost.TryVehicleState(vehicle, out VehicleState runtimeState)
                    || runtimeState != VehicleState.Retiring)
                {
                    continue;
                }

                if (!TryBuildRetireGuardInputSnapshot(vehicle, out RetireGuardInputSnapshot guardInput))
                    continue;

                Entity ownerDepot = guardInput.OwnerDepot;
                int serviceDispatchCount;
                int publicRequestCount = 0;
                int cargoRequestCount = 0;
                bool clearedDispatch = false;
                bool changedState = false;
                bool changedTarget = false;
                bool clampedDeparture = false;
                bool publicWasReturning = false;
                bool cargoWasReturning = false;
                bool wasBoarding = false;

                Target target = guardInput.Target;
                Entity headVehicle = guardInput.HeadVehicle;
                bool targetWasRouteWaypoint = target.m_Target != Entity.Null
                    && EntityManager.Exists(target.m_Target)
                    && m_RetireHost.IsRouteWaypointTarget(vehicle, target.m_Target);
                bool alreadyDepotTarget = m_RetireHost.IsDepotTarget(target.m_Target, ownerDepot);
                PathFlags pathState = guardInput.PathState;

                bool repairedPathEndReached = TryRepairRetireEndReached(
                    vehicle,
                    headVehicle,
                    targetWasRouteWaypoint,
                    pathState,
                    nowFrame,
                    watch,
                    guardInput.HasHeadTrainCurrentLane,
                    guardInput.HeadTrainCurrentLane,
                    guardInput.HasHeadTrainNavigation,
                    guardInput.HeadTrainNavigation,
                    out _);

                bool vehiclePathEndReached = guardInput.HasVehicleTrainCurrentLane
                    && (guardInput.VehicleTrainCurrentLane.m_Front.m_LaneFlags
                        & (TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached))
                        == (TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached);
                bool headPathEndReached = headVehicle != vehicle
                    && guardInput.HasHeadTrainCurrentLane
                    && (guardInput.HeadTrainCurrentLane.m_Front.m_LaneFlags
                        & (TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached))
                        == (TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached);
                bool pathEndReached = repairedPathEndReached || vehiclePathEndReached || headPathEndReached;
                bool handoffBoundaryReady = IsRetireBoundaryReady(
                    vehicle,
                    headVehicle,
                    pathEndReached,
                    out string handoffBoundary);

                if (guardInput.HasPublicTransport)
                {
                    PublicTransport publicSnapshot = guardInput.PublicTransport;
                    publicWasReturning = (publicSnapshot.m_State & PublicTransportFlags.Returning) != 0;
                    wasBoarding |= (publicSnapshot.m_State & PublicTransportFlags.Boarding) != 0;
                }
                if (guardInput.HasCargoTransport)
                {
                    CargoTransport cargoSnapshot = guardInput.CargoTransport;
                    cargoWasReturning = (cargoSnapshot.m_State & CargoTransportFlags.Returning) != 0;
                    wasBoarding |= (cargoSnapshot.m_State & CargoTransportFlags.Boarding) != 0;
                }
                bool officialDepotReturning = (publicWasReturning || cargoWasReturning) && alreadyDepotTarget;

                bool accelerateOfficialBoardingClose = wasBoarding && handoffBoundaryReady && targetWasRouteWaypoint;
                uint officialBoardingCloseFrame = nowFrame > DispatchRuntimeSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                    ? nowFrame - DispatchRuntimeSystem.OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                    : 1;

                m_RetireHost.ClearServiceDispatch(vehicle, out serviceDispatchCount);
                clearedDispatch = serviceDispatchCount > 0;

                bool publicReturning = false;
                bool cargoReturning = false;
                if (guardInput.HasPublicTransport)
                {
                    PublicTransport publicTransport = guardInput.PublicTransport;
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
                        m_RetireHost.SetPublicTransport(vehicle, publicTransport);
                        changedState = true;
                    }
                }

                if (guardInput.HasCargoTransport)
                {
                    CargoTransport cargoTransport = guardInput.CargoTransport;
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
                        m_RetireHost.SetCargoTransport(vehicle, cargoTransport);
                        changedState = true;
                    }
                }

                if (!officialDepotReturning && !wasBoarding && handoffBoundaryReady && targetWasRouteWaypoint)
                {
                    target.m_Target = ownerDepot;
                    m_RetireHost.SetTarget(vehicle, target);
                    changedTarget = true;
                }

                bool changed = clearedDispatch || changedState || changedTarget || clampedDeparture;
                if (!changed)
                    continue;

                string lineTag = m_RetireHost.TryVehicleLine(vehicle, out Entity lineEntity)
                    ? "线路" + lineEntity.Index
                    : "线路?";

                bool guardCooled = watch.LastDispatchGuardLogFrame == 0
                    || nowFrame - watch.LastDispatchGuardLogFrame >= 180;
                if (RtLog.VerboseEnabled && clearedDispatch && guardCooled)
                {
                    watch.LastDispatchGuardLogFrame = nowFrame;
                    Log.Info("[RetireHandoffGuard] " + lineTag + " 车辆" + vehicle.Index
                        + " 清理未停稳回库车dispatch输入"
                        + " serviceDispatch=" + serviceDispatchCount
                        + " publicReq=" + publicRequestCount
                        + " cargoReq=" + cargoRequestCount);
                }

                bool redispatchBlocked = officialDepotReturning
                    && (serviceDispatchCount > 0 || publicRequestCount > 0 || cargoRequestCount > 0);
                bool redispatchCooled = watch.LastRedispatchBlockedLogFrame == 0
                    || nowFrame - watch.LastRedispatchBlockedLogFrame >= 180;
                if (RtLog.VerboseEnabled && redispatchBlocked && redispatchCooled)
                {
                    watch.LastRedispatchBlockedLogFrame = nowFrame;
                    Log.Info("[RetireHandoffGuard] " + lineTag + " 车辆" + vehicle.Index
                        + " 清理官方回库车再派发输入"
                        + " redispatchBlocked=1"
                        + " serviceDispatch=" + serviceDispatchCount
                        + " publicReq=" + publicRequestCount
                        + " cargoReq=" + cargoRequestCount
                        + " target=" + m_RetireHost.DescribeEntity(target.m_Target)
                        + " targetKind=" + m_RetireHost.DescribeTargetKind(target.m_Target)
                        + " path=" + pathState
                        + " reason=" + watch.ReasonCode);
                }

                bool preCommitCooled = watch.LastPreCommitLogFrame == 0
                    || nowFrame - watch.LastPreCommitLogFrame >= 180;
                if (RtLog.VerboseEnabled && preCommitCooled)
                {
                    watch.LastPreCommitLogFrame = nowFrame;
                    Log.Info("[RetireHandoffArmVanillaReturn] " + lineTag + " 车辆" + vehicle.Index
                        + " owner=" + m_RetireHost.DescribeEntity(ownerDepot)
                        + " target=" + m_RetireHost.DescribeEntity(target.m_Target)
                        + " targetKind=" + m_RetireHost.DescribeTargetKind(target.m_Target)
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

        private bool TryBuildRetireGuardInputSnapshot(Entity vehicle, out RetireGuardInputSnapshot snapshot)
        {
            snapshot = default;
            Entity ownerDepot = EntityManager.HasComponent<Owner>(vehicle)
                ? EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                : Entity.Null;
            if (ownerDepot == Entity.Null || !EntityManager.HasComponent<Target>(vehicle))
                return false;

            Target target = EntityManager.GetComponentData<Target>(vehicle);
            Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);
            PathFlags pathState = EntityManager.HasComponent<PathOwner>(vehicle)
                ? EntityManager.GetComponentData<PathOwner>(vehicle).m_State
                : 0;

            bool hasVehicleTrainCurrentLane = EntityManager.HasComponent<TrainCurrentLane>(vehicle);
            TrainCurrentLane vehicleTrainCurrentLane = hasVehicleTrainCurrentLane
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle)
                : default;
            bool hasHeadTrainCurrentLane = headVehicle != Entity.Null
                && EntityManager.Exists(headVehicle)
                && EntityManager.HasComponent<TrainCurrentLane>(headVehicle);
            TrainCurrentLane headTrainCurrentLane = hasHeadTrainCurrentLane
                ? EntityManager.GetComponentData<TrainCurrentLane>(headVehicle)
                : default;
            bool hasHeadTrainNavigation = headVehicle != Entity.Null
                && EntityManager.Exists(headVehicle)
                && EntityManager.HasComponent<TrainNavigation>(headVehicle);
            TrainNavigation headTrainNavigation = hasHeadTrainNavigation
                ? EntityManager.GetComponentData<TrainNavigation>(headVehicle)
                : default;
            bool hasPublicTransport = EntityManager.HasComponent<PublicTransport>(vehicle);
            PublicTransport publicTransport = hasPublicTransport
                ? EntityManager.GetComponentData<PublicTransport>(vehicle)
                : default;
            bool hasCargoTransport = EntityManager.HasComponent<CargoTransport>(vehicle);
            CargoTransport cargoTransport = hasCargoTransport
                ? EntityManager.GetComponentData<CargoTransport>(vehicle)
                : default;

            snapshot = new RetireGuardInputSnapshot(
                ownerDepot,
                target,
                headVehicle,
                pathState,
                hasVehicleTrainCurrentLane,
                vehicleTrainCurrentLane,
                hasHeadTrainCurrentLane,
                headTrainCurrentLane,
                hasHeadTrainNavigation,
                headTrainNavigation,
                hasPublicTransport,
                publicTransport,
                hasCargoTransport,
                cargoTransport);
            return true;
        }

        public void TickRetireHandoffWatch(EntityCommandBuffer ecb, uint nowFrame)
        {
            if (WatchCount == 0)
                return;

            List<Entity> watchedVehicles = WatchKeys();
            List<Entity> removals = null;
            foreach (Entity vehicle in watchedVehicles)
            {
                if (!TryGetWatch(vehicle, out RetireHandoffWatchRecord watch))
                    continue;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                {
                    FlushShadow(vehicle, "entity-removed");
                    ResetShadow(vehicle);
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                bool hasRuntimeState = m_RetireHost.TryVehicleState(vehicle, out VehicleState runtimeState);
                if (!hasRuntimeState || runtimeState != VehicleState.Retiring)
                {
                    RecordShadow(vehicle, "handoff-abort-runtime-state");
                    if (RtLog.VerboseEnabled)
                    {
                        Log.Info("[RetireHandoffAbort] 车辆" + vehicle.Index
                            + " runtime state=" + (hasRuntimeState ? runtimeState.ToString() : "-")
                            + "，停止回库watch");
                    }
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                if (EntityManager.HasComponent<Deleted>(vehicle) || EntityManager.HasComponent<ParkedTrain>(vehicle))
                {
                    RecordShadow(
                        vehicle,
                        EntityManager.HasComponent<ParkedTrain>(vehicle) ? "parked" : "deleted-marked");
                    ReleaseRetireOwnership(
                        vehicle,
                        EntityManager.HasComponent<ParkedTrain>(vehicle)
                            ? "retire-handoff-parked"
                            : "retire-handoff-deleted");
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                string lineTag = m_RetireHost.TryVehicleLine(vehicle, out Entity lineEntity)
                    ? "线路" + lineEntity.Index
                    : "线路?";

                if (!EntityManager.HasComponent<Owner>(vehicle)
                    || !EntityManager.HasComponent<Target>(vehicle)
                    || !EntityManager.HasComponent<PublicTransport>(vehicle))
                {
                    RecordShadow(vehicle, "handoff-abort-missing-components");
                    if (RtLog.VerboseEnabled)
                    {
                        Log.Info("[RetireHandoffAbort] " + lineTag + " 车辆" + vehicle.Index
                            + " 缺少Owner/Target/PublicTransport，停止回库watch，保留RT Retiring ownership");
                    }
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
                Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);
                Entity pathInfoDestination = EntityManager.HasComponent<PathInformation>(vehicle)
                    ? EntityManager.GetComponentData<PathInformation>(vehicle).m_Destination
                    : Entity.Null;
                Entity headPathInfoDestination = EntityManager.HasComponent<PathInformation>(headVehicle)
                    ? EntityManager.GetComponentData<PathInformation>(headVehicle).m_Destination
                    : Entity.Null;
                PublicTransport publicTransport = EntityManager.GetComponentData<PublicTransport>(vehicle);
                bool returning = (publicTransport.m_State & PublicTransportFlags.Returning) != 0;
                bool parking = m_RetireHost.HasParkingNavLane(vehicle)
                    || (headVehicle != vehicle && m_RetireHost.HasParkingNavLane(headVehicle));
                bool waypointLikeTarget = targetEntity != Entity.Null
                    && EntityManager.Exists(targetEntity)
                    && m_RetireHost.IsRouteWaypointTarget(vehicle, targetEntity);
                bool targetDepotSemantic = m_RetireHost.IsDepotTarget(targetEntity, ownerDepot);
                bool pathDepotSemantic = m_RetireHost.HasDepotPathTarget(vehicle, ownerDepot)
                    || (headVehicle != vehicle && m_RetireHost.HasDepotPathTarget(headVehicle, ownerDepot));
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

                LogRetireHandoffTrace(
                    vehicle,
                    lineTag,
                    watch,
                    nowFrame,
                    currentRoute,
                    targetEntity,
                    ownerDepot,
                    pathInfoDestination,
                    headPathInfoDestination,
                    currentPathState,
                    softAck,
                    hardAck,
                    returning,
                    parking,
                    "sample",
                    force: false);
                LogRetireParkingDiagnostic(
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
                    RecordShadow(vehicle, "handoff-soft-ack");
                    if (RtLog.VerboseEnabled && watch.HasIntervention)
                    {
                        Log.Info("[RetireHandoffAck] " + lineTag + " 车辆" + vehicle.Index
                            + " soft target=" + m_RetireHost.DescribeEntity(targetEntity)
                            + " attempt=" + watch.AttemptCount);
                    }
                }

                if (hardAck && watch.HardAckFrame == 0)
                {
                    watch.HardAckFrame = nowFrame;
                    watch.HardAckStallLogged = false;
                    RecordShadow(vehicle, "handoff-hard-ack");
                    LogRetireHandoffTrace(
                        vehicle,
                        lineTag,
                        watch,
                        nowFrame,
                        currentRoute,
                        targetEntity,
                        ownerDepot,
                        pathInfoDestination,
                        headPathInfoDestination,
                        currentPathState,
                        softAck,
                        hardAck,
                        returning,
                        parking,
                        "hard-ack",
                        force: true);
                    if (RtLog.VerboseEnabled && watch.HasIntervention)
                    {
                        Log.Info("[RetireHandoffAck] " + lineTag + " 车辆" + vehicle.Index
                            + " hard target=" + m_RetireHost.DescribeEntity(targetEntity)
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
                    RecordShadow(vehicle, "handoff-hard-ack-regressed");
                    LogRetireHandoffTrace(
                        vehicle,
                        lineTag,
                        watch,
                        nowFrame,
                        currentRoute,
                        targetEntity,
                        ownerDepot,
                        pathInfoDestination,
                        headPathInfoDestination,
                        currentPathState,
                        softAck,
                        hardAck,
                        returning,
                        parking,
                        routeRegression ? "route-regressed" : "depot-semantics-lost",
                        force: true);
                    ArmRetireHandoffRetry(vehicle, ownerDepot, watch, nowFrame, lineTag);
                    watch.RequestedFrame = nowFrame;
                    watch.SoftAckFrame = 0;
                    watch.HardAckFrame = 0;
                    watch.HardAckStallLogged = false;
                    continue;
                }

                if (softAck && !hardAck && maxAgeReached)
                {
                    RecordShadow(vehicle, "handoff-soft-ack-stagnant-retry");
                    LogRetireHandoffTrace(
                        vehicle,
                        lineTag,
                        watch,
                        nowFrame,
                        currentRoute,
                        targetEntity,
                        ownerDepot,
                        pathInfoDestination,
                        headPathInfoDestination,
                        currentPathState,
                        softAck,
                        hardAck,
                        returning,
                        parking,
                        "soft-stagnant-retry",
                        force: true);
                    if (RtLog.VerboseEnabled)
                    {
                        Log.Info("[RetireHandoffRetry] " + lineTag + " 车辆" + vehicle.Index
                            + " soft ack后未进入hard ack，超时重投"
                            + " attempts=" + watch.AttemptCount
                            + " ageFrames=" + (nowFrame - watch.RequestedFrame)
                            + " target=" + m_RetireHost.DescribeEntity(targetEntity)
                            + " reason=" + watch.ReasonCode);
                    }
                    watch.HasIntervention = true;
                    ArmRetireHandoffRetry(vehicle, ownerDepot, watch, nowFrame, lineTag);
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
                        RecordShadow(vehicle, "handoff-hard-ack-stalled");
                        LogRetireHandoffTrace(
                            vehicle,
                            lineTag,
                            watch,
                            nowFrame,
                            currentRoute,
                            targetEntity,
                            ownerDepot,
                            pathInfoDestination,
                            headPathInfoDestination,
                            currentPathState,
                            softAck,
                            hardAck,
                            returning,
                            parking,
                            "hard-stalled",
                            force: true);
                        if (RtLog.VerboseEnabled)
                        {
                            Log.Info("[RetireHandoffStall] " + lineTag + " 车辆" + vehicle.Index
                                + " hard ack后长期未收口"
                                + " hardAckAgeFrames=" + hardAckAgeFrames
                                + " attempts=" + watch.AttemptCount
                                + " target=" + m_RetireHost.DescribeEntity(targetEntity)
                                + " targetKind=" + m_RetireHost.DescribeTargetKind(targetEntity)
                                + " targetExists=" + ((targetEntity != Entity.Null && EntityManager.Exists(targetEntity)) ? "1" : "0")
                                + " owner=" + m_RetireHost.DescribeEntity(ownerDepot)
                                + " route=" + m_RetireHost.DescribeEntity(currentRoute)
                                + " returning=" + (returning ? "1" : "0")
                                + " ptState=" + publicTransport.m_State
                                + " piDest=" + m_RetireHost.DescribeEntity(pathInfoDestination)
                                + " headPiDest=" + m_RetireHost.DescribeEntity(headPathInfoDestination)
                                + " parking=" + (parking ? "1" : "0")
                                + " headParking=" + ((headVehicle != vehicle && m_RetireHost.HasParkingNavLane(headVehicle)) ? "1" : "0")
                                + " reason=" + watch.ReasonCode);
                        }
                    }

                    if (ShouldRetryRetireHandoff(watch, nowFrame))
                    {
                        ArmRetireHandoffRetry(vehicle, ownerDepot, watch, nowFrame, lineTag);
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
                    RecordShadow(vehicle, "handoff-abort-timeout");
                    LogRetireHandoffTrace(
                        vehicle,
                        lineTag,
                        watch,
                        nowFrame,
                        currentRoute,
                        targetEntity,
                        ownerDepot,
                        pathInfoDestination,
                        headPathInfoDestination,
                        currentPathState,
                        softAck,
                        hardAck,
                        returning,
                        parking,
                        "abort-timeout",
                        force: true);
                    if (RtLog.VerboseEnabled)
                    {
                        Log.Info("[RetireHandoffAbort] " + lineTag + " 车辆" + vehicle.Index
                            + " 回库交接未被vanilla接住，停止重投"
                            + " attempts=" + watch.AttemptCount
                            + " ageFrames=" + (nowFrame - watch.RequestedFrame)
                            + " target=" + m_RetireHost.DescribeEntity(targetEntity)
                            + " reason=" + watch.ReasonCode);
                    }
                    removals ??= new List<Entity>();
                    removals.Add(vehicle);
                    continue;
                }

                if (!ShouldRetryRetireHandoff(watch, nowFrame))
                    continue;

                ArmRetireHandoffRetry(vehicle, ownerDepot, watch, nowFrame, lineTag);
            }

            if (removals == null)
                return;

            for (int i = 0; i < removals.Count; i++)
                RemoveRetireHandoff(removals[i]);
        }

        public void ReleaseCompletedRetireHandoffs()
        {
            NativeList<Entity> handedOffKeys = new NativeList<Entity>(Allocator.Temp);
            try
            {
                m_RetireHost.CollectRetiringVehicles(handedOffKeys);
                for (int i = 0; i < handedOffKeys.Length; i++)
                {
                    Entity vehicle = handedOffKeys[i];
                    RecordShadow(vehicle, "retiring");
                    if (!EntityManager.HasComponent<Deleted>(vehicle)
                        && !EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        continue;
                    }

                    RecordShadow(
                        vehicle,
                        EntityManager.HasComponent<ParkedTrain>(vehicle) ? "parked" : "deleted-marked");

                    if (EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        if (EntityManager.HasComponent<PublicTransport>(vehicle))
                        {
                            PublicTransport publicTransport = EntityManager.GetComponentData<PublicTransport>(vehicle);
                            publicTransport.m_State &= ~PublicTransportFlags.Disabled;
                            m_RetireHost.SetPublicTransport(vehicle, publicTransport);
                        }

                        if (EntityManager.HasComponent<CargoTransport>(vehicle))
                        {
                            CargoTransport cargoTransport = EntityManager.GetComponentData<CargoTransport>(vehicle);
                            cargoTransport.m_State &= ~CargoTransportFlags.Disabled;
                            m_RetireHost.SetCargoTransport(vehicle, cargoTransport);
                        }
                    }

                    ReleaseRetireOwnership(
                        vehicle,
                        EntityManager.HasComponent<ParkedTrain>(vehicle)
                            ? "retire-handoff-parked"
                            : "retire-handoff-deleted");
                }
            }
            finally
            {
                handedOffKeys.Dispose();
            }
        }

        public void RemoveRetireHandoff(Entity vehicle)
        {
            RemoveWatch(vehicle);
        }

        public void ClearRetireHandoffState()
        {
            ClearAll();
        }

        public void FlushRetireShadowSnapshots(Entity vehicle, string reason)
        {
            FlushShadow(vehicle, reason);
        }

        public void ResetRetireShadowSnapshots(Entity vehicle)
        {
            ResetShadow(vehicle);
        }

        public string DescribeRetireShadowTargetKind(Entity entity)
        {
            return m_RetireHost.DescribeTargetKind(entity);
        }

        public static string DescribeRetireShadowEntity(Entity entity)
        {
            return entity == Entity.Null ? "-" : entity.Index.ToString();
        }

        private void ReleaseRetireOwnership(Entity vehicle, string reason)
        {
            RemoveWatch(vehicle);
            m_RetireHost.ReleaseRetireRuntimeOwnership(vehicle, reason);
            FlushShadow(vehicle, reason);
            ResetShadow(vehicle);
        }

        private bool TryRepairRetireEndReached(
            Entity vehicle,
            Entity headVehicle,
            bool targetWasRouteWaypoint,
            PathFlags pathState,
            uint nowFrame,
            RetireHandoffWatchRecord watch,
            bool hasHeadTrainCurrentLane,
            TrainCurrentLane headTrainCurrentLane,
            bool hasHeadTrainNavigation,
            TrainNavigation headTrainNavigation,
            out string boundary)
        {
            boundary = "not-repaired";
            if (!targetWasRouteWaypoint
                || (pathState & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Updated | PathFlags.Stuck | PathFlags.Failed)) != 0
                || headVehicle == Entity.Null
                || !EntityManager.Exists(headVehicle)
                || !hasHeadTrainCurrentLane
                || !hasHeadTrainNavigation)
            {
                return false;
            }

            TrainNavigation navigation = headTrainNavigation;
            if (!(navigation.m_Speed < 0.1f))
            {
                boundary = "speed-not-stopped";
                return false;
            }

            TrainCurrentLane currentLane = headTrainCurrentLane;
            TrainLaneFlags beforeFlags = currentLane.m_Front.m_LaneFlags;
            TrainLaneFlags movedFlags = 0;
            int navLenBefore = EntityManager.HasBuffer<TrainNavigationLane>(vehicle)
                ? EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true).Length
                : -1;
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
            else if (m_RetireHost.MoveRetireNavEnd(vehicle, ref currentLane, out movedFlags, out navConsumed))
            {
                boundary = "nav-end-marker";
            }
            else if (m_RetireHost.HasConsumedPath(vehicle)
                || (headVehicle != vehicle && m_RetireHost.HasConsumedPath(headVehicle)))
            {
                movedFlags = 0;
                navConsumed = 0;
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
            m_RetireHost.SetTrainCurrentLane(headVehicle, currentLane);

            bool cooled = watch.LastEndReachedRepairLogFrame == 0
                || nowFrame - watch.LastEndReachedRepairLogFrame >= 180;
            if (RtLog.VerboseEnabled && cooled)
            {
                watch.LastEndReachedRepairLogFrame = nowFrame;
                Log.Info("[RetireHandoffEndReachedRepair] 车辆" + vehicle.Index
                    + " head=" + m_RetireHost.DescribeEntity(headVehicle)
                    + " frontBefore=" + beforeFlags
                    + " frontAfter=" + currentLane.m_Front.m_LaneFlags
                    + " movedFlags=" + movedFlags
                    + " navLenBefore=" + navLenBefore
                    + " navConsumed=" + navConsumed
                    + " speed=" + navigation.m_Speed
                    + " targetKind=" + (EntityManager.HasComponent<Target>(vehicle)
                        ? m_RetireHost.DescribeTargetKind(EntityManager.GetComponentData<Target>(vehicle).m_Target)
                        : "-")
                    + " pathState=" + pathState
                    + " boundary=" + boundary
                    + " reason=" + watch.ReasonCode);
            }

            return true;
        }

        private bool IsRetireBoundaryReady(
            Entity vehicle,
            Entity headVehicle,
            bool strictPathEndReached,
            out string boundary)
        {
            if (strictPathEndReached || m_RetireHost.HasTrainLaneFlags(headVehicle, TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached))
            {
                boundary = "path-end-reached";
                return true;
            }

            bool vehicleEndOfPath = m_RetireHost.HasTrainLaneFlags(vehicle, TrainLaneFlags.EndOfPath);
            bool headEndOfPath = headVehicle != vehicle
                && m_RetireHost.HasTrainLaneFlags(headVehicle, TrainLaneFlags.EndOfPath);
            if ((vehicleEndOfPath || headEndOfPath)
                && m_RetireHost.HasNoTrainNavigation(vehicle)
                && (headVehicle == vehicle || m_RetireHost.HasNoTrainNavigation(headVehicle)))
            {
                boundary = "end-of-path-without-nav";
                return true;
            }
            if (m_RetireHost.HasConsumedPath(vehicle)
                || (headVehicle != vehicle && m_RetireHost.HasConsumedPath(headVehicle)))
            {
                boundary = "path-consumed-no-nav";
                return true;
            }

            boundary = "not-ready";
            return false;
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
            if (targetEntity != Entity.Null
                && EntityManager.Exists(targetEntity)
                && !m_RetireHost.IsRouteWaypointTarget(vehicle, targetEntity))
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
            if (EntityManager.HasComponent<PublicTransport>(vehicle))
            {
                PublicTransport publicTransport = EntityManager.GetComponentData<PublicTransport>(vehicle);
                if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0)
                    return true;
            }
            if (EntityManager.HasComponent<CargoTransport>(vehicle))
            {
                CargoTransport cargoTransport = EntityManager.GetComponentData<CargoTransport>(vehicle);
                if ((cargoTransport.m_State & CargoTransportFlags.Returning) != 0)
                    return true;
            }
            if (m_RetireHost.HasDepotPathTarget(vehicle, ownerDepot))
                return true;

            Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);
            if (headVehicle != vehicle && m_RetireHost.HasDepotPathTarget(headVehicle, ownerDepot))
                return true;

            return m_RetireHost.HasParkingNavLane(vehicle)
                || (headVehicle != vehicle && m_RetireHost.HasParkingNavLane(headVehicle));
        }

        private void LogRetireParkingDiagnostic(
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
            if (!RtLog.VerboseEnabled || !parking)
                return;

            bool cooled = watch.LastParkingDiagLogFrame == 0 || nowFrame - watch.LastParkingDiagLogFrame >= 180;
            if (!cooled)
                return;

            watch.LastParkingDiagLogFrame = nowFrame;
            Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);
            Log.Info("[RetireParkingDiag] " + lineTag + " 车辆" + vehicle.Index
                + " attempt=" + watch.AttemptCount
                + " target=" + m_RetireHost.DescribeEntity(targetEntity)
                + " targetKind=" + m_RetireHost.DescribeTargetKind(targetEntity)
                + " owner=" + m_RetireHost.DescribeEntity(ownerDepot)
                + " route=" + m_RetireHost.DescribeEntity(currentRoute)
                + " returning=" + (returning ? "1" : "0")
                + " piDest=" + m_RetireHost.DescribeEntity(pathInfoDestination)
                + " headPiDest=" + m_RetireHost.DescribeEntity(headPathInfoDestination)
                + " ctrl{" + m_RetireHost.FormatParkingEntity(vehicle) + "}"
                + " head{" + m_RetireHost.FormatParkingEntity(headVehicle) + "}");
        }

        private void LogRetireHandoffTrace(
            Entity vehicle,
            string lineTag,
            RetireHandoffWatchRecord watch,
            uint nowFrame,
            Entity currentRoute,
            Entity targetEntity,
            Entity ownerDepot,
            Entity pathInfoDestination,
            Entity headPathInfoDestination,
            PathFlags currentPathState,
            bool softAck,
            bool hardAck,
            bool returning,
            bool parking,
            string reason,
            bool force)
        {
            if (!RtLog.VerboseEnabled)
                return;

            bool gateCooled = watch.LastTraceFrame == 0
                || (nowFrame - watch.LastTraceFrame) >= DispatchRuntimeSystem.RETIRE_HANDOFF_TRACE_COOLDOWN_FRAMES;
            string gateKey = targetEntity.Index.ToString()
                + "|"
                + currentRoute.Index
                + "|"
                + pathInfoDestination.Index
                + "|"
                + headPathInfoDestination.Index
                + "|"
                + (int)currentPathState
                + "|"
                + (returning ? "1" : "0")
                + "|"
                + (parking ? "1" : "0")
                + "|"
                + (softAck ? "1" : "0")
                + "|"
                + (hardAck ? "1" : "0")
                + "|"
                + watch.AttemptCount;
            bool gateChanged = !string.Equals(watch.LastTraceGateKey, gateKey, StringComparison.Ordinal);
            if (!force && !gateChanged && !gateCooled)
                return;

            watch.LastTraceGateKey = gateKey;

            string pathFlags = currentPathState.ToString();
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
            Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);
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

            bool targetDepotSemantic = m_RetireHost.IsDepotTarget(targetEntity, ownerDepot);
            bool pathDepotSemantic = m_RetireHost.IsDepotTarget(pathInfoDestination, ownerDepot)
                || m_RetireHost.IsDepotTarget(headPathInfoDestination, ownerDepot);
            string key = "target=" + m_RetireHost.DescribeEntity(targetEntity)
                + "|targetKind=" + m_RetireHost.DescribeTargetKind(targetEntity)
                + "|targetDepot=" + (targetDepotSemantic ? "1" : "0")
                + "|route=" + m_RetireHost.DescribeEntity(currentRoute)
                + "|piDest=" + m_RetireHost.DescribeEntity(pathInfoDestination)
                + "|headPiDest=" + m_RetireHost.DescribeEntity(headPathInfoDestination)
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
            bool cooled = gateCooled;
            if (!force && !changed && !cooled)
                return;
            if (!force && !changed)
                reason = "cooldown";

            Log.Info("[RetireHandoffTrace] " + lineTag + " 车辆" + vehicle.Index
                + " reason=" + reason
                + " attempt=" + watch.AttemptCount
                + " target=" + m_RetireHost.DescribeEntity(targetEntity)
                + " targetKind=" + m_RetireHost.DescribeTargetKind(targetEntity)
                + " targetDepot=" + (targetDepotSemantic ? "1" : "0")
                + " route=" + m_RetireHost.DescribeEntity(currentRoute)
                + " piDest=" + m_RetireHost.DescribeEntity(pathInfoDestination)
                + " headPiDest=" + m_RetireHost.DescribeEntity(headPathInfoDestination)
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
            watch.LastTraceGateKey = gateKey;
            watch.LastTraceKey = key;
        }

        private void ArmRetireHandoffRetry(
            Entity vehicle,
            Entity ownerDepot,
            RetireHandoffWatchRecord watch,
            uint nowFrame,
            string lineTag)
        {
            watch.LastWriteFrame = nowFrame;
            watch.AttemptCount = (byte)(watch.AttemptCount + 1);
            RecordShadow(vehicle, "handoff-precommit-requested");
            if (RtLog.VerboseEnabled && (watch.HasIntervention || watch.AttemptCount > 2))
            {
                Log.Info("[RetireHandoffRetry] " + lineTag + " 车辆" + vehicle.Index
                    + " attempt=" + watch.AttemptCount
                    + " target=depot#" + ownerDepot.Index
                    + " mode=precommit"
                    + " reason=" + watch.ReasonCode);
            }
        }

        private int WatchCount => m_RetireHandoffWatch.Count;

        private bool TryGetWatch(Entity vehicle, out RetireHandoffWatchRecord watch)
        {
            return m_RetireHandoffWatch.TryGetValue(vehicle, out watch);
        }

        private void PutWatch(Entity vehicle, RetireHandoffWatchRecord watch)
        {
            m_RetireHandoffWatch[vehicle] = watch;
        }

        private void RemoveWatch(Entity vehicle)
        {
            m_RetireHandoffWatch.Remove(vehicle);
        }

        private void ClearWatch()
        {
            m_RetireHandoffWatch.Clear();
        }

        private void ClearAll()
        {
            ClearWatch();
            m_RetireShadowHistory.Clear();
            m_RetireShadowLastSnapshot.Clear();
            m_RetireShadowLastFrame.Clear();
            m_RetireShadowLastRetiringFrame.Clear();
        }

        private List<Entity> WatchKeys()
        {
            m_WatchKeysScratch.Clear();
            foreach (Entity vehicle in m_RetireHandoffWatch.Keys)
                m_WatchKeysScratch.Add(vehicle);
            return m_WatchKeysScratch;
        }

        private void RecordShadow(Entity vehicle, string phase)
        {
            if (!RtLog.VerboseEnabled
                || vehicle == Entity.Null
                || !EntityManager.Exists(vehicle))
                return;

            uint nowFrame = m_RetireHost.Frame;
            if (!ShouldRecordShadowBeforeSnapshot(vehicle, phase, nowFrame))
                return;

            string snapshot = BuildShadowSnapshot(vehicle, phase);
            bool shouldSample = true;
            if (phase == "retiring"
                && m_RetireShadowLastSnapshot.TryGetValue(vehicle, out string lastSnapshot)
                && lastSnapshot == snapshot
                && m_RetireShadowLastFrame.TryGetValue(vehicle, out uint lastFrame)
                && (nowFrame - lastFrame) < DispatchRuntimeSystem.RETIRE_SHADOW_SAMPLE_INTERVAL_FRAMES)
            {
                shouldSample = false;
            }

            if (!shouldSample)
            {
                if (phase == "retiring")
                    m_RetireShadowLastRetiringFrame[vehicle] = nowFrame;
                return;
            }

            m_RetireShadowLastSnapshot[vehicle] = snapshot;
            m_RetireShadowLastFrame[vehicle] = nowFrame;
            if (phase == "retiring")
                m_RetireShadowLastRetiringFrame[vehicle] = nowFrame;

            if (!m_RetireShadowHistory.TryGetValue(vehicle, out List<string> history) || history == null)
            {
                history = new List<string>(DispatchRuntimeSystem.RETIRE_SHADOW_HISTORY_LIMIT);
                m_RetireShadowHistory[vehicle] = history;
            }

            if (history.Count >= DispatchRuntimeSystem.RETIRE_SHADOW_HISTORY_LIMIT)
                history.RemoveAt(0);
            history.Add(snapshot);
        }

        private void FlushShadow(Entity vehicle, string reason)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (!m_RetireShadowHistory.TryGetValue(vehicle, out List<string> history) || history == null || history.Count == 0)
                return;

            for (int i = 0; i < history.Count; i++)
            {
                Log.Info("[RetireShadow] 车辆" + vehicle.Index
                    + " reason=" + reason
                    + " step=" + (i + 1) + "/" + history.Count
                    + " " + history[i]);
            }
        }

        private void ResetShadow(Entity vehicle)
        {
            m_RetireShadowHistory.Remove(vehicle);
            m_RetireShadowLastSnapshot.Remove(vehicle);
            m_RetireShadowLastFrame.Remove(vehicle);
            m_RetireShadowLastRetiringFrame.Remove(vehicle);
        }

        private bool ShouldRecordShadowBeforeSnapshot(Entity vehicle, string phase, uint nowFrame)
        {
            if (phase != "retiring")
                return true;

            if (EntityManager.HasComponent<Deleted>(vehicle)
                || EntityManager.HasComponent<ParkedTrain>(vehicle))
            {
                return true;
            }

            return !m_RetireShadowLastRetiringFrame.TryGetValue(vehicle, out uint lastFrame)
                || nowFrame - lastFrame >= DispatchRuntimeSystem.RETIRE_SHADOW_SAMPLE_INTERVAL_FRAMES;
        }

        private string BuildShadowSnapshot(Entity vehicle, string phase)
        {
            string state = m_RetireHost.TryVehicleState(vehicle, out VehicleState runtimeState)
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
            string publicFlags = EntityManager.HasComponent<PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<PublicTransport>(vehicle).m_State.ToString()
                : "-";
            string cargoFlags = EntityManager.HasComponent<CargoTransport>(vehicle)
                ? EntityManager.GetComponentData<CargoTransport>(vehicle).m_State.ToString()
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
            int layoutLen = EntityManager.HasBuffer<LayoutElement>(vehicle) ? EntityManager.GetBuffer<LayoutElement>(vehicle, true).Length : -1;
            Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);

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

            string targetKind = m_RetireHost.DescribeTargetKind(targetEntity);
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

            return "frame=" + m_RetireHost.Frame
                + " phase=" + phase
                + " state=" + state
                + " pending=" + (hasHandoffWatch ? "1" : "0")
                + " attempt=" + (hasHandoffWatch ? handoffWatch.AttemptCount.ToString() : "-")
                + " softAck=" + (hasHandoffWatch && handoffWatch.SoftAckFrame > 0 ? handoffWatch.SoftAckFrame.ToString() : "-")
                + " hardAck=" + (hasHandoffWatch && handoffWatch.HardAckFrame > 0 ? handoffWatch.HardAckFrame.ToString() : "-")
                + " lastWrite=" + (hasHandoffWatch && handoffWatch.LastWriteFrame > 0 ? handoffWatch.LastWriteFrame.ToString() : "-")
                + " guess=" + guess
                + " ctrl=" + m_RetireHost.DescribeEntity(controllerEntity)
                + " owner=" + m_RetireHost.DescribeEntity(ownerDepot)
                + " ownerFlags=" + ownerDepotFlags
                + " ownerAvail=" + ownerDepotAvailable
                + " target=" + m_RetireHost.DescribeEntity(targetEntity)
                + " targetKind=" + targetKind
                + " targetExists=" + ((targetEntity != Entity.Null && EntityManager.Exists(targetEntity)) ? "1" : "0")
                + " targetIsOwner=" + ((targetEntity != Entity.Null && targetEntity == ownerDepot) ? "1" : "0")
                + " route=" + m_RetireHost.DescribeEntity(currentRoute)
                + " deleted=" + (EntityManager.HasComponent<Deleted>(vehicle) ? "1" : "0")
                + " parked=" + (EntityManager.HasComponent<ParkedTrain>(vehicle) ? "1" : "0")
                + " pfu=" + (EntityManager.HasComponent<PathfindUpdated>(vehicle) ? "1" : "0")
                + " upd=" + (EntityManager.HasComponent<Updated>(vehicle) ? "1" : "0")
                + " pt=" + publicFlags
                + " cargo=" + cargoFlags
                + " path=" + pathFlags
                + " pathLen=" + pathLen
                + " pathIdx=" + pathElementIndex
                + " piDest=" + m_RetireHost.DescribeEntity(pathInfoDest)
                + " piState=" + pathInfoState
                + " navLen=" + navLen
                + " navLane=" + m_RetireHost.DescribeEntity(lastNavLane)
                + " navLast=" + lastNavFlags
                + " front=" + frontLaneFlags
                + " frontLane=" + m_RetireHost.DescribeEntity(frontLane)
                + " rearLane=" + m_RetireHost.DescribeEntity(rearLane)
                + " layout=" + layoutLen
                + " head=" + m_RetireHost.DescribeEntity(headVehicle)
                + " headSelf=" + (headVehicle == vehicle ? "1" : "0")
                + " headPfu=" + (EntityManager.HasComponent<PathfindUpdated>(headVehicle) ? "1" : "0")
                + " headUpd=" + (EntityManager.HasComponent<Updated>(headVehicle) ? "1" : "0")
                + " headNav=" + headNavLen
                + " headNavLane=" + m_RetireHost.DescribeEntity(headLastNavLane)
                + " headNavLast=" + headLastNavFlags
                + " headPath=" + headPathLen
                + " headPathFlags=" + headPathFlags
                + " headPiDest=" + m_RetireHost.DescribeEntity(headPathInfoDest)
                + " headPiState=" + headPathInfoState
                + " headFront=" + headFrontFlags
                + " headFrontLane=" + m_RetireHost.DescribeEntity(headFrontLane)
                + " headRearLane=" + m_RetireHost.DescribeEntity(headRearLane);
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

        private static bool HasFlagText(string text, string flag)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf(flag, StringComparison.Ordinal) >= 0;
        }
    }
}
