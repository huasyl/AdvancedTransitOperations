using Colossal.Mathematics;
using Game.Prefabs;
using System.Threading;
using Unity.Mathematics;

namespace RapidTransitMod.RailTravel
{
    internal sealed class Calculator
    {
        internal const float TimeStep = 4f / 15f;
        internal const float ConnectionSpeed = 277.77777f;

        private const float CompletionEpsilon = 0.01f;
        private const float MinSpeedEpsilon = 0.001f;

        public Result Calculate(Request request) => Calculate(request, CancellationToken.None);

        public Result Calculate(Request request, CancellationToken cancellationToken)
        {
            Result result = CreateResult(request);
            if (request == null)
            {
                result.Error = "rail-travel-request-missing";
                return result;
            }

            if (request.Path == null || request.Path.IsEmpty)
            {
                result.Error = "rail-travel-path-missing";
                return result;
            }

            if (request.MaxTicks <= 0)
            {
                result.Error = "rail-travel-max-ticks-invalid";
                return result;
            }

            TrainData effectiveTrain = BuildEffectiveTrain(request.LeadUnit, request.CoupledUnits);
            result.Diagnostics.EffectiveTrain = effectiveTrain;
            float trainLength = CalculateTrainLength(request.LeadUnit, request.CoupledUnits);
            result.Diagnostics.TrainLength = trainLength;
            if (effectiveTrain.m_MaxSpeed <= 0f || effectiveTrain.m_Acceleration <= 0f || effectiveTrain.m_Braking <= 0f)
            {
                result.Error = "rail-travel-train-data-invalid";
                return result;
            }

            Cursor cursor = new Cursor(request.Path.Segments);
            float remainingDistance = cursor.TotalLength;
            float currentSpeed = math.max(0f, request.InitialSpeed);
            float elapsed = 0f;
            float travelled = 0f;
            int ticks = 0;

            while (ticks < request.MaxTicks)
            {
                if ((ticks & 0xFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                ticks++;

                float oldSpeed = currentSpeed;
                bool isConnectionActive = cursor.HasConnectionInTrainRange(trainLength);
                if (!isConnectionActive)
                    oldSpeed = math.min(oldSpeed, effectiveTrain.m_MaxSpeed);
                result.Diagnostics.PeakSpeed = math.max(result.Diagnostics.PeakSpeed, oldSpeed);

                TrainData tickTrain = isConnectionActive
                    ? CreateConnectionTrain(effectiveTrain)
                    : effectiveTrain;

                if (isConnectionActive)
                    result.Diagnostics.ConnectionTicks++;

                Bounds1 speedRange = isConnectionActive
                    ? new Bounds1(0f, tickTrain.m_MaxSpeed)
                    : CalculateSpeedRange(tickTrain, oldSpeed, TimeStep);

                float nextSpeed = speedRange.max;
                float driveLimitedSpeed = nextSpeed;
                float brakingLimitedSpeed = nextSpeed;

                float scannedDistance = 0f;
                bool hitDriveLimit = false;
                bool hitBrakingLimit = false;

                for (int i = cursor.SegmentIndex; i < request.Path.Segments.Length; i++)
                {
                    Segment segment = request.Path.Segments[i];
                    float segmentLength = cursor.GetRemainingLength(segment, i);
                    if (segmentLength <= CompletionEpsilon)
                        continue;

                    float laneDriveSpeed = GetLaneDriveSpeed(tickTrain, segment);
                    float laneSpeed = math.max(
                        laneDriveSpeed,
                        GetMaxBrakingSpeed(tickTrain, scannedDistance, laneDriveSpeed, TimeStep));

                    float clampedLaneSpeed = ClampToRange(laneSpeed, speedRange);
                    if (clampedLaneSpeed < nextSpeed)
                    {
                        nextSpeed = clampedLaneSpeed;
                        if (laneDriveSpeed <= nextSpeed + CompletionEpsilon)
                            hitDriveLimit = true;
                        else
                            hitBrakingLimit = true;
                    }

                    driveLimitedSpeed = math.min(driveLimitedSpeed, ClampToRange(laneDriveSpeed, speedRange));
                    scannedDistance += segmentLength;
                }

                if (request.StopAtEnd)
                {
                    float endStopSpeed = ClampToRange(GetMaxBrakingSpeed(tickTrain, remainingDistance, TimeStep), speedRange);
                    brakingLimitedSpeed = math.min(brakingLimitedSpeed, endStopSpeed);
                    if (endStopSpeed < nextSpeed)
                    {
                        nextSpeed = endStopSpeed;
                        hitBrakingLimit = true;
                    }
                }

                if (hitDriveLimit || driveLimitedSpeed < speedRange.max - CompletionEpsilon)
                    result.Diagnostics.DriveLimitedTicks++;
                if (hitBrakingLimit || brakingLimitedSpeed < speedRange.max - CompletionEpsilon)
                    result.Diagnostics.BrakingLimitedTicks++;

                currentSpeed = math.max(0f, nextSpeed);

                float moveDistance = math.min(oldSpeed * TimeStep, remainingDistance);
                if (moveDistance > 0f)
                {
                    cursor.Advance(moveDistance);
                    travelled += moveDistance;
                    remainingDistance = math.max(0f, remainingDistance - moveDistance);
                }

                elapsed += TimeStep;
                result.Diagnostics.FinalRemainingDistance = remainingDistance;

                if (remainingDistance <= CompletionEpsilon && (!request.StopAtEnd || (currentSpeed <= CompletionEpsilon && oldSpeed <= CompletionEpsilon)))
                {
                    result.Success = true;
                    result.Distance = travelled;
                    result.Duration = elapsed;
                    result.TickCount = ticks;
                    result.ExitSpeed = currentSpeed;
                    return result;
                }

                // Vanilla writes the next navigation speed, then advances distance using the old speed.
                if (moveDistance <= CompletionEpsilon && oldSpeed <= MinSpeedEpsilon && currentSpeed <= MinSpeedEpsilon)
                {
                    result.Error = "rail-travel-stalled";
                    result.Distance = travelled;
                    result.Duration = elapsed;
                    result.TickCount = ticks;
                    result.ExitSpeed = currentSpeed;
                    return result;
                }
            }

            result.Error = "rail-travel-max-ticks-reached";
            result.Distance = travelled;
            result.Duration = elapsed;
            result.TickCount = ticks;
            result.ExitSpeed = currentSpeed;
            result.Diagnostics.FinalRemainingDistance = remainingDistance;
            result.Diagnostics.HitTickLimit = true;
            return result;
        }

        private static Result CreateResult(Request request)
        {
            Path path = request?.Path;
            return new Result
            {
                Diagnostics = new Diagnostics
                {
                    PathLength = path?.TotalLength ?? 0f,
                    SegmentCount = path?.Segments?.Length ?? 0,
                    ConnectionSegmentCount = path?.ConnectionSegmentCount ?? 0,
                    SourceElementCount = path?.SourceElementCount ?? 0,
                    SkippedElementCount = path?.SkippedElementCount ?? 0
                }
            };
        }

        private static TrainData BuildEffectiveTrain(TrainData leadUnit, TrainData[] coupledUnits)
        {
            TrainData effective = leadUnit;
            if (coupledUnits == null)
                return effective;

            for (int i = 0; i < coupledUnits.Length; i++)
            {
                TrainData unit = coupledUnits[i];
                effective.m_MaxSpeed = math.min(effective.m_MaxSpeed, unit.m_MaxSpeed);
                effective.m_Acceleration = math.min(effective.m_Acceleration, unit.m_Acceleration);
                effective.m_Braking = math.min(effective.m_Braking, unit.m_Braking);
            }

            return effective;
        }

        private static float CalculateTrainLength(TrainData leadUnit, TrainData[] coupledUnits)
        {
            float length = math.max(0f, math.csum(leadUnit.m_AttachOffsets));
            if (coupledUnits == null)
                return length;

            for (int i = 0; i < coupledUnits.Length; i++)
                length += math.max(0f, math.csum(coupledUnits[i].m_AttachOffsets));

            return length;
        }

        private static TrainData CreateConnectionTrain(TrainData baseTrain)
        {
            baseTrain.m_MaxSpeed = ConnectionSpeed;
            baseTrain.m_Acceleration = ConnectionSpeed;
            baseTrain.m_Braking = ConnectionSpeed;
            return baseTrain;
        }

        private static float GetLaneDriveSpeed(TrainData train, Segment segment)
        {
            if (segment.IsConnectionLane)
                return ConnectionSpeed;

            float turningLimitedSpeed = train.m_Turning.x * train.m_MaxSpeed
                / math.max(1E-06f, segment.Curviness * train.m_MaxSpeed + train.m_Turning.x - train.m_Turning.y);
            turningLimitedSpeed = math.max(1f, turningLimitedSpeed);
            return math.min(segment.SpeedLimit, turningLimitedSpeed);
        }

        private static float GetMaxBrakingSpeed(TrainData train, float distance, float maxResultSpeed, float timeStep)
        {
            float brakingStep = timeStep * train.m_Braking;
            return math.max(
                0f,
                math.sqrt(math.max(0f, brakingStep * brakingStep + 2f * train.m_Braking * distance + maxResultSpeed * maxResultSpeed))
                    - brakingStep);
        }

        private static float GetMaxBrakingSpeed(TrainData train, float distance, float timeStep)
        {
            float brakingStep = timeStep * train.m_Braking;
            return math.max(
                0f,
                math.sqrt(math.max(0f, brakingStep * brakingStep + 2f * train.m_Braking * distance))
                    - brakingStep);
        }

        private static Bounds1 CalculateSpeedRange(TrainData train, float currentSpeed, float timeStep)
        {
            float driveAcceleration = MathUtils.InverseSmoothStep(train.m_MaxSpeed, 0f, currentSpeed) * train.m_Acceleration;
            float minSpeed = math.max(0f, currentSpeed - train.m_Braking * timeStep);
            float maxSpeed = math.min(currentSpeed + driveAcceleration * timeStep, math.max(minSpeed, train.m_MaxSpeed));
            return new Bounds1(minSpeed, maxSpeed);
        }

        private static float ClampToRange(float value, Bounds1 range)
        {
            return math.clamp(value, range.min, range.max);
        }

        private sealed class Cursor
        {
            private readonly Segment[] m_Segments;
            private readonly float[] m_SegmentStarts;
            private float m_CurrentSegmentProgress;

            public Cursor(Segment[] segments)
            {
                m_Segments = segments ?? System.Array.Empty<Segment>();
                m_SegmentStarts = new float[m_Segments.Length];
                TotalLength = 0f;
                for (int i = 0; i < m_Segments.Length; i++)
                {
                    m_SegmentStarts[i] = TotalLength;
                    TotalLength += m_Segments[i].Length;
                }

                SegmentIndex = FindNextSegment(0);
                m_CurrentSegmentProgress = 0f;
            }

            public float TotalLength { get; }
            public int SegmentIndex { get; private set; }
            public float Position { get; private set; }

            public Segment CurrentSegment
            {
                get
                {
                    if (SegmentIndex < 0 || SegmentIndex >= m_Segments.Length)
                        return default;

                    return m_Segments[SegmentIndex];
                }
            }

            public void Advance(float distance)
            {
                float remaining = math.max(0f, distance);
                Position = math.min(TotalLength, Position + remaining);
                while (remaining > 0f && SegmentIndex < m_Segments.Length)
                {
                    Segment current = m_Segments[SegmentIndex];
                    float segmentRemaining = math.max(0f, current.Length - m_CurrentSegmentProgress);
                    if (segmentRemaining <= CompletionEpsilon)
                    {
                        SegmentIndex = FindNextSegment(SegmentIndex + 1);
                        m_CurrentSegmentProgress = 0f;
                        continue;
                    }

                    if (remaining < segmentRemaining - CompletionEpsilon)
                    {
                        m_CurrentSegmentProgress += remaining;
                        return;
                    }

                    remaining -= segmentRemaining;
                    SegmentIndex = FindNextSegment(SegmentIndex + 1);
                    m_CurrentSegmentProgress = 0f;
                }
            }

            public float GetRemainingLength(Segment segment, int index)
            {
                if (index < SegmentIndex)
                    return 0f;
                if (index == SegmentIndex)
                    return math.max(0f, segment.Length - m_CurrentSegmentProgress);
                return segment.Length;
            }

            public bool HasConnectionInTrainRange(float trainLength)
            {
                if (trainLength <= CompletionEpsilon)
                    return CurrentSegment.IsConnectionLane;

                float rangeStart = math.max(0f, Position - trainLength);
                float rangeEnd = math.min(TotalLength, Position);
                for (int i = 0; i < m_Segments.Length; i++)
                {
                    Segment segment = m_Segments[i];
                    if (!segment.IsConnectionLane)
                        continue;

                    float segmentStart = m_SegmentStarts[i];
                    float segmentEnd = segmentStart + segment.Length;
                    if (segmentStart <= rangeEnd + CompletionEpsilon && segmentEnd >= rangeStart + CompletionEpsilon)
                        return true;
                }

                return false;
            }

            private int FindNextSegment(int startIndex)
            {
                for (int i = math.max(0, startIndex); i < m_Segments.Length; i++)
                {
                    if (m_Segments[i].Length > CompletionEpsilon)
                        return i;
                }

                return m_Segments.Length;
            }
        }
    }
}
