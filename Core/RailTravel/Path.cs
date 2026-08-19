using System;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.RailTravel
{
    internal enum SegmentKind
    {
        TrackLane = 0,
        ConnectionLane = 1
    }

    internal readonly struct Segment
    {
        public Segment(
            Entity laneEntity,
            SegmentKind kind,
            int pathElementIndex,
            float2 targetDelta,
            Curve curve,
            PathElementFlags pathFlags,
            TrackLaneFlags trackFlags,
            ConnectionLaneFlags connectionFlags,
            float speedLimit,
            float curviness,
            Entity accessRestriction,
            TrackTypes connectionTrackTypes,
            RoadTypes connectionRoadTypes,
            float edgeDeltaStart,
            float edgeDeltaEnd,
            int edgeConnectedStartCount,
            int edgeConnectedEndCount)
        {
            LaneEntity = laneEntity;
            Kind = kind;
            PathElementIndex = pathElementIndex;
            TargetDelta = targetDelta;
            Curve = curve;
            CurveLength = math.max(0f, curve.m_Length);
            PathFlags = pathFlags;
            TrackFlags = trackFlags;
            ConnectionFlags = connectionFlags;
            SpeedLimit = math.max(0f, speedLimit);
            Curviness = math.max(0f, curviness);
            AccessRestriction = accessRestriction;
            ConnectionTrackTypes = connectionTrackTypes;
            ConnectionRoadTypes = connectionRoadTypes;
            EdgeDeltaStart = edgeDeltaStart;
            EdgeDeltaEnd = edgeDeltaEnd;
            EdgeConnectedStartCount = edgeConnectedStartCount;
            EdgeConnectedEndCount = edgeConnectedEndCount;
            Length = CurveLength * math.abs(TargetDelta.y - TargetDelta.x);
        }

        public Entity LaneEntity { get; }
        public SegmentKind Kind { get; }
        public int PathElementIndex { get; }
        public float2 TargetDelta { get; }
        public Curve Curve { get; }
        public float CurveLength { get; }
        public float Length { get; }
        public PathElementFlags PathFlags { get; }
        public TrackLaneFlags TrackFlags { get; }
        public ConnectionLaneFlags ConnectionFlags { get; }
        public float SpeedLimit { get; }
        public float Curviness { get; }
        public Entity AccessRestriction { get; }
        public TrackTypes ConnectionTrackTypes { get; }
        public RoadTypes ConnectionRoadTypes { get; }
        public float EdgeDeltaStart { get; }
        public float EdgeDeltaEnd { get; }
        public int EdgeConnectedStartCount { get; }
        public int EdgeConnectedEndCount { get; }

        public bool IsTrackLane => Kind == SegmentKind.TrackLane;
        public bool IsConnectionLane => Kind == SegmentKind.ConnectionLane;
    }

    internal sealed class Path
    {
        public Path(
            Entity sourceEntity,
            Segment[] segments,
            int sourceElementCount = 0,
            int skippedElementCount = 0,
            ulong sourceSignature = 0)
        {
            SourceEntity = sourceEntity;
            Segments = segments ?? Array.Empty<Segment>();
            SourceElementCount = math.max(0, sourceElementCount);
            SkippedElementCount = math.max(0, skippedElementCount);
            SourceSignature = sourceSignature;

            float totalLength = 0f;
            int connectionSegmentCount = 0;
            for (int i = 0; i < Segments.Length; i++)
            {
                Segment segment = Segments[i];
                totalLength += segment.Length;
                if (segment.IsConnectionLane)
                    connectionSegmentCount++;
            }

            TotalLength = totalLength;
            ConnectionSegmentCount = connectionSegmentCount;
        }

        public Entity SourceEntity { get; }
        public Segment[] Segments { get; }
        public int SourceElementCount { get; }
        public int SkippedElementCount { get; }
        public ulong SourceSignature { get; }
        public int ConnectionSegmentCount { get; }
        public float TotalLength { get; }

        public bool IsEmpty => Segments.Length == 0 || TotalLength <= 0f;
    }
}
