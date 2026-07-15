using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RapidTransitMod.RailEta.Contracts;

namespace RapidTransitMod.RailEtaHost
{
    internal sealed class RailEtaService : IDisposable
    {
        private static RailEtaService s_Current;
        private static long s_NextInstanceId;
        private readonly RailEtaRequestQueue m_Ingress = new RailEtaRequestQueue();
        private readonly RailEtaTicketStore m_Tickets = new RailEtaTicketStore();
        private readonly RailEtaResultStore m_Results;
        private readonly RailEtaPredictorRouter m_Predictors = new RailEtaPredictorRouter();
        private long m_NextTicket;
        private long m_NextBatch;
        private int m_Generation = 1;
        private int m_Disposed;
        private int m_WorkerLost;
        private int m_HoldSnapshotRequested;
        private uint m_LastObservedFrame;

        public RailEtaService(RailEtaWorker worker, IRailEtaGeometryProvider geometryProvider)
        {
            Worker = worker ?? throw new ArgumentNullException(nameof(worker));
            GeometryProvider = geometryProvider ?? throw new ArgumentNullException(nameof(geometryProvider));
            m_Results = new RailEtaResultStore(m_Tickets);
            InstanceId = Interlocked.Increment(ref s_NextInstanceId);
        }

        public RailEtaWorker Worker { get; }
        public IRailEtaGeometryProvider GeometryProvider { get; }
        internal RailEtaPredictorRouter Predictors => m_Predictors;
        public int Generation => Volatile.Read(ref m_Generation);
        public long InstanceId { get; }
        public bool IsDisposed => Volatile.Read(ref m_Disposed) != 0;
        public bool WorkerLost => Volatile.Read(ref m_WorkerLost) != 0 || Worker.WorkerLost;
        internal uint LastObservedFrame => Volatile.Read(ref m_LastObservedFrame);
        public int PendingCount => m_Ingress.Count;
        public static RailEtaService Current => Volatile.Read(ref s_Current);
        public static void Bind(RailEtaService service) => Volatile.Write(ref s_Current, service);
        public static void Unbind(RailEtaService service) { if (ReferenceEquals(Current, service)) Volatile.Write(ref s_Current, null); }

#if RT_DEBUG_TOOLS
        internal void SetHotRuntime(RailEtaHotRuntime runtime) => m_Predictors.SetHotRuntime(runtime);
#endif

        public RailEtaTicket RequestEta(RailEtaRequestDescriptor descriptor)
        {
            if (Volatile.Read(ref m_Disposed) != 0 || WorkerLost) return default;
            RailEtaTicket ticket = new RailEtaTicket(Interlocked.Increment(ref m_NextTicket));
            int generation = Generation;
            m_Tickets.Add(ticket, descriptor, generation);
            // Ingress-only: do not request hold capture here. Active batches must not level-trigger
            // Dispatch hold rebuild every frame; SnapshotSystem requests capture only when ready to drain.
            if (!m_Ingress.TryEnqueue(new RailEtaRequestEnvelope { Ticket = ticket, Descriptor = descriptor, EnqueueGeneration = generation }))
                m_Tickets.Transition(ticket, RailEtaRequestState.Busy, 0, 0, generation, RailEtaFailure.Busy, "ETA ingress queue is full.");
            return ticket;
        }

        public bool TryGetState(RailEtaTicket ticket, out RailEtaTicketStatus status) => m_Tickets.TryGetStatus(ticket, out status);
        public bool TryGetSnapshot(RailEtaTicket ticket, out RailEtaWorldSnapshot snapshot) => m_Tickets.TryGetSnapshot(ticket, out snapshot);
        public bool TryGetPrediction(RailEtaTicket ticket, out RailEtaPrediction prediction) => m_Tickets.TryGetPrediction(ticket, out prediction);
        internal bool TryGetRequest(RailEtaTicket ticket, out RailEtaRequest request) => m_Tickets.TryGetRequest(ticket, out request);
        public bool Cancel(RailEtaTicket ticket) => m_Tickets.Cancel(ticket);

        internal long NextBatchId() => Interlocked.Increment(ref m_NextBatch);
        internal bool TryDrain(out RailEtaRequestEnvelope request) => m_Ingress.TryDequeue(out request);
        internal void BindRequestFrame(RailEtaTicket ticket, uint requestFrame, long batchId, int generation) => m_Tickets.BindRequestFrame(ticket, requestFrame, batchId, generation);
        internal void Transition(RailEtaTicket ticket, RailEtaRequestState state, uint requestFrame, long batchId, int generation, RailEtaFailure failure = RailEtaFailure.None, string detail = "") => m_Tickets.Transition(ticket, state, requestFrame, batchId, generation, failure, detail);
        internal void Publish(RailEtaTicket ticket, RailEtaWorldSnapshot snapshot, int generation) => m_Results.Publish(ticket, snapshot, generation, LastObservedFrame);
        internal void StoreRequest(RailEtaTicket ticket, RailEtaRequest request, int generation) => m_Tickets.StoreRequest(ticket, request, generation);
        internal void MarkPredictionFinished(RailEtaTicket ticket, int generation) => m_Tickets.MarkPredictionFinished(ticket, generation, LastObservedFrame);
        internal void PublishPrediction(RailEtaTicket ticket, RailEtaPrediction prediction, int generation) => m_Results.PublishPrediction(ticket, prediction, generation, LastObservedFrame);
        internal void ObserveFrame(uint frame) => Volatile.Write(ref m_LastObservedFrame, frame);
        /// <summary>
        /// Level is only set by SnapshotSystem when Idle, topology-ready, and about to drain.
        /// Dispatch consumes it once via ClearHoldSnapshotCapture after publishing that frame's holds.
        /// </summary>
        internal bool ShouldCaptureHoldSnapshot => PendingCount > 0 && Volatile.Read(ref m_HoldSnapshotRequested) != 0;
        internal void RequestHoldSnapshotCapture() { if (PendingCount > 0) Interlocked.Exchange(ref m_HoldSnapshotRequested, 1); }
        internal void ClearHoldSnapshotCapture() => Interlocked.Exchange(ref m_HoldSnapshotRequested, 0);

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
            RailEtaComparisonDebugApi.StopForReset();
#endif
            int old = Interlocked.Increment(ref m_Generation) - 1;
            m_Tickets.CancelGeneration(old);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_Disposed, 1) != 0) return;
            ResetCity();
            Unbind(this);
            Worker.Dispose();
        }

#if RT_DEBUG_TOOLS
        internal Task<string> ExportDebugAsync(RailEtaTicket ticket, string filePath)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (IsDisposed || WorkerLost)
            {
                completion.TrySetException(new InvalidOperationException("Rail ETA worker is unavailable."));
                return completion.Task;
            }
            if (!TryGetSnapshot(ticket, out RailEtaWorldSnapshot snapshot) || !m_Tickets.TryGetRequest(ticket, out RailEtaRequest request)
                || !TryGetPrediction(ticket, out RailEtaPrediction prediction))
            {
                completion.TrySetException(new InvalidOperationException("Rail ETA snapshot, request, or prediction is not ready."));
                return completion.Task;
            }
            if (!Worker.TryEnqueue(() =>
            {
                try { completion.TrySetResult(RailEtaSnapshotDiagnostics.Export(snapshot, request, prediction, 1, filePath)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            })) completion.TrySetException(new InvalidOperationException("Rail ETA worker queue is unavailable."));
            return completion.Task;
        }
#endif
    }

#if RT_DEBUG_TOOLS
    internal static class RailEtaDebugApi
    {
        public static RailEtaTicket RequestSnapshot(int vehicleIndex, int vehicleVersion, long checkpointId = 0)
            => RailEtaService.Current?.RequestEta(new RailEtaRequestDescriptor(vehicleIndex, vehicleVersion, checkpointId)) ?? default;
        public static bool TryGetState(RailEtaTicket ticket, out RailEtaTicketStatus status)
        {
            RailEtaService service = RailEtaService.Current;
            if (service != null) return service.TryGetState(ticket, out status);
            status = null;
            return false;
        }
        public static bool TryGetSnapshot(RailEtaTicket ticket, out RailEtaWorldSnapshot snapshot)
        {
            RailEtaService service = RailEtaService.Current;
            if (service != null) return service.TryGetSnapshot(ticket, out snapshot);
            snapshot = null;
            return false;
        }
        public static bool TryGetPrediction(RailEtaTicket ticket, out RailEtaPrediction prediction)
        {
            RailEtaService service = RailEtaService.Current;
            if (service != null) return service.TryGetPrediction(ticket, out prediction);
            prediction = null;
            return false;
        }
    }
#endif
}
