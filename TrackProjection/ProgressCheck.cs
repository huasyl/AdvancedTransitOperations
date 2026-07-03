using System.Collections.Generic;
using RapidTransitMod.TrackModel;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackProjection
{
    internal sealed class ProgressCheck
    {
        private readonly TrackProjectionService m_Service;

        private readonly Dictionary<Entity, uint> m_SuspectProgressSinceFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> m_SuspectProgressLastValidationFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, bool> m_SuspectProgressProjectionInvalid = new Dictionary<Entity, bool>();
        private readonly Dictionary<Entity, string> m_SuspectProgressReason = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_SuspectProgressLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, int> m_SuspectProgressRecoveryWaypoint = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_SuspectProgressValidationCount = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, SuspectProgressSample> m_SuspectProgressFirstSample = new Dictionary<Entity, SuspectProgressSample>();

        private readonly struct SuspectProgressSample
        {
            public readonly int ProjectedAtomIndex;
            public readonly int BestAtomIndex;
            public readonly float3 VehiclePosition;
            public readonly float ProjectedDistanceMeters;
            public readonly float BestDistanceMeters;

            public SuspectProgressSample(
                int projectedAtomIndex,
                int bestAtomIndex,
                float3 vehiclePosition,
                float projectedDistanceMeters,
                float bestDistanceMeters)
            {
                ProjectedAtomIndex = projectedAtomIndex;
                BestAtomIndex = bestAtomIndex;
                VehiclePosition = vehiclePosition;
                ProjectedDistanceMeters = projectedDistanceMeters;
                BestDistanceMeters = bestDistanceMeters;
            }
        }

        internal ProgressCheck(TrackProjectionService service)
        {
            m_Service = service;
        }

        private static bool IsProgressSuspectValidationLoggingEnabled() => false;

        internal void Clear()
        {
            m_SuspectProgressSinceFrame.Clear();
            m_SuspectProgressLastValidationFrame.Clear();
            m_SuspectProgressProjectionInvalid.Clear();
            m_SuspectProgressReason.Clear();
            m_SuspectProgressLogCache.Clear();
            m_SuspectProgressRecoveryWaypoint.Clear();
            m_SuspectProgressValidationCount.Clear();
            m_SuspectProgressFirstSample.Clear();
        }

        internal void MarkVehicleProgressSuspect(Entity vehicle, string reason)
        {
            if (vehicle == Entity.Null)
                return;

            uint nowFrame = m_Service.Runtime.Frame;
            m_SuspectProgressSinceFrame[vehicle] = nowFrame;
            m_SuspectProgressReason[vehicle] = reason ?? "unknown";
            m_SuspectProgressProjectionInvalid.Remove(vehicle);
            m_Service.Cursors.Remove(vehicle, keepCursor: true);
            m_Service.ClearFacts(vehicle);
            m_SuspectProgressRecoveryWaypoint.Remove(vehicle);
            m_SuspectProgressValidationCount.Remove(vehicle);
            m_SuspectProgressFirstSample.Remove(vehicle);

            if (!RtLog.VerboseEnabled)
                return;

            string summary = vehicle.Index + "|" + reason;
            if (m_SuspectProgressLogCache.TryGetValue(vehicle, out string previous) && previous == summary)
                return;

            m_SuspectProgressLogCache[vehicle] = summary;
            m_Service.Runtime.Log.Info("[ProgressSuspect] 杞﹁締" + vehicle.Index + " reason=" + reason + " sinceFrame=" + nowFrame);
        }

        internal void ClearVehicleProgressSuspect(Entity vehicle, string reason = null)
        {
            if (vehicle == Entity.Null)
                return;

            bool hadState = m_SuspectProgressSinceFrame.Remove(vehicle);
            m_SuspectProgressLastValidationFrame.Remove(vehicle);
            m_SuspectProgressProjectionInvalid.Remove(vehicle);
            m_Service.Cursors.Remove(vehicle, keepCursor: true);
            m_Service.ClearFacts(vehicle);
            m_SuspectProgressReason.Remove(vehicle);
            m_SuspectProgressLogCache.Remove(vehicle);
            m_SuspectProgressRecoveryWaypoint.Remove(vehicle);
            m_SuspectProgressValidationCount.Remove(vehicle);
            m_SuspectProgressFirstSample.Remove(vehicle);

            if (RtLog.VerboseEnabled && hadState)
            {
                m_Service.Runtime.Log.Info("[ProgressSuspectClear] 杞﹁締" + vehicle.Index
                    + (!string.IsNullOrWhiteSpace(reason) ? " reason=" + reason : string.Empty));
            }
        }

        internal void NoteVehicleProgressSuspectRecoveryBoarding(Entity vehicle, int waypointIndex)
        {
            if (vehicle == Entity.Null
                || waypointIndex < 0
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle))
            {
                return;
            }

            m_SuspectProgressRecoveryWaypoint[vehicle] = waypointIndex;
        }

        internal void TryClearVehicleProgressSuspectOnStableDeparture(Entity vehicle, int departedWaypointIndex)
        {
            if (vehicle == Entity.Null
                || departedWaypointIndex < 0
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle)
                || !m_SuspectProgressRecoveryWaypoint.TryGetValue(vehicle, out int recoveryWaypointIndex)
                || recoveryWaypointIndex != departedWaypointIndex)
            {
                return;
            }

            ClearVehicleProgressSuspect(vehicle, "stable-stop-cycle wp=" + departedWaypointIndex);
        }

        internal bool IsVehicleProgressProjectionInvalid(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int segmentIndex,
            int projectedAtomIndex)
        {
            if (vehicle == Entity.Null
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle))
            {
                return false;
            }

            if (m_SuspectProgressProjectionInvalid.TryGetValue(vehicle, out bool alreadyInvalid) && alreadyInvalid)
                return true;

            uint nowFrame = m_Service.Runtime.Frame;
            if (m_SuspectProgressLastValidationFrame.TryGetValue(vehicle, out uint lastValidationFrame)
                && nowFrame - lastValidationFrame < TrackProjectionService.SUSPECT_PROGRESS_VALIDATE_INTERVAL_FRAMES)
            {
                return false;
            }

            m_SuspectProgressLastValidationFrame[vehicle] = nowFrame;
            bool logValidation = RtLog.VerboseEnabled && IsProgressSuspectValidationLoggingEnabled();
            if (!TryValidateSuspectVehicleProjection(vehicle, line, chain, segmentIndex, projectedAtomIndex, logValidation, out SuspectProgressSample sample, out string validationSummary, out bool projectionInvalid))
                return false;

            int validationCount = m_SuspectProgressValidationCount.TryGetValue(vehicle, out int previousCount)
                ? previousCount + 1
                : 1;
            m_SuspectProgressValidationCount[vehicle] = validationCount;
            if (!m_SuspectProgressFirstSample.ContainsKey(vehicle))
                m_SuspectProgressFirstSample[vehicle] = sample;

            if (logValidation)
            {
                string logKey = vehicle.Index + "|" + validationSummary;
                if (!m_SuspectProgressLogCache.TryGetValue(vehicle, out string previous) || previous != logKey)
                {
                    m_SuspectProgressLogCache[vehicle] = logKey;
                    m_Service.Runtime.Log.Info("[ProgressSuspectCheck] " + validationSummary);
                }

                if (validationCount % 36 == 0
                    && m_SuspectProgressFirstSample.TryGetValue(vehicle, out SuspectProgressSample firstSample))
                {
                    m_Service.Runtime.Log.Info("[ProgressSuspectWindow] vehicle=" + vehicle.Index
                        + " scans=" + validationCount
                        + " startAtom=" + firstSample.ProjectedAtomIndex
                        + " startBestAtom=" + firstSample.BestAtomIndex
                        + " startPos=(" + firstSample.VehiclePosition.x.ToString("F1")
                        + "," + firstSample.VehiclePosition.y.ToString("F1")
                        + "," + firstSample.VehiclePosition.z.ToString("F1") + ")"
                        + " startDist=" + firstSample.ProjectedDistanceMeters.ToString("F1")
                        + "/" + firstSample.BestDistanceMeters.ToString("F1")
                        + " currentAtom=" + sample.ProjectedAtomIndex
                        + " currentBestAtom=" + sample.BestAtomIndex
                        + " currentPos=(" + sample.VehiclePosition.x.ToString("F1")
                        + "," + sample.VehiclePosition.y.ToString("F1")
                        + "," + sample.VehiclePosition.z.ToString("F1") + ")"
                        + " currentDist=" + sample.ProjectedDistanceMeters.ToString("F1")
                        + "/" + sample.BestDistanceMeters.ToString("F1")
                        + (projectionInvalid ? " invalid=true" : " invalid=false"));
                }
            }

            if (!projectionInvalid)
                return false;

            m_SuspectProgressProjectionInvalid[vehicle] = true;
            return true;
        }

        private bool TryValidateSuspectVehicleProjection(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int projectedSegmentIndex,
            int projectedAtomIndex,
            bool buildValidationSummary,
            out SuspectProgressSample sample,
            out string validationSummary,
            out bool projectionInvalid)
        {
            sample = default;
            validationSummary = string.Empty;
            projectionInvalid = false;

            if (!m_Service.TryGetVehicleWorldPosition(vehicle, out float3 vehiclePosition))
                return false;

            if (!m_Service.TryGetTrackAtomWorldPosition(chain, projectedAtomIndex, out float3 projectedAtomPosition))
                return false;

            float projectedDistance = math.distance(vehiclePosition, projectedAtomPosition);
            int candidateStartSegment = math.max(0, projectedSegmentIndex - TrackProjectionService.SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS);
            int candidateEndSegment = math.min(chain.SegmentRanges.Count - 1, projectedSegmentIndex + TrackProjectionService.SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS);

            int bestAtomIndex = -1;
            float bestDistance = float.MaxValue;
            for (int seg = candidateStartSegment; seg <= candidateEndSegment; seg++)
            {
                TrackSegmentRange candidateRange = chain.SegmentRanges[seg];
                for (int atomIndex = candidateRange.StartAtomIndex; atomIndex < candidateRange.EndAtomIndexExclusive; atomIndex++)
                {
                    if (!m_Service.TryGetTrackAtomWorldPosition(chain, atomIndex, out float3 atomPosition))
                        continue;

                    float distance = math.distance(vehiclePosition, atomPosition);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestAtomIndex = atomIndex;
                    }
                }
            }

            if (bestAtomIndex < 0)
                return false;

            int atomDelta = math.abs(bestAtomIndex - projectedAtomIndex);
            projectionInvalid =
                atomDelta >= TrackProjectionService.SUSPECT_PROGRESS_ATOM_MISMATCH_THRESHOLD
                && projectedDistance - bestDistance >= TrackProjectionService.SUSPECT_PROGRESS_POSITION_IMPROVEMENT_METERS;

            sample = new SuspectProgressSample(
                projectedAtomIndex,
                bestAtomIndex,
                vehiclePosition,
                projectedDistance,
                bestDistance);

            if (buildValidationSummary)
            {
                validationSummary = "vehicle=" + vehicle.Index
                    + " line=" + line.Index
                    + " projectedAtom=" + projectedAtomIndex
                    + " bestAtom=" + bestAtomIndex
                    + " projectedDist=" + projectedDistance.ToString("F1")
                    + "m bestDist=" + bestDistance.ToString("F1")
                    + "m delta=" + atomDelta
                    + (projectionInvalid ? " invalid=true" : " invalid=false");
            }
            return true;
        }
    }
}
