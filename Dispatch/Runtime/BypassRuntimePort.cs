using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using RapidTransitMod.Core;
using RapidTransitMod.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class BypassRuntimePort : BypassAdmissionPort, IRuntimeContext
    {
        private readonly Func<bool> m_IsBypassRuntimeLoggingEnabled;
        private readonly Action<Entity, Entity, Entity, int, uint, string> m_RecordHold;
        private readonly Action<Entity, Entity, uint, string> m_RecordRelease;
        private readonly Action<BypassFact> m_RecordBypassFact;
        private readonly Action<Entity, Entity, DynamicBuffer<RouteWaypoint>, int> m_TriggerWaiting;
        private readonly Action<Entity, Game.Vehicles.PublicTransport> m_RecordPublicTransportWrite;
        private readonly Func<bool> m_RuntimeEnabled;
        private readonly Action<Entity, DeadlineKind, uint> m_SetDeadline;
        private readonly Action<Entity, DeadlineKind> m_ClearDeadline;
        private readonly Action<DeadlineKind> m_ClearDeadlines;
        private readonly Action<Entity, bool> m_SetBypassActive;
        private readonly Action m_ClearBypassActive;
        private readonly Action<Entity, bool> m_SetBypassWatch;
        private readonly Action m_ClearBypassWatch;

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
            Action<BypassFact> recordBypassFact,
            Action<Entity, Entity, DynamicBuffer<RouteWaypoint>, int> triggerWaiting,
            Action<Entity, Game.Vehicles.PublicTransport> recordPublicTransportWrite,
            Func<bool> runtimeEnabled,
            Action<Entity, DeadlineKind, uint> setDeadline,
            Action<Entity, DeadlineKind> clearDeadline,
            Action<DeadlineKind> clearDeadlines,
            Action<Entity, bool> setBypassActive,
            Action clearBypassActive,
            Action<Entity, bool> setBypassWatch,
            Action clearBypassWatch)
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
            m_RecordBypassFact = recordBypassFact;
            m_TriggerWaiting = triggerWaiting;
            m_RecordPublicTransportWrite = recordPublicTransportWrite;
            m_RuntimeEnabled = runtimeEnabled;
            m_SetDeadline = setDeadline;
            m_ClearDeadline = clearDeadline;
            m_ClearDeadlines = clearDeadlines;
            m_SetBypassActive = setBypassActive;
            m_ClearBypassActive = clearBypassActive;
            m_SetBypassWatch = setBypassWatch;
            m_ClearBypassWatch = clearBypassWatch;
        }

        uint IControlContext.Frame => FrameGetter();
        bool IControlContext.IsBypassRuntimeLoggingEnabled() => m_IsBypassRuntimeLoggingEnabled();
        void IControlContext.LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message) => LogVehicleStateOnceAction(cache, vehicle, key, message);
        Entity IControlContext.ResolveStation(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => waypointIndex >= 0 && waypointIndex < waypoints.Length ? Resolve.Stop(waypoints[waypointIndex].m_Waypoint) : Entity.Null;
        void IControlContext.RecordHold(Entity vehicle, Entity blocker, string lineTag, Entity holdStation, int waypointIndex, string stateTag) => m_RecordHold(vehicle, blocker, holdStation, waypointIndex, FrameGetter(), stateTag);
        void IControlContext.RecordRelease(Entity vehicle, Entity blocker, string reason) => m_RecordRelease(vehicle, blocker, FrameGetter(), reason);
        void IControlContext.RecordBypassFact(BypassFact fact) => m_RecordBypassFact(fact);
        void IControlContext.TriggerWaiting(Entity vehicle, Entity route, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_TriggerWaiting(vehicle, route, waypoints, waypointIndex);
        void IControlContext.RecordPublicTransportWrite(Entity vehicle, Game.Vehicles.PublicTransport publicTransport) => m_RecordPublicTransportWrite(vehicle, publicTransport);
        bool IRuntimeContext.RuntimeEnabled() => m_RuntimeEnabled();
        public override void SetRuntimeDeadline(Entity vehicle, DeadlineKind kind, uint frame) => m_SetDeadline(vehicle, kind, frame);
        public override void ClearRuntimeDeadline(Entity vehicle, DeadlineKind kind) => m_ClearDeadline(vehicle, kind);
        public override void ClearRuntimeDeadlines(DeadlineKind kind) => m_ClearDeadlines(kind);
        public override void SetRuntimeBypassActive(Entity vehicle, bool active) => m_SetBypassActive(vehicle, active);
        public override void ClearRuntimeBypassActive() => m_ClearBypassActive();
        public override void SetRuntimeBypassWatch(Entity vehicle, bool active) => m_SetBypassWatch(vehicle, active);
        public override void ClearRuntimeBypassWatch() => m_ClearBypassWatch();
    }
}
