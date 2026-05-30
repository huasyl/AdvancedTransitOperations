using System;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal readonly struct SceneKey : IEquatable<SceneKey>
    {
        public readonly Entity Line;
        public readonly Entity CurrentBypassBuilding;
        public readonly Entity NextBypassBuilding;
        public readonly int ProtectedIntervalIndex;

        public SceneKey(
            Entity line,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            int protectedIntervalIndex)
        {
            Line = line;
            CurrentBypassBuilding = currentBypassBuilding;
            NextBypassBuilding = nextBypassBuilding;
            ProtectedIntervalIndex = protectedIntervalIndex;
        }

        public bool Equals(SceneKey other)
        {
            return Line == other.Line
                && CurrentBypassBuilding == other.CurrentBypassBuilding
                && NextBypassBuilding == other.NextBypassBuilding
                && ProtectedIntervalIndex == other.ProtectedIntervalIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is SceneKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Line.GetHashCode();
                hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                hash = (hash * 397) ^ NextBypassBuilding.GetHashCode();
                hash = (hash * 397) ^ ProtectedIntervalIndex;
                return hash;
            }
        }
    }

    internal readonly struct SceneDefinition
    {
        public readonly SceneKey Key;
        public readonly Entity Line;
        public readonly int WaypointIndex;
        public readonly Entity CurrentBypassBuilding;
        public readonly Entity NextBypassBuilding;
        public readonly int ProtectedIntervalIndex;
        public readonly BypassProtectedInterval ProtectedInterval;
        public readonly ProtectedIntervalSummary Summary;
        public readonly float DepartureReleaseCoordinate;
        public readonly float IntervalDisplayLength;

        public SceneDefinition(
            SceneKey key,
            Entity line,
            int waypointIndex,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            ProtectedIntervalSummary summary,
            float departureReleaseCoordinate,
            float intervalDisplayLength)
        {
            Key = key;
            Line = line;
            WaypointIndex = waypointIndex;
            CurrentBypassBuilding = currentBypassBuilding;
            NextBypassBuilding = nextBypassBuilding;
            ProtectedIntervalIndex = protectedIntervalIndex;
            ProtectedInterval = protectedInterval;
            Summary = summary;
            DepartureReleaseCoordinate = departureReleaseCoordinate;
            IntervalDisplayLength = intervalDisplayLength;
        }
    }

    internal readonly struct VehicleSceneBinding
    {
        public readonly Entity Vehicle;
        public readonly SceneKey SceneKey;
        public readonly int WaypointIndex;

        public VehicleSceneBinding(Entity vehicle, SceneKey sceneKey, int waypointIndex)
        {
            Vehicle = vehicle;
            SceneKey = sceneKey;
            WaypointIndex = waypointIndex;
        }
    }

    internal readonly struct LocalBypassWaypointSceneBinding
    {
        public readonly bool Available;
        public readonly SceneKey SceneKey;
        public readonly Entity CurrentBypassBuilding;
        public readonly Entity NextBypassBuilding;
        public readonly int ProtectedIntervalIndex;
        public readonly BypassProtectedInterval ProtectedInterval;
        public readonly ProtectedIntervalSummary Summary;
        public readonly float DepartureReleaseCoordinate;
        public readonly float IntervalDisplayLength;

        public LocalBypassWaypointSceneBinding(
            bool available,
            SceneKey sceneKey,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            ProtectedIntervalSummary summary,
            float departureReleaseCoordinate,
            float intervalDisplayLength)
        {
            Available = available;
            SceneKey = sceneKey;
            CurrentBypassBuilding = currentBypassBuilding;
            NextBypassBuilding = nextBypassBuilding;
            ProtectedIntervalIndex = protectedIntervalIndex;
            ProtectedInterval = protectedInterval;
            Summary = summary;
            DepartureReleaseCoordinate = departureReleaseCoordinate;
            IntervalDisplayLength = intervalDisplayLength;
        }
    }
}
