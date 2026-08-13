using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class RunChartSegment
    {
        internal string FromStopKey;
        internal string ToStopKey;
        internal int FromWaypointIndex;
        internal int ToWaypointIndex;
        internal uint Frames;
        internal int Minutes;
        internal double ExactMinutes;
    }

    internal sealed class RunChartDwell
    {
        internal string StopKey;
        internal int WaypointIndex;
        internal float Frames;
        internal int Minutes;
        internal int SampleCount;
        internal bool HasObservation;
    }

    internal static class RunChartSignatures
    {
        internal static ulong Route(EntityManager entities, Entity line, RoutePlan plan)
        {
            if (line == Entity.Null || plan == null || plan.Waypoints.Length == 0)
                return 0;
            ulong hash = RailEtaTheorySignatures.Seed;
            hash = MixEntity(hash, line);
            hash = RailEtaTheorySignatures.Mix(hash, plan.StopSig);
            hash = RailEtaTheorySignatures.Mix(hash, plan.Waypoints.Length);
            for (int i = 0; i < plan.Waypoints.Length; i++)
            {
                hash = MixEntity(hash, plan.Waypoints[i].Waypoint);
                hash = MixEntity(hash, plan.Waypoints[i].Stop);
                hash = RailEtaTheorySignatures.Mix(hash, plan.Waypoints[i].StopKey);
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
            DynamicBuffer<RouteSegment> segments = entities.GetBuffer<RouteSegment>(line, true);
            ulong hash = RailEtaTheorySignatures.Seed;
            hash = RailEtaTheorySignatures.Mix(hash, indices.Count);
            for (int i = 0; i < indices.Count - 1; i++)
            {
                int index = indices[i];
                int next = indices[i + 1];
                if (index < 0 || next < 0 || index >= segments.Length || next >= waypoints.Length)
                    return 0;
                hash = MixEntity(hash, waypoints[index].m_Waypoint);
                hash = MixEntity(hash, waypoints[next].m_Waypoint);
                Entity owner = segments[index].m_Segment;
                hash = MixEntity(hash, owner);
                if (owner == Entity.Null || !entities.HasBuffer<PathElement>(owner))
                    continue;
                DynamicBuffer<PathElement> elements = entities.GetBuffer<PathElement>(owner, true);
                hash = RailEtaTheorySignatures.Mix(hash, elements.Length);
                for (int j = 0; j < elements.Length; j++)
                {
                    PathElement element = elements[j];
                    hash = MixEntity(hash, element.m_Target);
                    hash = RailEtaTheorySignatures.Mix(hash, element.m_TargetDelta.x);
                    hash = RailEtaTheorySignatures.Mix(hash, element.m_TargetDelta.y);
                    hash = RailEtaTheorySignatures.Mix(hash, (int)element.m_Flags);
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
            if (line == Entity.Null || entryIndex < 0 || !entities.HasBuffer<VehicleModel>(line))
                return 0;
            DynamicBuffer<VehicleModel> models = entities.GetBuffer<VehicleModel>(line, true);
            if (entryIndex >= models.Length || models[entryIndex].m_PrimaryPrefab != primary
                || models[entryIndex].m_SecondaryPrefab != secondary)
                return 0;
            ulong hash = RailEtaTheorySignatures.Seed;
            hash = MixEntity(hash, line);
            hash = RailEtaTheorySignatures.Mix(hash, entryIndex);
            hash = MixEntity(hash, primary);
            return MixEntity(hash, secondary);
        }

        private static ulong MixEntity(ulong hash, Entity entity)
        {
            hash = RailEtaTheorySignatures.Mix(hash, entity.Index);
            return RailEtaTheorySignatures.Mix(hash, entity.Version);
        }
    }

    internal sealed class FullRunTimeQuery
    {
        private const int MaxWaypoints = 256;
        private const int MaxSegments = 64;
        private const int MaxPathSlots = 256;
        private const int TimeoutMilliseconds = 8000;
        private readonly EntityManager m_Entities;
        private readonly RoutePlanQuery m_RoutePlans;
        private readonly ObservationPort m_Observation;
        private readonly Func<string, Entity> m_LineById;
        private readonly Func<double> m_FramesPerMinute;
        private readonly Func<string, ulong> m_LineGeneration;
        private readonly Action<DispatchWorkbenchRunTimeQueryStatusDto> m_Push;
        private readonly Action<RunTimeInvalidationDto> m_PushInvalidation;
        private readonly Dictionary<string, FullRunTimeSession> m_Active = new Dictionary<string, FullRunTimeSession>(StringComparer.Ordinal);
        private readonly Dictionary<string, FullRunTimeResult> m_Results = new Dictionary<string, FullRunTimeResult>(StringComparer.Ordinal);

        internal FullRunTimeQuery(EntityManager entities, RoutePlanQuery routePlans, ObservationPort observation,
            Func<string, Entity> lineById, Func<double> framesPerMinute, Func<string, ulong> lineGeneration,
            Action<DispatchWorkbenchRunTimeQueryStatusDto> push,
            Action<RunTimeInvalidationDto> pushInvalidation)
        {
            m_Entities = entities;
            m_RoutePlans = routePlans ?? throw new ArgumentNullException(nameof(routePlans));
            m_Observation = observation ?? throw new ArgumentNullException(nameof(observation));
            m_LineById = lineById ?? throw new ArgumentNullException(nameof(lineById));
            m_FramesPerMinute = framesPerMinute ?? throw new ArgumentNullException(nameof(framesPerMinute));
            m_LineGeneration = lineGeneration ?? throw new ArgumentNullException(nameof(lineGeneration));
            m_Push = push ?? throw new ArgumentNullException(nameof(push));
            m_PushInvalidation = pushInvalidation ?? throw new ArgumentNullException(nameof(pushInvalidation));
        }

        internal DispatchWorkbenchRunTimeQueryStatusDto Start(DispatchWorkbenchRunTimeQueryRequestDto request)
        {
            request ??= new DispatchWorkbenchRunTimeQueryRequestDto();
            string editor = request.editorSessionId ?? string.Empty;
            if (string.IsNullOrEmpty(editor)) return Failure(string.Empty, editor, "run-time-editor-session-required");
            if (request.source != "historical" && request.source != "theory") return Failure(string.Empty, editor, "run-time-source-invalid");
            ClearActive(editor, request.lineId, request.source);
            ClearResults(editor, request.lineId, request.source);
            Entity line = m_LineById(request.lineId);
            if (line == Entity.Null || !m_Entities.Exists(line)) return Failure(string.Empty, editor, "run-time-line-missing");
            LifecycleKind lifecycle = TransportModeProfile.GetProfile(TransportModeResolver.Resolve(m_Entities, line)).Lifecycle;
            if (!m_RoutePlans.TryGet(line, lifecycle, out RoutePlan plan) || plan.Waypoints.Length > MaxWaypoints)
                return Failure(string.Empty, editor, "run-time-route-plan-unavailable");
            int[] stops = plan.Stops.Select(stop => stop.WaypointIndex).ToArray();
            int segmentCount = request.source == "theory" ? stops.Length : stops.Length - 1;
            if (stops.Length < 2 || segmentCount > MaxSegments)
                return Failure(string.Empty, editor, "run-time-segment-limit");
            FullRunTimeSession session = new FullRunTimeSession
            {
                Id = Guid.NewGuid().ToString("N"), EditorSessionId = editor, Line = line,
                LineId = request.lineId ?? string.Empty, Lifecycle = lifecycle, Source = request.source,
                Plan = plan, StopWaypointIndices = stops, Generation = m_LineGeneration(request.lineId)
            };
            session.Dwells = BuildDwells(session);
            if (request.source == "historical")
            {
                if (!BuildHistorical(session, out string detail))
                    return Failure(session, "run-time-historical-missing", detail);
                Complete(session);
                m_Active[ActiveKey(session)] = session;
                return Status(session);
            }
            if (lifecycle != LifecycleKind.Rail) return Failure(session, "run-time-theory-unsupported");
            if (!ResolveModel(line, out session.Model, out session.SecondaryModel,
                    out session.ModelEntryIndex))
                return Failure(session, "run-time-model-unavailable");
            session.WaypointIndices = Enumerable.Range(0, plan.Waypoints.Length).ToArray();
            session.RouteSignature = RunChartSignatures.Route(m_Entities, line, plan);
            session.PathSignature = RunChartSignatures.Path(m_Entities, line, session.WaypointIndices);
            session.ModelSignature = RunChartSignatures.Model(m_Entities, session.Model, session.SecondaryModel);
            session.ModelPairSignature = RunChartSignatures.ModelPair(m_Entities, line, session.ModelEntryIndex, session.Model, session.SecondaryModel);
            RailEtaTheorySegmentRequest[] requests = BuildTheoryRequests(session);
            RailEtaBridgeService service = RailEtaBridgeService.Current;
            if (session.RouteSignature == 0 || session.PathSignature == 0 || session.ModelSignature == 0
                || session.ModelPairSignature == 0 || requests.Length == 0 || requests.Length > MaxPathSlots)
                return Failure(session, "run-time-theory-signature-invalid");
            if (service == null || !service.CanSubmit) return Failure(session, "run-time-theory-busy");
            session.Ticket = service.RequestTheorySegments(line.Index, line.Version, session.Model.Index, session.Model.Version,
                requests, session.SecondaryModel.Index, session.SecondaryModel.Version, session.RouteSignature,
                session.PathSignature, session.ModelSignature);
            if (!session.Ticket.IsValid) return Failure(session, "run-time-theory-submit-failed");
            session.State = "Running";
            session.StartTicks = Stopwatch.GetTimestamp();
            m_Active[ActiveKey(session)] = session;
            return Status(session);
        }

        internal DispatchWorkbenchRunTimeQueryStatusDto Status(string editorSessionId, string queryId = null)
        {
            if (!TryFindActive(editorSessionId, queryId, out _, out FullRunTimeSession session))
                return Failure(queryId ?? string.Empty, editorSessionId, "run-time-query-missing");
            return Status(session);
        }

        internal DispatchWorkbenchRunTimeQueryStatusDto Cancel(string editorSessionId, string queryId)
        {
            if (!TryFindActive(editorSessionId, queryId, out string activeKey, out FullRunTimeSession session))
                return Failure(queryId ?? string.Empty, editorSessionId, "run-time-query-missing");
            if (session.Ticket.IsValid) RailEtaBridgeService.Current?.Cancel(session.Ticket);
            session.State = "Cancelled";
            session.Error = "run-time-query-cancelled";
            session.ResultId = string.Empty;
            Release(session);
            DispatchWorkbenchRunTimeQueryStatusDto cancelled = Status(session);
            m_Active.Remove(activeKey);
            m_Push(cancelled);
            return cancelled;
        }

        internal DispatchWorkbenchRunTimeQueryStatusDto CloseEditor(string editorSessionId)
        {
            string editor = editorSessionId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(editor))
                return Failure(string.Empty, editor, "run-time-editor-session-required");
            ClearEditor(editor);
            return new DispatchWorkbenchRunTimeQueryStatusDto
            {
                editorSessionId = editor,
                state = "Idle",
                segments = Array.Empty<DispatchWorkbenchRunChartSegmentDto>(),
                dwells = Array.Empty<DispatchWorkbenchRunChartDwellDto>()
            };
        }

        internal void Tick()
        {
            foreach (FullRunTimeSession session in m_Active.Values.ToArray())
            {
                if (session == null || session.State != "Running") continue;
                string before = session.State;
                Poll(session);
                if (before != session.State) m_Push(Status(session));
            }
        }

        internal bool TryGetResult(string editorSessionId, string resultId, out FullRunTimeResult result)
        {
            result = null;
            return !string.IsNullOrEmpty(editorSessionId) && !string.IsNullOrEmpty(resultId)
                && m_Results.TryGetValue(resultId, out result)
                && result.EditorSessionId == editorSessionId
                && result.Generation == m_LineGeneration(result.LineId);
        }

        internal void InvalidateLines(IEnumerable<string> lineIds)
        {
            HashSet<string> ids = new HashSet<string>(lineIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            Dictionary<string, RunTimeInvalidationDto> invalidations =
                new Dictionary<string, RunTimeInvalidationDto>(StringComparer.Ordinal);
            foreach (FullRunTimeResult result in m_Results.Values
                .Where(result => ids.Contains(result.LineId))
                .ToArray())
            {
                AddInvalidation(invalidations, result.EditorSessionId, result.LineId, result.Source,
                    "run-time-line-invalidated");
                m_Results.Remove(result.ResultId);
            }
            foreach (KeyValuePair<string, FullRunTimeSession> entry in m_Active
                .Where(entry => ids.Contains(entry.Value.LineId))
                .ToArray())
            {
                FullRunTimeSession session = entry.Value;
                if (session.LineInvalidationNotified)
                    continue;

                AddInvalidation(invalidations, session.EditorSessionId, session.LineId, session.Source,
                    "run-time-line-invalidated");
                if (session.Ticket.IsValid) RailEtaBridgeService.Current?.Cancel(session.Ticket);
                session.State = "Failed";
                session.Error = "run-time-line-invalidated";
                session.ResultId = string.Empty;
                DispatchWorkbenchRunTimeQueryStatusDto failed = Status(session);
                session.LineInvalidationNotified = true;
                try
                {
                    m_Push(failed);
                }
                finally
                {
                    Release(session);
                    m_Active.Remove(entry.Key);
                }
            }
            foreach (RunTimeInvalidationDto invalidation in invalidations.Values)
                m_PushInvalidation(invalidation);
        }

        internal void InvalidateSources(
            IEnumerable<string> lineIds,
            IEnumerable<string> sources,
            string reason)
        {
            HashSet<string> ids = new HashSet<string>(
                (lineIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)),
                StringComparer.Ordinal);
            HashSet<string> sourceSet = new HashSet<string>(
                (sources ?? Enumerable.Empty<string>()).Where(source => !string.IsNullOrEmpty(source)),
                StringComparer.Ordinal);
            if (sourceSet.Count == 0)
                return;

            bool allLines = ids.Count == 0;
            Dictionary<string, RunTimeInvalidationDto> invalidations =
                new Dictionary<string, RunTimeInvalidationDto>(StringComparer.Ordinal);
            foreach (FullRunTimeResult result in m_Results.Values
                .Where(result => (allLines || ids.Contains(result.LineId)) && sourceSet.Contains(result.Source))
                .ToArray())
            {
                AddInvalidation(invalidations, result.EditorSessionId, result.LineId, result.Source, reason);
                m_Results.Remove(result.ResultId);
            }
            foreach (KeyValuePair<string, FullRunTimeSession> entry in m_Active
                .Where(entry => (allLines || ids.Contains(entry.Value.LineId))
                    && sourceSet.Contains(entry.Value.Source))
                .ToArray())
            {
                FullRunTimeSession session = entry.Value;
                AddInvalidation(invalidations, session.EditorSessionId, session.LineId, session.Source, reason);
                if (session.Ticket.IsValid) RailEtaBridgeService.Current?.Cancel(session.Ticket);
                Release(session);
                m_Active.Remove(entry.Key);
            }
            foreach (RunTimeInvalidationDto invalidation in invalidations.Values)
                m_PushInvalidation(invalidation);
        }

        internal void ClearEditor(string editorSessionId)
        {
            string editor = editorSessionId ?? string.Empty;
            ClearActive(editor);
            foreach (string id in m_Results.Values.Where(result => result.EditorSessionId == editor).Select(result => result.ResultId).ToArray())
                m_Results.Remove(id);
        }

        private void ClearActive(string editorSessionId, string lineId = null, string source = null)
        {
            string editor = editorSessionId ?? string.Empty;
            foreach (KeyValuePair<string, FullRunTimeSession> entry in m_Active
                .Where(entry => string.Equals(entry.Value.EditorSessionId, editor, StringComparison.Ordinal)
                    && (lineId == null || string.Equals(entry.Value.LineId, lineId, StringComparison.Ordinal))
                    && (source == null || string.Equals(entry.Value.Source, source, StringComparison.Ordinal)))
                .ToArray())
            {
                FullRunTimeSession session = entry.Value;
                if (session.Ticket.IsValid) RailEtaBridgeService.Current?.Cancel(session.Ticket);
                Release(session);
                m_Active.Remove(entry.Key);
            }
        }

        private void ClearResults(string editorSessionId, string lineId, string source)
        {
            foreach (string resultId in m_Results.Values
                .Where(result => string.Equals(result.EditorSessionId, editorSessionId, StringComparison.Ordinal)
                    && string.Equals(result.LineId, lineId, StringComparison.Ordinal)
                    && string.Equals(result.Source, source, StringComparison.Ordinal))
                .Select(result => result.ResultId)
                .ToArray())
            {
                m_Results.Remove(resultId);
            }
        }

        private static void AddInvalidation(
            Dictionary<string, RunTimeInvalidationDto> invalidations,
            string editorSessionId,
            string lineId,
            string source,
            string reason)
        {
            string key = (editorSessionId ?? string.Empty) + "\u001f"
                + (lineId ?? string.Empty) + "\u001f" + (source ?? string.Empty);
            invalidations[key] = new RunTimeInvalidationDto
            {
                editorSessionId = editorSessionId ?? string.Empty,
                lineId = lineId ?? string.Empty,
                source = source ?? string.Empty,
                reason = reason ?? string.Empty
            };
        }

        private bool TryFindActive(
            string editorSessionId,
            string queryId,
            out string activeKey,
            out FullRunTimeSession session)
        {
            string editor = editorSessionId ?? string.Empty;
            foreach (KeyValuePair<string, FullRunTimeSession> entry in m_Active)
            {
                if (!string.Equals(entry.Value.EditorSessionId, editor, StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(queryId) && entry.Value.Id != queryId))
                {
                    continue;
                }
                activeKey = entry.Key;
                session = entry.Value;
                return true;
            }
            activeKey = string.Empty;
            session = null;
            return false;
        }

        private static string ActiveKey(FullRunTimeSession session)
        {
            return (session.EditorSessionId ?? string.Empty) + "\u001f"
                + (session.LineId ?? string.Empty) + "\u001f" + (session.Source ?? string.Empty);
        }

        internal void Clear()
        {
            foreach (FullRunTimeSession session in m_Active.Values)
            {
                if (session.Ticket.IsValid) RailEtaBridgeService.Current?.Cancel(session.Ticket);
                Release(session);
            }
            m_Active.Clear();
            m_Results.Clear();
        }

        private void Poll(FullRunTimeSession session)
        {
            if (Elapsed(session.StartTicks) >= TimeoutMilliseconds) { Fail(session, "run-time-theory-timeout"); return; }
            if (m_LineGeneration(session.LineId) != session.Generation) { Fail(session, "run-time-line-invalidated"); return; }
            if (RailEtaBridgeService.Current == null || !RailEtaBridgeService.Current.TryGetState(session.Ticket, out RailEtaPublicStatus status))
            { Fail(session, "run-time-theory-status-missing"); return; }
            if (status.State == "Completed") { if (ApplyTheory(session, status.TheorySegments)) Complete(session); else Fail(session, "run-time-theory-result-invalid"); return; }
            if (status.State == "Failed" || status.State == "Cancelled" || status.State == "Busy" || status.State == "Unavailable" || status.State == "ClockChanged" || status.State == "WorkerLost")
            {
                RailEtaTheoryFailure theoryFailure = status.TheoryFailure;
                if (theoryFailure != null && (!string.IsNullOrEmpty(theoryFailure.Failure)
                    || !string.IsNullOrEmpty(theoryFailure.Detail)))
                {
                    Fail(session,
                        string.IsNullOrEmpty(theoryFailure.Failure) ? status.Failure : theoryFailure.Failure,
                        theoryFailure.Detail);
                }
                else
                {
                    Fail(session, string.IsNullOrEmpty(status.Failure) ? status.Detail : status.Failure,
                        status.Detail);
                }
            }
        }

        private bool BuildHistorical(FullRunTimeSession session, out string detail)
        {
            detail = string.Empty;
            List<RunChartSegment> segments = BuildIntervals(session.Plan, session.StopWaypointIndices);
            for (int i = 0; i < segments.Count; i++)
            {
                RunChartSegment segment = segments[i];
                RouteWaypointRef from = session.Plan.Waypoints[segment.FromWaypointIndex];
                RouteWaypointRef to = session.Plan.Waypoints[segment.ToWaypointIndex];
                float frames;
                bool found;
                string cause;
                if (session.Lifecycle == LifecycleKind.Rail)
                {
                    found = m_Observation.TryTraversalFrames(
                        session.Line,
                        from.WaypointIndex,
                        to.WaypointIndex,
                        out frames,
                        out cause);
                }
                else
                {
                    found = m_Observation.TryBusSegFrames(
                        session.Line,
                        from.Waypoint,
                        from.Stop,
                        to.Waypoint,
                        to.Stop,
                        out frames);
                    cause = found ? string.Empty : "code=bus-segment-missing";
                }
                if (!found || frames <= 0f || float.IsNaN(frames) || float.IsInfinity(frames))
                {
                    if (session.Lifecycle == LifecycleKind.Rail && RtLog.VerboseEnabled)
                    {
                        string diagnostic = string.IsNullOrEmpty(cause) ? "code=invalid-frames" : cause;
                        if (!diagnostic.Contains(";required="))
                        {
                            diagnostic += ";required=unavailable;requiredTotal=0;requiredTruncated=0"
                                + ";hit=none;hitTotal=0;hitTruncated=0"
                                + ";missing=unavailable;missingTotal=0;missingTruncated=0";
                        }
                        Mod.log.Info("[TraversalSliceHistoricalMissing] lineId=" + session.LineId
                            + ";line=" + session.Line.Index + ":" + session.Line.Version
                            + ";segment=" + i
                            + ";from=" + from.WaypointIndex
                            + ";to=" + to.WaypointIndex
                            + ";error=run-time-historical-missing;"
                            + diagnostic);
                    }
                    detail = "segment=" + i
                        + ";from=" + from.WaypointIndex
                        + ";to=" + to.WaypointIndex
                        + ";" + (string.IsNullOrEmpty(cause) ? "code=invalid-frames" : cause);
                    return false;
                }
                segment.Frames = (uint)Math.Max(1, Math.Round(frames));
                segment.Minutes = ToMinutes(frames);
                segment.ExactMinutes = ToExactMinutes(frames);
                segments[i] = segment;
            }
            session.Segments = segments;
            if (segments.Count == 0)
            {
                detail = "code=no-segments";
                return false;
            }
            return true;
        }

        private List<RunChartDwell> BuildDwells(FullRunTimeSession session)
        {
            List<RunChartDwell> dwells = new List<RunChartDwell>(session.Plan.Stops.Length);
            for (int i = 0; i < session.Plan.Stops.Length; i++)
            {
                RouteStopRef stop = session.Plan.Stops[i];
                RunChartDwell dwell = new RunChartDwell
                {
                    StopKey = stop.StopKey,
                    WaypointIndex = stop.WaypointIndex
                };
                if (m_Observation.TryObservedWaypointDwell(
                        session.Line,
                        stop.WaypointIndex,
                        out StationDwellObservation observation))
                {
                    dwell.Frames = observation.AverageFrames;
                    dwell.Minutes = ToMinutes(observation.AverageFrames);
                    dwell.SampleCount = observation.SampleCount;
                    dwell.HasObservation = true;
                }
                dwells.Add(dwell);
            }
            return dwells;
        }

        private bool ApplyTheory(FullRunTimeSession session, RailEtaTheorySegmentResult[] results)
        {
            if (results == null || results.Length != session.StopWaypointIndices.Length) return false;
            List<RunChartSegment> segments = BuildIntervals(session.Plan, session.StopWaypointIndices, true);
            bool[] seen = new bool[segments.Count];
            for (int i = 0; i < results.Length; i++)
            {
                RailEtaTheorySegmentResult result = results[i];
                if (result == null || result.State != "Completed" || result.SegmentFrames == 0 || result.SegmentIndex < 0 || result.SegmentIndex >= segments.Count || seen[result.SegmentIndex]) return false;
                RunChartSegment segment = segments[result.SegmentIndex];
                if (result.FromWaypointIndex != segment.FromWaypointIndex || result.ToWaypointIndex != segment.ToWaypointIndex) return false;
                seen[result.SegmentIndex] = true;
                segment.Frames = result.SegmentFrames;
                segment.Minutes = ToMinutes(result.SegmentFrames);
                segment.ExactMinutes = ToExactMinutes(result.SegmentFrames);
                segments[result.SegmentIndex] = segment;
            }
            if (seen.Any(value => !value)) return false;
            session.Segments = segments;
            return true;
        }

        private RailEtaTheorySegmentRequest[] BuildTheoryRequests(FullRunTimeSession session)
        {
            List<RailEtaTheorySegmentRequest> requests = new List<RailEtaTheorySegmentRequest>();
            for (int segment = 0; segment + 1 < session.StopWaypointIndices.Length; segment++)
            {
                int fromStop = session.StopWaypointIndices[segment];
                int toStop = session.StopWaypointIndices[segment + 1];
                for (int i = fromStop; i < toStop; i++)
                {
                    RouteWaypointRef from = session.Plan.Waypoints[i];
                    RouteWaypointRef to = session.Plan.Waypoints[i + 1];
                    requests.Add(new RailEtaTheorySegmentRequest
                    {
                        SegmentIndex = segment, PathSlotIndex = from.WaypointIndex,
                        FromWaypointIndex = from.WaypointIndex, FromWaypointVersion = from.Waypoint.Version,
                        ToWaypointIndex = to.WaypointIndex, ToWaypointVersion = to.Waypoint.Version,
                        SegmentFromWaypointIndex = fromStop, SegmentToWaypointIndex = toStop
                    });
                }
            }

            int closingSegment = session.StopWaypointIndices.Length - 1;
            int closingFrom = session.StopWaypointIndices[closingSegment];
            int closingTo = session.StopWaypointIndices[0];
            int waypointCount = session.Plan.Waypoints.Length;
            int current = closingFrom;
            for (int step = 0; step < waypointCount; step++)
            {
                int next = (current + 1) % waypointCount;
                RouteWaypointRef from = session.Plan.Waypoints[current];
                RouteWaypointRef to = session.Plan.Waypoints[next];
                requests.Add(new RailEtaTheorySegmentRequest
                {
                    SegmentIndex = closingSegment, PathSlotIndex = from.WaypointIndex,
                    FromWaypointIndex = from.WaypointIndex, FromWaypointVersion = from.Waypoint.Version,
                    ToWaypointIndex = to.WaypointIndex, ToWaypointVersion = to.Waypoint.Version,
                    SegmentFromWaypointIndex = closingFrom, SegmentToWaypointIndex = closingTo
                });
                if (next == closingTo)
                    break;
                current = next;
            }
            return requests.ToArray();
        }

        private static List<RunChartSegment> BuildIntervals(RoutePlan plan, int[] stops, bool includeClosing = false)
        {
            List<RunChartSegment> result = new List<RunChartSegment>();
            for (int i = 0; i + 1 < stops.Length; i++)
            {
                RouteWaypointRef from = plan.Waypoints[stops[i]];
                RouteWaypointRef to = plan.Waypoints[stops[i + 1]];
                result.Add(new RunChartSegment { FromStopKey = from.StopKey, ToStopKey = to.StopKey, FromWaypointIndex = from.WaypointIndex, ToWaypointIndex = to.WaypointIndex });
            }
            if (includeClosing && stops.Length > 1)
            {
                RouteWaypointRef from = plan.Waypoints[stops[stops.Length - 1]];
                RouteWaypointRef to = plan.Waypoints[stops[0]];
                result.Add(new RunChartSegment { FromStopKey = from.StopKey, ToStopKey = to.StopKey, FromWaypointIndex = from.WaypointIndex, ToWaypointIndex = to.WaypointIndex });
            }
            return result;
        }

        private bool ResolveModel(Entity line, out Entity primary, out Entity secondary,
            out int entryIndex)
        {
            primary = Entity.Null;
            secondary = Entity.Null;
            entryIndex = -1;
            if (!m_Entities.HasBuffer<VehicleModel>(line)) return false;
            DynamicBuffer<VehicleModel> models = m_Entities.GetBuffer<VehicleModel>(line, true);
            for (int i = 0; i < models.Length; i++)
            {
                Entity candidate = models[i].m_PrimaryPrefab;
                if (candidate == Entity.Null || !m_Entities.Exists(candidate) || !m_Entities.HasComponent<TrainData>(candidate) || !m_Entities.HasComponent<ObjectGeometryData>(candidate)) continue;
                Entity paired = models[i].m_SecondaryPrefab;
                if (paired != Entity.Null && (!m_Entities.Exists(paired) || !m_Entities.HasComponent<TrainData>(paired) || !m_Entities.HasComponent<ObjectGeometryData>(paired))) continue;
                primary = candidate;
                secondary = paired;
                entryIndex = i;
                return true;
            }
            return false;
        }

        private void Complete(FullRunTimeSession session)
        {
            session.State = "Completed";
            foreach (string resultId in m_Results.Values
                .Where(result => string.Equals(result.EditorSessionId, session.EditorSessionId, StringComparison.Ordinal)
                    && string.Equals(result.LineId, session.LineId, StringComparison.Ordinal)
                    && string.Equals(result.Source, session.Source, StringComparison.Ordinal))
                .Select(result => result.ResultId)
                .ToArray())
            {
                m_Results.Remove(resultId);
            }
            session.ResultId = Guid.NewGuid().ToString("N");
            m_Results[session.ResultId] = new FullRunTimeResult
            {
                ResultId = session.ResultId, EditorSessionId = session.EditorSessionId, LineId = session.LineId,
                Line = session.Line, Source = session.Source, StopSig = session.Plan.StopSig, Generation = session.Generation,
                StopKeys = session.Plan.Stops.Select(stop => stop.StopKey ?? string.Empty).ToArray(),
                Segments = session.Segments?.ToArray() ?? Array.Empty<RunChartSegment>()
            };
            Release(session);
        }

        private static void Fail(FullRunTimeSession session, string error, string detail = "")
        {
            if (session.Ticket.IsValid) RailEtaBridgeService.Current?.Cancel(session.Ticket);
            session.State = "Failed";
            session.Error = error ?? "run-time-query-failed";
            session.Detail = detail ?? string.Empty;
            session.LineInvalidationNotified = string.Equals(
                session.Error,
                "run-time-line-invalidated",
                StringComparison.Ordinal);
            Release(session);
        }

        private DispatchWorkbenchRunTimeQueryStatusDto Failure(
            FullRunTimeSession session,
            string error,
            string detail = "")
        {
            session.State = "Failed";
            session.Error = error ?? "run-time-query-failed";
            session.Detail = detail ?? string.Empty;
            session.ResultId = string.Empty;
            session.Segments = null;
            return Status(session);
        }

        private static void Release(FullRunTimeSession session)
        {
            if (session?.Ticket.IsValid == true) RailEtaBridgeService.Current?.Release(session.Ticket);
            if (session != null) session.Ticket = default;
        }

        private DispatchWorkbenchRunTimeQueryStatusDto Status(FullRunTimeSession session)
        {
            return new DispatchWorkbenchRunTimeQueryStatusDto
            {
                queryId = session.Id, editorSessionId = session.EditorSessionId, state = session.State,
                resultId = session.ResultId ?? string.Empty, error = session.Error ?? string.Empty,
                detail = session.Detail ?? string.Empty,
                lineId = session.LineId ?? string.Empty, source = session.Source ?? string.Empty,
                segments = session.Segments == null ? Array.Empty<DispatchWorkbenchRunChartSegmentDto>() : session.Segments.Select(segment => new DispatchWorkbenchRunChartSegmentDto
                {
                    fromStopKey = segment.FromStopKey, toStopKey = segment.ToStopKey,
                    fromWaypointIndex = segment.FromWaypointIndex, toWaypointIndex = segment.ToWaypointIndex,
                    segmentFrames = segment.Frames, segmentMinutes = segment.Minutes,
                    segmentMinutesExact = segment.ExactMinutes
                }).ToArray(),
                dwells = session.Dwells == null ? Array.Empty<DispatchWorkbenchRunChartDwellDto>() : session.Dwells.Select(dwell => new DispatchWorkbenchRunChartDwellDto
                {
                    stopKey = dwell.StopKey ?? string.Empty,
                    waypointIndex = dwell.WaypointIndex,
                    averageFrames = dwell.Frames,
                    averageMinutes = dwell.Minutes,
                    sampleCount = dwell.SampleCount,
                    hasObservation = dwell.HasObservation
                }).ToArray()
            };
        }

        private static DispatchWorkbenchRunTimeQueryStatusDto Failure(string queryId, string editor, string error)
        {
            return new DispatchWorkbenchRunTimeQueryStatusDto
            {
                queryId = queryId ?? string.Empty, editorSessionId = editor ?? string.Empty,
                state = "Failed", error = error ?? string.Empty,
                segments = Array.Empty<DispatchWorkbenchRunChartSegmentDto>(),
                dwells = Array.Empty<DispatchWorkbenchRunChartDwellDto>()
            };
        }

        private int ToMinutes(double frames)
        {
            double rate = m_FramesPerMinute();
            return rate > 0d && !double.IsNaN(rate) && !double.IsInfinity(rate)
                ? Math.Max(1, (int)Math.Round(frames / rate, MidpointRounding.AwayFromZero))
                : Math.Max(1, (int)Math.Round(frames, MidpointRounding.AwayFromZero));
        }

        private double ToExactMinutes(double frames)
        {
            double rate = m_FramesPerMinute();
            double minutes = rate > 0d && !double.IsNaN(rate) && !double.IsInfinity(rate)
                ? frames / rate
                : frames;
            return Math.Max(0.1d, Math.Round(minutes, 1, MidpointRounding.AwayFromZero));
        }

        private static long Elapsed(long startTicks)
        {
            return startTicks <= 0 ? 0 : (long)((Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency);
        }
    }

    internal sealed class FullRunTimeSession
    {
        internal string Id;
        internal string EditorSessionId;
        internal string LineId;
        internal Entity Line;
        internal LifecycleKind Lifecycle;
        internal string Source;
        internal string State = "Idle";
        internal string ResultId = string.Empty;
        internal string Error = string.Empty;
        internal string Detail = string.Empty;
        internal RoutePlan Plan;
        internal int[] StopWaypointIndices = Array.Empty<int>();
        internal int[] WaypointIndices = Array.Empty<int>();
        internal List<RunChartSegment> Segments;
        internal List<RunChartDwell> Dwells;
        internal Entity Model;
        internal Entity SecondaryModel;
        internal int ModelEntryIndex = -1;
        internal ulong RouteSignature;
        internal ulong PathSignature;
        internal ulong ModelSignature;
        internal ulong ModelPairSignature;
        internal ulong Generation;
        internal long StartTicks;
        internal bool LineInvalidationNotified;
        internal RailEtaPublicTicket Ticket;
    }

    internal sealed class FullRunTimeResult
    {
        internal string ResultId;
        internal string EditorSessionId;
        internal string LineId;
        internal Entity Line;
        internal string Source;
        internal string StopSig;
        internal ulong Generation;
        internal string[] StopKeys;
        internal RunChartSegment[] Segments;
    }
}
