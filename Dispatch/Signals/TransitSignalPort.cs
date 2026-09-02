using System;
using Game.Routes;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.TrackProjection;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Signals
{
    internal delegate bool TrySignalVehicle(
        Entity vehicle,
        out Entity line,
        out TransitMode mode,
        out VehicleState state);

    internal delegate bool TrySignalStop(
        Entity vehicle,
        out bool hasSession,
        out bool departurePending,
        out int waypoint);

    internal delegate bool TrySignalTramPosition(
        Entity vehicle,
        Entity line,
        out SignalLineModel signalLine,
        out TramSignalPosition position,
        out TramSignalPositionDiagnostic diagnostic);

    internal enum TramSignalPositionFailure : byte
    {
        None = 0,
        RouteWaypointsUnavailable = 1,
        StableLineInputsUnavailable = 2,
        LineMileageUnavailable = 3,
        SignalLineModelUnavailable = 4,
        ProjectionUnavailable = 5,
    }

    internal readonly struct TramSignalPositionDiagnostic
    {
        internal readonly TramSignalPositionFailure Failure;
        internal readonly CurrentLanePositionDiagnostic Projection;

        internal TramSignalPositionDiagnostic(
            TramSignalPositionFailure failure,
            CurrentLanePositionDiagnostic projection)
        {
            Failure = failure;
            Projection = projection;
        }
    }

    internal delegate bool TrySignalTramNavigation(
        Entity vehicle,
        Entity lane0,
        Entity lane1,
        Entity lane2,
        out byte forwardMask);

    internal sealed class TransitSignalPort
    {
        internal TrySignalVehicle TryVehicle;
        internal TrySignalStop TryStop;
        internal Func<Entity, bool> LineEnabled;
        internal Func<Entity, int, Entity> Waypoint;
        internal Func<int> RailSourceCount;
        internal Func<int> RoadSourceCount;
        internal Func<int, uint, (bool Success, ManagedSourceVehicle Source)> RailSource;
        internal Func<int, uint, (bool Success, ManagedSourceVehicle Source)> RoadSource;
        internal Func<
            Entity,
            System.Collections.Generic.List<RoadEventSource.RoadNavigationSlice>,
            RoadEventSource.RoadNavigationResult> RoadNavigation;
        internal TrySignalTramPosition TryTramPosition;
        internal TrySignalTramNavigation TryTramNavigation;
        internal Func<System.Collections.Generic.IReadOnlyList<DeparturePendingEvent>> PendingFacts;
        internal Func<System.Collections.Generic.IReadOnlyList<DispatchEvent>> DispatchFacts;
        internal Func<System.Collections.Generic.IReadOnlyList<StopEvent>> StopEvents;
        internal Func<System.Collections.Generic.IReadOnlyList<LifecycleEvent>> LifecycleFacts;
        internal Func<System.Collections.Generic.IReadOnlyList<FrameEventRef>> FactsBySequence;
        internal SignalLanePort Signals;
        internal Action<string> Log;
    }
}
