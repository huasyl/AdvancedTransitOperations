using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;
using RapidTransitMod.RailTravel;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class RunChartQuery
    {
        private const int MaxWaypoints = 256;
        private const int MaxSegments = 64;
        private const int MaxPathSlots = 256;
        private const int TheoryTimeoutMilliseconds = 8000;
        private readonly EntityManager m_Entities;
        private readonly RoutePlanQuery m_RoutePlans;
        private readonly ObservationPort m_Observation;
        private readonly Func<string, Entity> m_LineById;
        private readonly Func<double> m_FramesPerMinute;
        private readonly Dictionary<string, RunChartSession> m_Sessions =
            new Dictionary<string, RunChartSession>(StringComparer.Ordinal);

        internal RunChartQuery(
            EntityManager entities,
            RoutePlanQuery routePlans,
            ObservationPort observation,
            Func<string, Entity> lineById,
            Func<double> framesPerMinute)
        {
            m_Entities = entities;
            m_RoutePlans = routePlans ?? throw new ArgumentNullException(nameof(routePlans));
            m_Observation = observation ?? throw new ArgumentNullException(nameof(observation));
            m_LineById = lineById ?? throw new ArgumentNullException(nameof(lineById));
            m_FramesPerMinute = framesPerMinute ?? throw new ArgumentNullException(nameof(framesPerMinute));
        }

        internal DispatchWorkbenchRunChartStatusDto Start(DispatchWorkbenchRunChartRequestDto request)
        {
            request = request ?? new DispatchWorkbenchRunChartRequestDto();
            RunChartSession session;
            if (!string.IsNullOrEmpty(request.queryId))
            {
                if (!m_Sessions.TryGetValue(request.queryId, out session))
                    return Failure(string.Empty, "run-chart-query-missing");
                if (session.State != "SelectionRequired")
                    return Status(session);
                if (!request.hasCandidate || request.candidateIndex < 0
                    || request.candidateIndex >= session.Candidates.Count)
                    return Failure(session.Id, "run-chart-candidate-required", session);
                session.Selected = session.Candidates[request.candidateIndex];
                return Begin(session, request);
            }

            Entity line = m_LineById(request.lineId);
            if (line == Entity.Null || !m_Entities.Exists(line))
                return Failure(string.Empty, "run-chart-line-missing");
            LifecycleKind lifecycle = TransportModeProfile.GetProfile(
                TransportModeResolver.Resolve(m_Entities, line)).Lifecycle;
            if (!m_RoutePlans.TryGet(line, lifecycle, out RoutePlan plan)
                || plan.Waypoints.Length > MaxWaypoints)
                return Failure(string.Empty, "run-chart-route-plan-unavailable");

            List<RunChartCandidate> candidates = BuildCandidates(
                plan,
                request.fromStopKey,
                request.toStopKey,
                request.viaStopKeys);
            if (candidates.Count == 0)
                return Failure(string.Empty, "run-chart-stop-sequence-unavailable");

            session = new RunChartSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Line = line,
                LineId = request.lineId ?? string.Empty,
                RowId = request.rowId ?? string.Empty,
                Lifecycle = lifecycle,
                Plan = plan,
                Candidates = candidates,
                Source = NormalizeSource(request.source),
                State = "SelectionRequired"
            };
            if (!TryReserveSession())
                return Failure(string.Empty, "run-chart-session-limit");
            m_Sessions[session.Id] = session;
            if (candidates.Count > 1 && !request.hasCandidate)
                return Status(session);
            int selectedIndex = request.hasCandidate ? request.candidateIndex : 0;
            if (selectedIndex < 0 || selectedIndex >= candidates.Count)
                return Failure(session.Id, "run-chart-candidate-required", session);
            session.Selected = candidates[selectedIndex];
            return Begin(session, request);
        }

        internal DispatchWorkbenchRunChartStatusDto Status(string queryId)
        {
            if (string.IsNullOrEmpty(queryId) || !m_Sessions.TryGetValue(queryId, out RunChartSession session))
                return Failure(queryId, "run-chart-query-missing");
            return PollStatus(session);
        }

        internal DispatchWorkbenchRunChartStatusDto Cancel(string queryId)
        {
            if (!m_Sessions.TryGetValue(queryId ?? string.Empty, out RunChartSession session))
                return Failure(queryId, "run-chart-query-missing");
            if (session.Ticket.IsValid)
                RailEtaBridgeService.Current?.Cancel(session.Ticket);
            session.State = "Cancelled";
            session.Error = "run-chart-query-cancelled";
            ReleaseSession(session);
            return Status(session);
        }

        internal bool TryGetResult(string queryId, out RunChartResult result)
        {
            result = null;
            if (!m_Sessions.TryGetValue(queryId ?? string.Empty, out RunChartSession session)
                || session.State != "Completed"
                || session.Segments == null)
                return false;
            if (!CurrentPlan(session, out RoutePlan current))
                return false;
            if (session.Source == "theory"
                && (!CurrentSignatures(session, current)
                    || !RunChartSignatures.ValidatePathFacts(
                        m_Entities, session.Line, session.TheorySegments, session.ActualPathSignature)))
                return false;
            result = new RunChartResult
            {
                QueryId = session.Id,
                Line = session.Line,
                LineId = session.LineId,
                RowId = session.RowId,
                Source = session.Source,
                StopSig = current.StopSig,
                Model = session.Model,
                SecondaryModel = session.SecondaryModel,
                ModelEntryIndex = session.ModelEntryIndex,
                ModelPairSignature = session.ModelPairSignature,
                RouteSignature = session.RouteSignature,
                PathSignature = session.Source == "theory" ? session.ActualPathSignature : session.PathSignature,
                RequestPathSignature = session.PathSignature,
                ModelSignature = session.ModelSignature,
                TheorySegments = session.TheorySegments,
                WaypointIndices = session.Selected.WaypointIndices.ToArray(),
                Segments = session.Segments.ToArray()
            };
            return true;
        }

        internal void Consume(string queryId)
        {
            if (string.IsNullOrEmpty(queryId) || !m_Sessions.TryGetValue(queryId, out RunChartSession session))
                return;
            if (session.Ticket.IsValid && session.State != "Completed")
                RailEtaBridgeService.Current?.Cancel(session.Ticket);
            ReleaseSession(session);
            m_Sessions.Remove(queryId);
        }

        internal void Clear()
        {
            foreach (RunChartSession session in m_Sessions.Values)
            {
                if (session.Ticket.IsValid && session.State != "Completed")
                    RailEtaBridgeService.Current?.Cancel(session.Ticket);
                ReleaseSession(session);
            }
            m_Sessions.Clear();
        }

        internal void CancelLines(IEnumerable<string> lineIds)
        {
            if (lineIds == null)
                return;
            HashSet<string> invalidated = new HashSet<string>(lineIds.Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
            if (invalidated.Count == 0)
                return;
            List<string> remove = new List<string>();
            foreach (KeyValuePair<string, RunChartSession> entry in m_Sessions)
            {
                if (entry.Value == null || !invalidated.Contains(entry.Value.LineId))
                    continue;
                if (entry.Value.Ticket.IsValid && entry.Value.State != "Completed")
                    RailEtaBridgeService.Current?.Cancel(entry.Value.Ticket);
                ReleaseSession(entry.Value);
                remove.Add(entry.Key);
            }
            for (int i = 0; i < remove.Count; i++)
                m_Sessions.Remove(remove[i]);
        }

        private DispatchWorkbenchRunChartStatusDto Begin(
            RunChartSession session,
            DispatchWorkbenchRunChartRequestDto request)
        {
            if (!CurrentPlan(session, out RoutePlan current))
                return FailSession(session, "run-chart-route-changed");
            session.Plan = current;
            if (session.Source == "observed")
            {
                if (!BuildObserved(session))
                    return FailSession(session, "run-chart-observation-missing");
                session.State = "Completed";
                return Status(session);
            }
            if (session.Lifecycle != LifecycleKind.Rail)
                return FailSession(session, "run-chart-theory-unsupported");
            if (!ResolveModels(session.Line, request.modelIndex, request.modelVersion,
                request.secondaryModelIndex, request.secondaryModelVersion,
                out session.Model, out session.SecondaryModel, out session.ModelEntryIndex,
                out string modelFailure))
                return FailSession(session, modelFailure);
            if (session.Model == Entity.Null || !m_Entities.Exists(session.Model)
                || !m_Entities.HasComponent<TrainData>(session.Model)
                || !m_Entities.HasComponent<ObjectGeometryData>(session.Model))
                return FailSession(session, "run-chart-theory-model-missing");

            session.RouteSignature = RunChartSignatures.Route(m_Entities, session.Line, current);
            session.PathSignature = RunChartSignatures.Path(
                m_Entities, session.Line, session.Selected.WaypointIndices);
            session.ModelSignature = RunChartSignatures.Model(
                m_Entities, session.Model, session.SecondaryModel);
            session.ModelPairSignature = RunChartSignatures.ModelPair(
                m_Entities, session.Line, session.ModelEntryIndex,
                session.Model, session.SecondaryModel);
            if (session.RouteSignature == 0 || session.PathSignature == 0
                || session.ModelSignature == 0 || session.ModelPairSignature == 0)
                return FailSession(session, "run-chart-theory-signature-missing");

            RailEtaTheorySegmentRequest[] requests = BuildTheoryRequests(session);
            if (requests.Length == 0
                || requests.Length > MaxPathSlots
                || session.Selected.StopWaypointIndices.Count - 1 > MaxSegments)
                return FailSession(session, "run-chart-segment-limit");
            RailEtaBridgeService service = RailEtaBridgeService.Current;
            if (service == null || !service.CanSubmit)
                return FailSession(session, "run-chart-theory-busy");
            session.Ticket = service.RequestTheorySegments(
                session.Line.Index,
                session.Line.Version,
                session.Model.Index,
                session.Model.Version,
                requests,
                session.SecondaryModel.Index,
                session.SecondaryModel.Version,
                session.RouteSignature,
                session.PathSignature,
                session.ModelSignature);
            if (!session.Ticket.IsValid)
                return FailSession(session, "run-chart-theory-submit-failed");
            session.State = "Queued";
            session.Error = string.Empty;
            session.StartTicks = Stopwatch.GetTimestamp();
            return Status(session);
        }

        private static long ElapsedMilliseconds(long startTicks)
        {
            return startTicks <= 0
                ? 0
                : (long)((Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency);
        }

        private DispatchWorkbenchRunChartStatusDto PollStatus(RunChartSession session)
        {
            if (session.State == "Completed" && !CurrentPlan(session, out _))
            {
                session.State = "Failed";
                session.Error = "run-chart-route-changed";
                session.Segments = null;
                return Status(session);
            }
            if (session.Ticket.IsValid && session.State != "Completed" && session.State != "Failed"
                && session.State != "Cancelled")
            {
                if (!CurrentPlan(session, out _))
                {
                    RailEtaBridgeService.Current?.Cancel(session.Ticket);
                    session.State = "Failed";
                    session.Error = "run-chart-route-changed";
                    ReleaseSession(session);
                    return Status(session);
                }
                if (ElapsedMilliseconds(session.StartTicks) >= TheoryTimeoutMilliseconds)
                {
                    RailEtaBridgeService.Current?.Cancel(session.Ticket);
                    session.State = "Failed";
                    session.Error = "run-chart-theory-timeout";
                    SetSessionFailure(session, RailEtaFailure.FuturePathfindFailed.ToString(), session.Error);
                    ReleaseSession(session);
                    return Status(session);
                }
                RailEtaPublicStatus status;
                if (RailEtaBridgeService.Current == null
                    || !RailEtaBridgeService.Current.TryGetState(session.Ticket, out status))
                {
                    session.State = "Failed";
                    session.Error = "run-chart-theory-status-missing";
                    SetSessionFailure(session, RailEtaFailure.InvalidResult.ToString(), session.Error);
                    ReleaseSession(session);
                }
                else if (status.State == "Completed")
                {
                    if (!ApplyTheory(session, status.TheorySegments))
                    {
                        FailSession(session, "run-chart-theory-result-invalid");
                        ReleaseSession(session);
                    }
                    else
                    {
                        session.State = "Completed";
                        ReleaseSession(session);
                    }
                }
                else if (status.State == "Failed" || status.State == "Cancelled"
                    || status.State == "Busy" || status.State == "Unavailable"
                    || status.State == "ClockChanged" || status.State == "WorkerLost")
                {
                    session.State = status.State;
                    session.TheoryFailure = status.TheoryFailure;
                    session.Error = String.IsNullOrEmpty(status.Detail)
                        ? status.Failure
                        : status.Detail;
                    SetSessionFailure(session, status.Failure, session.Error);
                    ReleaseSession(session);
                }
            }
            return Status(session);
        }

        private bool ApplyTheory(RunChartSession session, RailEtaTheorySegmentResult[] results)
        {
            if (results == null || results.Length == 0 || !CurrentPlan(session, out RoutePlan current)
                || results.Length != session.Selected.StopWaypointIndices.Count - 1)
                return false;
            if (!CurrentSignatures(session, current))
                return false;
            var seen = new HashSet<int>();
            for (int i = 0; i < results.Length; i++)
            {
                RailEtaTheorySegmentResult result = results[i];
                if (result == null || result.State != "Completed" || result.SegmentFrames == 0
                    || result.SegmentIndex < 0
                    || result.SegmentIndex >= session.Selected.StopWaypointIndices.Count - 1
                    || !seen.Add(result.SegmentIndex)
                    || result.FromWaypointIndex != session.Selected.StopWaypointIndices[result.SegmentIndex]
                    || result.ToWaypointIndex != session.Selected.StopWaypointIndices[result.SegmentIndex + 1]
                    || result.RouteSignature != session.RouteSignature
                    || result.PathSignature == 0
                    || result.ModelSignature != session.ModelSignature)
                    return false;
            }
            ulong actualPathSignature = results[0].PathSignature;
            for (int i = 1; i < results.Length; i++)
                if (results[i].PathSignature != actualPathSignature)
                    return false;
            if (!RunChartSignatures.ValidatePathFacts(m_Entities, session.Line, results, actualPathSignature))
                return false;
            List<RunChartSegment> segments = BuildIntervals(session.Selected, current);
            for (int i = 0; i < segments.Count; i++)
            {
                RunChartSegment interval = segments[i];
                RailEtaTheorySegmentResult result = results.FirstOrDefault(value =>
                    value != null && value.SegmentIndex == i);
                if (result == null || result.SegmentFrames == 0) return false;
                interval.Frames = result.SegmentFrames;
                interval.Minutes = ToMinutes(result.SegmentFrames);
                segments[i] = interval;
            }
            session.Segments = segments;
            session.ActualPathSignature = actualPathSignature;
            session.TheorySegments = results.ToArray();
            return true;
        }

        private bool CurrentSignatures(RunChartSession session, RoutePlan current)
        {
            return session.RouteSignature != 0
                && session.PathSignature != 0
                && session.ModelSignature != 0
                && RunChartSignatures.Route(m_Entities, session.Line, current) == session.RouteSignature
                && RunChartSignatures.Path(m_Entities, session.Line, session.Selected.WaypointIndices) == session.PathSignature
                && RunChartSignatures.ModelPair(
                    m_Entities, session.Line, session.ModelEntryIndex,
                    session.Model, session.SecondaryModel) == session.ModelPairSignature
                && RunChartSignatures.Model(m_Entities, session.Model, session.SecondaryModel) == session.ModelSignature;
        }

        private bool BuildObserved(RunChartSession session)
        {
            if (!CurrentPlan(session, out RoutePlan current)) return false;
            List<RunChartSegment> segments = BuildIntervals(session.Selected, current);
            for (int i = 0; i < segments.Count; i++)
            {
                RunChartSegment interval = segments[i];
                RouteWaypointRef from = current.Waypoints[interval.FromWaypointIndex];
                RouteWaypointRef to = current.Waypoints[interval.ToWaypointIndex];
                float frames;
                bool found = session.Lifecycle == LifecycleKind.Rail
                    ? m_Observation.TryRailSegmentFrames(
                        session.Line, from.Waypoint, from.Stop, to.Waypoint, to.Stop, out frames)
                    : m_Observation.TryBusSegFrames(
                        session.Line, from.Waypoint, from.Stop, to.Waypoint, to.Stop, out frames);
                if (!found || frames <= 0f || float.IsNaN(frames) || float.IsInfinity(frames))
                    return false;
                interval.Frames = (uint)Math.Max(1, Math.Round(frames));
                interval.Minutes = ToMinutes(frames);
                segments[i] = interval;
            }
            session.Segments = segments;
            return segments.Count > 0;
        }

        private RailEtaTheorySegmentRequest[] BuildTheoryRequests(RunChartSession session)
        {
            List<int> waypoints = session.Selected.WaypointIndices;
            List<int> stops = session.Selected.StopWaypointIndices;
            var requests = new List<RailEtaTheorySegmentRequest>(Math.Max(0, waypoints.Count - 1));
            for (int segmentIndex = 0; segmentIndex + 1 < stops.Count; segmentIndex++)
            {
                int fromStop = stops[segmentIndex];
                int toStop = stops[segmentIndex + 1];
                int fromPosition = waypoints.IndexOf(fromStop);
                int toPosition = waypoints.IndexOf(toStop, fromPosition + 1);
                if (fromPosition < 0 || toPosition <= fromPosition) continue;
                for (int position = fromPosition; position < toPosition; position++)
                {
                    RouteWaypointRef from = session.Plan.Waypoints[waypoints[position]];
                    RouteWaypointRef to = session.Plan.Waypoints[waypoints[position + 1]];
                    requests.Add(new RailEtaTheorySegmentRequest
                    {
                        SegmentIndex = segmentIndex,
                        PathSlotIndex = from.WaypointIndex,
                        FromWaypointIndex = from.WaypointIndex,
                        FromWaypointVersion = from.Waypoint.Version,
                        ToWaypointIndex = to.WaypointIndex,
                        ToWaypointVersion = to.Waypoint.Version,
                        SegmentFromWaypointIndex = fromStop,
                        SegmentToWaypointIndex = toStop
                    });
                }
            }
            return requests.ToArray();
        }

        private List<RunChartSegment> BuildIntervals(RunChartCandidate candidate, RoutePlan plan)
        {
            var result = new List<RunChartSegment>();
            for (int i = 0; i + 1 < candidate.StopWaypointIndices.Count; i++)
            {
                result.Add(new RunChartSegment
                {
                    FromWaypointIndex = candidate.StopWaypointIndices[i],
                    ToWaypointIndex = candidate.StopWaypointIndices[i + 1],
                    FromStopKey = plan.Waypoints[candidate.StopWaypointIndices[i]].StopKey,
                    ToStopKey = plan.Waypoints[candidate.StopWaypointIndices[i + 1]].StopKey
                });
            }
            return result;
        }

        private bool CurrentPlan(RunChartSession session, out RoutePlan plan)
        {
            plan = null;
            if (!m_RoutePlans.TryGet(session.Line, session.Lifecycle, out plan)
                || plan.Waypoints.Length != session.Plan.Waypoints.Length
                || !String.Equals(plan.StopSig, session.Plan.StopSig, StringComparison.Ordinal))
                return false;
            for (int i = 0; i < plan.Waypoints.Length; i++)
                if (plan.Waypoints[i].Waypoint != session.Plan.Waypoints[i].Waypoint)
                    return false;
            return true;
        }

        private bool ResolveModels(Entity line, int index, int version, int secondaryIndex, int secondaryVersion,
            out Entity primary, out Entity secondary, out int modelEntryIndex, out string failure)
        {
            primary = Entity.Null;
            secondary = Entity.Null;
            modelEntryIndex = -1;
            failure = string.Empty;
            bool primarySpecified = index != 0;
            bool secondarySpecified = secondaryIndex != 0;
            Entity requested = primarySpecified
                ? new Entity { Index = index, Version = version }
                : Entity.Null;
            Entity requestedSecondary = secondarySpecified
                ? new Entity { Index = secondaryIndex, Version = secondaryVersion }
                : Entity.Null;
            if (!m_Entities.HasBuffer<VehicleModel>(line))
            {
                failure = "run-chart-theory-model-pair-missing";
                return false;
            }
            DynamicBuffer<VehicleModel> models = m_Entities.GetBuffer<VehicleModel>(line, true);
            if (models.Length == 0)
            {
                failure = "run-chart-theory-model-pair-missing";
                return false;
            }
            if (primarySpecified && !HasPhysics(requested))
            {
                failure = "run-chart-theory-model-missing";
                return false;
            }
            if (secondarySpecified && !HasPhysics(requestedSecondary))
            {
                failure = "run-chart-theory-secondary-model-missing";
                return false;
            }
            bool primaryOnLine = false;
            for (int i = 0; i < models.Length; i++)
            {
                bool primaryMatches = !primarySpecified || models[i].m_PrimaryPrefab == requested;
                if (models[i].m_PrimaryPrefab == requested)
                    primaryOnLine = true;
                bool secondaryMatches = !secondarySpecified
                    || models[i].m_SecondaryPrefab == requestedSecondary;
                if (primaryMatches && secondaryMatches)
                {
                    modelEntryIndex = i;
                    break;
                }
            }
            if (modelEntryIndex < 0)
            {
                failure = primarySpecified && !primaryOnLine
                    ? "run-chart-theory-model-line-mismatch"
                    : secondarySpecified
                        ? "run-chart-theory-model-pair-mismatch"
                        : "run-chart-theory-model-line-mismatch";
                return false;
            }
            primary = models[modelEntryIndex].m_PrimaryPrefab;
            if (!HasPhysics(primary))
            {
                failure = "run-chart-theory-model-missing";
                return false;
            }
            Entity pairedSecondary = models[modelEntryIndex].m_SecondaryPrefab;
            if (secondarySpecified)
            {
                if (requestedSecondary != pairedSecondary)
                {
                    failure = "run-chart-theory-model-pair-mismatch";
                    return false;
                }
                secondary = requestedSecondary;
            }
            else
            {
                if (pairedSecondary != Entity.Null && !HasPhysics(pairedSecondary))
                {
                    failure = "run-chart-theory-secondary-model-missing";
                    return false;
                }
                secondary = pairedSecondary;
            }
            return true;
        }

        private bool HasPhysics(Entity model)
        {
            return model != Entity.Null && m_Entities.Exists(model)
                && m_Entities.HasComponent<TrainData>(model)
                && m_Entities.HasComponent<ObjectGeometryData>(model);
        }

        private int ToMinutes(double frames)
        {
            double rate = m_FramesPerMinute();
            return rate > 0d && !double.IsNaN(rate) && !double.IsInfinity(rate)
                ? Math.Max(1, (int)Math.Round(frames / rate, MidpointRounding.AwayFromZero))
                : Math.Max(1, (int)Math.Round(frames, MidpointRounding.AwayFromZero));
        }

        private static string NormalizeSource(string source)
        {
            return String.Equals(source, "observed", StringComparison.OrdinalIgnoreCase)
                ? "observed"
                : "theory";
        }

        private static List<RunChartCandidate> BuildCandidates(
            RoutePlan plan,
            string fromKey,
            string toKey,
            string[] viaKeys)
        {
            var result = new List<RunChartCandidate>();
            if (plan == null || plan.Waypoints.Length == 0
                || String.IsNullOrEmpty(fromKey) || String.IsNullOrEmpty(toKey))
                return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int start = 0; start < plan.Waypoints.Length; start++)
            {
                if (!String.Equals(plan.Waypoints[start].StopKey, fromKey, StringComparison.Ordinal)) continue;
                for (int distance = 1; distance <= plan.Waypoints.Length; distance++)
                {
                    int end = (start + distance) % plan.Waypoints.Length;
                    if (!String.Equals(plan.Waypoints[end].StopKey, toKey, StringComparison.Ordinal)) continue;
                    string identity = start + ":" + end;
                    if (!seen.Add(identity) || !MatchesVia(plan, start, distance, viaKeys)) continue;
                    var waypointIndices = new List<int>(distance + 1);
                    var stopWaypointIndices = new List<int>();
                    var stopKeys = new List<string>();
                    for (int offset = 0; offset <= distance; offset++)
                    {
                        int index = (start + offset) % plan.Waypoints.Length;
                        waypointIndices.Add(index);
                        if (plan.Waypoints[index].Stop != Entity.Null)
                        {
                            stopWaypointIndices.Add(index);
                            stopKeys.Add(plan.Waypoints[index].StopKey);
                        }
                    }
                    result.Add(new RunChartCandidate
                    {
                        WaypointIndices = waypointIndices,
                        StopWaypointIndices = stopWaypointIndices,
                        StopKeys = stopKeys
                    });
                }
            }
            return result;
        }

        private static bool MatchesVia(RoutePlan plan, int start, int distance, string[] viaKeys)
        {
            int cursor = 0;
            if (viaKeys == null || viaKeys.Length == 0) return true;
            for (int offset = 0; offset <= distance && cursor < viaKeys.Length; offset++)
            {
                int index = (start + offset) % plan.Waypoints.Length;
                if (String.Equals(plan.Waypoints[index].StopKey, viaKeys[cursor], StringComparison.Ordinal)) cursor++;
            }
            return cursor == viaKeys.Length;
        }

        private DispatchWorkbenchRunChartStatusDto FailSession(RunChartSession session, string error)
        {
            session.State = "Failed";
            session.Error = error;
            return Status(session);
        }

        private static void SetSessionFailure(RunChartSession session, string failure, string detail)
        {
            if (session == null || session.TheoryFailure != null
                || session.Selected == null || session.Selected.StopWaypointIndices.Count < 2)
                return;
            session.TheoryFailure = new RailEtaTheoryFailure
            {
                SegmentIndex = 0,
                FromWaypointIndex = session.Selected.StopWaypointIndices[0],
                ToWaypointIndex = session.Selected.StopWaypointIndices[1],
                Failure = failure ?? string.Empty,
                Detail = detail ?? string.Empty
            };
        }

        private static DispatchWorkbenchRunChartStatusDto Failure(
            string queryId,
            string error,
            RunChartSession session = null)
        {
            return new DispatchWorkbenchRunChartStatusDto
            {
                queryId = queryId ?? string.Empty,
                state = "Failed",
                source = session?.Source ?? string.Empty,
                lineId = session?.LineId ?? string.Empty,
                stopSig = session?.Plan?.StopSig ?? string.Empty,
                error = error,
                failureSegmentIndex = session?.TheoryFailure?.SegmentIndex ?? -1,
                failureFromWaypointIndex = session?.TheoryFailure?.FromWaypointIndex ?? -1,
                failureToWaypointIndex = session?.TheoryFailure?.ToWaypointIndex ?? -1,
                failureCode = session?.TheoryFailure?.Failure ?? string.Empty,
                failureDetail = session?.TheoryFailure?.Detail ?? string.Empty,
                candidates = session == null ? Array.Empty<DispatchWorkbenchRunChartCandidateDto>() : Candidates(session),
                segments = Array.Empty<DispatchWorkbenchRunChartSegmentDto>()
            };
        }

        private static DispatchWorkbenchRunChartStatusDto Status(RunChartSession session)
        {
            return new DispatchWorkbenchRunChartStatusDto
            {
                queryId = session.Id,
                state = session.State,
                source = session.Source,
                lineId = session.LineId,
                stopSig = session.Plan?.StopSig ?? string.Empty,
                error = session.Error ?? string.Empty,
                failureSegmentIndex = session.TheoryFailure?.SegmentIndex ?? -1,
                failureFromWaypointIndex = session.TheoryFailure?.FromWaypointIndex ?? -1,
                failureToWaypointIndex = session.TheoryFailure?.ToWaypointIndex ?? -1,
                failureCode = session.TheoryFailure?.Failure ?? string.Empty,
                failureDetail = session.TheoryFailure?.Detail ?? string.Empty,
                candidates = Candidates(session),
                segments = session.Segments == null
                    ? Array.Empty<DispatchWorkbenchRunChartSegmentDto>()
                    : session.Segments.Select(segment => new DispatchWorkbenchRunChartSegmentDto
                    {
                        fromStopKey = segment.FromStopKey,
                        toStopKey = segment.ToStopKey,
                        fromWaypointIndex = segment.FromWaypointIndex,
                        toWaypointIndex = segment.ToWaypointIndex,
                        segmentFrames = segment.Frames,
                        segmentMinutes = segment.Minutes
                    }).ToArray()
            };
        }

        private static DispatchWorkbenchRunChartCandidateDto[] Candidates(RunChartSession session)
        {
            return (session.Candidates ?? new List<RunChartCandidate>()).Select((candidate, index) => new DispatchWorkbenchRunChartCandidateDto
            {
                candidateIndex = index,
                waypointIndices = candidate.WaypointIndices.ToArray(),
                stopWaypointIndices = candidate.StopWaypointIndices.ToArray(),
                stopKeys = candidate.StopKeys.ToArray(),
                direction = "forward"
            }).ToArray();
        }

        private bool TryReserveSession()
        {
            while (m_Sessions.Count >= 8)
            {
                string remove = m_Sessions.Keys.FirstOrDefault(key => m_Sessions[key].State == "Completed"
                    || m_Sessions[key].State == "Failed" || m_Sessions[key].State == "Cancelled");
                if (remove == null) return false;
                if (m_Sessions.TryGetValue(remove, out RunChartSession session))
                    ReleaseSession(session);
                m_Sessions.Remove(remove);
            }
            return true;
        }

        private static void ReleaseSession(RunChartSession session)
        {
            if (session == null || !session.Ticket.IsValid)
                return;
            RailEtaBridgeService.Current?.Release(session.Ticket);
            session.Ticket = default;
        }
    }

    internal sealed class RunChartSession
    {
        internal string Id;
        internal Entity Line;
        internal string LineId;
        internal string RowId;
        internal LifecycleKind Lifecycle;
        internal RoutePlan Plan;
        internal List<RunChartCandidate> Candidates;
        internal RunChartCandidate Selected;
        internal string Source;
        internal string State;
        internal string Error;
        internal RailEtaTheoryFailure TheoryFailure;
        internal Entity Model;
        internal Entity SecondaryModel;
        internal int ModelEntryIndex = -1;
        internal ulong ModelPairSignature;
        internal ulong RouteSignature;
        internal ulong PathSignature;
        internal ulong ActualPathSignature;
        internal ulong ModelSignature;
        internal long StartTicks;
        internal RailEtaPublicTicket Ticket;
        internal RailEtaTheorySegmentResult[] TheorySegments;
        internal List<RunChartSegment> Segments;
    }

    internal sealed class RunChartCandidate
    {
        internal List<int> WaypointIndices = new List<int>();
        internal List<int> StopWaypointIndices = new List<int>();
        internal List<string> StopKeys = new List<string>();
    }

    internal sealed class RunChartSegment
    {
        internal string FromStopKey;
        internal string ToStopKey;
        internal int FromWaypointIndex;
        internal int ToWaypointIndex;
        internal uint Frames;
        internal int Minutes;
    }

    internal sealed class RunChartResult
    {
        internal string QueryId;
        internal Entity Line;
        internal string LineId;
        internal string RowId;
        internal string Source;
        internal string StopSig;
        internal Entity Model;
        internal Entity SecondaryModel;
        internal int ModelEntryIndex;
        internal ulong ModelPairSignature;
        internal ulong RouteSignature;
        internal ulong PathSignature;
        internal ulong RequestPathSignature;
        internal ulong ModelSignature;
        internal RailEtaTheorySegmentResult[] TheorySegments;
        internal int[] WaypointIndices;
        internal RunChartSegment[] Segments;
    }

    internal static class RunChartSignatures
    {
        internal static ulong Route(EntityManager entities, Entity line, RoutePlan plan)
        {
            if (line == Entity.Null || plan == null || plan.Waypoints.Length == 0)
                return 0;
            ulong hash = RailEtaTheorySignatures.Seed;
            hash = MixEntity(hash, line);
            hash = RailEtaTheorySignatures.Mix(hash, plan.Waypoints.Length);
            hash = RailEtaTheorySignatures.Mix(hash, plan.StopSig);
            for (int i = 0; i < plan.Waypoints.Length; i++)
            {
                RouteWaypointRef waypoint = plan.Waypoints[i];
                hash = MixEntity(hash, waypoint.Waypoint);
                hash = MixEntity(hash, waypoint.Stop);
                hash = RailEtaTheorySignatures.Mix(hash, waypoint.StopKey);
            }
            return hash;
        }

        internal static ulong Path(EntityManager entities, Entity line, IReadOnlyList<int> indices)
        {
            if (line == Entity.Null || indices == null || indices.Count < 2
                || !entities.HasBuffer<RouteWaypoint>(line)
                || !entities.HasBuffer<RouteSegment>(line))
                return 0;
            DynamicBuffer<RouteWaypoint> waypoints = entities.GetBuffer<RouteWaypoint>(line, true);
            DynamicBuffer<RouteSegment> routeSegments = entities.GetBuffer<RouteSegment>(line, true);
            ulong hash = RailEtaTheorySignatures.Seed;
            hash = RailEtaTheorySignatures.Mix(hash, indices.Count - 1);
            for (int i = 0; i + 1 < indices.Count; i++)
            {
                int index = indices[i];
                int nextIndex = indices[i + 1];
                if (index < 0 || index >= routeSegments.Length || index >= waypoints.Length
                    || nextIndex < 0 || nextIndex >= waypoints.Length)
                    return 0;
                Entity waypoint = waypoints[index].m_Waypoint;
                hash = MixEntity(hash, waypoint);
                if (entities.HasComponent<RouteLane>(waypoint))
                {
                    RouteLane lane = entities.GetComponentData<RouteLane>(waypoint);
                    hash = MixEntity(hash, lane.m_StartLane);
                    hash = MixEntity(hash, lane.m_EndLane);
                    hash = RailEtaTheorySignatures.Mix(hash, lane.m_StartCurvePos);
                    hash = RailEtaTheorySignatures.Mix(hash, lane.m_EndCurvePos);
                }
                Entity owner = routeSegments[index].m_Segment;
                hash = MixEntity(hash, owner);
                Entity targetWaypoint = waypoints[nextIndex].m_Waypoint;
                hash = MixEntity(hash, targetWaypoint);
                if (entities.HasComponent<RouteLane>(targetWaypoint))
                {
                    RouteLane targetLane = entities.GetComponentData<RouteLane>(targetWaypoint);
                    hash = MixEntity(hash, targetLane.m_StartLane);
                    hash = MixEntity(hash, targetLane.m_EndLane);
                    hash = RailEtaTheorySignatures.Mix(hash, targetLane.m_StartCurvePos);
                    hash = RailEtaTheorySignatures.Mix(hash, targetLane.m_EndCurvePos);
                }
                if (owner != Entity.Null && entities.HasBuffer<PathElement>(owner))
                {
                    DynamicBuffer<PathElement> elements = entities.GetBuffer<PathElement>(owner, true);
                    hash = RailEtaTheorySignatures.Mix(hash, elements.Length);
                    for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
                    {
                        PathElement element = elements[elementIndex];
                        hash = MixEntity(hash, element.m_Target);
                        hash = RailEtaTheorySignatures.Mix(hash, element.m_TargetDelta.x);
                        hash = RailEtaTheorySignatures.Mix(hash, element.m_TargetDelta.y);
                        hash = RailEtaTheorySignatures.Mix(hash, (int)element.m_Flags);
                        if (entities.HasComponent<Curve>(element.m_Target))
                            hash = RailEtaTheorySignatures.Mix(
                                hash, entities.GetComponentData<Curve>(element.m_Target).m_Length);
                    }
                }
            }
            return hash;
        }

        internal static ulong Model(EntityManager entities, Entity primary, Entity secondary)
        {
            return RapidTransitMod.RailEta.BuiltIn.RailEtaTheoryVehicle.TryGetModelSignature(
                entities, primary, secondary, out ulong signature) ? signature : 0;
        }

        internal static ulong ModelPair(EntityManager entities, Entity line, int entryIndex,
            Entity primary, Entity secondary)
        {
            if (line == Entity.Null || entryIndex < 0
                || !entities.HasBuffer<VehicleModel>(line))
                return 0;
            DynamicBuffer<VehicleModel> models = entities.GetBuffer<VehicleModel>(line, true);
            if (entryIndex >= models.Length
                || models[entryIndex].m_PrimaryPrefab != primary
                || models[entryIndex].m_SecondaryPrefab != secondary)
                return 0;
            ulong hash = RailEtaTheorySignatures.Seed;
            hash = MixEntity(hash, line);
            hash = RailEtaTheorySignatures.Mix(hash, entryIndex);
            hash = MixEntity(hash, primary);
            return MixEntity(hash, secondary);
        }

        internal static bool ValidatePathFacts(
            EntityManager entities, Entity line, RailEtaTheorySegmentResult[] results, ulong expectedSignature)
        {
            if (entities == default || line == Entity.Null || !entities.Exists(line)
                || results == null || results.Length == 0 || expectedSignature == 0)
                return false;
            RailEtaTheorySegmentResult[] ordered = results.ToArray();
            Array.Sort(ordered, (left, right) => left.SegmentIndex.CompareTo(right.SegmentIndex));
            ulong hash = RailEtaTheorySignatures.Seed;
            int factCount = 0;
            long totalSourceElements = 0;
            var factsBySlot = new Dictionary<int, List<RailEtaTheoryPathFact>>();
            for (int i = 0; i < ordered.Length; i++)
            {
                RailEtaTheorySegmentResult result = ordered[i];
                if (result == null || result.SegmentIndex != i
                    || result.FromWaypointIndex < 0 || result.ToWaypointIndex < 0
                    || result.PathSourceElementCount <= 0
                    || result.PathSourceElementCount > RailEtaTheorySignatures.MaxPathFacts
                    || result.PathSkippedElementCount < 0
                    || result.PathSkippedElementCount > result.PathSourceElementCount
                    || result.PathFacts == null || result.PathFacts.Length == 0)
                    return false;
                totalSourceElements += result.PathSourceElementCount;
                if (totalSourceElements > RailEtaTheorySignatures.MaxPathFacts)
                    return false;
                factCount += result.PathFacts.Length;
                if (factCount > RailEtaTheorySignatures.MaxPathFacts
                    || result.PathFacts.Length > result.PathSourceElementCount)
                    return false;
                for (int factIndex = 0; factIndex < result.PathFacts.Length; factIndex++)
                {
                    RailEtaTheoryPathFact fact = result.PathFacts[factIndex];
                    if (!ValidatePathFact(entities, line, fact))
                        return false;
                    if (!factsBySlot.TryGetValue(fact.PathSlotIndex, out List<RailEtaTheoryPathFact> slotFacts))
                        factsBySlot[fact.PathSlotIndex] = slotFacts = new List<RailEtaTheoryPathFact>();
                    slotFacts.Add(fact);
                }
                hash = RailEtaTheorySignatures.MixPath(hash, result.SegmentIndex,
                    result.FromWaypointIndex, result.ToWaypointIndex,
                    result.PathSourceElementCount, result.PathSkippedElementCount, result.PathFacts);
            }
            foreach (KeyValuePair<int, List<RailEtaTheoryPathFact>> entry in factsBySlot)
                if (!ValidatePathSequence(entities, entry.Value))
                    return false;
            return hash == expectedSignature;
        }

        private static bool ValidatePathSequence(
            EntityManager entities, List<RailEtaTheoryPathFact> facts)
        {
            if (facts == null || facts.Count == 0)
                return false;
            RailEtaTheoryPathFact first = facts[0];
            RailEtaTheoryPathFact last = facts[facts.Count - 1];
            Entity firstLane = FactEntity(first.LaneIndex, first.LaneVersion);
            Entity lastLane = FactEntity(last.LaneIndex, last.LaneVersion);
            if (!ValidateRouteLaneEndpoint(first, true, firstLane)
                || !ValidateRouteLaneEndpoint(last, false, lastLane))
                return false;
            for (int i = 0; i < facts.Count; i++)
            {
                RailEtaTheoryPathFact fact = facts[i];
                Entity previous = FactEntity(fact.PreviousLaneIndex, fact.PreviousLaneVersion);
                Entity next = FactEntity(fact.NextLaneIndex, fact.NextLaneVersion);
                if (i == 0)
                {
                    if (previous != Entity.Null) return false;
                }
                else
                {
                    RailEtaTheoryPathFact prior = facts[i - 1];
                    if (previous != FactEntity(prior.LaneIndex, prior.LaneVersion))
                        return false;
                }
                if (i + 1 >= facts.Count)
                {
                    if (next != Entity.Null || fact.NextConnectionDistance != 0f)
                        return false;
                    continue;
                }
                RailEtaTheoryPathFact following = facts[i + 1];
                Entity followingLane = FactEntity(following.LaneIndex, following.LaneVersion);
                if (next != followingLane
                    || !Finite(fact.NextConnectionDistance)
                    || fact.NextConnectionDistance < 0f
                    || fact.NextConnectionDistance > 0.5f
                    || !TryConnectionDistance(entities, fact, following, out float distance)
                    || Math.Abs(distance - fact.NextConnectionDistance) > 0.001f)
                    return false;
            }
            return true;
        }

        private static bool ValidateRouteLaneEndpoint(
            RailEtaTheoryPathFact fact, bool from, Entity lane)
        {
            if (fact == null || lane == Entity.Null)
                return false;
            if (from)
            {
                if (fact.FromRouteLanePresent == 0 || fact.FromRouteLaneSide < 0
                    || fact.FromRouteLaneSide > 1)
                    return false;
                return lane == (fact.FromRouteLaneSide == 0
                    ? FactEntity(fact.FromStartLaneIndex, fact.FromStartLaneVersion)
                    : FactEntity(fact.FromEndLaneIndex, fact.FromEndLaneVersion));
            }
            if (fact.ToRouteLanePresent == 0 || fact.ToRouteLaneSide < 0
                || fact.ToRouteLaneSide > 1)
                return false;
            return lane == (fact.ToRouteLaneSide == 0
                ? FactEntity(fact.ToStartLaneIndex, fact.ToStartLaneVersion)
                : FactEntity(fact.ToEndLaneIndex, fact.ToEndLaneVersion));
        }

        private static bool ValidatePathFact(
            EntityManager entities, Entity line, RailEtaTheoryPathFact fact)
        {
            if (fact == null || fact.Kind < 0 || fact.Kind > 1
                || fact.PathSlotIndex < 0 || fact.PathElementIndex < -1
                || fact.PathOwnerStable < 0 || fact.PathOwnerStable > 1
                || !Finite(fact.StartFraction) || !Finite(fact.EndFraction)
                || !Finite(fact.CurveLength) || !Finite(fact.Length)
                || !Finite(fact.SpeedLimit) || !Finite(fact.Curviness)
                || !Finite(fact.NextConnectionDistance)
                || !Finite(fact.CurveAX) || !Finite(fact.CurveAY) || !Finite(fact.CurveAZ)
                || !Finite(fact.CurveBX) || !Finite(fact.CurveBY) || !Finite(fact.CurveBZ)
                || !Finite(fact.CurveCX) || !Finite(fact.CurveCY) || !Finite(fact.CurveCZ)
                || !Finite(fact.CurveDX) || !Finite(fact.CurveDY) || !Finite(fact.CurveDZ))
                return false;
            if (!TryReadRouteNetwork(entities, line, fact, out RailEtaTheoryPathFact current))
                return false;
            if (fact.RouteNetworkSignature == 0
                || fact.RouteNetworkSignature != RailEtaTheorySignatures.RouteNetworkSignature(fact)
                || fact.RouteNetworkSignature != current.RouteNetworkSignature)
                return false;
            if (fact.PathOwnerStable != 0)
            {
                if (fact.PathElementIndex < 0)
                    return false;
                Entity owner = new Entity { Index = fact.PathOwnerIndex, Version = fact.PathOwnerVersion };
                if (owner == Entity.Null || !entities.HasBuffer<PathElement>(owner))
                    return false;
                DynamicBuffer<PathElement> elements = entities.GetBuffer<PathElement>(owner, true);
                if (fact.PathElementIndex >= elements.Length)
                    return false;
                PathElement element = elements[fact.PathElementIndex];
                if (element.m_Target.Index != fact.LaneIndex
                    || element.m_Target.Version != fact.LaneVersion
                    || element.m_TargetDelta.x != fact.StartFraction
                    || element.m_TargetDelta.y != fact.EndFraction
                    || (uint)element.m_Flags != fact.PathFlags)
                    return false;
            }
            else if (fact.PathElementIndex != -1)
                return false;
            Entity lane = new Entity { Index = fact.LaneIndex, Version = fact.LaneVersion };
            if (lane == Entity.Null || !entities.Exists(lane) || !entities.HasComponent<Curve>(lane))
                return false;
            Curve curve = entities.GetComponentData<Curve>(lane);
            float curveLength = Math.Max(0f, curve.m_Length);
            float length = curveLength * Math.Abs(fact.EndFraction - fact.StartFraction);
            if (fact.CurveLength != curveLength || fact.Length != length
                || fact.Direction != (fact.EndFraction > fact.StartFraction ? 1 : -1)
                || !SameCurve(fact, curve))
                return false;
            if (fact.Kind == 0)
            {
                if (!entities.HasComponent<Game.Net.TrackLane>(lane)) return false;
                Game.Net.TrackLane track = entities.GetComponentData<Game.Net.TrackLane>(lane);
                return fact.TrackFlags == (uint)track.m_Flags
                    && fact.SpeedLimit == Math.Max(0f, track.m_SpeedLimit)
                    && fact.Curviness == Math.Max(0f, track.m_Curviness)
                    && fact.AccessRestrictionIndex == track.m_AccessRestriction.Index
                    && fact.AccessRestrictionVersion == track.m_AccessRestriction.Version
                    && fact.ConnectionFlags == 0
                    && fact.ConnectionTrackTypes == 0
                    && fact.ConnectionRoadTypes == 0
                    && fact.EdgeDeltaStart == 0f
                    && fact.EdgeDeltaEnd == 0f
                    && fact.EdgeConnectedStartCount == 0
                    && fact.EdgeConnectedEndCount == 0;
            }
            if (entities.HasComponent<Game.Net.TrackLane>(lane)
                || (!entities.HasComponent<Game.Net.ConnectionLane>(lane)
                    && !entities.HasComponent<Game.Net.EdgeLane>(lane)
                    && (fact.PathFlags & (uint)(PathElementFlags.Secondary
                        | PathElementFlags.Return | PathElementFlags.Leader)) == 0))
                return false;
            if (fact.TrackFlags != 0 || fact.SpeedLimit != Calculator.ConnectionSpeed || fact.Curviness != 0f)
                return false;
            if (entities.HasComponent<Game.Net.ConnectionLane>(lane))
            {
                Game.Net.ConnectionLane connection = entities.GetComponentData<Game.Net.ConnectionLane>(lane);
                if (fact.ConnectionFlags != (uint)connection.m_Flags
                    || fact.ConnectionTrackTypes != (uint)connection.m_TrackTypes
                    || fact.ConnectionRoadTypes != (uint)connection.m_RoadTypes
                    || fact.AccessRestrictionIndex != connection.m_AccessRestriction.Index
                    || fact.AccessRestrictionVersion != connection.m_AccessRestriction.Version)
                    return false;
            }
            else if (fact.ConnectionFlags != 0 || fact.ConnectionTrackTypes != 0
                || fact.ConnectionRoadTypes != 0
                || fact.AccessRestrictionIndex != Entity.Null.Index
                || fact.AccessRestrictionVersion != Entity.Null.Version)
                return false;
            if (entities.HasComponent<Game.Net.EdgeLane>(lane))
            {
                Game.Net.EdgeLane edge = entities.GetComponentData<Game.Net.EdgeLane>(lane);
                return fact.EdgeDeltaStart == edge.m_EdgeDelta.x
                    && fact.EdgeDeltaEnd == edge.m_EdgeDelta.y
                    && fact.EdgeConnectedStartCount == edge.m_ConnectedStartCount
                    && fact.EdgeConnectedEndCount == edge.m_ConnectedEndCount;
            }
            return fact.EdgeDeltaStart == 0f && fact.EdgeDeltaEnd == 0f
                && fact.EdgeConnectedStartCount == 0 && fact.EdgeConnectedEndCount == 0;
        }

        private static bool TryReadRouteNetwork(
            EntityManager entities, Entity line, RailEtaTheoryPathFact expected,
            out RailEtaTheoryPathFact current)
        {
            current = null;
            if (!entities.HasBuffer<RouteSegment>(line) || !entities.HasBuffer<RouteWaypoint>(line))
                return false;
            DynamicBuffer<RouteSegment> routeSegments = entities.GetBuffer<RouteSegment>(line, true);
            DynamicBuffer<RouteWaypoint> waypoints = entities.GetBuffer<RouteWaypoint>(line, true);
            if (expected.PathSlotIndex >= routeSegments.Length
                || expected.FromWaypointIndex < 0 || expected.FromWaypointIndex >= waypoints.Length
                || expected.ToWaypointIndex < 0 || expected.ToWaypointIndex >= waypoints.Length)
                return false;
            Entity from = waypoints[expected.FromWaypointIndex].m_Waypoint;
            Entity to = waypoints[expected.ToWaypointIndex].m_Waypoint;
            Entity owner = routeSegments[expected.PathSlotIndex].m_Segment;
            if (from == Entity.Null || to == Entity.Null
                || from.Index != expected.FromWaypointEntityIndex
                || from.Version != expected.FromWaypointEntityVersion
                || from.Version != expected.FromWaypointVersion
                || to.Index != expected.ToWaypointEntityIndex
                || to.Version != expected.ToWaypointEntityVersion
                || to.Version != expected.ToWaypointVersion
                || owner.Index != expected.PathOwnerIndex
                || owner.Version != expected.PathOwnerVersion)
                return false;
            if (owner != Entity.Null && !entities.Exists(owner))
                return false;
            current = new RailEtaTheoryPathFact
            {
                PathSlotIndex = expected.PathSlotIndex,
                PathOwnerIndex = owner.Index,
                PathOwnerVersion = owner.Version,
                FromWaypointIndex = expected.FromWaypointIndex,
                FromWaypointVersion = from.Version,
                FromWaypointEntityIndex = from.Index,
                FromWaypointEntityVersion = from.Version,
                ToWaypointIndex = expected.ToWaypointIndex,
                ToWaypointVersion = to.Version,
                ToWaypointEntityIndex = to.Index,
                ToWaypointEntityVersion = to.Version
            };
            FillRouteLaneFact(entities, from, out int fromPresent,
                out int fromStartIndex, out int fromStartVersion,
                out int fromEndIndex, out int fromEndVersion,
                out float fromStartCurve, out float fromEndCurve);
            FillRouteLaneFact(entities, to, out int toPresent,
                out int toStartIndex, out int toStartVersion,
                out int toEndIndex, out int toEndVersion,
                out float toStartCurve, out float toEndCurve);
            current.FromRouteLanePresent = fromPresent;
            current.FromStartLaneIndex = fromStartIndex;
            current.FromStartLaneVersion = fromStartVersion;
            current.FromEndLaneIndex = fromEndIndex;
            current.FromEndLaneVersion = fromEndVersion;
            current.FromStartCurve = fromStartCurve;
            current.FromEndCurve = fromEndCurve;
            current.ToRouteLanePresent = toPresent;
            current.ToStartLaneIndex = toStartIndex;
            current.ToStartLaneVersion = toStartVersion;
            current.ToEndLaneIndex = toEndIndex;
            current.ToEndLaneVersion = toEndVersion;
            current.ToStartCurve = toStartCurve;
            current.ToEndCurve = toEndCurve;
            current.PathElementsSignature = PathElementsSignature(entities, owner,
                out int pathElementsPresent, out int pathElementCount);
            current.PathElementsPresent = pathElementsPresent;
            current.PathElementCount = pathElementCount;
            current.RouteNetworkSignature = RailEtaTheorySignatures.RouteNetworkSignature(current);
            return true;
        }

        private static void FillRouteLaneFact(EntityManager entities, Entity waypoint,
            out int present, out int startIndex, out int startVersion, out int endIndex,
            out int endVersion, out float startCurve, out float endCurve)
        {
            present = 0;
            startIndex = 0;
            startVersion = 0;
            endIndex = 0;
            endVersion = 0;
            startCurve = 0f;
            endCurve = 0f;
            if (!entities.HasComponent<RouteLane>(waypoint)) return;
            RouteLane lane = entities.GetComponentData<RouteLane>(waypoint);
            present = 1;
            startIndex = lane.m_StartLane.Index;
            startVersion = lane.m_StartLane.Version;
            endIndex = lane.m_EndLane.Index;
            endVersion = lane.m_EndLane.Version;
            startCurve = lane.m_StartCurvePos;
            endCurve = lane.m_EndCurvePos;
        }

        private static ulong PathElementsSignature(EntityManager entities, Entity owner,
            out int present, out int count)
        {
            present = 0;
            count = 0;
            ulong hash = RailEtaTheorySignatures.Seed;
            if (owner == Entity.Null || !entities.HasBuffer<PathElement>(owner))
                return hash;
            DynamicBuffer<PathElement> elements = entities.GetBuffer<PathElement>(owner, true);
            present = 1;
            count = elements.Length;
            for (int i = 0; i < elements.Length; i++)
            {
                PathElement element = elements[i];
                hash = RailEtaTheorySignatures.MixPathElement(hash, i,
                    element.m_Target.Index, element.m_Target.Version,
                    element.m_TargetDelta.x, element.m_TargetDelta.y, (int)element.m_Flags);
            }
            return hash;
        }

        private static Entity FactEntity(int index, int version)
        {
            return new Entity { Index = index, Version = version };
        }

        private static bool TryConnectionDistance(
            EntityManager entities, RailEtaTheoryPathFact current,
            RailEtaTheoryPathFact next, out float distance)
        {
            distance = 0f;
            Entity currentLane = FactEntity(current.LaneIndex, current.LaneVersion);
            Entity nextLane = FactEntity(next.LaneIndex, next.LaneVersion);
            if (!entities.Exists(currentLane) || !entities.Exists(nextLane)
                || !entities.HasComponent<Curve>(currentLane)
                || !entities.HasComponent<Curve>(nextLane))
                return false;
            Curve currentCurve = entities.GetComponentData<Curve>(currentLane);
            Curve nextCurve = entities.GetComponentData<Curve>(nextLane);
            float3 end = BezierPoint(currentCurve.m_Bezier, current.EndFraction);
            float3 start = BezierPoint(nextCurve.m_Bezier, next.StartFraction);
            distance = math.distance(end, start);
            return Finite(distance);
        }

        private static bool SameCurve(RailEtaTheoryPathFact fact, Curve curve)
        {
            return fact.CurveAX == curve.m_Bezier.a.x
                && fact.CurveAY == curve.m_Bezier.a.y
                && fact.CurveAZ == curve.m_Bezier.a.z
                && fact.CurveBX == curve.m_Bezier.b.x
                && fact.CurveBY == curve.m_Bezier.b.y
                && fact.CurveBZ == curve.m_Bezier.b.z
                && fact.CurveCX == curve.m_Bezier.c.x
                && fact.CurveCY == curve.m_Bezier.c.y
                && fact.CurveCZ == curve.m_Bezier.c.z
                && fact.CurveDX == curve.m_Bezier.d.x
                && fact.CurveDY == curve.m_Bezier.d.y
                && fact.CurveDZ == curve.m_Bezier.d.z;
        }

        private static float3 BezierPoint(Bezier4x3 bezier, float value)
        {
            float t = math.clamp(value, 0f, 1f);
            float inverse = 1f - t;
            return inverse * inverse * inverse * bezier.a
                + 3f * inverse * inverse * t * bezier.b
                + 3f * inverse * t * t * bezier.c
                + t * t * t * bezier.d;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static ulong MixEntity(ulong hash, Entity entity)
        {
            hash = RailEtaTheorySignatures.Mix(hash, entity.Index);
            return RailEtaTheorySignatures.Mix(hash, entity.Version);
        }
    }
}
