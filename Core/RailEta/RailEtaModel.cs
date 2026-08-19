using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using RapidTransitMod.RailEta.Contracts;
using Unity.Entities;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal static class RailEtaLimits
    {
        public const int MaxPendingTickets = 64;
        public const int MaxScopeVehicles = 256;
        public const int MaxScopeResources = 2048;
        public const int MaxFrozenLaneFacts = 262144;
        public const int MaxFrozenLaneOccupancies = 65536;
        public const int MaxEvents = 65536;
        public const int MaxBlockerDepth = 100;
        public const int MaxRetainedTickets = 256;
        public const int MaxStageTimings = 32;
        public const int MaxTraceEvents = 256;
        public const int MaxDiagnostics = 32;
        public const int MaxCheckpoints = 2048;
        public const int MaxTheoryPathFacts = RailEtaTheorySignatures.MaxPathFacts;
        public const int WorkerWatchdogMilliseconds = 10000;
        public const int TheoryBatchTimeoutMilliseconds = 8000;
    }

    public readonly struct RailEtaTicket
    {
        public RailEtaTicket(long value) { Value = value; }
        public long Value { get; }
        public bool IsValid => Value != 0;
    }

    public readonly struct RailEtaRequestDescriptor
    {
        public RailEtaRequestDescriptor(int vehicleIndex, int vehicleVersion, long targetCheckpointId, RailEtaMode mode,
            int depotIndex = 0, int depotVersion = 0, int modelIndex = 0, int modelVersion = 0,
            int secondaryModelIndex = 0, int secondaryModelVersion = 0,
            ulong routeSignature = 0, ulong pathSignature = 0, ulong modelSignature = 0)
        {
            VehicleIndex = vehicleIndex;
            VehicleVersion = vehicleVersion;
            TargetCheckpointId = targetCheckpointId;
            Mode = mode;
            DepotIndex = depotIndex;
            DepotVersion = depotVersion;
            ModelIndex = modelIndex;
            ModelVersion = modelVersion;
            SecondaryModelIndex = secondaryModelIndex;
            SecondaryModelVersion = secondaryModelVersion;
            RouteSignature = routeSignature;
            PathSignature = pathSignature;
            ModelSignature = modelSignature;
        }

        public int VehicleIndex { get; }
        public int VehicleVersion { get; }
        public long TargetCheckpointId { get; }
        public RailEtaMode Mode { get; }
        public int DepotIndex { get; }
        public int DepotVersion { get; }
        public int ModelIndex { get; }
        public int ModelVersion { get; }
        public int SecondaryModelIndex { get; }
        public int SecondaryModelVersion { get; }
        public ulong RouteSignature { get; }
        public ulong PathSignature { get; }
        public ulong ModelSignature { get; }
    }

    public enum RailEtaRequestState
    {
        Queued,
        IndexJobScheduled,
        IndexReady,
        ScopeReady,
        SnapshotReady,
        PredictorQueued,
        Predicting,
        Validating,
        Completed,
        Failed,
        Cancelled,
        Busy,
        WorkerLost
    }

    public sealed class RailEtaTicketStatus
    {
        public RailEtaTicket Ticket { get; internal set; }
        public RailEtaRequestState State { get; internal set; }
        public RailEtaFailure Failure { get; internal set; }
        public uint RequestFrame { get; internal set; }
        public bool RequestFrameBound { get; internal set; }
        public uint IndexScheduledFrame { get; internal set; }
        public uint IndexReadyFrame { get; internal set; }
        public uint ScopeReadyFrame { get; internal set; }
        public uint OriginFrame { get; internal set; }
        public RailEtaTheoryFailure TheoryFailure { get; internal set; }
        public uint SnapshotReadyFrame { get; internal set; }
        public uint PredictorQueuedFrame { get; internal set; }
        public uint PredictionStartedFrame { get; internal set; }
        public uint PredictionFinishedFrame { get; internal set; }
        public uint PublishedFrame { get; internal set; }
        public long BatchId { get; internal set; }
        public int ServiceGeneration { get; internal set; }
        public string Detail { get; internal set; } = string.Empty;
    }

    internal sealed class RailEtaRequestEnvelope
    {
        public RailEtaTicket Ticket;
        public RailEtaRequestDescriptor Descriptor;
        public int EnqueueGeneration;
        public RailEtaTheorySegmentRequest[] TheorySegments;
    }

    internal sealed class RailEtaRequestQueue
    {
        private readonly ConcurrentQueue<RailEtaRequestEnvelope> m_Queue = new ConcurrentQueue<RailEtaRequestEnvelope>();
        private int m_Count;

        public bool TryEnqueue(RailEtaRequestEnvelope request)
        {
            if (Interlocked.Increment(ref m_Count) > RailEtaLimits.MaxPendingTickets)
            {
                Interlocked.Decrement(ref m_Count);
                return false;
            }
            m_Queue.Enqueue(request);
            return true;
        }

        public bool TryDequeue(out RailEtaRequestEnvelope request)
        {
            if (!m_Queue.TryDequeue(out request)) return false;
            Interlocked.Decrement(ref m_Count);
            return true;
        }

        public bool TryPeek(out RailEtaRequestEnvelope request) => m_Queue.TryPeek(out request);

        public int Count => Volatile.Read(ref m_Count);
    }

    internal sealed class RailEtaTicketStore
    {
        private sealed class Entry
        {
            public readonly object Gate = new object();
            public RailEtaRequestDescriptor Descriptor;
            public RailEtaTicketStatus Status;
            public RailEtaWorldSnapshot Snapshot;
            public RailEtaRequest Request;
            public RailEtaPrediction Prediction;
            public RailEtaTheorySegmentResult[] TheorySegments;
            public RailEtaTheoryFailure TheoryFailure;
            public int RetentionQueued;
        }

        private readonly ConcurrentDictionary<long, Entry> m_Entries = new ConcurrentDictionary<long, Entry>();
        private readonly ConcurrentQueue<long> m_RetentionOrder = new ConcurrentQueue<long>();

        public void Add(RailEtaTicket ticket, RailEtaRequestDescriptor descriptor, int generation)
        {
            m_Entries[ticket.Value] = new Entry
            {
                Descriptor = descriptor,
                Status = new RailEtaTicketStatus { Ticket = ticket, State = RailEtaRequestState.Queued, ServiceGeneration = generation }
            };
        }

        public void BindRequestFrame(RailEtaTicket ticket, uint requestFrame, long batchId, int generation)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return;
            lock (entry.Gate)
            {
                if (entry.Status.ServiceGeneration != generation || entry.Status.RequestFrameBound || entry.Status.State == RailEtaRequestState.Cancelled) return;
                entry.Status.RequestFrame = requestFrame;
                entry.Status.RequestFrameBound = true;
                entry.Status.BatchId = batchId;
            }
        }

        public bool TryGetDescriptor(RailEtaTicket ticket, out RailEtaRequestDescriptor descriptor)
        {
            if (m_Entries.TryGetValue(ticket.Value, out Entry entry)) { descriptor = entry.Descriptor; return true; }
            descriptor = default;
            return false;
        }

        public bool TryGetStatus(RailEtaTicket ticket, out RailEtaTicketStatus status)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) { status = null; return false; }
            lock (entry.Gate) status = Copy(entry.Status);
            return true;
        }

        public bool TryGetSnapshot(RailEtaTicket ticket, out RailEtaWorldSnapshot snapshot)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) { snapshot = null; return false; }
            lock (entry.Gate) { snapshot = entry.Snapshot; return snapshot != null; }
        }

        public void StoreRequest(RailEtaTicket ticket, RailEtaRequest request, int generation)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return;
            lock (entry.Gate)
            {
                if (entry.Status.ServiceGeneration != generation || entry.Status.State == RailEtaRequestState.Cancelled) return;
                entry.Request = request;
            }
        }

        public bool TryGetRequest(RailEtaTicket ticket, out RailEtaRequest request)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) { request = null; return false; }
            lock (entry.Gate) { request = entry.Request; return request != null; }
        }

        public bool TryGetPrediction(RailEtaTicket ticket, out RailEtaPrediction prediction)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) { prediction = null; return false; }
            lock (entry.Gate) { prediction = entry.Prediction; return prediction != null; }
        }

        public bool TryGetTheorySegments(RailEtaTicket ticket, out RailEtaTheorySegmentResult[] segments)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) { segments = null; return false; }
            lock (entry.Gate) { segments = entry.TheorySegments; return segments != null; }
        }

        public bool TryGetTheoryFailure(RailEtaTicket ticket, out RailEtaTheoryFailure failure)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) { failure = null; return false; }
            lock (entry.Gate) { failure = entry.TheoryFailure; return failure != null; }
        }

        public void Transition(RailEtaTicket ticket, RailEtaRequestState state, uint stageFrame, long batchId, int generation, RailEtaFailure failure = RailEtaFailure.None, string detail = "")
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return;
            lock (entry.Gate)
            {
                if (entry.Status.State == RailEtaRequestState.Cancelled) return;
                entry.Status.State = state;
                entry.Status.ServiceGeneration = generation;
                entry.Status.Failure = failure;
                entry.Status.Detail = detail ?? string.Empty;
                ApplyStageFrame(entry.Status, state, stageFrame);
                if (IsTerminal(state)) Retain(ticket.Value, entry);
            }
        }

        public void PublishSnapshot(RailEtaTicket ticket, RailEtaWorldSnapshot snapshot, int generation, uint publishedFrame)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return;
            lock (entry.Gate)
            {
                if (entry.Status.ServiceGeneration != generation || entry.Status.State == RailEtaRequestState.Cancelled) return;
                entry.Snapshot = snapshot;
                entry.Status.OriginFrame = snapshot.OriginFrame;
                entry.Status.State = RailEtaRequestState.SnapshotReady;
                entry.Status.SnapshotReadyFrame = publishedFrame;
                Retain(ticket.Value, entry);
            }
        }

        public void MarkPredictionFinished(RailEtaTicket ticket, int generation, uint finishedFrame)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return;
            lock (entry.Gate)
            {
                if (entry.Status.ServiceGeneration != generation || entry.Status.State == RailEtaRequestState.Cancelled) return;
                entry.Status.PredictionFinishedFrame = finishedFrame;
            }
        }

        public void PublishPrediction(RailEtaTicket ticket, RailEtaPrediction prediction, int generation, uint publishedFrame)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return;
            lock (entry.Gate)
            {
                if (entry.Status.ServiceGeneration != generation || entry.Status.State == RailEtaRequestState.Cancelled) return;
                if (prediction != null && prediction.Failure == RailEtaFailure.None
                    && HasReachedOrPassed(prediction.PredictedArrivalFrame, publishedFrame))
                {
                    prediction.Failure = RailEtaFailure.ResultStale;
                    prediction.Confidence = RailEtaConfidence.Unknown;
                    prediction.Reason = "result-stale";
                }
                entry.Prediction = prediction;
                entry.Status.State = prediction != null && prediction.Failure == RailEtaFailure.None ? RailEtaRequestState.Completed : RailEtaRequestState.Failed;
                entry.Status.Failure = prediction?.Failure ?? RailEtaFailure.InvalidResult;
                entry.Status.Detail = prediction?.Reason ?? "prediction-missing";
                entry.Status.PublishedFrame = publishedFrame;
                Retain(ticket.Value, entry);
            }
        }

        public void SetTheoryFailure(RailEtaTicket ticket, RailEtaTheoryFailure failure, int generation)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return;
            lock (entry.Gate)
            {
                if (entry.Status.ServiceGeneration != generation || entry.Status.State == RailEtaRequestState.Cancelled) return;
                entry.TheoryFailure = failure;
                entry.Status.TheoryFailure = failure;
            }
        }

        public void PublishTheorySegments(RailEtaTicket ticket, RailEtaTheorySegmentResult[] segments,
            int generation, uint publishedFrame, RailEtaFailure failure, string detail,
            RailEtaTheoryFailure theoryFailure)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return;
            lock (entry.Gate)
            {
                if (entry.Status.ServiceGeneration != generation || entry.Status.State == RailEtaRequestState.Cancelled) return;
                entry.TheorySegments = segments ?? Array.Empty<RailEtaTheorySegmentResult>();
                entry.TheoryFailure = theoryFailure;
                entry.Status.TheoryFailure = theoryFailure;
                entry.Status.State = failure == RailEtaFailure.None
                    ? RailEtaRequestState.Completed
                    : RailEtaRequestState.Failed;
                entry.Status.Failure = failure;
                entry.Status.Detail = detail ?? string.Empty;
                entry.Status.PublishedFrame = publishedFrame;
                Retain(ticket.Value, entry);
            }
        }

        public void MarkWorkerLost(int generation, string detail)
        {
            foreach (KeyValuePair<long, Entry> pair in m_Entries)
            {
                Entry entry = pair.Value;
                lock (entry.Gate)
                {
                    if (entry.Status.ServiceGeneration != generation || IsTerminal(entry.Status.State)) continue;
                    entry.Status.State = RailEtaRequestState.WorkerLost;
                    entry.Status.Failure = RailEtaFailure.WorkerLost;
                    entry.Status.Detail = detail ?? "Rail ETA worker is lost.";
                    Retain(pair.Key, entry);
                }
            }
        }

        public bool Cancel(RailEtaTicket ticket)
        {
            if (!m_Entries.TryGetValue(ticket.Value, out Entry entry)) return false;
            lock (entry.Gate)
            {
                if (IsTerminal(entry.Status.State)) return false;
                entry.Status.State = RailEtaRequestState.Cancelled;
                entry.Status.Failure = RailEtaFailure.Cancelled;
                Retain(ticket.Value, entry);
                return true;
            }
        }

        public void CancelGeneration(int generation)
        {
            foreach (KeyValuePair<long, Entry> pair in m_Entries)
            {
                Entry entry = pair.Value;
                lock (entry.Gate)
                {
                    if (entry.Status.ServiceGeneration == generation && !IsTerminal(entry.Status.State))
                    {
                        entry.Status.State = RailEtaRequestState.Cancelled;
                        entry.Status.Failure = RailEtaFailure.Cancelled;
                    }
                }
                if (entry.Status.ServiceGeneration == generation) m_Entries.TryRemove(pair.Key, out _);
            }
        }

        private void Retain(long ticket, Entry entry)
        {
            if (Interlocked.Exchange(ref entry.RetentionQueued, 1) == 0) m_RetentionOrder.Enqueue(ticket);
            while (m_RetentionOrder.Count > RailEtaLimits.MaxRetainedTickets && m_RetentionOrder.TryDequeue(out long expired)) m_Entries.TryRemove(expired, out _);
        }

        private static bool IsTerminal(RailEtaRequestState state) => state == RailEtaRequestState.Completed || state == RailEtaRequestState.Failed || state == RailEtaRequestState.Cancelled || state == RailEtaRequestState.Busy || state == RailEtaRequestState.WorkerLost;

        private static bool HasReachedOrPassed(uint targetFrame, uint currentFrame) => unchecked(currentFrame - targetFrame) < 0x80000000u;

        private static RailEtaTicketStatus Copy(RailEtaTicketStatus source) => new RailEtaTicketStatus
        {
            Ticket = source.Ticket,
            State = source.State,
            Failure = source.Failure,
            RequestFrame = source.RequestFrame,
            RequestFrameBound = source.RequestFrameBound,
            IndexScheduledFrame = source.IndexScheduledFrame,
            IndexReadyFrame = source.IndexReadyFrame,
            ScopeReadyFrame = source.ScopeReadyFrame,
            OriginFrame = source.OriginFrame,
            TheoryFailure = source.TheoryFailure,
            SnapshotReadyFrame = source.SnapshotReadyFrame,
            PredictorQueuedFrame = source.PredictorQueuedFrame,
            PredictionStartedFrame = source.PredictionStartedFrame,
            PredictionFinishedFrame = source.PredictionFinishedFrame,
            PublishedFrame = source.PublishedFrame,
            BatchId = source.BatchId,
            ServiceGeneration = source.ServiceGeneration,
            Detail = source.Detail
        };

        private static void ApplyStageFrame(RailEtaTicketStatus status, RailEtaRequestState state, uint frame)
        {
            switch (state)
            {
                case RailEtaRequestState.IndexJobScheduled: status.IndexScheduledFrame = frame; break;
                case RailEtaRequestState.IndexReady: status.IndexReadyFrame = frame; break;
                case RailEtaRequestState.ScopeReady: status.ScopeReadyFrame = frame; break;
                case RailEtaRequestState.SnapshotReady: status.SnapshotReadyFrame = frame; break;
                case RailEtaRequestState.PredictorQueued: status.PredictorQueuedFrame = frame; break;
                case RailEtaRequestState.Predicting: status.PredictionStartedFrame = frame; break;
            }
        }
    }

    internal static class RailEtaEntityId
    {
        public static long Pack(Entity entity) => ((long)(uint)entity.Index << 32) | (uint)entity.Version;
        public static Entity ToEntity(RailEtaRequestDescriptor request) => new Entity { Index = request.VehicleIndex, Version = request.VehicleVersion };
        public static Entity ToEntity(long packed) => new Entity { Index = unchecked((int)(packed >> 32)), Version = unchecked((int)packed) };
    }
}
