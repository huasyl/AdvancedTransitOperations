using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal sealed class BypassLineDistanceModel
    {
        public ulong Signature;
        public float TotalDistanceMeters;
        public float[] WaypointDistances = System.Array.Empty<float>();
        public float[] BypassWaypointDistances = System.Array.Empty<float>();
        public float[] BypassStopNodeDistances = System.Array.Empty<float>();
        public List<BypassCorridorNode> CorridorNodes = new List<BypassCorridorNode>();
        public Dictionary<Entity, float> BuildingDistances = new Dictionary<Entity, float>();
    }

    internal struct BypassCorridorNode
    {
        public Entity Building;
        public float DistanceMeters;
        public bool IsStopNode;
    }

    internal struct BypassLineDistanceProjection
    {
        public float TotalDistanceMeters;
        public float DistanceMeters;
        public float Progress01;
        public int NextWaypointIndex;
        public float SegmentPosition;
    }
}
