#if RT_DEBUG_TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;
using Unity.Collections;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    public sealed class RailEtaComparisonPathToken
    {
        public long LaneId { get; set; }
        public int Direction { get; set; }
        public double StartFraction { get; set; }
        public double EndFraction { get; set; }
        public uint NavigationFlags { get; set; }
    }

    public sealed class RailEtaComparisonVehicleSample
    {
        public long VehicleId { get; set; }
        public bool EntityExists { get; set; }
        public bool HasTarget { get; set; }
        public bool HasPathOwner { get; set; }
        public long TargetEntityId { get; set; }
        public double SpeedMetresPerSecond { get; set; }
        public long FrontLaneId { get; set; }
        public double FrontPosition { get; set; }
        public uint FrontFlags { get; set; }
        public int PathElementIndex { get; set; }
        public uint PathState { get; set; }
        public ulong PathSignature { get; set; }
        public bool Boarding { get; set; }
        public uint DepartureFrame { get; set; }
        public long BlockerEntityId { get; set; }
        public string BlockerExternalKind { get; set; } = string.Empty;
        public string BlockerType { get; set; } = string.Empty;
        public byte BlockerMaximumSpeedCode { get; set; }
        public double BlockerMaximumSpeedMetresPerSecond { get; set; }
    }

    public sealed class RailEtaComparisonLaneSample
    {
        public long LaneId { get; set; }
        public uint TrackFlags { get; set; }
        public bool HasReservation { get; set; }
        public long ReservationBlockerEntityId { get; set; }
        public string ReservationExternalKind { get; set; } = string.Empty;
        public int PreviousPriority { get; set; }
        public int PreviousOffset { get; set; }
        public int NextPriority { get; set; }
        public int NextOffset { get; set; }
        public bool HasUpdateFrame { get; set; }
        public uint UpdateFrameIndex { get; set; }
        public long SignalPetitionerEntityId { get; set; }
        public long SignalBlockerEntityId { get; set; }
        public string SignalPetitionerExternalKind { get; set; } = string.Empty;
        public string SignalBlockerExternalKind { get; set; } = string.Empty;
        public string SignalType { get; set; } = string.Empty;
        public int SignalPriority { get; set; }
        public uint SignalFlags { get; set; }
    }

    public sealed class RailEtaComparisonOccupancySample
    {
        public long LaneId { get; set; }
        public long OccupantEntityId { get; set; }
        public string OccupantExternalKind { get; set; } = string.Empty;
        public double StartFraction { get; set; }
        public double EndFraction { get; set; }
    }

    public sealed class RailEtaComparisonSample
    {
        public uint Frame { get; set; }
        public int MissedIntervalsBeforeSample { get; set; }
        public int PathRevisionId { get; set; }
        public RailEtaComparisonVehicleSample[] Vehicles { get; set; } = Array.Empty<RailEtaComparisonVehicleSample>();
        public RailEtaComparisonLaneSample[] Lanes { get; set; } = Array.Empty<RailEtaComparisonLaneSample>();
        public RailEtaComparisonOccupancySample[] Occupancies { get; set; } = Array.Empty<RailEtaComparisonOccupancySample>();
    }

    public sealed class RailEtaComparisonActualEvent
    {
        public int Sequence { get; set; }
        public string Kind { get; set; } = string.Empty;
        public uint Frame { get; set; }
        public long OtherEntityId { get; set; }
        public long ResourceId { get; set; }
        public long LaneId { get; set; }
        public string Evidence { get; set; } = string.Empty;
    }

    public sealed class RailEtaComparisonRevision
    {
        public int RevisionId { get; set; }
        public uint Frame { get; set; }
        public string Kind { get; set; } = string.Empty;
        public long PreviousTargetEntityId { get; set; }
        public long CurrentTargetEntityId { get; set; }
        public bool HasTarget { get; set; }
        public bool HasPathOwner { get; set; }
        public uint PathState { get; set; }
        public ulong PathSignature { get; set; }
        public RailEtaComparisonPathToken[] Tokens { get; set; } = Array.Empty<RailEtaComparisonPathToken>();
    }

    public sealed class RailEtaComparisonSummary
    {
        public string State { get; set; } = string.Empty;
        public bool ComparisonValid { get; set; }
        public bool AccuracyContaminated { get; set; }
        public string InvalidReason { get; set; } = string.Empty;
        public uint ActualStopCompleteFrame { get; set; }
        public long ActualStopMinusPredictionFinishedFrames { get; set; }
        public long ActualStopMinusPublishedFrames { get; set; }
        public long ActualStopMinusOriginFrames { get; set; }
        public long ActualStopMinusPredictedArrivalFrames { get; set; }
        public int MissedSampleIntervals { get; set; }
    }

    public sealed class RailEtaComparisonExport
    {
        public long SessionIdentity { get; set; }
        public long ExportIdentity { get; set; }
        public long Ticket { get; set; }
        public long VehicleId { get; set; }
        public uint ExportFrame { get; set; }
        public uint RequestFrame { get; set; }
        public uint OriginFrame { get; set; }
        public uint PredictionFinishedFrame { get; set; }
        public uint PublishedFrame { get; set; }
        public uint PredictedArrivalFrame { get; set; }
        public RailEtaWorldSnapshot Snapshot { get; set; }
        public RailEtaRequest Request { get; set; }
        public RailEtaPrediction Prediction { get; set; }
        public RailEtaComparisonPathToken[] InitialPathTokens { get; set; } = Array.Empty<RailEtaComparisonPathToken>();
        public RailEtaComparisonSample[] Samples { get; set; } = Array.Empty<RailEtaComparisonSample>();
        public RailEtaComparisonActualEvent[] ActualEvents { get; set; } = Array.Empty<RailEtaComparisonActualEvent>();
        public RailEtaComparisonRevision[] Revisions { get; set; } = Array.Empty<RailEtaComparisonRevision>();
        public RailEtaComparisonSummary Summary { get; set; }
    }

    internal sealed class RailEtaReplayPackage
    {
        public string Format { get; set; } = "rail-eta-replay-v1";
        public string HotAssemblyVersion { get; set; } = string.Empty;
        public string PredictorBuildId { get; set; } = string.Empty;
        public RailEtaFrozenWorld FrozenWorld { get; set; }
        public RailEtaWorldSnapshot Snapshot { get; set; }
        public RailEtaRequest Request { get; set; }
        public RailEtaPrediction Prediction { get; set; }
    }

    public sealed class RailEtaComparisonStatus
    {
        public long Ticket { get; internal set; }
        public long VehicleId { get; internal set; }
        public uint OriginFrame { get; internal set; }
        public double EtaGameMinutes { get; internal set; }
        public string State { get; internal set; } = string.Empty;
        public bool ComparisonValid { get; internal set; }
        public string InvalidReason { get; internal set; } = string.Empty;
        public uint PredictedArrivalFrame { get; internal set; }
        public uint ActualArrivalFrame { get; internal set; }
        public long ActualStopMinusPredictionFinishedFrames { get; internal set; }
        public long ActualStopMinusPublishedFrames { get; internal set; }
        public long ActualStopMinusOriginFrames { get; internal set; }
        public long ActualStopMinusPredictedArrivalFrames { get; internal set; }
        public long FramesToOrPastPrediction { get; internal set; }
    }

    internal sealed class RailEtaComparisonSession
    {
        private const uint EndOfPath = 1u;
        private const uint EndReached = 2u;
        private const double FractionTolerance = 0.0001d;
        private readonly List<RailEtaComparisonSample> m_Samples = new List<RailEtaComparisonSample>();
        private readonly List<RailEtaComparisonActualEvent> m_Events = new List<RailEtaComparisonActualEvent>();
        private readonly List<RailEtaComparisonRevision> m_Revisions = new List<RailEtaComparisonRevision>();
        private readonly Dictionary<long, long[]> m_ResourceLanes = new Dictionary<long, long[]>();
        private readonly RailEtaComparisonPathToken[] m_InitialTokens;
        private RailEtaComparisonPathToken[] m_ActiveTokens;
        private RailEtaComparisonSample m_PreviousSample;
        private int m_ActiveProgress;
        private int m_RevisionId;
        private int m_MissedIntervals;
        private bool m_AccuracyContaminated;
        private string m_State = "Observing";
        private string m_InvalidReason = string.Empty;
        private uint m_ActualStopFrame;
        private uint m_LastFrame;

        public RailEtaComparisonSession(long identity, RailEtaTicket ticket, RailEtaTicketStatus status, RailEtaWorldSnapshot snapshot,
            RailEtaRequest request, RailEtaPrediction prediction, RailVehicleSnapshot target, RailEtaFrozenWorld frozenWorld)
        {
            Identity = identity;
            Ticket = ticket;
            Status = status;
            Snapshot = snapshot;
            Request = request;
            Prediction = prediction;
            FrozenWorld = frozenWorld;
            VehicleId = target.VehicleId.Value;
            OriginalTargetId = Pack(target.Target);
            m_InitialTokens = BuildTokens(target.RemainingPath);
            m_ActiveTokens = m_InitialTokens;
            RailResourceSnapshot[] resources = snapshot.Resources ?? Array.Empty<RailResourceSnapshot>();
            for (int i = 0; i < resources.Length; i++)
            {
                RailResourceSnapshot resource = resources[i];
                if (resource == null) continue;
                RailLaneId[] lanes = resource.LaneIds ?? Array.Empty<RailLaneId>();
                long[] ids = new long[lanes.Length];
                for (int j = 0; j < lanes.Length; j++) ids[j] = lanes[j].Value;
                m_ResourceLanes[resource.ResourceId.Value] = ids;
            }
        }

        public long Identity { get; }
        public RailEtaTicket Ticket { get; }
        public RailEtaTicketStatus Status { get; }
        public RailEtaWorldSnapshot Snapshot { get; }
        public RailEtaRequest Request { get; }
        public RailEtaPrediction Prediction { get; }
        public RailEtaFrozenWorld FrozenWorld { get; }
        public long VehicleId { get; }
        public long OriginalTargetId { get; }
        public bool IsTerminal => m_State == "Completed" || m_State == "VehicleGone" || m_State == "Stopped";
        public bool ComparisonValid => String.IsNullOrEmpty(m_InvalidReason);
        public string State => m_State;
        public string InvalidReason => m_InvalidReason;
        public uint LastFrame => m_LastFrame;

        public void AddMissedInterval() => m_MissedIntervals++;

        public void AddSample(RailEtaComparisonSample sample, NativeArray<RailEtaComparisonPathRow> pathRows)
        {
            if (sample == null || IsTerminal) return;
            if (m_Samples.Count != 0 && m_Samples[m_Samples.Count - 1].Frame == sample.Frame) return;
            m_LastFrame = sample.Frame;
            RailEtaComparisonVehicleSample selected = FindVehicle(sample, VehicleId);
            if (selected == null || !selected.EntityExists)
            {
                AddEvent("VehicleGone", sample.Frame, 0, 0, 0, "entity storage missing");
                m_State = "VehicleGone";
                sample.PathRevisionId = m_RevisionId;
                m_Samples.Add(sample);
                return;
            }

            CheckTargetAndPath(sample.Frame, selected, pathRows);
            sample.PathRevisionId = m_RevisionId;
            m_Samples.Add(sample);
            CheckEvents(sample, selected);
            if (!selected.HasTarget)
            {
                Stop(sample.Frame, "TargetMissing");
                m_PreviousSample = sample;
                return;
            }
            if ((selected.FrontFlags & (EndOfPath | EndReached)) == (EndOfPath | EndReached) && selected.SpeedMetresPerSecond < 0.1d)
            {
                m_ActualStopFrame = sample.Frame;
                AddEvent("StopComplete", sample.Frame, 0, 0, selected.FrontLaneId, "EndOfPath|EndReached and speed<0.1");
                m_State = "Completed";
            }
            else if (!ComparisonValid)
            {
                m_State = "ObservingInvalid";
            }
            m_PreviousSample = sample;
        }

        public void Stop(uint frame, string reason)
        {
            if (IsTerminal) return;
            m_LastFrame = frame;
            AddEvent("Stopped", frame, 0, 0, 0, reason ?? "manual");
            m_State = "Stopped";
        }

        public RailEtaComparisonStatus BuildStatus(uint currentFrame)
        {
            RailEtaComparisonSummary summary = BuildSummary();
            return new RailEtaComparisonStatus
            {
                Ticket = Ticket.Value,
                VehicleId = VehicleId,
                OriginFrame = Snapshot.OriginFrame,
                EtaGameMinutes = FrozenWorld?.RuntimeFacts != null && FrozenWorld.RuntimeFacts.FramesPerMinute > 0d
                    ? (float)(ForwardDelta(Snapshot.OriginFrame, Prediction.PredictedArrivalFrame)
                        / FrozenWorld.RuntimeFacts.FramesPerMinute)
                    : 0f,
                State = m_State,
                ComparisonValid = ComparisonValid,
                InvalidReason = m_InvalidReason,
                PredictedArrivalFrame = Prediction.PredictedArrivalFrame,
                ActualArrivalFrame = m_ActualStopFrame,
                ActualStopMinusPredictionFinishedFrames = summary.ActualStopMinusPredictionFinishedFrames,
                ActualStopMinusPublishedFrames = summary.ActualStopMinusPublishedFrames,
                ActualStopMinusOriginFrames = summary.ActualStopMinusOriginFrames,
                ActualStopMinusPredictedArrivalFrames = summary.ActualStopMinusPredictedArrivalFrames,
                FramesToOrPastPrediction = m_ActualStopFrame == 0 ? SignedFrameDelta(currentFrame, Prediction.PredictedArrivalFrame) : 0
            };
        }

        public RailEtaComparisonExport FreezeExport(long exportIdentity, uint exportFrame)
        {
            return new RailEtaComparisonExport
            {
                SessionIdentity = Identity,
                ExportIdentity = exportIdentity,
                Ticket = Ticket.Value,
                VehicleId = VehicleId,
                ExportFrame = exportFrame,
                RequestFrame = Status.RequestFrame,
                OriginFrame = Status.OriginFrame,
                PredictionFinishedFrame = Status.PredictionFinishedFrame,
                PublishedFrame = Status.PublishedFrame,
                PredictedArrivalFrame = Prediction.PredictedArrivalFrame,
                Snapshot = Snapshot,
                Request = Request,
                Prediction = Prediction,
                InitialPathTokens = CloneTokens(m_InitialTokens),
                Samples = m_Samples.ToArray(),
                ActualEvents = CloneEvents(m_Events),
                Revisions = CloneRevisions(m_Revisions),
                Summary = BuildSummary()
            };
        }

        public RailEtaReplayPackage FreezeReplay()
        {
            return new RailEtaReplayPackage
            {
                HotAssemblyVersion = typeof(RailEtaComparisonSession).Assembly.GetName().Version?.ToString() ?? string.Empty,
                PredictorBuildId = Prediction?.PredictorBuildId ?? string.Empty,
                FrozenWorld = FrozenWorld,
                Snapshot = Snapshot,
                Request = Request,
                Prediction = Prediction
            };
        }

        public static Task<string> ExportAsync(RailEtaComparisonExport value, RailEtaReplayPackage replay, string filePath = null)
        {
            return Task.Run(() =>
            {
                string path = String.IsNullOrWhiteSpace(filePath) ? DefaultExportPath(value) : filePath;
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    var serializer = new DataContractJsonSerializer(typeof(RailEtaComparisonExport), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 10000000 });
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) serializer.WriteObject(stream, value);
                    if (replay?.FrozenWorld != null)
                    {
                        string replayPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + "-replay.json");
                        RailEtaReplayJson.Write(replayPath, replay);
                    }
                    Mod.log.Info("[RailEtaComparisonExport] completed ticket=" + value.Ticket + " path=" + path);
                    return path;
                }
                catch (Exception ex)
                {
                    Mod.log.Info("[RailEtaComparisonExport] failed ticket=" + value.Ticket + " error=" + ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            });
        }

        private void CheckTargetAndPath(uint frame, RailEtaComparisonVehicleSample selected, NativeArray<RailEtaComparisonPathRow> rows)
        {
            RailEtaComparisonVehicleSample previous = FindVehicle(m_PreviousSample, VehicleId);
            long previousTarget = previous?.TargetEntityId ?? OriginalTargetId;
            bool availabilityChanged = previous != null && (previous.HasTarget != selected.HasTarget || previous.HasPathOwner != selected.HasPathOwner);
            bool targetChanged = selected.HasTarget && selected.TargetEntityId != previousTarget;
            bool pathStateChanged = previous != null && previous.PathState != selected.PathState;
            if (!selected.HasTarget || !selected.HasPathOwner)
            {
                if (m_PreviousSample == null || availabilityChanged || targetChanged || pathStateChanged)
                    AddRevision(frame, !selected.HasTarget ? "TargetMissing" : "PathOwnerMissing", previousTarget, selected, rows);
                Invalidate(!selected.HasTarget ? "TargetMissing" : "PathOwnerMissing");
                return;
            }
            if (targetChanged)
            {
                AddRevision(frame, "TargetChanged", previousTarget, selected, rows);
            }
            bool aligned = TryAlign(m_ActiveTokens, rows, ref m_ActiveProgress);
            if (availabilityChanged && aligned)
                AddRevision(frame, !previous.HasTarget ? "TargetRestored" : "PathOwnerRestored", previousTarget, selected, rows);
            else if (pathStateChanged && aligned)
                AddRevision(frame, "PathStateChanged", previousTarget, selected, rows);
            if (!aligned)
            {
                bool tokenChanged = m_PreviousSample == null || !TokensEqualToRevision(rows, m_Revisions.Count == 0 ? m_InitialTokens : m_Revisions[m_Revisions.Count - 1].Tokens);
                if (tokenChanged || targetChanged || availabilityChanged || pathStateChanged)
                {
                    AddRevision(frame, "PathChanged", previousTarget, selected, rows);
                    m_ActiveTokens = m_Revisions[m_Revisions.Count - 1].Tokens;
                    m_ActiveProgress = 0;
                }
                Invalidate("PathChanged");
            }
        }

        private void CheckEvents(RailEtaComparisonSample sample, RailEtaComparisonVehicleSample selected)
        {
            RailEtaComparisonVehicleSample previous = FindVehicle(m_PreviousSample, VehicleId);
            bool blocked = selected.BlockerEntityId != 0 && String.IsNullOrEmpty(selected.BlockerExternalKind);
            bool wasBlocked = previous != null && previous.BlockerEntityId != 0 && String.IsNullOrEmpty(previous.BlockerExternalKind);
            if (blocked && !wasBlocked) AddEvent("BlockerStarted", sample.Frame, selected.BlockerEntityId, 0, selected.FrontLaneId, BlockerEvidence(selected));
            else if (blocked && wasBlocked && (selected.BlockerEntityId != previous.BlockerEntityId || selected.BlockerType != previous.BlockerType || selected.BlockerExternalKind != previous.BlockerExternalKind))
            {
                AddEvent("BlockerCleared", sample.Frame, previous.BlockerEntityId, 0, previous.FrontLaneId, BlockerEvidence(previous));
                AddEvent("BlockerChanged", sample.Frame, selected.BlockerEntityId, 0, selected.FrontLaneId, BlockerEvidence(selected));
            }
            else if (!blocked && wasBlocked) AddEvent("BlockerCleared", sample.Frame, previous.BlockerEntityId, 0, previous.FrontLaneId, BlockerEvidence(previous));

            DirectReservationEvidence reservation = FindDirectReservation(sample, selected);
            DirectReservationEvidence previousReservation = previous == null ? default : FindDirectReservation(m_PreviousSample, previous);
            if (reservation.Active && !previousReservation.Active) AddEvent("ReservationBlocked", sample.Frame, reservation.OtherEntity, reservation.Resource, reservation.Lane, reservation.Evidence);
            else if (!reservation.Active && previousReservation.Active) AddEvent("ReservationCleared", sample.Frame, previousReservation.OtherEntity, previousReservation.Resource, previousReservation.Lane, previousReservation.Evidence);
            else if (reservation.Active && previousReservation.Active && (reservation.OtherEntity != previousReservation.OtherEntity || reservation.Resource != previousReservation.Resource))
            {
                AddEvent("ReservationCleared", sample.Frame, previousReservation.OtherEntity, previousReservation.Resource, previousReservation.Lane, previousReservation.Evidence);
                AddEvent("ReservationBlocked", sample.Frame, reservation.OtherEntity, reservation.Resource, reservation.Lane, reservation.Evidence);
            }

            bool targetReached = (selected.FrontFlags & EndOfPath) != 0;
            bool wasTargetReached = previous != null && (previous.FrontFlags & EndOfPath) != 0;
            if (targetReached && !wasTargetReached) AddEvent("TargetReached", sample.Frame, 0, 0, selected.FrontLaneId, "EndOfPath flag");

            bool dispatchHold = selected.Boarding && ForwardDelta(sample.Frame, selected.DepartureFrame) >= 6000u;
            bool previousHold = previous != null && previous.Boarding && ForwardDelta(m_PreviousSample.Frame, previous.DepartureFrame) >= 6000u;
            if (dispatchHold && !previousHold)
            {
                AddEvent("DispatchHold", sample.Frame, 0, 0, selected.FrontLaneId, "departureFrame=" + selected.DepartureFrame);
            }

            string excluded = FindExcludedEvidence(sample, selected);
            string previousExcluded = previous == null ? string.Empty : FindExcludedEvidence(m_PreviousSample, previous);
            if (!String.IsNullOrEmpty(previousExcluded) && excluded != previousExcluded)
                AddEvent("ExcludedInterferenceCleared", sample.Frame, previous.BlockerEntityId, 0, previous.FrontLaneId, previousExcluded);
            if (!String.IsNullOrEmpty(excluded) && excluded != previousExcluded)
            {
                AddEvent("ExcludedInterferenceStarted", sample.Frame, selected.BlockerEntityId, 0, selected.FrontLaneId, excluded);
                m_AccuracyContaminated = true;
            }
        }

        private RailEtaComparisonSummary BuildSummary()
        {
            return new RailEtaComparisonSummary
            {
                State = m_State,
                ComparisonValid = ComparisonValid,
                AccuracyContaminated = m_AccuracyContaminated,
                InvalidReason = m_InvalidReason,
                ActualStopCompleteFrame = m_ActualStopFrame,
                ActualStopMinusPredictionFinishedFrames = m_ActualStopFrame == 0 ? 0 : SignedFrameDelta(Status.PredictionFinishedFrame, m_ActualStopFrame),
                ActualStopMinusPublishedFrames = m_ActualStopFrame == 0 ? 0 : SignedFrameDelta(Status.PublishedFrame, m_ActualStopFrame),
                ActualStopMinusOriginFrames = m_ActualStopFrame == 0 ? 0 : SignedFrameDelta(Status.OriginFrame, m_ActualStopFrame),
                ActualStopMinusPredictedArrivalFrames = m_ActualStopFrame == 0 ? 0 : SignedFrameDelta(Prediction.PredictedArrivalFrame, m_ActualStopFrame),
                MissedSampleIntervals = m_MissedIntervals
            };
        }

        private DirectReservationEvidence FindDirectReservation(RailEtaComparisonSample sample, RailEtaComparisonVehicleSample selected)
        {
            RailEtaComparisonLaneSample[] lanes = sample?.Lanes ?? Array.Empty<RailEtaComparisonLaneSample>();
            for (int i = 0; i < lanes.Length; i++)
            {
                RailEtaComparisonLaneSample lane = lanes[i];
                if (lane == null) continue;
                long resource = ResolveResource(lane.LaneId);
                if (lane.HasReservation && lane.ReservationBlockerEntityId != 0 && selected.BlockerEntityId == lane.ReservationBlockerEntityId)
                    return new DirectReservationEvidence(true, lane.ReservationBlockerEntityId, resource, lane.LaneId, "target Blocker equals LaneReservation blocker");
                if (lane.SignalPetitionerEntityId == selected.VehicleId && lane.SignalBlockerEntityId != 0)
                    return new DirectReservationEvidence(true, lane.SignalBlockerEntityId, resource, lane.LaneId, "LaneSignal petitioner/blocker direct evidence");
            }
            return default;
        }

        private void AddRevision(uint frame, string kind, long previousTarget, RailEtaComparisonVehicleSample selected, NativeArray<RailEtaComparisonPathRow> rows)
        {
            RailEtaComparisonPathToken[] tokens = BuildTokens(rows);
            m_RevisionId++;
            m_Revisions.Add(new RailEtaComparisonRevision
            {
                RevisionId = m_RevisionId,
                Frame = frame,
                Kind = kind,
                PreviousTargetEntityId = previousTarget,
                CurrentTargetEntityId = selected.TargetEntityId,
                HasTarget = selected.HasTarget,
                HasPathOwner = selected.HasPathOwner,
                PathState = selected.PathState,
                PathSignature = selected.PathSignature,
                Tokens = tokens
            });
            AddEvent(kind, frame, 0, 0, selected.FrontLaneId, "path revision=" + m_RevisionId);
        }

        private void Invalidate(string reason) { if (String.IsNullOrEmpty(m_InvalidReason)) m_InvalidReason = reason ?? "Invalidated"; }

        private void AddEvent(string kind, uint frame, long other, long resource, long lane, string evidence)
        {
            m_Events.Add(new RailEtaComparisonActualEvent { Sequence = m_Events.Count, Kind = kind, Frame = frame, OtherEntityId = other, ResourceId = resource, LaneId = lane, Evidence = evidence ?? string.Empty });
        }

        private string FindExcludedEvidence(RailEtaComparisonSample sample, RailEtaComparisonVehicleSample selected)
        {
            if (selected.BlockerExternalKind == "RoadVehicle" || selected.BlockerExternalKind == "Creature") return "vehicle blocker=" + selected.BlockerExternalKind;
            RailEtaComparisonLaneSample[] lanes = sample?.Lanes ?? Array.Empty<RailEtaComparisonLaneSample>();
            for (int i = 0; i < lanes.Length; i++)
            {
                RailEtaComparisonLaneSample lane = lanes[i];
                if (lane == null || lane.SignalPetitionerEntityId != selected.VehicleId) continue;
                if (lane.SignalBlockerExternalKind == "RoadVehicle" || lane.SignalBlockerExternalKind == "Creature") return "signal blocker=" + lane.SignalBlockerExternalKind + " lane=" + lane.LaneId;
            }
            return string.Empty;
        }

        private long ResolveResource(long lane)
        {
            foreach (KeyValuePair<long, long[]> pair in m_ResourceLanes) for (int i = 0; i < pair.Value.Length; i++) if (pair.Value[i] == lane) return pair.Key;
            return lane;
        }

        private static bool TryAlign(RailEtaComparisonPathToken[] frozen, NativeArray<RailEtaComparisonPathRow> current, ref int progress)
        {
            if ((frozen == null || frozen.Length == 0) && current.Length == 0) return true;
            if (frozen == null || frozen.Length == 0 || current.Length == 0) return false;
            for (int start = Math.Max(0, progress); start < frozen.Length; start++)
            {
                if (!CurrentTokenMatches(frozen[start], current[0], true)) continue;
                if (current.Length > frozen.Length - start) continue;
                bool matches = true;
                for (int i = 1; i < current.Length; i++) if (!CurrentTokenMatches(frozen[start + i], current[i], false)) { matches = false; break; }
                if (!matches) continue;
                progress = start;
                return true;
            }
            return false;
        }

        private static bool CurrentTokenMatches(RailEtaComparisonPathToken frozen, RailEtaComparisonPathRow current, bool currentSegment)
        {
            if (frozen == null || frozen.LaneId != Pack(current.Lane) || frozen.Direction != current.Direction || Math.Abs(frozen.EndFraction - current.End) > FractionTolerance) return false;
            if (!currentSegment) return Math.Abs(frozen.StartFraction - current.Start) <= FractionTolerance;
            if (frozen.Direction >= 0) return current.Start + FractionTolerance >= frozen.StartFraction && current.Start <= frozen.EndFraction + FractionTolerance;
            return current.Start - FractionTolerance <= frozen.StartFraction && current.Start >= frozen.EndFraction - FractionTolerance;
        }

        private static bool TokensEqualToRevision(NativeArray<RailEtaComparisonPathRow> current, RailEtaComparisonPathToken[] tokens)
        {
            if (tokens == null || current.Length != tokens.Length) return false;
            for (int i = 0; i < current.Length; i++) if (!CurrentTokenMatches(tokens[i], current[i], i == 0)) return false;
            return true;
        }

        private static RailEtaComparisonPathToken[] BuildTokens(RailPathSegment[] path)
        {
            path = path ?? Array.Empty<RailPathSegment>();
            var result = new RailEtaComparisonPathToken[path.Length];
            for (int i = 0; i < path.Length; i++)
            {
                RailPathSegment value = path[i];
                result[i] = new RailEtaComparisonPathToken { LaneId = value?.LaneId.Value ?? 0, Direction = Direction(value?.StartFraction ?? 0, value?.EndFraction ?? 0), StartFraction = value?.StartFraction ?? 0, EndFraction = value?.EndFraction ?? 0, NavigationFlags = value?.NavigationFlags ?? 0 };
            }
            return result;
        }

        private static RailEtaComparisonPathToken[] BuildTokens(NativeArray<RailEtaComparisonPathRow> rows)
        {
            var result = new RailEtaComparisonPathToken[rows.Length];
            for (int i = 0; i < rows.Length; i++) result[i] = new RailEtaComparisonPathToken { LaneId = Pack(rows[i].Lane), Direction = rows[i].Direction, StartFraction = rows[i].Start, EndFraction = rows[i].End, NavigationFlags = rows[i].NavigationFlags };
            return result;
        }

        private static RailEtaComparisonVehicleSample FindVehicle(RailEtaComparisonSample sample, long id)
        {
            RailEtaComparisonVehicleSample[] values = sample?.Vehicles ?? Array.Empty<RailEtaComparisonVehicleSample>();
            for (int i = 0; i < values.Length; i++) if (values[i] != null && values[i].VehicleId == id) return values[i];
            return null;
        }

        private static uint ForwardDelta(uint origin, uint target) { uint value = unchecked(target - origin); return value < 0x80000000u ? value : 0; }
        private static long SignedFrameDelta(uint origin, uint frame) => unchecked((int)(frame - origin));
        private static int Direction(double start, double end) => end >= start ? 1 : -1;
        private static long Pack(RailEntityIdentity identity) => identity == null ? 0 : ((long)(uint)identity.Index << 32) | (uint)identity.Version;
        private static long Pack(Unity.Entities.Entity entity) => entity == Unity.Entities.Entity.Null ? 0 : ((long)(uint)entity.Index << 32) | (uint)entity.Version;
        private static int UnpackIndex(long value) => unchecked((int)(uint)((ulong)value >> 32));
        private static string BlockerEvidence(RailEtaComparisonVehicleSample value) => "type=" + value.BlockerType + " external=" + value.BlockerExternalKind + " maxSpeedCode=" + value.BlockerMaximumSpeedCode;

        private static RailEtaComparisonPathToken[] CloneTokens(RailEtaComparisonPathToken[] values)
        {
            values = values ?? Array.Empty<RailEtaComparisonPathToken>();
            var result = new RailEtaComparisonPathToken[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = values[i] == null ? null : new RailEtaComparisonPathToken { LaneId = values[i].LaneId, Direction = values[i].Direction, StartFraction = values[i].StartFraction, EndFraction = values[i].EndFraction, NavigationFlags = values[i].NavigationFlags };
            return result;
        }

        private static RailEtaComparisonActualEvent[] CloneEvents(List<RailEtaComparisonActualEvent> values)
        {
            var result = new RailEtaComparisonActualEvent[values.Count];
            for (int i = 0; i < values.Count; i++) result[i] = new RailEtaComparisonActualEvent { Sequence = values[i].Sequence, Kind = values[i].Kind, Frame = values[i].Frame, OtherEntityId = values[i].OtherEntityId, ResourceId = values[i].ResourceId, LaneId = values[i].LaneId, Evidence = values[i].Evidence };
            return result;
        }

        private static RailEtaComparisonRevision[] CloneRevisions(List<RailEtaComparisonRevision> values)
        {
            var result = new RailEtaComparisonRevision[values.Count];
            for (int i = 0; i < values.Count; i++) result[i] = new RailEtaComparisonRevision { RevisionId = values[i].RevisionId, Frame = values[i].Frame, Kind = values[i].Kind, PreviousTargetEntityId = values[i].PreviousTargetEntityId, CurrentTargetEntityId = values[i].CurrentTargetEntityId, HasTarget = values[i].HasTarget, HasPathOwner = values[i].HasPathOwner, PathState = values[i].PathState, PathSignature = values[i].PathSignature, Tokens = CloneTokens(values[i].Tokens) };
            return result;
        }

        private static string DefaultExportPath(RailEtaComparisonExport value)
        {
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Colossal Order", "Cities Skylines II", "Logs");
            return Path.Combine(logs, "RailEtaComparison-" + value.Ticket + "-" + UnpackIndex(value.VehicleId) + "-" + value.OriginFrame + "-" + value.ExportFrame + "-" + value.SessionIdentity + "-" + value.ExportIdentity + "-" + DateTime.UtcNow.Ticks + "-" + Guid.NewGuid().ToString("N") + "-final.json");
        }

        private readonly struct DirectReservationEvidence
        {
            public DirectReservationEvidence(bool active, long other, long resource, long lane, string evidence) { Active = active; OtherEntity = other; Resource = resource; Lane = lane; Evidence = evidence; }
            public bool Active { get; }
            public long OtherEntity { get; }
            public long Resource { get; }
            public long Lane { get; }
            public string Evidence { get; }
        }
    }
}
#endif
