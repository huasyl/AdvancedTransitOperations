using System;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Diagnostics;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal delegate void CaptureRetireSpawnTargetDelegate(
        Entity line,
        out int preActive,
        out bool hadSpawnTarget,
        out int oldSpawnTarget);

    internal sealed class RetireHost
    {
        private readonly EntityManager m_EntityManager;
        private readonly TimedLogger m_Log;
        private readonly Func<uint> m_Frame;
        private readonly Func<Entity, Entity> m_ResolveVehicle;
        private readonly Func<Entity, Entity> m_CanonDepot;
        private readonly EntityQuery m_LineQuery;
        private readonly Func<bool, BufferLookup<RouteVehicle>> m_RouteVehicles;
        private readonly VehicleView m_VehicleView;
        private readonly VehicleRegistry m_VehicleRegistry;
        private readonly VehicleStateStore.MapRef<VehicleState> m_VehicleStates;
        private readonly SpawnIntentTrace m_SpawnIntentTrace;
        private readonly Action<Entity> m_RetireRuntime;
        private readonly CaptureRetireSpawnTargetDelegate m_CaptureRetireSpawnTarget;
        private readonly Action<Entity, int, bool, int> m_ApplyRetireSpawnTarget;
        private readonly Action<Entity> m_ClearAssistLaunchPending;
        private readonly Action<Entity> m_ClearForcedMidStopClosingConsist;
        private readonly Action<Entity> m_RemoveAnnouncementVehicle;
        private readonly RuntimeVehicleLabels m_VehicleLabels;
        private readonly Action<Entity> m_ClearVehicleLabel;
        private readonly Action<Entity> m_ClearLap;
        private readonly Action<Entity> m_ClearDwell;
        private readonly Action<Entity> m_ClearDwellDeadlineCache;
        private readonly Action<Entity> m_ClearTrackProjectionVehicle;
        private readonly Action<Entity, string> m_ClearTrackProjectionVehicleProgressSuspect;
        private readonly Action<Entity, string> m_ClearBypassVehicle;
        private readonly NativeHashMap<Entity, FixedString64Bytes> m_UICache;
        private readonly NativeHashMap<Entity, byte> m_LastEffectiveBoardingState;
        private readonly NativeHashMap<Entity, byte> m_LastOfficialBoardingState;
        private readonly NativeHashMap<Entity, Entity> m_StopSessionLine;
        private readonly NativeHashMap<Entity, int> m_StopSessionWaypointIndex;
        private readonly NativeHashMap<Entity, uint> m_StopSessionArrivalFrame;
        private readonly NativeHashMap<Entity, uint> m_StopSessionBoardingChangeCount;
        private readonly NativeHashMap<Entity, uint> m_DeparturePendingSinceFrame;
        private readonly NativeHashMap<Entity, int> m_CachedWaypoint;
        private readonly NativeHashSet<Entity> m_InvalidatedMidStopRecoveryPending;
        private readonly NativeHashSet<Entity> m_Misfire;
        private readonly NativeHashMap<Entity, uint> m_MisfireStartFrame;
        private readonly NativeHashMap<Entity, uint> m_PreparingFixCooldownUntil;

        public RetireHost(DispatchRuntimeSystem runtime)
        {
            m_EntityManager = runtime.EntityManager;
            m_Log = runtime.log;
            m_Frame = () => runtime.m_SimulationSystem.frameIndex;
            m_ResolveVehicle = runtime.m_Resolve.RuntimeVehicle;
            m_CanonDepot = runtime.CanonDepot;
            m_LineQuery = runtime.m_LineQuery;
            m_RouteVehicles = readOnly => runtime.GetBufferLookup<RouteVehicle>(readOnly);
            m_VehicleView = runtime.m_VehicleView;
            m_VehicleRegistry = runtime.m_VehicleRegistry;
            m_VehicleStates = runtime.m_VehicleStateStore.State;
            m_SpawnIntentTrace = runtime.m_SpawnIntentTrace;
            m_RetireRuntime = runtime.m_RuntimeController.Retire;
            m_CaptureRetireSpawnTarget = runtime.m_RuntimeController.CaptureRetireSpawnTarget;
            m_ApplyRetireSpawnTarget = runtime.m_RuntimeController.ApplyRetireSpawnTarget;
            m_ClearAssistLaunchPending = runtime.m_RuntimeController.ClearAssistLaunchPending;
            m_ClearForcedMidStopClosingConsist = runtime.m_Observation.ClearForcedMidStop;
            m_RemoveAnnouncementVehicle = runtime.m_Announcements.RemoveVehicle;
            m_VehicleLabels = runtime.m_VehicleLabels;
            m_ClearVehicleLabel = runtime.m_VehicleLabels.Remove;
            m_ClearLap = runtime.m_ObsPersist.ClearLap;
            m_ClearDwell = runtime.m_ObsPersist.ClearDwell;
            m_ClearDwellDeadlineCache = runtime.m_Observation.ClearDwellDeadlineCache;
            m_ClearTrackProjectionVehicle = runtime.TrackProjection.ClearVehicle;
            m_ClearTrackProjectionVehicleProgressSuspect = runtime.TrackProjection.ClearVehicleProgressSuspect;
            m_ClearBypassVehicle = runtime.Bypass.ClearVehicle;
            m_UICache = runtime.m_UICache;
            m_LastEffectiveBoardingState = runtime.m_LastEffectiveBoardingState;
            m_LastOfficialBoardingState = runtime.m_LastOfficialBoardingState;
            m_StopSessionLine = runtime.m_StopSessionLine;
            m_StopSessionWaypointIndex = runtime.m_StopSessionWaypointIndex;
            m_StopSessionArrivalFrame = runtime.m_StopSessionArrivalFrame;
            m_StopSessionBoardingChangeCount = runtime.m_StopSessionBoardingChangeCount;
            m_DeparturePendingSinceFrame = runtime.m_DeparturePendingSinceFrame;
            m_CachedWaypoint = runtime.m_CachedWpIdx;
            m_InvalidatedMidStopRecoveryPending = runtime.m_InvalidatedMidStopRecoveryPending;
            m_Misfire = runtime.m_BVMisfire;
            m_MisfireStartFrame = runtime.m_BVMisfireStartFrame;
            m_PreparingFixCooldownUntil = runtime.m_PreparingFixCooldownUntil;
        }

        public string RetireIntent(Entity vehicle) => m_SpawnIntentTrace.Retire(vehicle, Frame);

        public EntityManager EntityManager => m_EntityManager;
        public TimedLogger Log => m_Log;
        public uint Frame => m_Frame();

        public Entity ResolveVehicle(Entity vehicle)
        {
            return m_ResolveVehicle(vehicle);
        }

        public bool TryVehicleState(Entity vehicle, out VehicleState state)
        {
            return m_VehicleView.TryGetState(vehicle, out state);
        }

        public bool TryVehicleLine(Entity vehicle, out Entity line)
        {
            return m_VehicleView.TryGetLine(vehicle, out line);
        }

        public NativeArray<Entity> LineEntities(Allocator allocator)
        {
            return m_LineQuery.ToEntityArray(allocator);
        }

        public BufferLookup<RouteVehicle> RouteVehicles(bool readOnly)
        {
            return m_RouteVehicles(readOnly);
        }

        public void RetireRuntimeVehicle(Entity vehicle)
        {
            m_RetireRuntime(vehicle);
        }

        public void CaptureRetireSpawnTarget(
            Entity line,
            out int preActive,
            out bool hadSpawnTarget,
            out int oldSpawnTarget)
        {
            m_CaptureRetireSpawnTarget(
                line,
                out preActive,
                out hadSpawnTarget,
                out oldSpawnTarget);
        }

        public void ApplyRetireSpawnTarget(
            Entity line,
            int preActive,
            bool hadSpawnTarget,
            int oldSpawnTarget)
        {
            m_ApplyRetireSpawnTarget(
                line,
                preActive,
                hadSpawnTarget,
                oldSpawnTarget);
        }

        public void ClearRetireRequestState(Entity vehicle)
        {
            m_Misfire.Remove(vehicle);
            m_MisfireStartFrame.Remove(vehicle);
            ClearStopSessionState(vehicle);
            m_PreparingFixCooldownUntil.Remove(vehicle);
            m_ClearAssistLaunchPending(vehicle);
            m_RemoveAnnouncementVehicle(vehicle);
        }

        private void ClearStopSessionState(Entity vehicle)
        {
            m_LastEffectiveBoardingState.Remove(vehicle);
            m_LastOfficialBoardingState.Remove(vehicle);
            m_StopSessionLine.Remove(vehicle);
            m_StopSessionWaypointIndex.Remove(vehicle);
            m_StopSessionArrivalFrame.Remove(vehicle);
            m_StopSessionBoardingChangeCount.Remove(vehicle);
            m_DeparturePendingSinceFrame.Remove(vehicle);
            m_InvalidatedMidStopRecoveryPending.Remove(vehicle);
            m_ClearForcedMidStopClosingConsist(vehicle);
            m_ClearDwellDeadlineCache(vehicle);
            m_ClearDwell(vehicle);
        }

        public void SetRetireLabel(Entity vehicle, string reason)
        {
            m_VehicleLabels.SetLocalized(vehicle, "Returning", "回库中", reason.Length > 0 ? "(" + reason + ")" : "");
        }

        public void ProjectRetireDispatchLock(Entity vehicle, out int clearedDispatchCount)
        {
            if (!m_EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                m_EntityManager.AddComponent<RtRetireDispatchLock>(vehicle);

            if (m_EntityManager.HasComponent<PublicTransport>(vehicle))
            {
                PublicTransport publicTransport = m_EntityManager.GetComponentData<PublicTransport>(vehicle);
                if (publicTransport.m_RequestCount != 1)
                {
                    publicTransport.m_RequestCount = 1;
                    m_EntityManager.SetComponentData(vehicle, publicTransport);
                }
            }
            ClearServiceDispatch(vehicle, out clearedDispatchCount);
        }

        public bool HasRetireDispatchLock(Entity vehicle)
        {
            return vehicle != Entity.Null
                && m_EntityManager.Exists(vehicle)
                && m_EntityManager.HasComponent<RtRetireDispatchLock>(vehicle);
        }

        public void ClearServiceDispatch(Entity vehicle, out int count)
        {
            count = 0;
            if (!m_EntityManager.HasBuffer<ServiceDispatch>(vehicle))
                return;

            DynamicBuffer<ServiceDispatch> dispatchBuffer = m_EntityManager.GetBuffer<ServiceDispatch>(vehicle);
            count = dispatchBuffer.Length;
            if (dispatchBuffer.Length > 0)
                dispatchBuffer.Clear();
        }

        public void SetPublicTransport(Entity vehicle, PublicTransport value)
        {
            m_EntityManager.SetComponentData(vehicle, value);
        }

        public bool HasConsumedPath(Entity entity)
        {
            if (entity == Entity.Null
                || !m_EntityManager.Exists(entity)
                || !HasNoTrainNavigation(entity)
                || !m_EntityManager.HasBuffer<PathElement>(entity)
                || !m_EntityManager.HasComponent<PathOwner>(entity))
            {
                return false;
            }

            DynamicBuffer<PathElement> path = m_EntityManager.GetBuffer<PathElement>(entity, true);
            PathOwner pathOwner = m_EntityManager.GetComponentData<PathOwner>(entity);
            return path.Length >= 0 && pathOwner.m_ElementIndex >= path.Length;
        }

        public bool HasNoTrainNavigation(Entity entity)
        {
            return entity != Entity.Null
                && m_EntityManager.Exists(entity)
                && m_EntityManager.HasBuffer<TrainNavigationLane>(entity)
                && m_EntityManager.GetBuffer<TrainNavigationLane>(entity, true).Length == 0;
        }

        public bool HasTrainLaneFlags(Entity entity, TrainLaneFlags flags)
        {
            return entity != Entity.Null
                && m_EntityManager.Exists(entity)
                && m_EntityManager.HasComponent<TrainCurrentLane>(entity)
                && (m_EntityManager.GetComponentData<TrainCurrentLane>(entity).m_Front.m_LaneFlags & flags) == flags;
        }

        public Entity ResolveHandoffHead(Entity vehicle)
        {
            if (vehicle == Entity.Null || !m_EntityManager.Exists(vehicle) || !m_EntityManager.HasBuffer<LayoutElement>(vehicle))
                return vehicle;

            DynamicBuffer<LayoutElement> layout = m_EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            return layout.Length == 0 ? vehicle : layout[0].m_Vehicle;
        }

        public bool IsDepotTarget(Entity entity, Entity ownerDepot)
        {
            if (entity == Entity.Null || ownerDepot == Entity.Null || !m_EntityManager.Exists(entity))
                return false;
            if (entity == ownerDepot)
                return true;

            return m_CanonDepot(entity) == ownerDepot;
        }

        public bool IsRouteWaypointTarget(Entity vehicle, Entity target)
        {
            if (target == Entity.Null || !m_EntityManager.Exists(target) || !m_EntityManager.HasComponent<Waypoint>(target))
                return false;
            if (!m_EntityManager.HasComponent<CurrentRoute>(vehicle))
                return true;

            Entity route = m_EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            if (route == Entity.Null || !m_EntityManager.Exists(route) || !m_EntityManager.HasBuffer<RouteWaypoint>(route))
                return true;

            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(route, true);
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i].m_Waypoint == target)
                    return true;
            }

            return false;
        }

        public int GetRouteWaypointIndex(Entity vehicle, Entity target)
        {
            if (target == Entity.Null
                || !m_EntityManager.Exists(target)
                || !m_EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                return -1;
            }

            Entity route = m_EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            if (route == Entity.Null
                || !m_EntityManager.Exists(route)
                || !m_EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                return -1;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(route, true);
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i].m_Waypoint == target)
                    return i;
            }

            return -1;
        }

        public bool HasDepotPathTarget(Entity entity, Entity ownerDepot)
        {
            return entity != Entity.Null
                && m_EntityManager.Exists(entity)
                && ownerDepot != Entity.Null
                && m_EntityManager.HasComponent<PathInformation>(entity)
                && IsDepotTarget(m_EntityManager.GetComponentData<PathInformation>(entity).m_Destination, ownerDepot);
        }

        public bool HasParkingNavLane(Entity entity)
        {
            if (entity == Entity.Null || !m_EntityManager.Exists(entity) || !m_EntityManager.HasBuffer<TrainNavigationLane>(entity))
                return false;

            DynamicBuffer<TrainNavigationLane> lanes = m_EntityManager.GetBuffer<TrainNavigationLane>(entity, true);
            return lanes.Length > 0 && (lanes[lanes.Length - 1].m_Flags & TrainLaneFlags.ParkingSpace) != 0;
        }

        public string DescribeEntity(Entity entity)
        {
            return entity == Entity.Null ? "-" : entity.Index.ToString();
        }

        public string DescribeTargetKind(Entity entity)
        {
            if (entity == Entity.Null || !m_EntityManager.Exists(entity))
                return "-";
            if (m_EntityManager.HasComponent<Game.Buildings.TransportDepot>(entity))
                return "depot";
            if (m_EntityManager.HasComponent<Waypoint>(entity))
                return "waypoint";
            if (m_EntityManager.HasComponent<SpawnLocation>(entity))
                return "spawn";
            if (m_EntityManager.HasComponent<Connected>(entity))
                return "connected";

            return "other";
        }

        public void ReleaseRetireRuntimeOwnership(Entity vehicle, string reason)
        {
            m_RemoveAnnouncementVehicle(vehicle);
            m_VehicleRegistry.Remove(vehicle);
            m_ClearLap(vehicle);
            m_CachedWaypoint.Remove(vehicle);
            ClearStopSessionState(vehicle);
            m_ClearTrackProjectionVehicle(vehicle);
            m_UICache.Remove(vehicle);
            m_ClearVehicleLabel(vehicle);
            m_PreparingFixCooldownUntil.Remove(vehicle);
            m_Misfire.Remove(vehicle);
            m_MisfireStartFrame.Remove(vehicle);
            m_ClearBypassVehicle(vehicle, reason);
            m_ClearTrackProjectionVehicleProgressSuspect(vehicle, reason);
        }
    }
}
