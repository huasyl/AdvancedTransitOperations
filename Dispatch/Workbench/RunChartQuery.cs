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
        private const int MaxResults = 512;
        private const int MaxResultsPerEditor = 32;
        private const uint TheoryStabilityDelayFrames = 64;
        private const uint TheoryRetryDelayFrames = 60;
        private const uint TheoryLongRetryDelayFrames = 240;
        private const int MaxTheoryRetries = 2;
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
        private readonly Dictionary<string, TheoryEntry> m_Theory = new Dictionary<string, TheoryEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, TheoryWaiter> m_TheoryWaiters = new Dictionary<string, TheoryWaiter>(StringComparer.Ordinal);
        private readonly Queue<string> m_TheoryQueue = new Queue<string>();
        private readonly HashSet<string> m_TheoryQueued = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_TheoryLines = new HashSet<string>(StringComparer.Ordinal);
        private TheoryEntry m_TheoryActive;
        private uint m_TheoryFrame;
        private uint m_TheoryWakeFrame;
        private bool m_TheoryPaused;
        private long m_NextResultOrder;

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
            if (request.source != "sliceHistoricalEstimate"
                && request.source != "theory"
                && request.source != "monitorAverage"
                && request.source != "busHistorical") return Failure(string.Empty, editor, "run-time-source-invalid");
            if (request.source == "theory")
                return StartTheory(request);
            ClearActive(editor, request.lineId, request.source);
            ClearResults(editor, request.lineId, request.source);
            Entity line = m_LineById(request.lineId);
            if (line == Entity.Null || !m_Entities.Exists(line)) return Failure(string.Empty, editor, "run-time-line-missing");
            LifecycleKind lifecycle = TransportModeProfile.GetProfile(TransportModeResolver.Resolve(m_Entities, line)).Lifecycle;
            FullRunTimeSession session = new FullRunTimeSession
            {
                Id = Guid.NewGuid().ToString("N"), EditorSessionId = editor, Line = line,
                LineId = request.lineId ?? string.Empty, Lifecycle = lifecycle, Source = request.source,
                Generation = m_LineGeneration(request.lineId),
                FramesPerMinute = m_FramesPerMinute()
            };
            if (request.source == "busHistorical")
            {
                if (lifecycle != LifecycleKind.Road)
                    return Failure(session, "bus-historical-unsupported");
                session.State = "Running";
                session.Complete = false;
                session.StartTicks = Stopwatch.GetTimestamp();
                m_Active[ActiveKey(session)] = session;
                return Status(session);
            }
            if (!m_RoutePlans.TryGet(line, lifecycle, out RoutePlan plan) || plan.Waypoints.Length > MaxWaypoints)
                return Failure(string.Empty, editor, "run-time-route-plan-unavailable");
            int[] stops = plan.Stops.Select(stop => stop.WaypointIndex).ToArray();
            int segmentCount = stops.Length;
            if (stops.Length < 2 || segmentCount > MaxSegments)
                return Failure(string.Empty, editor, "run-time-segment-limit");
            session.Plan = plan;
            session.StopWaypointIndices = stops;
            session.Dwells = request.source == "sliceHistoricalEstimate"
                ? new List<RunChartDwell>()
                : BuildDwells(session);
            if (request.source == "sliceHistoricalEstimate")
            {
                if (lifecycle != LifecycleKind.Rail)
                    return Failure(session, "run-time-slice-historical-unsupported");
                if (!BuildHistorical(session, out string detail))
                    return Failure(session, "run-time-slice-historical-missing", detail);
                Complete(session);
#if RT_DEBUG_TOOLS
                if (!session.Complete)
                {
                    Mod.log.Info("[RunChartMissing] line=" + (session.LineId ?? string.Empty)
                        + ";source=" + (session.Source ?? string.Empty)
                        + ";kind=" + (session.MissingKind ?? "none")
                        + ";prefix=" + session.PrefixStopCount);
                }
#endif
                m_Active[ActiveKey(session)] = session;
                return Status(session);
            }
            if (request.source == "monitorAverage")
            {
                if (lifecycle != LifecycleKind.Rail)
                    return Failure(session, "monitor-average-unsupported");
                if (!BuildMonitorAverage(session, out string detail))
                    return Failure(session, "monitor-average-unavailable", detail);
                Complete(session);
                m_Active[ActiveKey(session)] = session;
                return Status(session);
            }
            return Failure(session, "run-time-source-invalid");
        }

        private DispatchWorkbenchRunTimeQueryStatusDto StartTheory(
            DispatchWorkbenchRunTimeQueryRequestDto request)
        {
            string editor = request.editorSessionId ?? string.Empty;
            string lineId = request.lineId ?? string.Empty;
            if (string.IsNullOrEmpty(lineId))
                return Failure(string.Empty, editor, "run-time-line-missing");

            RemoveTheoryWaiter(editor, lineId);
            if (TryFindTheoryResult(lineId, out FullRunTimeResult result))
            {
                TheoryWaiter ready = AddTheoryWaiter(editor, lineId);
                ready.State = "Completed";
                ready.ResultId = result.ResultId;
                ready.StopSig = result.StopSig;
                return TheoryStatus(ready, result);
            }

            Entity line = m_LineById(lineId);
            if (line == Entity.Null || !m_Entities.Exists(line))
                return Failure(string.Empty, editor, "run-time-line-missing");

            LifecycleKind lifecycle = TransportModeProfile.GetProfile(
                TransportModeResolver.Resolve(m_Entities, line)).Lifecycle;
            if (lifecycle != LifecycleKind.Rail)
                return Failure(string.Empty, editor, "run-time-theory-unsupported");

            TheoryWaiter waiter = AddTheoryWaiter(editor, lineId);
            if (!m_Theory.TryGetValue(lineId, out TheoryEntry entry))
            {
                QueueTheory(lineId, false, m_TheoryFrame + TheoryStabilityDelayFrames);
                m_Theory.TryGetValue(lineId, out entry);
            }
            else if (entry.State == "Completed" && !TryFindTheoryResult(lineId, out _))
            {
                QueueTheory(lineId, true, m_TheoryFrame + TheoryStabilityDelayFrames);
            }

            waiter.State = entry.State == "Completed" ? "Queued" : entry.State;
            waiter.ResultId = waiter.State == "Completed" ? entry.ResultId : string.Empty;
            waiter.StopSig = entry.Session?.Plan?.StopSig ?? string.Empty;
            return TheoryStatus(waiter, FindTheoryResult(lineId));
        }

        private bool TryBuildTheory(
            string lineId,
            out FullRunTimeSession session,
            out string error,
            out string detail)
        {
            session = null;
            error = string.Empty;
            detail = string.Empty;
            Entity line = m_LineById(lineId);
            if (line == Entity.Null || !m_Entities.Exists(line))
            {
                error = "run-time-line-missing";
                return false;
            }

            LifecycleKind lifecycle = TransportModeProfile.GetProfile(
                TransportModeResolver.Resolve(m_Entities, line)).Lifecycle;
            if (lifecycle != LifecycleKind.Rail)
            {
                error = "run-time-theory-unsupported";
                return false;
            }
            if (!m_RoutePlans.TryGet(line, lifecycle, out RoutePlan plan)
                || plan.Waypoints.Length > MaxWaypoints)
            {
                error = "run-time-route-plan-unavailable";
                return false;
            }

            int[] stops = plan.Stops.Select(stop => stop.WaypointIndex).ToArray();
            if (stops.Length < 2 || stops.Length > MaxSegments)
            {
                error = "run-time-segment-limit";
                return false;
            }

            session = new FullRunTimeSession
            {
                Id = Guid.NewGuid().ToString("N"),
                EditorSessionId = string.Empty,
                Line = line,
                LineId = lineId ?? string.Empty,
                Lifecycle = lifecycle,
                Source = "theory",
                Plan = plan,
                StopWaypointIndices = stops,
                Generation = m_LineGeneration(lineId),
                FramesPerMinute = m_FramesPerMinute()
            };
            session.Dwells = BuildDwells(session);
            session.WaypointIndices = Enumerable.Range(0, plan.Waypoints.Length).ToArray();
            session.RouteSignature = RunChartSignatures.Route(m_Entities, line, plan);
            session.PathSignature = RunChartSignatures.Path(m_Entities, line, session.WaypointIndices);
            if (!ResolveModel(line, out session.Model, out session.SecondaryModel,
                    out session.ModelEntryIndex))
            {
                error = "run-time-model-unavailable";
                return false;
            }
            session.ModelSignature = RunChartSignatures.Model(m_Entities, session.Model, session.SecondaryModel);
            session.ModelPairSignature = RunChartSignatures.ModelPair(
                m_Entities,
                line,
                session.ModelEntryIndex,
                session.Model,
                session.SecondaryModel);
            RailEtaTheorySegmentRequest[] requests = BuildTheoryRequests(session);
            if (session.RouteSignature == 0 || session.PathSignature == 0
                || session.ModelSignature == 0 || session.ModelPairSignature == 0
                || requests.Length == 0 || requests.Length > MaxPathSlots)
            {
                error = "run-time-theory-signature-invalid";
                return false;
            }
            session.TheoryRequests = requests;
            return true;
        }

        internal DispatchWorkbenchRunTimeQueryStatusDto Status(string editorSessionId, string queryId = null)
        {
            if (TryFindTheoryWaiter(editorSessionId, queryId, out TheoryWaiter theoryWaiter))
                return TheoryStatus(theoryWaiter, FindTheoryResult(theoryWaiter.LineId));
            if (!TryFindActive(editorSessionId, queryId, out _, out FullRunTimeSession session))
                return Failure(queryId ?? string.Empty, editorSessionId, "run-time-query-missing");
            return Status(session);
        }

        internal DispatchWorkbenchRunTimeQueryStatusDto Cancel(string editorSessionId, string queryId)
        {
            if (TryFindTheoryWaiter(editorSessionId, queryId, out TheoryWaiter theoryWaiter))
            {
                theoryWaiter.State = "Cancelled";
                theoryWaiter.Error = "run-time-query-cancelled";
                theoryWaiter.ResultId = string.Empty;
                DispatchWorkbenchRunTimeQueryStatusDto theoryCancelled = TheoryStatus(theoryWaiter, null);
                m_TheoryWaiters.Remove(TheoryWaiterKey(theoryWaiter.EditorSessionId, theoryWaiter.LineId));
                return theoryCancelled;
            }
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
                missingKind = "none",
                segments = Array.Empty<DispatchWorkbenchRunChartSegmentDto>(),
                dwells = Array.Empty<DispatchWorkbenchRunChartDwellDto>()
            };
        }

        internal void Tick(uint nowFrame = 0)
        {
            m_TheoryFrame = nowFrame;
            foreach (FullRunTimeSession session in m_Active.Values.ToArray())
            {
                if (session == null || session.State != "Running") continue;
                string before = session.State;
                Poll(session);
                if (before != session.State) m_Push(Status(session));
            }
            TickTheory(nowFrame);
        }

        internal void SyncPrewarm(IEnumerable<WorkbenchLineRuntime> lines)
        {
            HashSet<string> current = new HashSet<string>(StringComparer.Ordinal);
            foreach (WorkbenchLineRuntime line in lines ?? Enumerable.Empty<WorkbenchLineRuntime>())
            {
                string lineId = line?.Id ?? string.Empty;
                if (string.IsNullOrEmpty(lineId) || line.Entity == Entity.Null
                    || !m_Entities.Exists(line.Entity))
                    continue;
                LifecycleKind lifecycle = TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(m_Entities, line.Entity)).Lifecycle;
                if (lifecycle != LifecycleKind.Rail)
                    continue;

                current.Add(lineId);
                m_TheoryLines.Add(lineId);
                if (!m_Theory.TryGetValue(lineId, out TheoryEntry entry))
                {
                    if (!TryFindTheoryResult(lineId, out _))
                        QueueTheory(lineId, false, m_TheoryFrame);
                    continue;
                }

                if (entry.State == "Completed" && !TryFindTheoryResult(lineId, out _))
                    QueueTheory(lineId, true, m_TheoryFrame);
            }

            foreach (string lineId in m_TheoryLines
                .Where(lineId => !current.Contains(lineId))
                .ToArray())
            {
                m_TheoryLines.Remove(lineId);
            }
        }

        private void TickTheory(uint nowFrame)
        {
            TheoryEntry active = m_TheoryActive;
            if (active != null)
            {
                if (active.Session != null && active.Session.State == "Running")
                {
                    PollTheory(active, nowFrame);
                    return;
                }
                m_TheoryActive = null;
            }
            if (nowFrame < m_TheoryWakeFrame)
                return;

            RailEtaBridgeService service = RailEtaBridgeService.Current;
            if (m_TheoryPaused)
            {
                if (service == null || service.WorkerLost || service.HotGeneration == 0
                    || !service.CanSubmit)
                {
                    m_TheoryWakeFrame = nowFrame + TheoryRetryDelayFrames;
                    return;
                }
                m_TheoryPaused = false;
            }
            if (service == null || service.WorkerLost || service.HotGeneration == 0)
            {
                m_TheoryPaused = true;
                m_TheoryWakeFrame = nowFrame + TheoryRetryDelayFrames;
                return;
            }

            TheoryEntry next = DequeueTheory(nowFrame);
            if (next == null)
                return;
            if (!service.CanSubmit)
            {
                RequeueTheory(next, nowFrame, TheoryRetryDelayFrames);
                m_TheoryWakeFrame = nowFrame + TheoryRetryDelayFrames;
                return;
            }
            if (!TryBuildTheory(next.LineId, out FullRunTimeSession session,
                    out string error, out string detail))
            {
                FinishTheory(next, "Unavailable", error, detail, nowFrame);
                return;
            }

            RailEtaTheorySegmentRequest[] requests = session.TheoryRequests ?? Array.Empty<RailEtaTheorySegmentRequest>();
            RailEtaPublicTicket ticket;
            try
            {
                ticket = service.RequestTheorySegments(
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
            }
            catch (Exception ex)
            {
                RetryTheory(next, "run-time-theory-submit-failed", ex.Message, nowFrame);
                return;
            }
            if (!ticket.IsValid)
            {
                m_TheoryPaused = true;
                RequeueTheory(next, nowFrame, TheoryRetryDelayFrames);
                return;
            }

            next.Session = session;
            next.Session.Ticket = ticket;
            next.Session.State = "Running";
            next.Session.StartTicks = Stopwatch.GetTimestamp();
            next.State = "Running";
            next.Error = string.Empty;
            next.Detail = string.Empty;
            m_TheoryActive = next;
            PushTheoryWaiters(next);
        }

        private TheoryEntry DequeueTheory(uint nowFrame)
        {
            int count = m_TheoryQueue.Count;
            TheoryEntry fallback = null;
            uint earliestFrame = 0;
            for (int i = 0; i < count; i++)
            {
                string lineId = m_TheoryQueue.Dequeue();
                m_TheoryQueued.Remove(lineId);
                if (!m_Theory.TryGetValue(lineId, out TheoryEntry entry)
                    || entry.State != "Queued")
                    continue;
                if (entry.Generation != m_LineGeneration(lineId))
                {
                    QueueTheory(
                        lineId,
                        true,
                        nowFrame + TheoryStabilityDelayFrames);
                    continue;
                }
                if (entry.NextFrame > nowFrame)
                {
                    m_TheoryQueue.Enqueue(lineId);
                    m_TheoryQueued.Add(lineId);
                    if (fallback == null || entry.NextFrame < earliestFrame)
                    {
                        fallback = entry;
                        earliestFrame = entry.NextFrame;
                    }
                    continue;
                }
                return entry;
            }
            if (fallback != null)
                m_TheoryWakeFrame = fallback.NextFrame;
            return null;
        }

        private void PollTheory(TheoryEntry entry, uint nowFrame)
        {
            FullRunTimeSession session = entry.Session;
            if (session == null)
            {
                ClearTheoryActive(entry);
                return;
            }
            if (m_LineGeneration(session.LineId) != session.Generation)
            {
                FinishTheory(entry, "Unavailable", "run-time-line-invalidated", string.Empty, nowFrame);
                return;
            }
            if (Elapsed(session.StartTicks) >= TimeoutMilliseconds)
            {
                RetryTheory(entry, "run-time-theory-timeout", string.Empty, nowFrame);
                return;
            }

            RailEtaBridgeService service = RailEtaBridgeService.Current;
            if (service == null || !service.TryGetState(session.Ticket, out RailEtaPublicStatus status))
            {
                RetryTheory(entry, "run-time-theory-status-missing", string.Empty, nowFrame);
                return;
            }
            if (status.State == "Completed")
            {
                if (ApplyTheory(session, status.TheorySegments))
                    CompleteTheory(entry);
                else
                    FinishTheory(entry, "Unavailable", "run-time-theory-result-invalid", status.Detail, nowFrame);
                return;
            }

            string failure = status.Failure ?? string.Empty;
            string detail = status.Detail ?? string.Empty;
            if (status.State == "Cancelled" || status.State == "Busy" || status.State == "ClockChanged")
            {
                RequeueTheory(entry, nowFrame, TheoryRetryDelayFrames);
                return;
            }
            if (status.State == "WorkerLost" || status.State == "Unavailable")
            {
                m_TheoryPaused = true;
                RequeueTheory(entry, nowFrame, TheoryRetryDelayFrames);
                return;
            }
            if (status.State == "NotConverged")
            {
                FinishTheory(entry, "Unavailable", failure, detail, nowFrame);
                return;
            }
            if (status.State == "Failed")
            {
                if (IsTemporaryTheoryFailure(failure, detail))
                    RetryTheory(entry, failure, detail, nowFrame);
                else
                    FinishTheory(entry, "Unavailable", failure, detail, nowFrame);
            }
        }

        private void CompleteTheory(TheoryEntry entry)
        {
            FullRunTimeSession session = entry.Session;
            ClearTheoryActive(entry);
            session.State = "Completed";
            session.ResultId = StoreResult(session);
            entry.State = "Completed";
            entry.Error = string.Empty;
            entry.Detail = string.Empty;
            entry.ResultId = session.ResultId;
            entry.Session = null;
            Release(session);
            PushTheoryWaiters(entry);
        }

        private void RetryTheory(TheoryEntry entry, string error, string detail, uint nowFrame)
        {
            FullRunTimeSession session = entry.Session;
            ClearTheoryActive(entry);
            entry.Session = null;
            Release(session);
            if (entry.RetryCount < MaxTheoryRetries)
            {
                entry.RetryCount++;
                entry.State = "Queued";
                entry.Error = string.Empty;
                entry.Detail = string.Empty;
                entry.NextFrame = nowFrame + (entry.RetryCount == 1
                    ? TheoryRetryDelayFrames
                    : TheoryLongRetryDelayFrames);
                EnqueueTheory(entry);
                PushTheoryWaiters(entry);
                return;
            }
            FinishTheory(entry, "Unavailable", error, detail, nowFrame);
        }

        private void RequeueTheory(TheoryEntry entry, uint nowFrame, uint delay)
        {
            FullRunTimeSession session = entry.Session;
            ClearTheoryActive(entry);
            entry.Session = null;
            Release(session);
            entry.State = "Queued";
            entry.Error = string.Empty;
            entry.Detail = string.Empty;
            entry.NextFrame = nowFrame + delay;
            EnqueueTheory(entry);
            PushTheoryWaiters(entry);
        }

        private void FinishTheory(
            TheoryEntry entry,
            string state,
            string error,
            string detail,
            uint nowFrame)
        {
            FullRunTimeSession session = entry.Session;
            ClearTheoryActive(entry);
            entry.Session = null;
            Release(session);
            entry.State = state ?? "Unavailable";
            entry.Error = error ?? string.Empty;
            entry.Detail = detail ?? string.Empty;
            entry.ResultId = string.Empty;
            entry.NextFrame = nowFrame;
            PushTheoryWaiters(entry);
        }

        private void QueueTheory(string lineId, bool force, uint nextFrame)
        {
            if (string.IsNullOrEmpty(lineId))
                return;
            if (!m_Theory.TryGetValue(lineId, out TheoryEntry entry))
            {
                entry = new TheoryEntry { LineId = lineId, State = "Queued" };
                m_Theory[lineId] = entry;
            }
            else if (!force && (entry.State == "Queued" || entry.State == "Running"))
            {
                return;
            }
            else if (!force && entry.State == "Unavailable"
                && entry.Generation == m_LineGeneration(lineId))
            {
                return;
            }
            ClearTheoryActive(entry);
            if (entry.Session?.Ticket.IsValid == true)
                RailEtaBridgeService.Current?.Cancel(entry.Session.Ticket);
            if (entry.Session != null)
                Release(entry.Session);
            entry.Session = null;
            entry.State = "Queued";
            entry.Error = string.Empty;
            entry.Detail = string.Empty;
            entry.ResultId = string.Empty;
            entry.Generation = m_LineGeneration(lineId);
            entry.RetryCount = 0;
            entry.NextFrame = nextFrame;
            EnqueueTheory(entry);
            if (m_TheoryActive == null
                && (m_TheoryWakeFrame == 0 || entry.NextFrame < m_TheoryWakeFrame))
            {
                m_TheoryWakeFrame = entry.NextFrame;
            }
            PushTheoryWaiters(entry);
        }

        private void EnqueueTheory(TheoryEntry entry)
        {
            if (entry == null || entry.State != "Queued"
                || !m_TheoryQueued.Add(entry.LineId))
                return;
            m_TheoryQueue.Enqueue(entry.LineId);
        }

        private void ClearTheoryActive(TheoryEntry entry)
        {
            if (ReferenceEquals(m_TheoryActive, entry))
                m_TheoryActive = null;
        }

        private static bool IsTemporaryTheoryFailure(string failure, string detail)
        {
            string value = (failure ?? string.Empty) + " " + (detail ?? string.Empty);
            return value.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("status-missing", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("submit", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private TheoryWaiter AddTheoryWaiter(string editor, string lineId)
        {
            string key = TheoryWaiterKey(editor, lineId);
            TheoryWaiter waiter = new TheoryWaiter
            {
                EditorSessionId = editor ?? string.Empty,
                LineId = lineId ?? string.Empty,
                QueryId = Guid.NewGuid().ToString("N"),
                State = "Queued"
            };
            m_TheoryWaiters[key] = waiter;
            return waiter;
        }

        private void RemoveTheoryWaiter(string editor, string lineId)
        {
            m_TheoryWaiters.Remove(TheoryWaiterKey(editor, lineId));
        }

        private bool TryFindTheoryWaiter(
            string editor,
            string queryId,
            out TheoryWaiter waiter)
        {
            string editorId = editor ?? string.Empty;
            waiter = m_TheoryWaiters.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.EditorSessionId, editorId, StringComparison.Ordinal)
                && (string.IsNullOrEmpty(queryId)
                    || string.Equals(candidate.QueryId, queryId, StringComparison.Ordinal)));
            return waiter != null;
        }

        private void PushTheoryWaiters(TheoryEntry entry)
        {
            if (entry == null)
                return;
            FullRunTimeResult result = FindTheoryResult(entry.LineId);
            foreach (TheoryWaiter waiter in m_TheoryWaiters.Values
                .Where(waiter => string.Equals(waiter.LineId, entry.LineId, StringComparison.Ordinal))
                .ToArray())
            {
                waiter.State = entry.State;
                waiter.Error = entry.Error ?? string.Empty;
                waiter.Detail = entry.Detail ?? string.Empty;
                waiter.ResultId = entry.ResultId ?? string.Empty;
                waiter.StopSig = entry.Session?.Plan?.StopSig ?? result?.StopSig ?? string.Empty;
                m_Push(TheoryStatus(waiter, result));
            }
        }

        private bool TryFindTheoryResult(string lineId, out FullRunTimeResult result)
        {
            result = FindTheoryResult(lineId);
            return result != null;
        }

        private FullRunTimeResult FindTheoryResult(string lineId)
        {
            return m_Theory.TryGetValue(lineId, out TheoryEntry entry)
                && !string.IsNullOrEmpty(entry.ResultId)
                && m_Results.TryGetValue(entry.ResultId, out FullRunTimeResult result)
                ? result
                : null;
        }

        private static string TheoryWaiterKey(string editor, string lineId)
        {
            return (editor ?? string.Empty) + "\u001f" + (lineId ?? string.Empty);
        }

        private static DispatchWorkbenchRunChartSegmentDto[] SegmentDtos(
            IEnumerable<RunChartSegment> segments)
        {
            return (segments ?? Enumerable.Empty<RunChartSegment>())
                .Select(segment => new DispatchWorkbenchRunChartSegmentDto
                {
                    fromStopKey = segment.FromStopKey,
                    toStopKey = segment.ToStopKey,
                    fromWaypointIndex = segment.FromWaypointIndex,
                    toWaypointIndex = segment.ToWaypointIndex,
                    segmentFrames = segment.Frames,
                    segmentMinutes = segment.Minutes,
                    segmentMinutesExact = segment.ExactMinutes
                })
                .ToArray();
        }

        private static DispatchWorkbenchRunChartDwellDto[] DwellDtos(
            IEnumerable<RunChartDwell> dwells)
        {
            return (dwells ?? Enumerable.Empty<RunChartDwell>())
                .Select(dwell => new DispatchWorkbenchRunChartDwellDto
                {
                    stopKey = dwell.StopKey ?? string.Empty,
                    waypointIndex = dwell.WaypointIndex,
                    averageFrames = dwell.Frames,
                    averageMinutes = dwell.Minutes,
                    sampleCount = dwell.SampleCount,
                    hasObservation = dwell.HasObservation
                })
                .ToArray();
        }

        private static DispatchWorkbenchRunTimeQueryStatusDto TheoryStatus(
            TheoryWaiter waiter,
            FullRunTimeResult result)
        {
            return new DispatchWorkbenchRunTimeQueryStatusDto
            {
                queryId = waiter?.QueryId ?? string.Empty,
                editorSessionId = waiter?.EditorSessionId ?? string.Empty,
                state = waiter?.State ?? "Unavailable",
                resultId = !string.IsNullOrEmpty(waiter?.ResultId)
                    ? waiter.ResultId
                    : result?.ResultId ?? string.Empty,
                error = waiter?.Error ?? string.Empty,
                detail = waiter?.Detail ?? string.Empty,
                lineId = waiter?.LineId ?? result?.LineId ?? string.Empty,
                source = "theory",
                stopSig = !string.IsNullOrEmpty(waiter?.StopSig)
                    ? waiter.StopSig
                    : result?.StopSig ?? string.Empty,
                sourceRevision = result?.SourceRevision ?? 0UL,
                complete = result != null,
                prefixStopCount = result?.StopKeys?.Length ?? 0,
                missingKind = "none",
                segments = SegmentDtos(result?.Segments),
                dwells = DwellDtos(result?.Dwells)
            };
        }

        internal bool TryGetResult(string editorSessionId, string resultId, out FullRunTimeResult result)
        {
            result = null;
            return !string.IsNullOrEmpty(editorSessionId) && !string.IsNullOrEmpty(resultId)
                && m_Results.TryGetValue(resultId, out result)
                && (result.Source == "theory" || result.EditorSessionId == editorSessionId)
                && result.Generation == m_LineGeneration(result.LineId);
        }

        internal void InvalidateLines(IEnumerable<string> lineIds)
        {
            HashSet<string> ids = new HashSet<string>(lineIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            Dictionary<string, RunTimeInvalidationDto> invalidations =
                new Dictionary<string, RunTimeInvalidationDto>(StringComparer.Ordinal);
            foreach (FullRunTimeResult result in m_Results.Values
                .Where(result => result.Source != "theory" && ids.Contains(result.LineId))
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
            foreach (string lineId in ids)
                ResetTheoryLine(lineId, "run-time-line-invalidated", invalidations, false);
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
                .Where(result => result.Source != "theory"
                    && (allLines || ids.Contains(result.LineId)) && sourceSet.Contains(result.Source))
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
            if (sourceSet.Contains("theory"))
            {
                IEnumerable<string> theoryLines = m_Theory.Keys
                    .Where(lineId => allLines || ids.Contains(lineId))
                    .ToArray();
                foreach (string lineId in theoryLines)
                    ResetTheoryLine(lineId, reason, invalidations, true);
            }
            foreach (RunTimeInvalidationDto invalidation in invalidations.Values)
                m_PushInvalidation(invalidation);
        }

        private void ResetTheoryLine(
            string lineId,
            string reason,
            Dictionary<string, RunTimeInvalidationDto> invalidations,
            bool requeue)
        {
            bool needed = m_TheoryLines.Contains(lineId) || HasTheoryWaiter(lineId);
            if (!m_Theory.TryGetValue(lineId, out TheoryEntry entry))
            {
                if (!needed)
                    return;
                entry = new TheoryEntry { LineId = lineId };
                m_Theory[lineId] = entry;
            }

            foreach (TheoryWaiter waiter in m_TheoryWaiters.Values
                .Where(waiter => string.Equals(waiter.LineId, lineId, StringComparison.Ordinal))
                .ToArray())
            {
                AddInvalidation(invalidations, waiter.EditorSessionId, lineId, "theory", reason);
            }
            if (!string.IsNullOrEmpty(entry.ResultId))
                m_Results.Remove(entry.ResultId);
            entry.ResultId = string.Empty;
            if (!needed)
            {
                ClearTheoryActive(entry);
                if (entry.Session?.Ticket.IsValid == true)
                    RailEtaBridgeService.Current?.Cancel(entry.Session.Ticket);
                Release(entry.Session);
                entry.Session = null;
                m_Theory.Remove(lineId);
                m_TheoryQueued.Remove(lineId);
                return;
            }
            QueueTheory(
                lineId,
                true,
                m_TheoryFrame + (requeue ? 0U : TheoryStabilityDelayFrames));
        }

        private bool HasTheoryWaiter(string lineId)
        {
            return m_TheoryWaiters.Values.Any(waiter =>
                string.Equals(waiter.LineId, lineId, StringComparison.Ordinal)
                && waiter.State != "Cancelled");
        }

        internal void RefreshBusHistorical(string lineId)
        {
            string key = lineId ?? string.Empty;
            if (string.IsNullOrEmpty(key))
                return;

            foreach (FullRunTimeSession session in m_Active.Values
                .Where(session => session != null
                    && session.State != "Running"
                    && !session.Complete
                    && string.Equals(session.LineId, key, StringComparison.Ordinal)
                    && string.Equals(session.Source, "busHistorical", StringComparison.Ordinal))
                .ToArray())
            {
                ClearResults(session.EditorSessionId, session.LineId, session.Source);
                session.State = "Running";
                session.ResultId = string.Empty;
                session.Error = string.Empty;
                session.Detail = string.Empty;
                session.Complete = false;
                session.PrefixStopCount = 0;
                session.MissingKind = "none";
                session.Segments = null;
                session.Dwells = new List<RunChartDwell>();
                session.StartTicks = Stopwatch.GetTimestamp();
                m_Push(Status(session));
            }
        }

        internal void ClearEditor(string editorSessionId)
        {
            string editor = editorSessionId ?? string.Empty;
            ClearActive(editor);
            foreach (string key in m_TheoryWaiters
                .Where(entry => string.Equals(entry.Value.EditorSessionId, editor, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .ToArray())
                m_TheoryWaiters.Remove(key);
            foreach (string id in m_Results.Values
                .Where(result => result.Source != "theory" && result.EditorSessionId == editor)
                .Select(result => result.ResultId)
                .ToArray())
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
            foreach (TheoryEntry entry in m_Theory.Values)
            {
                if (entry?.Session?.Ticket.IsValid == true)
                    RailEtaBridgeService.Current?.Cancel(entry.Session.Ticket);
                Release(entry?.Session);
            }
            m_Active.Clear();
            m_Results.Clear();
            m_Theory.Clear();
            m_TheoryWaiters.Clear();
            m_TheoryQueue.Clear();
            m_TheoryQueued.Clear();
            m_TheoryLines.Clear();
            m_TheoryActive = null;
            m_TheoryPaused = false;
            m_TheoryWakeFrame = 0;
        }

        private void Poll(FullRunTimeSession session)
        {
            if (m_LineGeneration(session.LineId) != session.Generation) { Fail(session, "run-time-line-invalidated"); return; }
            if (session.Source == "busHistorical")
                PollBusHistorical(session);
        }

        private void PollBusHistorical(FullRunTimeSession session)
        {
            if (!m_RoutePlans.TryGet(session.Line, session.Lifecycle, out RoutePlan plan)
                || plan.Waypoints.Length > MaxWaypoints)
            {
                Fail(session, "run-time-route-plan-unavailable");
                return;
            }
            int[] stops = plan.Stops.Select(stop => stop.WaypointIndex).ToArray();
            if (stops.Length < 2 || stops.Length > MaxSegments)
            {
                Fail(session, "run-time-segment-limit");
                return;
            }
            session.Plan = plan;
            session.StopWaypointIndices = stops;
            session.Dwells = new List<RunChartDwell>();
            if (!BuildBusHistorical(session, out string detail))
            {
                Fail(session, "bus-historical-invalid", detail);
                return;
            }
            if (session.Complete)
                Complete(session);
            else
                CompleteIncomplete(session, detail);
        }

        private bool BuildHistorical(FullRunTimeSession session, out string detail)
        {
            detail = string.Empty;
            List<RunChartSegment> segments = BuildIntervals(
                session.Plan,
                session.StopWaypointIndices,
                true);
            for (int i = 0; i < segments.Count; i++)
            {
                RunChartSegment segment = segments[i];
                RouteWaypointRef from = session.Plan.Waypoints[segment.FromWaypointIndex];
                RouteWaypointRef to = session.Plan.Waypoints[segment.ToWaypointIndex];
                float frames;
                bool found = m_Observation.TryTraversalFrames(
                    session.Line,
                    from.WaypointIndex,
                    to.WaypointIndex,
                    out frames,
                    out _);
                if (!found || frames <= 0f || float.IsNaN(frames) || float.IsInfinity(frames))
                {
                    session.MissingKind = "slice";
                    segments.RemoveRange(i, segments.Count - i);
                    session.Segments = segments;
                    session.Complete = false;
                    session.PrefixStopCount = segments.Count + 1;
                    return true;
                }
                segment.Frames = (uint)Math.Max(1, Math.Round(frames));
                segment.Minutes = ToMinutes(frames, session.FramesPerMinute) + 5;
                segment.ExactMinutes = ToExactMinutes(frames, session.FramesPerMinute) + 5d;
                segments[i] = segment;

                if (i + 1 < session.StopWaypointIndices.Length)
                {
                    RunChartDwell dwell = BuildDwell(session, i + 1);
                    session.Dwells.Add(dwell);
                    bool validDwell = dwell != null
                        && dwell.HasObservation
                        && dwell.Frames > 0f
                        && !float.IsNaN(dwell.Frames)
                        && !float.IsInfinity(dwell.Frames);
                    if (!validDwell)
                    {
                        session.MissingKind = "dwell";
                        segments.RemoveRange(i + 1, segments.Count - i - 1);
                        session.Segments = segments;
                        session.Complete = false;
                        session.PrefixStopCount = segments.Count + 1;
                        return true;
                    }
                }
            }
            session.Segments = segments;
            session.Complete = true;
            session.MissingKind = "none";
            session.PrefixStopCount = segments.Count + 1;
            if (segments.Count == 0)
            {
                detail = "code=no-segments";
                return false;
            }
            return true;
        }

        private bool BuildMonitorAverage(FullRunTimeSession session, out string detail)
        {
            detail = string.Empty;
            if (!m_Observation.TryMonitorAverageSnapshot(
                    session.Line,
                    session.Plan.StopSig,
                    out MonitorAverageSnapshot snapshot)
                || snapshot.AverageFrames.Length != session.Plan.Stops.Length)
            {
                detail = "monitor-average-unavailable";
                return false;
            }

            List<RunChartSegment> segments = BuildIntervals(
                session.Plan,
                session.StopWaypointIndices,
                true);
            if (segments.Count != snapshot.AverageFrames.Length)
            {
                detail = "monitor-average-layout-mismatch";
                return false;
            }
            for (int i = 0; i < segments.Count; i++)
            {
                double averageFrames = snapshot.AverageFrames[i];
                if (!(averageFrames > 0d)
                    || double.IsNaN(averageFrames)
                    || double.IsInfinity(averageFrames)
                    || averageFrames >= uint.MaxValue)
                {
                    detail = "monitor-average-segment-invalid";
                    return false;
                }
                RunChartSegment segment = segments[i];
                segment.Frames = (uint)Math.Max(
                    1d,
                    Math.Round(averageFrames, MidpointRounding.AwayFromZero));
                segment.Minutes = ToMinutes(averageFrames, session.FramesPerMinute);
                segment.ExactMinutes = ToExactMinutes(averageFrames, session.FramesPerMinute);
                segments[i] = segment;
            }
            session.SourceRevision = snapshot.Revision;
            session.Segments = segments;
            session.Complete = true;
            session.PrefixStopCount = segments.Count + 1;
            return true;
        }

        private bool BuildBusHistorical(FullRunTimeSession session, out string detail)
        {
            detail = string.Empty;
            List<RunChartSegment> segments = BuildIntervals(
                session.Plan,
                session.StopWaypointIndices,
                true);
            for (int i = 0; i < segments.Count; i++)
            {
                RunChartSegment segment = segments[i];
                RouteWaypointRef from = session.Plan.Waypoints[segment.FromWaypointIndex];
                RouteWaypointRef to = session.Plan.Waypoints[segment.ToWaypointIndex];
                if (!m_Observation.TryBusSegFrames(
                        session.Line,
                        from.Waypoint,
                        from.Stop,
                        to.Waypoint,
                        to.Stop,
                        out float frames)
                    || frames <= 0f
                    || float.IsNaN(frames)
                    || float.IsInfinity(frames))
                {
                    detail = "segment=" + i
                        + ";from=" + from.WaypointIndex
                        + ";to=" + to.WaypointIndex
                        + ";code=bus-segment-missing";
                    segments.RemoveRange(i, segments.Count - i);
                    session.Segments = segments;
                    session.Complete = false;
                    session.MissingKind = "busSegment";
                    session.PrefixStopCount = segments.Count + 1;
                    return true;
                }
                segment.Frames = (uint)Math.Max(1, Math.Round(frames));
                segment.Minutes = ToMinutes(frames, session.FramesPerMinute);
                segment.ExactMinutes = ToExactMinutes(frames, session.FramesPerMinute);
                segments[i] = segment;
            }
            session.Segments = segments;
            session.Complete = true;
            session.MissingKind = "none";
            session.PrefixStopCount = segments.Count + 1;
            if (segments.Count > 0)
                return true;
            detail = "code=no-segments";
            return false;
        }

        private List<RunChartDwell> BuildDwells(FullRunTimeSession session)
        {
            List<RunChartDwell> dwells = new List<RunChartDwell>(session.Plan.Stops.Length);
            for (int i = 0; i < session.Plan.Stops.Length; i++)
                dwells.Add(BuildDwell(session, i));
            return dwells;
        }

        private RunChartDwell BuildDwell(FullRunTimeSession session, int stopIndex)
        {
            RouteStopRef stop = session.Plan.Stops[stopIndex];
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
                dwell.Minutes = ToMinutes(observation.AverageFrames, session.FramesPerMinute);
                dwell.SampleCount = observation.SampleCount;
                dwell.HasObservation = true;
            }
            return dwell;
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
                segment.Minutes = ToMinutes(result.SegmentFrames, session.FramesPerMinute);
                segment.ExactMinutes = ToExactMinutes(result.SegmentFrames, session.FramesPerMinute);
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
            session.ResultId = StoreResult(session);
            Release(session);
        }

        private string StoreResult(FullRunTimeSession session)
        {
            bool shared = session.Source == "theory";
            if (shared)
            {
                if (m_Theory.TryGetValue(session.LineId, out TheoryEntry previous))
                {
                    if (!string.IsNullOrEmpty(previous.ResultId))
                        m_Results.Remove(previous.ResultId);
                    previous.ResultId = string.Empty;
                }
            }
            else
            {
                foreach (string resultId in m_Results.Values
                    .Where(result => string.Equals(result.EditorSessionId, session.EditorSessionId, StringComparison.Ordinal)
                        && string.Equals(result.LineId, session.LineId, StringComparison.Ordinal)
                        && string.Equals(result.Source, session.Source, StringComparison.Ordinal))
                    .Select(result => result.ResultId)
                    .ToArray())
                {
                    m_Results.Remove(resultId);
                }
            }
            string storedResultId = Guid.NewGuid().ToString("N");
            m_Results[storedResultId] = new FullRunTimeResult
            {
                ResultId = storedResultId, EditorSessionId = shared ? string.Empty : session.EditorSessionId,
                LineId = session.LineId,
                Line = session.Line, Source = session.Source, StopSig = session.Plan.StopSig, Generation = session.Generation,
                SourceRevision = session.SourceRevision,
                CompletedOrder = ++m_NextResultOrder,
                StopKeys = session.Plan.Stops.Select(stop => stop.StopKey ?? string.Empty).ToArray(),
                Segments = session.Segments?.ToArray() ?? Array.Empty<RunChartSegment>(),
                Dwells = session.Dwells?.ToArray() ?? Array.Empty<RunChartDwell>()
            };
            if (shared && m_Theory.TryGetValue(session.LineId, out TheoryEntry current))
                current.ResultId = storedResultId;
            if (!shared)
                TrimResults();
            return storedResultId;
        }

        private static void CompleteIncomplete(FullRunTimeSession session, string detail)
        {
            session.State = "Completed";
            session.ResultId = string.Empty;
            session.Error = string.Empty;
            session.Detail = detail ?? string.Empty;
            Release(session);
        }

        private void TrimResults()
        {
            HashSet<string> editors = new HashSet<string>(
                m_Results.Values
                    .Where(result => result.Source != "theory")
                    .Select(result => result.EditorSessionId),
                StringComparer.Ordinal);
            foreach (string editor in editors)
            {
                while (m_Results.Values.Count(result => result.Source != "theory"
                    && result.EditorSessionId == editor) > MaxResultsPerEditor)
                {
                    FullRunTimeResult oldest = FindOldestResult(editor);
                    if (oldest == null)
                        break;
                    m_Results.Remove(oldest.ResultId);
                }
            }
            while (m_Results.Values.Count(result => result.Source != "theory") > MaxResults)
            {
                FullRunTimeResult oldest = FindOldestResult(null);
                if (oldest == null)
                    break;
                m_Results.Remove(oldest.ResultId);
            }
        }

        private FullRunTimeResult FindOldestResult(string editorSessionId)
        {
            FullRunTimeResult fallback = null;
            FullRunTimeResult idle = null;
            foreach (FullRunTimeResult result in m_Results.Values
                .Where(result => result.Source != "theory"
                    && (editorSessionId == null || result.EditorSessionId == editorSessionId))
                .OrderBy(result => result.CompletedOrder))
            {
                fallback ??= result;
                bool active = m_Active.Values.Any(session => session.State == "Running"
                    && session.EditorSessionId == result.EditorSessionId
                    && session.LineId == result.LineId
                    && session.Source == result.Source);
                if (!active)
                {
                    idle = result;
                    break;
                }
            }
            return idle ?? fallback;
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
                stopSig = session.Plan?.StopSig ?? string.Empty,
                sourceRevision = session.SourceRevision,
                complete = session.Complete,
                prefixStopCount = session.PrefixStopCount > 0
                    ? session.PrefixStopCount
                    : session.Plan?.Stops.Length ?? 0,
                missingKind = string.IsNullOrEmpty(session.MissingKind)
                    ? "none"
                    : session.MissingKind,
                segments = SegmentDtos(session.Segments),
                dwells = DwellDtos(session.Dwells)
            };
        }

        private static DispatchWorkbenchRunTimeQueryStatusDto Failure(string queryId, string editor, string error)
        {
            return new DispatchWorkbenchRunTimeQueryStatusDto
            {
                queryId = queryId ?? string.Empty, editorSessionId = editor ?? string.Empty,
                state = "Failed", error = error ?? string.Empty,
                missingKind = "none",
                segments = Array.Empty<DispatchWorkbenchRunChartSegmentDto>(),
                dwells = Array.Empty<DispatchWorkbenchRunChartDwellDto>()
            };
        }

        private static int ToMinutes(double frames, double framesPerMinute)
        {
            return framesPerMinute > 0d && !double.IsNaN(framesPerMinute) && !double.IsInfinity(framesPerMinute)
                ? Math.Max(1, (int)Math.Round(frames / framesPerMinute, MidpointRounding.AwayFromZero))
                : Math.Max(1, (int)Math.Round(frames, MidpointRounding.AwayFromZero));
        }

        private static double ToExactMinutes(double frames, double framesPerMinute)
        {
            double minutes = framesPerMinute > 0d && !double.IsNaN(framesPerMinute) && !double.IsInfinity(framesPerMinute)
                ? frames / framesPerMinute
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
        internal RailEtaTheorySegmentRequest[] TheoryRequests = Array.Empty<RailEtaTheorySegmentRequest>();
        internal ulong Generation;
        internal ulong SourceRevision;
        internal double FramesPerMinute;
        internal bool Complete = true;
        internal int PrefixStopCount;
        internal string MissingKind = "none";
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
        internal ulong SourceRevision;
        internal long CompletedOrder;
        internal string[] StopKeys;
        internal RunChartSegment[] Segments;
        internal RunChartDwell[] Dwells;
    }

    internal sealed class TheoryEntry
    {
        internal string LineId = string.Empty;
        internal string State = "Queued";
        internal string ResultId = string.Empty;
        internal string Error = string.Empty;
        internal string Detail = string.Empty;
        internal ulong Generation;
        internal int RetryCount;
        internal uint NextFrame;
        internal FullRunTimeSession Session;
    }

    internal sealed class TheoryWaiter
    {
        internal string EditorSessionId = string.Empty;
        internal string LineId = string.Empty;
        internal string QueryId = string.Empty;
        internal string State = "Queued";
        internal string ResultId = string.Empty;
        internal string Error = string.Empty;
        internal string Detail = string.Empty;
        internal string StopSig = string.Empty;
    }
}
