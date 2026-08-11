using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal sealed class RailEtaService : IDisposable
    {
        private static RailEtaService s_Current;
        private static long s_NextInstanceId;
        private readonly RailEtaRequestQueue m_Ingress = new RailEtaRequestQueue();
        private readonly RailEtaTicketStore m_Tickets = new RailEtaTicketStore();
        private readonly RailPredictionSolver m_Predictor = new RailPredictionSolver();
        private readonly ConcurrentDictionary<long, byte> m_IncompleteTickets = new ConcurrentDictionary<long, byte>();
#if RT_DEBUG_TOOLS
        private readonly ConcurrentDictionary<long, RailEtaFrozenWorld> m_ReplayWorlds = new ConcurrentDictionary<long, RailEtaFrozenWorld>();
#endif
        private long m_NextBatch;
        private int m_Generation = 1;
        private int m_Disposed;
        private int m_WorkerLost;
        private uint m_LastObservedFrame;

        public RailEtaService(RailEtaWorker worker)
        {
            Worker = worker ?? throw new ArgumentNullException(nameof(worker));
            InstanceId = Interlocked.Increment(ref s_NextInstanceId);
        }

        public RailEtaWorker Worker { get; }
        internal RailPredictionSolver Predictor => m_Predictor;
        public int Generation => Volatile.Read(ref m_Generation);
        public long InstanceId { get; }
        public bool IsDisposed => Volatile.Read(ref m_Disposed) != 0;
        public bool WorkerLost => Volatile.Read(ref m_WorkerLost) != 0 || Worker.WorkerLost;
        internal uint LastObservedFrame => Volatile.Read(ref m_LastObservedFrame);
        public int PendingCount => m_Ingress.Count;
        public static RailEtaService Current => Volatile.Read(ref s_Current);
        public static void Bind(RailEtaService service) => Volatile.Write(ref s_Current, service);
        public static void Unbind(RailEtaService service) { if (ReferenceEquals(Current, service)) Volatile.Write(ref s_Current, null); }

        internal RailEtaTicket SubmitHot(long ticketValue, int generation, int vehicleIndex, int vehicleVersion, long checkpointId, RailEtaMode mode,
            int depotIndex = 0, int depotVersion = 0, int modelIndex = 0, int modelVersion = 0,
            int secondaryModelIndex = 0, int secondaryModelVersion = 0,
            RailEtaTheorySegmentRequest[] theorySegments = null,
            ulong routeSignature = 0, ulong pathSignature = 0, ulong modelSignature = 0)
        {
            Interlocked.Exchange(ref m_Generation, generation);
            RailEtaTicket ticket = new RailEtaTicket(ticketValue);
            RailEtaRequestDescriptor descriptor = new RailEtaRequestDescriptor(vehicleIndex, vehicleVersion, checkpointId, mode,
                depotIndex, depotVersion, modelIndex, modelVersion, secondaryModelIndex, secondaryModelVersion,
                routeSignature, pathSignature, modelSignature);
            m_Tickets.Add(ticket, descriptor, generation);
            if (!m_Ingress.TryEnqueue(new RailEtaRequestEnvelope
            {
                Ticket = ticket,
                Descriptor = descriptor,
                EnqueueGeneration = generation,
                TheorySegments = theorySegments ?? Array.Empty<RailEtaTheorySegmentRequest>()
            }))
                m_Tickets.Transition(ticket, RailEtaRequestState.Busy, 0, 0, generation, RailEtaFailure.Busy, "ETA ingress queue is full.");
            return ticket;
        }

        public bool TryGetState(RailEtaTicket ticket, out RailEtaTicketStatus status) => m_Tickets.TryGetStatus(ticket, out status);
        public bool TryGetSnapshot(RailEtaTicket ticket, out RailEtaWorldSnapshot snapshot) => m_Tickets.TryGetSnapshot(ticket, out snapshot);
        public bool TryGetPrediction(RailEtaTicket ticket, out RailEtaPrediction prediction) => m_Tickets.TryGetPrediction(ticket, out prediction);
        internal bool TryGetTheorySegments(RailEtaTicket ticket, out RailEtaTheorySegmentResult[] segments) => m_Tickets.TryGetTheorySegments(ticket, out segments);
        internal bool TryGetTheoryFailure(RailEtaTicket ticket, out RailEtaTheoryFailure failure) => m_Tickets.TryGetTheoryFailure(ticket, out failure);
        internal bool TryGetRequest(RailEtaTicket ticket, out RailEtaRequest request) => m_Tickets.TryGetRequest(ticket, out request);
        public bool Cancel(RailEtaTicket ticket) => m_Tickets.Cancel(ticket);

        internal long NextBatchId() => Interlocked.Increment(ref m_NextBatch);
        internal bool TryDrain(out RailEtaRequestEnvelope request) => m_Ingress.TryDequeue(out request);
        internal bool TryPeek(out RailEtaRequestEnvelope request) => m_Ingress.TryPeek(out request);
        internal void BindRequestFrame(RailEtaTicket ticket, uint requestFrame, long batchId, int generation) => m_Tickets.BindRequestFrame(ticket, requestFrame, batchId, generation);
        internal void Transition(RailEtaTicket ticket, RailEtaRequestState state, uint requestFrame, long batchId, int generation, RailEtaFailure failure = RailEtaFailure.None, string detail = "") => m_Tickets.Transition(ticket, state, requestFrame, batchId, generation, failure, detail);
        internal void Publish(RailEtaTicket ticket, RailEtaWorldSnapshot snapshot, int generation) => m_Tickets.PublishSnapshot(ticket, snapshot, generation, LastObservedFrame);
        internal void StoreRequest(RailEtaTicket ticket, RailEtaRequest request, int generation) => m_Tickets.StoreRequest(ticket, request, generation);
        internal void MarkPredictionFinished(RailEtaTicket ticket, int generation) => m_Tickets.MarkPredictionFinished(ticket, generation, LastObservedFrame);
        internal void MarkIncomplete(RailEtaTicket ticket) { if (ticket.IsValid) m_IncompleteTickets[ticket.Value] = 1; }
        internal bool IsIncomplete(RailEtaTicket ticket) => ticket.IsValid && m_IncompleteTickets.ContainsKey(ticket.Value);
        internal void PublishPrediction(RailEtaTicket ticket, RailEtaPrediction prediction, int generation) => m_Tickets.PublishPrediction(ticket, prediction, generation, LastObservedFrame);
        internal void PublishTheorySegments(RailEtaTicket ticket, RailEtaTheorySegmentResult[] segments, int generation,
            RailEtaFailure failure, string detail, RailEtaTheoryFailure theoryFailure) =>
            m_Tickets.PublishTheorySegments(ticket, segments, generation, LastObservedFrame, failure, detail, theoryFailure);
        internal void SetTheoryFailure(RailEtaTicket ticket, RailEtaTheoryFailure failure, int generation) =>
            m_Tickets.SetTheoryFailure(ticket, failure, generation);
#if RT_DEBUG_TOOLS
        internal void StoreReplayWorld(RailEtaTicket ticket, RailEtaFrozenWorld world)
        {
            if (ticket.IsValid && world != null) m_ReplayWorlds[ticket.Value] = world;
        }
        internal bool TryGetReplayWorld(RailEtaTicket ticket, out RailEtaFrozenWorld world)
        {
            world = null;
            return ticket.IsValid && m_ReplayWorlds.TryGetValue(ticket.Value, out world);
        }
#endif
        internal void ObserveFrame(uint frame) => Volatile.Write(ref m_LastObservedFrame, frame);
        internal void MarkWorkerLost(string detail)
        {
            string message = String.IsNullOrWhiteSpace(detail) ? "Rail ETA worker is lost." : detail;
            Interlocked.Exchange(ref m_WorkerLost, 1);
            Worker.MarkLost(new InvalidOperationException(message));
            m_Tickets.MarkWorkerLost(Generation, message);
        }

        public void ResetCity()
        {
#if RT_DEBUG_TOOLS
            if (RailEtaDebugSettings.HeavyExportsEnabled)
                RailEtaComparisonSystem.StopForReset();
#endif
            int old = Interlocked.Increment(ref m_Generation) - 1;
            m_Tickets.CancelGeneration(old);
            m_IncompleteTickets.Clear();
#if RT_DEBUG_TOOLS
            m_ReplayWorlds.Clear();
#endif
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_Disposed, 1) != 0) return;
            ResetCity();
            Unbind(this);
        }

#if RT_DEBUG_TOOLS
        internal Task<string> ExportFailureAsync(RailEtaTicket ticket)
        {
            if (!m_Tickets.TryGetStatus(ticket, out RailEtaTicketStatus status)
                || !m_Tickets.TryGetDescriptor(ticket, out RailEtaRequestDescriptor descriptor))
                return Task.FromException<string>(new InvalidOperationException("Rail ETA failure status is unavailable."));
            TryGetSnapshot(ticket, out RailEtaWorldSnapshot snapshot);
            m_Tickets.TryGetRequest(ticket, out RailEtaRequest request);
            TryGetPrediction(ticket, out RailEtaPrediction prediction);
            return Task.Run(() => RailEtaSnapshotDiagnostics.ExportFailure(status, descriptor, snapshot, request, prediction, null));
        }

#endif
    }
}
