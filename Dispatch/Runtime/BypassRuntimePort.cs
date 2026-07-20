using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using RapidTransitMod.Core;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class BypassRuntimePort : BypassAdmissionPort, IRuntimeContext
    {
        private readonly Func<bool> m_IsBypassRuntimeLoggingEnabled;
        private readonly Action<Entity, Entity, Entity, int, uint, string> m_RecordHold;
        private readonly Action<Entity, Entity, uint, string> m_RecordRelease;
        private readonly Action<Entity, Entity, DynamicBuffer<RouteWaypoint>, int> m_TriggerWaiting;
        private readonly Func<bool> m_RuntimeEnabled;
        private readonly Action m_ClearLineTimeProfiles;

        internal BypassRuntimePort(
            EntityManager entityManager,
            TimedLogger log,
            Func<uint> frame,
            Func<ClockSnapshot> clockSnapshot,
            Func<IEnumerable<KeyValuePair<string, AppliedLine>>> appliedLines,
            TrackModelService trackModel,
            Func<TrackProjectionService> trackProjection,
            TrackModelContext.IBuffers buffers,
            Func<bool> bypassFeatureEnabled,
            Func<Entity, bool> isManaged,
            Func<Entity, bool> isLocal,
            Func<Entity, bool> isExpress,
            RuntimeResolve resolve,
            Func<bool> isLineOrderedRuntimeLoggingEnabled,
            WaypointIndex waypointIndex,
            ObservationPort observation,
            SharedCorridorSupport shared,
            Action<Dictionary<Entity, string>, Entity, string, string> logVehicleStateOnce,
            VehicleView vehicleView,
            LineMileage lineMileage,
            LineTimes lineTimes,
            Func<Entity, string> entityName,
            RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe hotPathProbe,
            Func<bool> isBypassRuntimeLoggingEnabled,
            Action<Entity, Entity, Entity, int, uint, string> recordHold,
            Action<Entity, Entity, uint, string> recordRelease,
            Action<Entity, Entity, DynamicBuffer<RouteWaypoint>, int> triggerWaiting,
            Func<bool> runtimeEnabled,
            Action clearLineTimeProfiles)
            : base(
                entityManager,
                log,
                frame,
                clockSnapshot,
                appliedLines,
                trackModel,
                trackProjection,
                buffers,
                bypassFeatureEnabled,
                isManaged,
                isLocal,
                isExpress,
                resolve,
                isLineOrderedRuntimeLoggingEnabled,
                waypointIndex,
                observation,
                shared,
                logVehicleStateOnce,
                vehicleView,
                lineMileage,
                lineTimes,
                entityName,
                hotPathProbe)
        {
            m_IsBypassRuntimeLoggingEnabled = isBypassRuntimeLoggingEnabled;
            m_RecordHold = recordHold;
            m_RecordRelease = recordRelease;
            m_TriggerWaiting = triggerWaiting;
            m_RuntimeEnabled = runtimeEnabled;
            m_ClearLineTimeProfiles = clearLineTimeProfiles;
        }

        uint IControlContext.Frame => FrameGetter();
        bool IControlContext.IsBypassRuntimeLoggingEnabled() => m_IsBypassRuntimeLoggingEnabled();
        void IControlContext.LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message) => LogVehicleStateOnceAction(cache, vehicle, key, message);
        Entity IControlContext.ResolveStation(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => waypointIndex >= 0 && waypointIndex < waypoints.Length ? Resolve.Stop(waypoints[waypointIndex].m_Waypoint) : Entity.Null;
        void IControlContext.RecordHold(Entity vehicle, Entity blocker, string lineTag, Entity holdStation, int waypointIndex, string stateTag) => m_RecordHold(vehicle, blocker, holdStation, waypointIndex, FrameGetter(), stateTag);
        void IControlContext.RecordRelease(Entity vehicle, Entity blocker, string reason) => m_RecordRelease(vehicle, blocker, FrameGetter(), reason);
        void IControlContext.TriggerWaiting(Entity vehicle, Entity route, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_TriggerWaiting(vehicle, route, waypoints, waypointIndex);
        bool IRuntimeContext.RuntimeEnabled() => m_RuntimeEnabled();
        void IRuntimeContext.ClearLineTimeProfiles() => m_ClearLineTimeProfiles();
    }
}
