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
            float2 targetDelta,
            float curveLength,
            PathElementFlags pathFlags,
            TrackLaneFlags trackFlags,
            ConnectionLaneFlags connectionFlags,
            float speedLimit,
            float curviness)
        {
            LaneEntity = laneEntity;
            Kind = kind;
            TargetDelta = targetDelta;
            CurveLength = math.max(0f, curveLength);
            PathFlags = pathFlags;
            TrackFlags = trackFlags;
            ConnectionFlags = connectionFlags;
            SpeedLimit = math.max(0f, speedLimit);
            Curviness = math.max(0f, curviness);
            Length = CurveLength * math.abs(TargetDelta.y - TargetDelta.x);
        }

        public Entity LaneEntity { get; }
        public SegmentKind Kind { get; }
        public float2 TargetDelta { get; }
        public float CurveLength { get; }
        public float Length { get; }
        public PathElementFlags PathFlags { get; }
        public TrackLaneFlags TrackFlags { get; }
        public ConnectionLaneFlags ConnectionFlags { get; }
        public float SpeedLimit { get; }
        public float Curviness { get; }

        public bool IsTrackLane => Kind == SegmentKind.TrackLane;
        public bool IsConnectionLane => Kind == SegmentKind.ConnectionLane;
    }

    internal sealed class Path
    {
        public Path(
            Entity sourceEntity,
            Segment[] segments,
            int sourceElementCount = 0,
            int skippedElementCount = 0)
        {
            SourceEntity = sourceEntity;
            Segments = segments ?? Array.Empty<Segment>();
            SourceElementCount = math.max(0, sourceElementCount);
            SkippedElementCount = math.max(0, skippedElementCount);

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
        public int ConnectionSegmentCount { get; }
        public float TotalLength { get; }

        public bool IsEmpty => Segments.Length == 0 || TotalLength <= 0f;
    }
}
