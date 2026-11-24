using System.Collections.Generic;
using System.Diagnostics;
using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackProjection
{
    internal sealed class TrackProjectionService
    {
        internal const uint SUSPECT_PROGRESS_VALIDATE_INTERVAL_FRAMES = 60;
        internal const int SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS = 1;
        internal const int SUSPECT_PROGRESS_ATOM_MISMATCH_THRESHOLD = 12;
        internal const float SUSPECT_PROGRESS_POSITION_IMPROVEMENT_METERS = 120f;

        private readonly ITrackProjectionRuntimeContext m_Runtime;
        private readonly ProgressCheck m_ProgressCheck;

        internal TrackProjectionService(ITrackProjectionRuntimeContext runtime)
        {
            m_Runtime = runtime;
            m_Cursors = new VehicleTrackCursorCache();
            m_ProgressCheck = new ProgressCheck(this);
        }

        internal ITrackProjectionRuntimeContext Runtime => m_Runtime;
        internal VehicleTrackCursorCache Cursors => m_Cursors;
        private readonly VehicleTrackCursorCache m_Cursors;
        private readonly Dictionary<Entity, VehicleTrackFacts> m_Facts = new Dictionary<Entity, VehicleTrackFacts>();
        private readonly List<int> m_CurrentLaneCandidates = new List<int>();
        private readonly List<Entity> m_CurrentLaneOverlapCandidates = new List<Entity>();
        private readonly List<TrainNavigationLane> m_NavigationScratch = new List<TrainNavigationLane>();
        private readonly List<PathElement> m_PathTailScratch = new List<PathElement>();
        internal void Clear()
        {
            m_Cursors.Clear();
            m_Facts.Clear();
            m_ProgressCheck.Clear();
        }

        internal bool TrySnapshot(Entity vehicle, Entity line, ulong chainSignature, uint frame, out VehicleTrackCursor cursor)
        {
            if (m_Runtime.IsLinePending(line))
            {
                cursor = default;
                return false;
            }

            return m_Cursors.TrySnapshot(vehicle, line, chainSignature, frame, out cursor);
        }

        internal void ClearFacts(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_Facts.Remove(vehicle);
        }

        internal void InvalidateWaypointProjection(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Cursors.RemoveWaypointDependent(vehicle);
            m_Facts.Remove(vehicle);
        }

        internal void ClearVehicle(Entity vehicle)
        {
            ClearVehicleProgressSuspect(vehicle);
            m_Cursors.Remove(vehicle);
            m_Facts.Remove(vehicle);
        }

        internal bool ClearVehicleForStructure(Entity vehicle)
        {
            bool hadProjection = m_Facts.ContainsKey(vehicle)
                || m_Cursors.TryCursor(vehicle, out _);
            ClearVehicleProgressSuspect(vehicle);
            m_Cursors.Remove(vehicle);
            m_Facts.Remove(vehicle);
            return hadProjection;
        }

        internal void MarkVehicleProgressSuspect(Entity vehicle, string reason) => m_ProgressCheck.MarkVehicleProgressSuspect(vehicle, reason);
        internal void ClearVehicleProgressSuspect(Entity vehicle, string reason = null) => m_ProgressCheck.ClearVehicleProgressSuspect(vehicle, reason);
        internal void NoteVehicleProgressSuspectRecoveryBoarding(Entity vehicle, int waypointIndex) => m_ProgressCheck.NoteVehicleProgressSuspectRecoveryBoarding(vehicle, waypointIndex);
        internal void TryClearVehicleProgressSuspectOnStableDeparture(Entity vehicle, int departedWaypointIndex) => m_ProgressCheck.TryClearVehicleProgressSuspectOnStableDeparture(vehicle, departedWaypointIndex);
        internal bool IsVehicleProgressProjectionInvalid(Entity vehicle, LineTrackChain chain, int segmentIndex, int projectedAtomIndex) => m_ProgressCheck.IsVehicleProgressProjectionInvalid(vehicle, chain, segmentIndex, projectedAtomIndex);

        private bool TryRouteProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition) => m_Runtime.TryRouteProgress(vehicle, out nextWaypointIndex, out segmentPosition);
        private static int ResolveSegmentIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            if (chain == null || chain.SegmentRanges.Count == 0 || atomIndex < 0)
                return -1;

            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                if (atomIndex >= range.StartAtomIndex && atomIndex < range.EndAtomIndexExclusive)
                    return segmentIndex;
            }

            return -1;
        }

        internal bool TryGetTrackAtomWorldPosition(LineTrackChain chain, int atomIndex, out float3 position)
        {
            position = default;
            if (chain == null || atomIndex < 0 || atomIndex >= chain.TrackAtoms.Count)
                return false;

            TrackAtom atom = chain.TrackAtoms[atomIndex];
            if (TryGetTrackAtomCurveWorldPosition(atom, out position))
                return true;

            return TryGetEntityWorldPosition(atom.SourceTarget, out position)
                || TryGetEntityWorldPosition(atom.Key.PhysicalLaneKey, out position);
        }

        private bool TryGetTrackAtomCurveWorldPosition(TrackAtom atom, out float3 position)
        {
            position = default;
            if (TryGetEntityCurveWorldPosition(atom.SourceTarget, atom.TargetDelta.x, out position))
                return true;

            if (atom.Key.PhysicalLaneKey != atom.SourceTarget
                && TryGetEntityCurveWorldPosition(atom.Key.PhysicalLaneKey, atom.TargetDelta.x, out position))
            {
                return true;
            }

            return false;
        }

        private bool TryGetEntityCurveWorldPosition(Entity entity, float curvePosition, out float3 position)
        {
            position = default;
            if (entity == Entity.Null
                || !m_Runtime.EntityManager.Exists(entity)
                || !m_Runtime.EntityManager.HasComponent<Curve>(entity))
            {
                return false;
            }

            Curve curve = m_Runtime.EntityManager.GetComponentData<Curve>(entity);
            position = MathUtils.Position(curve.m_Bezier, math.saturate(curvePosition));
            return true;
        }

        private bool TryGetEntityWorldPosition(Entity entity, out float3 position)
        {
            position = default;
            if (entity == Entity.Null || !m_Runtime.EntityManager.Exists(entity))
                return false;

            if (m_Runtime.EntityManager.HasComponent<Position>(entity))
            {
                position = m_Runtime.EntityManager.GetComponentData<Position>(entity).m_Position;
                return true;
            }

            if (m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(entity))
            {
                position = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                return true;
            }

            return false;
        }

        private bool TryResolveWaypointAnchorConflict(
            float3 vehiclePosition,
            DynamicBuffer<RouteWaypoint> waypoints,
            int routeProgressNextWaypointIndex,
            out int nearbyWaypointIndex)
        {
            nearbyWaypointIndex = -1;
            const float stationAnchorRadiusMeters = 420f;

            float bestDistance = float.MaxValue;
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                if (!TryGetWaypointWorldPosition(waypoints[waypointIndex].m_Waypoint, out float3 waypointPosition))
                    continue;

                float distance = math.distance(vehiclePosition, waypointPosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearbyWaypointIndex = waypointIndex;
                }
            }

            if (nearbyWaypointIndex < 0 || bestDistance > stationAnchorRadiusMeters)
                return false;

            int waypointDelta = math.abs(routeProgressNextWaypointIndex - nearbyWaypointIndex);
            bool wrappedNeighbor =
                (routeProgressNextWaypointIndex == 0 && nearbyWaypointIndex == waypoints.Length - 1)
                || (nearbyWaypointIndex == 0 && routeProgressNextWaypointIndex == waypoints.Length - 1);

            return waypointDelta >= 1 && !wrappedNeighbor;
        }

        internal bool TryGetVehicleWorldPosition(Entity vehicle, out float3 position)
        {
            position = default;
            if (!m_Runtime.EntityManager.Exists(vehicle))
                return false;

            if (m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                position = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
                return true;
            }

            if (m_Runtime.EntityManager.HasComponent<Position>(vehicle))
            {
                position = m_Runtime.EntityManager.GetComponentData<Position>(vehicle).m_Position;
                return true;
            }

            return false;
        }

        private bool TryGetWaypointWorldPosition(Entity waypoint, out float3 position)
        {
            position = default;
            if (waypoint == Entity.Null || !m_Runtime.EntityManager.Exists(waypoint))
                return false;

            if (m_Runtime.EntityManager.HasComponent<Position>(waypoint))
            {
                position = m_Runtime.EntityManager.GetComponentData<Position>(waypoint).m_Position;
                return true;
            }

            if (m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(waypoint))
            {
                position = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(waypoint).m_Position;
                return true;
            }

            return false;
        }

        private bool TryResolveTrainCurrentLaneCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            bool captureDiagnostic,
            ref ProjectionOutcome outcome,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (vehicle == Entity.Null
                || chain == null
                || !m_Runtime.TryReadProjectionCurrentLane(vehicle, out TrainCurrentLane current))
            {
                if (captureDiagnostic)
                    outcome.ExactFailure = ProjectionExactFailure.CurrentLaneUnavailable;
                return false;
            }


            long matcherTicks = 0;
            m_CurrentLaneOverlapCandidates.Clear();
            if (!CurrentLaneMatcher.HasPhysicalCandidate(chain, current.m_Front.m_Lane))
                m_Runtime.CollectProjectionOverlapLanes(current.m_Front.m_Lane, m_CurrentLaneOverlapCandidates);
            CurrentLaneMatchDiagnostic first = default;
            CurrentLaneMatchState state = SelectInitialCurrentLane(
                chain,
                current,
                captureDiagnostic,
                out int atomIndex,
                out first,
                ref matcherTicks);
            int finalCandidates = first.FutureCandidates;
            bool independentBoardingUsed = false;
            bool departureSessionUsed = false;
            bool arrivalTargetUsed = false;
            bool pathReady = false;
            bool hasPathWrite = false;
            if (state == CurrentLaneMatchState.Ambiguous)
                hasPathWrite = m_Runtime.HasProjectionPathWrite(vehicle);

            if (state == CurrentLaneMatchState.Ambiguous
                && !hasPathWrite
                && m_Runtime.IsVehicleBoarding(vehicle)
                && m_Runtime.TryConfirmProjectionBoardingWaypoint(
                    vehicle,
                    line,
                    waypoints,
                    current,
                    out int boardingWaypointIndex))
            {
                CurrentLaneMatchDiagnostic independentBoarding = default;
                state = CurrentLaneMatcher.SelectWaypointCandidates(
                    chain,
                    boardingWaypointIndex,
                    m_CurrentLaneCandidates,
                    false,
                    ProjectionMatchBasis.IndependentBoarding,
                    captureDiagnostic,
                    out atomIndex,
                    out independentBoarding);
                independentBoardingUsed = state == CurrentLaneMatchState.Unique;
                finalCandidates = independentBoarding.FutureCandidates;
            }

            if (state == CurrentLaneMatchState.Ambiguous
                && !hasPathWrite)
            {
                bool navigationComplete = LoadNavigation(vehicle);

                CurrentLaneMatchDiagnostic navigation = default;
                state = FilterCurrentLaneByFuture(
                    chain,
                    current,
                    m_NavigationScratch,
                    null,
                    false,
                    captureDiagnostic,
                    out atomIndex,
                    out navigation,
                    ref matcherTicks);
                if (captureDiagnostic)
                {
                    outcome.NavigationCandidates = navigation.FutureCandidates;
                }
                finalCandidates = navigation.FutureCandidates;
                if (CanReadPathTail(state, navigationComplete))
                {
                    ProjectionReadStop pathStop = LoadPathTail(vehicle, out pathReady);

                    CurrentLaneMatchDiagnostic path = default;
                    state = FilterCurrentLaneByFuture(
                        chain,
                        current,
                        m_NavigationScratch,
                        m_PathTailScratch,
                        pathStop == ProjectionReadStop.None,
                        captureDiagnostic,
                        out atomIndex,
                        out path,
                        ref matcherTicks);
                    if (captureDiagnostic)
                    {
                        outcome.PathCandidates = path.FutureCandidates;
                    }
                    finalCandidates = path.FutureCandidates;
                }
            }
            else
            {
                m_NavigationScratch.Clear();
                m_PathTailScratch.Clear();
            }

            if (state == CurrentLaneMatchState.Ambiguous
                && !hasPathWrite
                && m_Runtime.TryGetDeparturePendingStopWaypoint(vehicle, line, out int sessionWaypointIndex))
            {
                CurrentLaneMatchDiagnostic departureSession = default;
                state = CurrentLaneMatcher.SelectWaypointCandidates(
                    chain,
                    sessionWaypointIndex,
                    m_CurrentLaneCandidates,
                    false,
                    ProjectionMatchBasis.DepartureSession,
                    captureDiagnostic,
                    out atomIndex,
                    out departureSession);
                departureSessionUsed = state == CurrentLaneMatchState.Unique;
                finalCandidates = departureSession.FutureCandidates;
            }

            if (state == CurrentLaneMatchState.Ambiguous
                && !hasPathWrite
                && pathReady
                && !m_Runtime.IsVehicleBoarding(vehicle)
                && !m_Runtime.HasProjectionStopSession(vehicle)
                && m_Runtime.IsVehicleArriving(vehicle)
                && m_Runtime.TryResolveProjectionTargetWaypoint(
                    vehicle, line, waypoints, out int targetWaypointIndex))
            {
                CurrentLaneMatchDiagnostic arrivalTarget = default;
                state = CurrentLaneMatcher.SelectWaypointCandidates(
                    chain,
                    targetWaypointIndex,
                    m_CurrentLaneCandidates,
                    true,
                    ProjectionMatchBasis.ArrivalTarget,
                    captureDiagnostic,
                    out atomIndex,
                    out arrivalTarget);
                arrivalTargetUsed = state == CurrentLaneMatchState.Unique;
                finalCandidates = arrivalTarget.FutureCandidates;
            }

            if (state != CurrentLaneMatchState.Unique
                || !TryGetCurrentLaneProgress(
                    chain,
                    atomIndex,
                    current.m_Front.m_CurvePosition.y,
                    captureDiagnostic,
                    out float atomPosition01,
                    ref matcherTicks))
            {
                if (captureDiagnostic)
                {
                    outcome.MatcherTicks = matcherTicks;
                    outcome.ExactFailure = ClassifyExactFailure(
                        state,
                        first.InitialCandidates,
                        finalCandidates,
                        first);
                    if (state == CurrentLaneMatchState.Unique)
                        outcome.ExactFailure = ProjectionExactFailure.ProgressUnavailable;
                }
                return false;
            }

            if (captureDiagnostic)
            {
                outcome.MatcherTicks = matcherTicks;
                outcome.ExactBasis = state == CurrentLaneMatchState.Unique
                    ? independentBoardingUsed
                        ? ProjectionMatchBasis.IndependentBoarding
                        : departureSessionUsed
                            ? ProjectionMatchBasis.DepartureSession
                            : arrivalTargetUsed
                                ? ProjectionMatchBasis.ArrivalTarget
                                : ResolveMatchBasis(first, outcome.NavigationCandidates, outcome.PathCandidates)
                    : ProjectionMatchBasis.None;
            }

            int segmentIndex = ResolveSegmentIndexForAtom(chain, atomIndex);
            if (segmentIndex < 0 || segmentIndex >= chain.SegmentRanges.Count)
                return false;

            TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
            cursor = new VehicleTrackCursor(
                line,
                chain.Signature,
                segmentIndex,
                segmentRange.StartAtomIndex,
                segmentRange.EndAtomIndexExclusive,
                atomIndex,
                atomPosition01,
                1f,
                VehicleTrackCursorSource.CurrentLane);
            return true;
        }

        private CurrentLaneMatchState SelectInitialCurrentLane(
            LineTrackChain chain,
            TrainCurrentLane current,
            bool captureDiagnostic,
            out int atomIndex,
            out CurrentLaneMatchDiagnostic diagnostic,
            ref long matcherTicks)
        {
            long started = captureDiagnostic ? Stopwatch.GetTimestamp() : 0;
            CurrentLaneMatchState state = CurrentLaneMatcher.SelectInitial(
                chain,
                current,
                m_CurrentLaneOverlapCandidates,
                m_CurrentLaneCandidates,
                captureDiagnostic,
                out atomIndex,
                out diagnostic);
            if (captureDiagnostic)
                matcherTicks += Stopwatch.GetTimestamp() - started;
            return state;
        }

        private CurrentLaneMatchState FilterCurrentLaneByFuture(
            LineTrackChain chain,
            TrainCurrentLane current,
            IList<TrainNavigationLane> navigation,
            IList<PathElement> pathTail,
            bool pathTailComplete,
            bool captureDiagnostic,
            out int atomIndex,
            out CurrentLaneMatchDiagnostic diagnostic,
            ref long matcherTicks)
        {
            long started = captureDiagnostic ? Stopwatch.GetTimestamp() : 0;
            CurrentLaneMatchState state = CurrentLaneMatcher.FilterByFuture(
                chain,
                current,
                m_CurrentLaneOverlapCandidates,
                navigation,
                pathTail,
                pathTailComplete,
                m_CurrentLaneCandidates,
                captureDiagnostic,
                out atomIndex,
                out diagnostic);
            if (captureDiagnostic)
                matcherTicks += Stopwatch.GetTimestamp() - started;
            return state;
        }

        private static bool TryGetCurrentLaneProgress(
            LineTrackChain chain,
            int atomIndex,
            float curveY,
            bool captureDiagnostic,
            out float progress,
            ref long matcherTicks)
        {
            if (!captureDiagnostic)
                return CurrentLaneMatcher.TryGetProgress(chain, atomIndex, curveY, out progress);

            long started = Stopwatch.GetTimestamp();
            bool available = CurrentLaneMatcher.TryGetProgress(chain, atomIndex, curveY, out progress);
            matcherTicks += Stopwatch.GetTimestamp() - started;
            return available;
        }

        private static ProjectionExactFailure ClassifyExactFailure(
            CurrentLaneMatchState state,
            int initialCandidates,
            int finalCandidates,
            CurrentLaneMatchDiagnostic initial)
        {
            if (initial.InvalidInput)
                return ProjectionExactFailure.InvalidParameter;
            if (initialCandidates == 0)
            {
                if (initial.IndexedCandidates == 0)
                    return ProjectionExactFailure.LaneNotInModel;
                if (initial.PhysicalCandidates == 0)
                    return ProjectionExactFailure.IndexedLaneMismatch;
                if (initial.CoordinateCandidates == 0)
                    return ProjectionExactFailure.ParameterOutsideRange;
                return ProjectionExactFailure.NoCandidates;
            }
            if (finalCandidates == 0)
                return ProjectionExactFailure.AllExcluded;

            switch (state)
            {
                case CurrentLaneMatchState.Ambiguous:
                    return ProjectionExactFailure.Ambiguous;
                case CurrentLaneMatchState.ZeroParameterSpan:
                    return ProjectionExactFailure.ZeroParameterSpan;
                default:
                    return ProjectionExactFailure.ProgressUnavailable;
            }
        }

        private static ProjectionMatchBasis ResolveMatchBasis(
            CurrentLaneMatchDiagnostic first,
            int navigationCandidates,
            int pathCandidates)
        {
            if (pathCandidates == 1)
                return ProjectionMatchBasis.PathTail;
            if (navigationCandidates == 1)
                return ProjectionMatchBasis.Navigation;
            return first.Basis;
        }

        internal static bool CanReadPathTail(
            CurrentLaneMatchState state,
            bool navigationComplete)
        {
            return state == CurrentLaneMatchState.Ambiguous && navigationComplete;
        }

        private bool LoadNavigation(Entity vehicle)
        {
            m_NavigationScratch.Clear();
            if (!m_Runtime.TryReadProjectionNavigation(vehicle, out DynamicBuffer<TrainNavigationLane> navigation))
            {
                return false;
            }

            const int navigationLimit = 64;
            int count = math.min(navigation.Length, navigationLimit);
            for (int i = 0; i < count; i++)
                m_NavigationScratch.Add(navigation[i]);
            if (count == navigation.Length)
                return true;

            return false;
        }

        private ProjectionReadStop LoadPathTail(Entity vehicle, out bool pathReady)
        {
            pathReady = false;
            m_PathTailScratch.Clear();
            if (!m_Runtime.TryReadProjectionPath(
                    vehicle,
                    out PathOwner pathOwner,
                    out DynamicBuffer<PathElement> pathElements)
                )
            {
                return ProjectionReadStop.PathReadFailed;
            }
            if ((pathOwner.m_State & (PathFlags.Failed | PathFlags.Obsolete | PathFlags.Updated)) != 0)
                return ProjectionReadStop.PathFlagsDisabled;

            if ((pathOwner.m_State & PathFlags.Pending) == 0)
                pathReady = true;

            int start = math.clamp(pathOwner.m_ElementIndex, 0, pathElements.Length);
            int endExclusive = pathElements.Length;
            bool pending = (pathOwner.m_State & PathFlags.Pending) != 0;
            if (pending)
                endExclusive = math.max(start, pathElements.Length - 1);

            const int pathLimit = 64;
            bool truncated = endExclusive > start + pathLimit;
            endExclusive = math.min(endExclusive, start + pathLimit);
            for (int i = start; i < endExclusive; i++)
                m_PathTailScratch.Add(pathElements[i]);
            if (truncated)
                return ProjectionReadStop.PathTruncated;
            return pending ? ProjectionReadStop.PathPendingTail : ProjectionReadStop.None;
        }

        internal bool TryGetCurrentLanePosition(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            out Entity currentLane,
            out CurrentLanePositionDiagnostic diagnostic)
        {
            cursor = default;
            currentLane = Entity.Null;
            diagnostic = new CurrentLanePositionDiagnostic(
                CurrentLanePositionFailure.CurrentFrameCursorUnavailable,
                Entity.Null,
                float.NaN,
                VehicleTrackCursorSource.Unknown,
                -1,
                -1);
            if (vehicle == Entity.Null
                || line == Entity.Null
                || chain == null)
                return false;
            if (m_Runtime.IsLinePending(line))
            {
                diagnostic = new CurrentLanePositionDiagnostic(
                    CurrentLanePositionFailure.LinePending,
                    Entity.Null,
                    float.NaN,
                    VehicleTrackCursorSource.Unknown,
                    -1,
                    -1);
                return false;
            }
            if (!TryGetExactCurrentFrameCursor(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    out VehicleTrackCursor snapshot,
                    ProjectionRequestSource.Signal)
                || !snapshot.Available)
            {
                return false;
            }

            diagnostic = new CurrentLanePositionDiagnostic(
                CurrentLanePositionFailure.None,
                Entity.Null,
                float.NaN,
                snapshot.Source,
                snapshot.SegmentIndex,
                snapshot.AtomCursorIndex);
            if (snapshot.Source != VehicleTrackCursorSource.CurrentLane)
            {
                diagnostic = new CurrentLanePositionDiagnostic(
                    CurrentLanePositionFailure.CursorSourceNotCurrentLane,
                    Entity.Null,
                    float.NaN,
                    snapshot.Source,
                    snapshot.SegmentIndex,
                    snapshot.AtomCursorIndex);
                return false;
            }
            if (snapshot.SegmentIndex < 0
                || snapshot.SegmentIndex >= chain.SegmentRanges.Count
                || snapshot.AtomCursorIndex < 0
                || snapshot.AtomCursorIndex >= chain.TrackAtoms.Count)
            {
                diagnostic = new CurrentLanePositionDiagnostic(
                    CurrentLanePositionFailure.CursorIndexInconsistent,
                    Entity.Null,
                    float.NaN,
                    snapshot.Source,
                    snapshot.SegmentIndex,
                    snapshot.AtomCursorIndex);
                return false;
            }
            TrackAtom atom = chain.TrackAtoms[snapshot.AtomCursorIndex];
            currentLane = atom.Key.PhysicalLaneKey != Entity.Null
                ? atom.Key.PhysicalLaneKey
                : atom.SourceTarget;
            float position = math.lerp(
                atom.TargetDelta.x,
                atom.TargetDelta.y,
                math.saturate(snapshot.AtomPosition01));
            diagnostic = new CurrentLanePositionDiagnostic(
                CurrentLanePositionFailure.None,
                currentLane,
                position,
                snapshot.Source,
                snapshot.SegmentIndex,
                snapshot.AtomCursorIndex);
            if (currentLane == Entity.Null)
            {
                diagnostic = new CurrentLanePositionDiagnostic(
                    CurrentLanePositionFailure.CurrentLaneUnavailable,
                    currentLane,
                    position,
                    snapshot.Source,
                    snapshot.SegmentIndex,
                    snapshot.AtomCursorIndex);
                return false;
            }
            if (!math.isfinite(position))
            {
                diagnostic = new CurrentLanePositionDiagnostic(
                    CurrentLanePositionFailure.CurrentLaneProgressNotFinite,
                    currentLane,
                    position,
                    snapshot.Source,
                    snapshot.SegmentIndex,
                    snapshot.AtomCursorIndex);
                return false;
            }
            cursor = snapshot;
            return true;
        }

        internal bool TryProjectVehicleTrackCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (m_Runtime.IsLinePending(line))
                return false;
            if (!m_Runtime.TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)
                || m_Runtime.IsLinePending(line)
                || chain.SegmentRanges.Count == 0)
            {
                return false;
            }

            return TryProjectVehicleTrackCursor(vehicle, line, waypoints, chain, out cursor, out _);
        }

        internal bool TryProjectVehicleTrackCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            out ProjectionOutcome outcome,
            bool tryExact = true,
            bool allowFallback = true,
            bool captureDiagnosticOverride = true,
            ProjectionRequestSource requestSource = ProjectionRequestSource.Unknown,
            ProjectionOutcome exactEvidence = default,
            bool recordDiagnostic = true,
            bool exactAlreadyCounted = false)
        {
            cursor = default;
            outcome = exactEvidence;
            bool captureDiagnostic = m_Runtime.ProjectionDiagnosticsEnabled && captureDiagnosticOverride;
            if (captureDiagnostic)
            {
                outcome.RequestSource = requestSource;
                outcome.NavigationCandidates = -1;
                outcome.PathCandidates = -1;
                m_Runtime.TryReadProjectionRuntimeContext(vehicle, out outcome.RuntimeContext);
                if (exactAlreadyCounted)
                {
                    outcome.ExactCounted = true;
                }
            }
            if (m_Runtime.IsLinePending(line)
                || chain == null || chain.SegmentRanges.Count == 0)
            {
                CompleteProjectionDiagnostic(captureDiagnostic && recordDiagnostic, ref outcome, false, cursor);
                return false;
            }

            if (tryExact && TryResolveTrainCurrentLaneCursor(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    captureDiagnostic,
                    ref outcome,
                    out cursor))
            {
                CompleteProjectionDiagnostic(captureDiagnostic && recordDiagnostic, ref outcome, true, cursor);
                return true;
            }

            if (!allowFallback)
            {
                if (captureDiagnostic)
                {
                    if (recordDiagnostic)
                    {
                        outcome.ExactCounted = true;
                        m_Runtime.RecordProjectionExactFailure(outcome);
                    }
                }
                return false;
            }

            bool trustedRouteProgress = TryRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition);
            VehicleTrackCursorSource cursorSource = trustedRouteProgress
                ? VehicleTrackCursorSource.RouteProgress
                : VehicleTrackCursorSource.CachedWaypoint;
            if (!trustedRouteProgress)
            {
                if (!m_Runtime.CachedWaypointIndex.TryGetValue(vehicle, out nextWaypointIndex))
                {
                    CompleteProjectionDiagnostic(captureDiagnostic && recordDiagnostic, ref outcome, false, cursor);
                    return false;
                }
                segmentPosition = 0f;
            }

            if (trustedRouteProgress && TryResolveStationAnchoredProgressFallback(
                vehicle,
                line,
                waypoints,
                chain,
                nextWaypointIndex,
                segmentPosition,
                out int anchoredWaypointIndex))
            {
                nextWaypointIndex = anchoredWaypointIndex;
                segmentPosition = 0f;
                trustedRouteProgress = false;
                cursorSource = VehicleTrackCursorSource.AnchoredRouteProgress;
            }

            nextWaypointIndex = math.clamp(nextWaypointIndex, 0, waypoints.Length - 1);
            int segmentIndex = nextWaypointIndex == 0
                ? math.max(0, chain.SegmentRanges.Count - 1)
                : nextWaypointIndex - 1;
            if (segmentIndex < 0 || segmentIndex >= chain.SegmentRanges.Count)
            {
                CompleteProjectionDiagnostic(captureDiagnostic && recordDiagnostic, ref outcome, false, cursor);
                return false;
            }

            TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
            if (segmentRange.EndAtomIndexExclusive <= segmentRange.StartAtomIndex)
            {
                CompleteProjectionDiagnostic(captureDiagnostic && recordDiagnostic, ref outcome, false, cursor);
                return false;
            }

            int segmentAtomLength = math.max(1, segmentRange.EndAtomIndexExclusive - segmentRange.StartAtomIndex);
            float segmentAtomCoordinate = segmentAtomLength * math.saturate(segmentPosition);
            int approximateAtomIndex = segmentRange.StartAtomIndex
                + math.min(segmentAtomLength - 1, (int)math.floor(segmentAtomCoordinate));

            float confidence = trustedRouteProgress ? 1f : 0.7f;
            if (m_Cursors.TryCursor(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                if (hint.SegmentIndex == segmentIndex)
                {
                    approximateAtomIndex = math.max(approximateAtomIndex, hint.AtomCursorIndex);
                }
                else
                {
                    bool wrappedForward = hint.SegmentIndex >= chain.SegmentRanges.Count - 2 && segmentIndex <= 1;
                    bool monotonicForward = segmentIndex >= hint.SegmentIndex || wrappedForward;
                    if (!monotonicForward)
                    {
                        confidence *= 0.4f;
                        approximateAtomIndex = math.max(segmentRange.StartAtomIndex, math.min(segmentRange.EndAtomIndexExclusive - 1, hint.AtomCursorIndex));
                    }
                }
            }

            if (IsVehicleProgressProjectionInvalid(vehicle, chain, segmentIndex, approximateAtomIndex))
            {
                CompleteProjectionDiagnostic(captureDiagnostic && recordDiagnostic, ref outcome, false, cursor);
                return false;
            }

            approximateAtomIndex = math.clamp(approximateAtomIndex, segmentRange.StartAtomIndex, segmentRange.EndAtomIndexExclusive - 1);
            float atomPosition01 = math.saturate(
                segmentAtomCoordinate - (approximateAtomIndex - segmentRange.StartAtomIndex));
            cursor = new VehicleTrackCursor(
                line,
                chain.Signature,
                segmentIndex,
                segmentRange.StartAtomIndex,
                segmentRange.EndAtomIndexExclusive,
                approximateAtomIndex,
                atomPosition01,
                confidence,
                cursorSource);
            CompleteProjectionDiagnostic(captureDiagnostic && recordDiagnostic, ref outcome, true, cursor);
            return true;
        }

        private void CompleteProjectionDiagnostic(
            bool captureDiagnostic,
            ref ProjectionOutcome outcome,
            bool success,
            VehicleTrackCursor cursor)
        {
            outcome.FinalSuccess = success;
            outcome.FinalSource = success ? cursor.Source : VehicleTrackCursorSource.Unknown;
            if (captureDiagnostic)
                m_Runtime.RecordProjectionOutcome(outcome);
        }

        internal bool TryGetVehicleTrackCursorCurrentFrame(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            ProjectionRequestSource requestSource = ProjectionRequestSource.Unknown)
        {
            cursor = default;
            if (vehicle == Entity.Null || line == Entity.Null || m_Runtime.IsLinePending(line) || chain == null)
                return false;

            uint nowFrame = m_Runtime.Frame;
            bool captureDiagnostic = m_Runtime.ProjectionDiagnosticsEnabled;
            bool cacheHit = m_Cursors.TryGetFramePosition(
                vehicle,
                line,
                chain.Signature,
                nowFrame,
                out bool available,
                out cursor);
            if (!cacheHit)
            {
                if (m_Cursors.TryGetFrameExact(
                        vehicle,
                        line,
                        chain.Signature,
                        nowFrame,
                        out bool exactAvailable,
                        out VehicleTrackCursor exactCursor))
                {
                    available = exactAvailable
                        ? true
                        : TryProjectVehicleTrackCursor(vehicle, line, waypoints, chain, out cursor, out ProjectionOutcome fallbackOutcome, false, true, true, requestSource, default, true, true);
                    if (exactAvailable)
                        cursor = exactCursor;
                }
                else
                {
                    exactAvailable = TryProjectVehicleTrackCursor(vehicle, line, waypoints, chain, out exactCursor, out ProjectionOutcome exactOutcome, true, false, true, requestSource, default, false);
                    m_Cursors.StoreFrameExact(vehicle, line, chain.Signature, nowFrame, exactAvailable, exactCursor);
                    available = exactAvailable
                        ? true
                        : TryProjectVehicleTrackCursor(vehicle, line, waypoints, chain, out cursor, out ProjectionOutcome fallbackOutcome, false, true, true, requestSource, exactOutcome);
                    if (exactAvailable)
                    {
                        cursor = exactCursor;
                        m_Runtime.RecordProjectionOutcome(exactOutcome);
                    }
                }
                m_Cursors.StoreFramePosition(
                    vehicle,
                    line,
                    chain.Signature,
                    nowFrame,
                    available,
                    cursor);
            }
            if (captureDiagnostic)
                m_Runtime.RecordProjectionCacheAccess(requestSource, cacheHit, available, available ? cursor.Source : VehicleTrackCursorSource.Unknown, false);
            return available;
        }

        internal bool TryGetExactCurrentFrameCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            ProjectionRequestSource requestSource = ProjectionRequestSource.Unknown)
        {
            cursor = default;
            uint frame = m_Runtime.Frame;
            bool cacheHit = m_Cursors.TryGetFrameExact(vehicle, line, chain.Signature, frame, out bool available, out cursor);
            if (cacheHit)
            {
                if (m_Runtime.ProjectionDiagnosticsEnabled)
                    m_Runtime.RecordProjectionCacheAccess(requestSource, true, available, available ? cursor.Source : VehicleTrackCursorSource.Unknown, true);
                return available;
            }

            available = TryProjectVehicleTrackCursor(vehicle, line, waypoints, chain, out cursor, out ProjectionOutcome exactOutcome, true, false, true, requestSource);
            m_Cursors.StoreFrameExact(vehicle, line, chain.Signature, frame, available, cursor);
            if (m_Runtime.ProjectionDiagnosticsEnabled)
                m_Runtime.RecordProjectionCacheAccess(requestSource, false, available, available ? cursor.Source : VehicleTrackCursorSource.Unknown, true);
            return available;
        }

        private bool TryFacts(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackFacts facts,
            ProjectionRequestSource requestSource = ProjectionRequestSource.Unknown)
        {
            facts = default;
            if (vehicle == Entity.Null || line == Entity.Null || m_Runtime.IsLinePending(line) || chain == null)
                return false;

            uint nowFrame = m_Runtime.Frame;
            if (m_Facts.TryGetValue(vehicle, out facts)
                && facts.Frame == nowFrame
                && facts.Vehicle == vehicle
                && facts.Line == line
                && facts.ChainSignature == chain.Signature)
            {
                if (m_Runtime.ProjectionDiagnosticsEnabled)
                    m_Runtime.RecordProjectionCacheAccess(requestSource, true, true, facts.Cursor.Source, false);
                return true;
            }

            if (!TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor, requestSource))
                return false;

            facts = new VehicleTrackFacts(
                nowFrame,
                vehicle,
                line,
                chain.Signature,
                cursor);
            m_Facts[vehicle] = facts;
            return true;
        }

        internal bool TryReadCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            ProjectionRequestSource requestSource)
        {
            bool available = TryFacts(vehicle, line, waypoints, chain, out VehicleTrackFacts facts, requestSource);
            cursor = available ? facts.Cursor : default;
            return available;
        }

        private bool TryResolveStationAnchoredProgressFallback(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            int nextWaypointIndex,
            float segmentPosition,
            out int anchoredWaypointIndex)
        {
            anchoredWaypointIndex = -1;
            if (!TryGetVehicleWorldPosition(vehicle, out float3 vehiclePosition))
                return false;

            bool hasNearbyAnchor = TryResolveWaypointAnchorConflict(
                vehiclePosition,
                waypoints,
                nextWaypointIndex,
                out int nearbyWaypointIndex);
            if (hasNearbyAnchor
                && m_Runtime.CachedWaypointIndex.TryGetValue(vehicle, out int cachedWpIdx)
                && cachedWpIdx == nearbyWaypointIndex)
            {
                anchoredWaypointIndex = nearbyWaypointIndex;
                return true;
            }

            if (m_Cursors.TryCursor(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                int hintedWaypointIndex = hint.SegmentIndex >= chain.SegmentRanges.Count - 1
                    ? 0
                    : hint.SegmentIndex + 1;
                if (hasNearbyAnchor && nearbyWaypointIndex == hintedWaypointIndex)
                {
                    anchoredWaypointIndex = nearbyWaypointIndex;
                    return true;
                }

                bool wrappedForward = hint.SegmentIndex >= chain.SegmentRanges.Count - 2 && nextWaypointIndex <= 1;
                bool monotonicForward = (nextWaypointIndex == 0 ? chain.SegmentRanges.Count - 1 : nextWaypointIndex - 1) >= hint.SegmentIndex || wrappedForward;
                if (!monotonicForward && math.saturate(segmentPosition) <= 0.15f)
                {
                    anchoredWaypointIndex = hintedWaypointIndex;
                    return true;
                }
            }

            return false;
        }

    }
}
