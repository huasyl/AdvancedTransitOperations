using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RapidTransitMod.RailEta.Contracts;
using Game.Pathfind;
using Unity.Collections;
using Unity.Entities;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal sealed class RailEtaFrameIndex
    {
        public readonly Dictionary<Entity, RailEtaVehicleIndexRow> Vehicles = new Dictionary<Entity, RailEtaVehicleIndexRow>();
        public readonly HashSet<Entity> ControllersWithPath = new HashSet<Entity>();
        public bool Overflow;

        public static RailEtaFrameIndex From(RailEtaScopedStaging staging)
        {
            RailEtaFrameIndex result = new RailEtaFrameIndex { Overflow = staging.Overflow.Value != 0 };
            NativeArray<RailEtaScopedVehicleRow> vehicles = staging.Vehicles.AsArray();
            for (int i = 0; i < vehicles.Length; i++)
            {
                RailEtaScopedVehicleRow row = vehicles[i];
                result.Vehicles[row.Controller] = new RailEtaVehicleIndexRow
                {
                    ControllerOrdinal = row.ControllerOrdinal, Controller = row.Controller, Target = row.Target, Blocker = row.Blocker, Route = row.Route,
                    TargetSegmentIndex = row.TargetSegmentIndex, IsPassenger = row.IsPassenger, IsCargo = row.IsCargo,
                    PathfindMaximumSpeed = row.PathfindMaximumSpeed, TrackTypes = row.TrackTypes, PathfindFlags = row.PathfindFlags,
                    Speed = row.Speed, PathElementIndex = row.PathElementIndex, PathState = row.PathState,
                    PathDestination = row.PathDestination, HasPathInformation = row.HasPathInformation,
                    DepartureFrame = row.DepartureFrame, Boarding = row.Boarding, PathSignature = row.PathSignature,
                    ResourceSignature = row.ResourceSignature, FrontLane = row.FrontLane, RearLane = row.RearLane,
                    FrontCacheLane = row.FrontCacheLane, RearCacheLane = row.RearCacheLane
                };
            }
            NativeArray<RailEtaScopedLaneRow> lanes = staging.Lanes.AsArray();
            for (int i = 0; i < lanes.Length; i++)
            {
                RailEtaScopedLaneRow row = lanes[i];
                if (row.Controller == Entity.Null) continue;
                if ((row.Source <= 2 || row.Source == 7) && row.Lane != Entity.Null) result.ControllersWithPath.Add(row.Controller);
            }
            return result;
        }

        public static ulong ResourceKey(Entity a, Entity b)
        {
            ulong x = (ulong)RailEtaEntityId.Pack(a);
            ulong y = (ulong)RailEtaEntityId.Pack(b);
            if (x > y) { ulong swap = x; x = y; y = swap; }
            return (x * 11400714819323198485UL) ^ y;
        }

    }

    internal sealed class RailEtaBatchRequest
    {
        public RailEtaTicket Ticket;
        public RailEtaRequestDescriptor Descriptor;
        public Entity ExpectedTarget;
    }

    internal sealed class RailEtaScopeWork
    {
        public RailEtaMode Mode;
        public long BatchId;
        public uint IndexOriginFrame;
        public int Generation;
        public List<RailEtaBatchRequest> Requests;
        public RailEtaScopedStaging Staging;
        public HashSet<Entity> ExcludedVehicles;
        public List<RailEtaVehiclePathFailure> VehiclePathFailures;
        public RailEtaRequestFrameFacts RequestFrameFacts;
        public List<RailEtaTicketFailure> TicketFailures;
        public HashSet<RailEtaFailedSegmentKey> FailedSegments;
    }

    internal sealed class RailEtaMissingRouteSegment
    {
        public Entity Line;
        public int SegmentIndex;
        public Entity FromWaypoint;
        public Entity ToWaypoint;
        public ulong ChainSignature;
        public Entity Controller;
        public Entity Target;
        public byte IsVehicleTarget;
        public byte NeedsGeometry;
        public readonly List<Entity> Consumers = new List<Entity>();
    }

    internal sealed class RailEtaScopeResult : IDisposable
    {
        public RailEtaMode Mode;
        public long BatchId;
        public uint IndexOriginFrame;
        public int Generation;
        public List<RailEtaBatchRequest> Requests;
        public List<RailEtaTicketFailure> TicketFailures;
        public RailEtaFrameIndex Index;
        public RailEtaRequestFrameFacts RequestFrameFacts;
        public RailEtaScopedStaging Staging;
        public HashSet<Entity> Lines;
        public List<RailEtaMissingRouteSegment> MissingSegments;
        public NativeArray<Entity> Controllers;
        public Entity[] ControllerKeys;
        public readonly HashSet<Entity> ExcludedVehicles = new HashSet<Entity>();
        public readonly List<RailEtaVehiclePathFailure> VehiclePathFailures = new List<RailEtaVehiclePathFailure>();
        public readonly HashSet<RailEtaFailedSegmentKey> FailedSegments = new HashSet<RailEtaFailedSegmentKey>();
        public RailEtaFailure Failure;
        public string Detail;
        public void Dispose()
        {
            if (Controllers.IsCreated) Controllers.Dispose();
            Staging?.Dispose();
            Staging = null;
        }
    }

    internal struct RailEtaTicketFailure
    {
        public RailEtaTicket Ticket;
        public RailEtaFailure Failure;
        public string Detail;
    }

    internal struct RailEtaVehiclePathFailure
    {
        public Entity Vehicle;
        public Entity Line;
        public int SegmentIndex;
        public uint Frame;
        public string Stage;
        public string Reason;
    }

    internal readonly struct RailEtaFailedSegmentKey : IEquatable<RailEtaFailedSegmentKey>
    {
        public RailEtaFailedSegmentKey(RailEtaMissingRouteSegment segment) { Line = segment.Line; SegmentIndex = segment.SegmentIndex; Controller = segment.Controller; Target = segment.Target; IsVehicleTarget = segment.IsVehicleTarget; }
        public readonly Entity Line; public readonly int SegmentIndex; public readonly Entity Controller; public readonly Entity Target; public readonly byte IsVehicleTarget;
        public bool Equals(RailEtaFailedSegmentKey other) => Line == other.Line && SegmentIndex == other.SegmentIndex && Controller == other.Controller && Target == other.Target && IsVehicleTarget == other.IsVehicleTarget;
        public override bool Equals(object obj) => obj is RailEtaFailedSegmentKey other && Equals(other);
        public override int GetHashCode() { unchecked { int hash = Line.GetHashCode(); hash = hash * 397 ^ SegmentIndex; hash = hash * 397 ^ Controller.GetHashCode(); hash = hash * 397 ^ Target.GetHashCode(); return hash * 397 ^ IsVehicleTarget; } }
    }

    internal sealed class RailEtaMaterializeWork
    {
        public RailEtaScopeResult Scope;
        public uint OriginFrame;
        public long IndexWallTicks;
    }

    internal sealed class RailEtaMaterializeResult
    {
        public RailEtaScopeResult Scope;
        public RailEtaWorldSnapshot Snapshot;
        public RailEtaFailure Failure;
        public string Detail;
        public string DiagnosticSummary = string.Empty;
        public long MaterializeWallTicks;
        public RailEtaFrozenWorld FrozenWorld;
    }

    internal sealed class RailEtaPredictionWork
    {
        public RailEtaScopeResult Scope;
        public RailEtaWorldSnapshot Snapshot;
        public long IndexWallTicks;
        public long ScopedWallTicks;
        public long MaterializeWallTicks;
        public RailEtaFrozenWorld FrozenWorld;
    }

    internal sealed class RailEtaRequestFrameFacts
    {
        public double FramesPerMinute;
        public long ClockEpoch;
        public readonly Dictionary<Entity, RailControlledHoldSnapshot> ControlledHolds = new Dictionary<Entity, RailControlledHoldSnapshot>();
        public readonly Dictionary<Entity, int> LineMaxDwellMinutes = new Dictionary<Entity, int>();
        public readonly Dictionary<Entity, RailEtaFrozenTrackChain> TrackChains = new Dictionary<Entity, RailEtaFrozenTrackChain>();
    }

    internal sealed class RailEtaFrozenTrackChain { public Entity Line; public ulong Signature; public RailEtaFrozenTrackAtom[] Atoms = Array.Empty<RailEtaFrozenTrackAtom>(); }
    internal struct RailEtaFrozenTrackAtom { public int Ordinal; public Entity PhysicalLane; public Entity PreviousTarget; public Entity NextTarget; public float Start; public float End; public uint SourceFlags; public byte AtomClass; public sbyte Direction; }
    internal sealed class RailEtaFrozenWorld
    {
        public RailEtaMode Mode;
        public uint OriginFrame;
        public RailEtaScopedVehicleRow[] Vehicles = Array.Empty<RailEtaScopedVehicleRow>();
        public RailEtaScopedUnitRow[] Layout = Array.Empty<RailEtaScopedUnitRow>();
        public RailEtaFrozenNavigationLaneRow[] NavigationLanes = Array.Empty<RailEtaFrozenNavigationLaneRow>();
        public RailEtaFrozenPathElementRow[] PathElements = Array.Empty<RailEtaFrozenPathElementRow>();
        public RailEtaScopedLaneRow[] Lanes = Array.Empty<RailEtaScopedLaneRow>();
        public RailEtaLaneOccupancyRow[] Occupancies = Array.Empty<RailEtaLaneOccupancyRow>();
        public RailEtaSignalPeerRow[] SignalPeers = Array.Empty<RailEtaSignalPeerRow>();
        public RailEtaLineRouteRow[] Lines = Array.Empty<RailEtaLineRouteRow>();
        public RailEtaRouteSegmentRow[] RouteSegments = Array.Empty<RailEtaRouteSegmentRow>();
        public RailEtaRoutePathRow[] RoutePaths = Array.Empty<RailEtaRoutePathRow>();
        public RailEtaRequestFrameFacts RuntimeFacts;
    }

    internal sealed class RailEtaPredictionResult
    {
        public RailEtaScopeResult Scope;
        public RailEtaFrozenWorld FrozenWorld;
        public List<RailEtaTicketPrediction> Predictions;
        public RailEtaFailure Failure;
        public string Detail;
    }

    internal struct RailEtaTicketPrediction
    {
        public RailEtaTicket Ticket;
        public long VehicleId;
        public RailEtaPrediction Prediction;
    }

    internal sealed class RailEtaScopeBuilder
    {
        public RailEtaScopeResult Build(RailEtaScopeWork work)
        {
            try
            {
                // Full receives the global staging set. Compact modes receive an already bounded
                // target/occupant staging set, so this remains the one authoritative scope builder.
                if (work.Staging == null || work.Staging.Overflow.Value != 0)
                    return Failed(work, work.Requests, new List<RailEtaTicketFailure>(), RailEtaFailure.ScopeTruncated, "Frozen request-frame rail facts exceeded their hard limit.");
                RailEtaFrameIndex index = RailEtaFrameIndex.From(work.Staging);
                HashSet<Entity> scope = new HashSet<Entity>();
                HashSet<Entity> lines = new HashSet<Entity>();
                Queue<Entity> pendingVehicles = new Queue<Entity>();
                Queue<Entity> pendingLines = new Queue<Entity>();
                List<RailEtaBatchRequest> activeRequests = new List<RailEtaBatchRequest>();
                List<RailEtaTicketFailure> failures = work.TicketFailures != null ? new List<RailEtaTicketFailure>(work.TicketFailures) : new List<RailEtaTicketFailure>();
                Dictionary<Entity, RailEtaLineRouteRow> lineRows = BuildLineRows(work.Staging);
                Dictionary<Entity, List<Entity>> passengerVehiclesByLine = new Dictionary<Entity, List<Entity>>();
                foreach (KeyValuePair<Entity, RailEtaVehicleIndexRow> pair in index.Vehicles)
                {
                    RailEtaVehicleIndexRow vehicle = pair.Value;
                    if (vehicle.IsPassenger == 0 || vehicle.Route == Entity.Null) continue;
                    if (!passengerVehiclesByLine.TryGetValue(vehicle.Route, out List<Entity> values)) passengerVehiclesByLine[vehicle.Route] = values = new List<Entity>();
                    values.Add(pair.Key);
                }
                var passengerLines = new HashSet<Entity>();
                foreach (KeyValuePair<Entity, RailEtaLineRouteRow> pair in lineRows)
                    if (pair.Value.IsPassenger != 0) passengerLines.Add(pair.Key);
                Dictionary<Entity, HashSet<Entity>> lineGraph = BuildLineGraph(work.Staging, passengerLines);
                foreach (RailEtaBatchRequest request in work.Requests)
                {
                    Entity target = RailEtaEntityId.ToEntity(request.Descriptor);
                    if (!index.Vehicles.ContainsKey(target)) { failures.Add(new RailEtaTicketFailure { Ticket = request.Ticket, Failure = RailEtaFailure.TargetGone, Detail = "Target controller is absent from the rail index." }); continue; }
                    RailEtaVehicleIndexRow indexed = index.Vehicles[target];
                    if (request.ExpectedTarget == Entity.Null) request.ExpectedTarget = indexed.Target;
                    activeRequests.Add(request);
                    AddVehicle(target, index, scope, pendingVehicles);
                    if (indexed.IsPassenger != 0 && indexed.Route != Entity.Null) AddLine(indexed.Route, lines, pendingLines);
                }
                if (work.Mode != RailEtaMode.Full)
                {
                    // Targeted capture is the compact-mode admission boundary. Seed every admitted
                    // controller here; the shared builder cannot add a vehicle absent from its index.
                    foreach (Entity controller in index.Vehicles.Keys)
                        AddVehicle(controller, index, scope, pendingVehicles);
                }
                while (pendingLines.Count > 0 || pendingVehicles.Count > 0)
                {
                    while (pendingLines.Count > 0)
                    {
                        Entity line = pendingLines.Dequeue();
                        if (lineGraph.TryGetValue(line, out HashSet<Entity> neighbours))
                            foreach (Entity neighbour in neighbours) AddLine(neighbour, lines, pendingLines);
                        if (passengerVehiclesByLine.TryGetValue(line, out List<Entity> lineVehicles))
                            for (int i = 0; i < lineVehicles.Count; i++)
                                AddVehicle(lineVehicles[i], index, scope, pendingVehicles);
                    }
                    while (pendingVehicles.Count > 0)
                    {
                        Entity controller = pendingVehicles.Dequeue();
                        RailEtaVehicleIndexRow vehicle = index.Vehicles[controller];
                        Entity blocker = NormalizeBlocker(index, vehicle.Blocker);
                        int blockerDepth = 0;
                        HashSet<Entity> blockerChain = new HashSet<Entity>();
                        while (blocker != Entity.Null && blockerChain.Add(blocker) && blockerDepth++ < RailEtaLimits.MaxBlockerDepth)
                        {
                            AddVehicle(blocker, index, scope, pendingVehicles);
                            blocker = index.Vehicles.TryGetValue(blocker, out RailEtaVehicleIndexRow blockedVehicle) ? NormalizeBlocker(index, blockedVehicle.Blocker) : Entity.Null;
                        }
                        if (blocker != Entity.Null && blockerDepth >= RailEtaLimits.MaxBlockerDepth) return Failed(work, activeRequests, failures, RailEtaFailure.ScopeTruncated, "Blocker depth limit reached.");
                        if (vehicle.IsPassenger != 0 && vehicle.Route != Entity.Null) AddLine(vehicle.Route, lines, pendingLines);
                        if (scope.Count > RailEtaLimits.MaxScopeVehicles) return Failed(work, activeRequests, failures, RailEtaFailure.ScopeTruncated, "Scope vehicle limit reached.");
                    }
                }
                if (index.Overflow) return Failed(work, activeRequests, failures, RailEtaFailure.ScopeTruncated, "Coarse suffix index exceeded its bounded staging capacity.");
                if (work.ExcludedVehicles != null) scope.ExceptWith(work.ExcludedVehicles);
                int resourceCount = CountRelevantResources(lines, scope, work.Staging);
                if (resourceCount > RailEtaLimits.MaxScopeResources) return Failed(work, activeRequests, failures, RailEtaFailure.ScopeTruncated, "Scope resource limit reached.");
                List<RailEtaMissingRouteSegment> missing = work.Mode == RailEtaMode.Theory
                    ? new List<RailEtaMissingRouteSegment>()
                    : BuildMissingSegments(lines, lineRows, work.Staging);
                AddMissingVehicleTargets(scope, index, work.Staging, missing);
                if (work.FailedSegments != null) missing.RemoveAll(segment => work.FailedSegments.Contains(new RailEtaFailedSegmentKey(segment)));
                AssignPathConsumers(missing, scope, work.Staging);
                NativeArray<Entity> controllers = new NativeArray<Entity>(scope.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                Entity[] controllerKeys = new Entity[scope.Count];
                int cursor = 0;
                NativeArray<RailEtaScopedVehicleRow> frozenVehicles = work.Staging.Vehicles.AsArray();
                for (int i = 0; i < frozenVehicles.Length; i++)
                {
                    Entity entity = frozenVehicles[i].Controller;
                    if (!scope.Contains(entity)) continue;
                    controllers[cursor] = entity;
                    controllerKeys[cursor++] = entity;
                }
                var result = new RailEtaScopeResult
                {
                    Mode = work.Mode,
                    BatchId = work.BatchId, IndexOriginFrame = work.IndexOriginFrame, Generation = work.Generation,
                    Requests = activeRequests, TicketFailures = failures, Index = index, Controllers = controllers, ControllerKeys = controllerKeys,
                    Staging = work.Staging,
                    RequestFrameFacts = work.RequestFrameFacts, Lines = lines, MissingSegments = missing
                };
                if (work.ExcludedVehicles != null) result.ExcludedVehicles.UnionWith(work.ExcludedVehicles);
                if (work.VehiclePathFailures != null) result.VehiclePathFailures.AddRange(work.VehiclePathFailures);
                if (work.FailedSegments != null) result.FailedSegments.UnionWith(work.FailedSegments);
                return result;
            }
            finally { }
        }

        private static Entity NormalizeBlocker(RailEtaFrameIndex index, Entity blocker) => index.Vehicles.ContainsKey(blocker) ? blocker : Entity.Null;
        private static void AddVehicle(Entity entity, RailEtaFrameIndex index, HashSet<Entity> scope, Queue<Entity> pending)
        {
            if (entity == Entity.Null || !index.Vehicles.ContainsKey(entity) || !scope.Add(entity)) return;
            pending.Enqueue(entity);
        }
        private static void AddLine(Entity line, HashSet<Entity> lines, Queue<Entity> pending)
        {
            if (line == Entity.Null || !lines.Add(line)) return;
            pending.Enqueue(line);
        }
        private static Dictionary<Entity, RailEtaLineRouteRow> BuildLineRows(RailEtaScopedStaging staging)
        {
            var result = new Dictionary<Entity, RailEtaLineRouteRow>();
            NativeArray<RailEtaLineRouteRow> rows = staging.Lines.AsArray();
            for (int i = 0; i < rows.Length; i++) result[rows[i].Line] = rows[i];
            return result;
        }
        private static Dictionary<Entity, HashSet<Entity>> BuildLineGraph(RailEtaScopedStaging scoped, HashSet<Entity> passengerLines)
        {
            var laneLines = new Dictionary<Entity, HashSet<Entity>>();
            NativeArray<RailEtaScopedLaneRow> rows = scoped.Lanes.AsArray();
            for (int i = 0; i < rows.Length; i++)
            {
                RailEtaScopedLaneRow row = rows[i];
                if (row.Line == Entity.Null || !passengerLines.Contains(row.Line) || row.Source != 5 || row.SharedPhysicalLane == Entity.Null) continue;
                if (!laneLines.TryGetValue(row.SharedPhysicalLane, out HashSet<Entity> values)) laneLines[row.SharedPhysicalLane] = values = new HashSet<Entity>();
                values.Add(row.Line);
            }
            var graph = new Dictionary<Entity, HashSet<Entity>>();
            foreach (HashSet<Entity> values in laneLines.Values)
            {
                foreach (Entity line in values)
                {
                    if (!graph.TryGetValue(line, out HashSet<Entity> neighbours)) graph[line] = neighbours = new HashSet<Entity>();
                    foreach (Entity other in values) if (other != line) neighbours.Add(other);
                }
            }
            return graph;
        }
        private static int CountRelevantResources(HashSet<Entity> lines, HashSet<Entity> scope, RailEtaScopedStaging scoped)
        {
            var resources = new HashSet<ulong>();
            NativeArray<RailEtaScopedLaneRow> rows = scoped.Lanes.AsArray();
            for (int i = 0; i < rows.Length; i++)
            {
                RailEtaScopedLaneRow row = rows[i];
                bool relevant = (row.Line != Entity.Null && lines.Contains(row.Line)) || (row.Controller != Entity.Null && scope.Contains(row.Controller));
                if (!relevant) continue;
                if (row.Source == 3 && row.OtherLane != Entity.Null)
                    resources.Add(RailEtaFrameIndex.ResourceKey(row.Lane, row.OtherLane));
                if (row.HasReservation != 0)
                    resources.Add(unchecked((ulong)RailEtaEntityId.Pack(row.Lane)));
                if ((row.TrackFlags & 0x10u) != 0)
                    resources.Add(unchecked((ulong)RailEtaEntityId.Pack(row.Lane)));
            }
            return resources.Count;
        }
        private static List<RailEtaMissingRouteSegment> BuildMissingSegments(HashSet<Entity> lines, Dictionary<Entity, RailEtaLineRouteRow> lineRows, RailEtaScopedStaging routes)
        {
            var result = new List<RailEtaMissingRouteSegment>();
            NativeArray<RailEtaRouteSegmentRow> segments = routes.Segments.AsArray();
            for (int i = 0; i < segments.Length; i++)
            {
                RailEtaRouteSegmentRow segment = segments[i];
                if (!lines.Contains(segment.Line)
                    || (segment.GeometryAvailable != 0 && segment.PathfindDelayKnown != 0)) continue;
                lineRows.TryGetValue(segment.Line, out RailEtaLineRouteRow line);
                result.Add(new RailEtaMissingRouteSegment { Line = segment.Line, SegmentIndex = segment.SegmentIndex,
                    FromWaypoint = segment.FromWaypoint, ToWaypoint = segment.ToWaypoint, ChainSignature = line.ChainSignature,
                    NeedsGeometry = (byte)(segment.GeometryAvailable == 0 ? 1 : 0) });
            }
            return result;
        }
        private static void AddMissingVehicleTargets(HashSet<Entity> scope, RailEtaFrameIndex index, RailEtaScopedStaging staging, List<RailEtaMissingRouteSegment> result)
        {
            NativeArray<RailEtaScopedVehicleRow> vehicles = staging.Vehicles.AsArray();
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity controller = vehicles[i].Controller;
                if (!scope.Contains(controller)) continue;
                if (!index.Vehicles.TryGetValue(controller, out RailEtaVehicleIndexRow vehicle) || vehicle.Target == Entity.Null) continue;
                PathFlags state = (PathFlags)vehicle.PathState;
                bool hasPath = index.ControllersWithPath.Contains(controller);
                bool validState = (state & (PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Updated)) == 0;
                bool destinationProven = vehicle.HasPathInformation != 0 && vehicle.PathDestination == vehicle.Target;
                if (validState && hasPath && destinationProven) continue;
                result.Add(new RailEtaMissingRouteSegment { Controller = controller, Target = vehicle.Target, IsVehicleTarget = 1 });
            }
        }
        private static void AssignPathConsumers(List<RailEtaMissingRouteSegment> missing, HashSet<Entity> scope, RailEtaScopedStaging staging)
        {
            NativeArray<RailEtaScopedVehicleRow> vehicles = staging.Vehicles.AsArray();
            for (int i = 0; i < missing.Count; i++)
            {
                RailEtaMissingRouteSegment segment = missing[i];
                if (segment.IsVehicleTarget != 0)
                {
                    if (scope.Contains(segment.Controller)) segment.Consumers.Add(segment.Controller);
                    continue;
                }
                for (int vehicleIndex = 0; vehicleIndex < vehicles.Length; vehicleIndex++)
                {
                    RailEtaScopedVehicleRow vehicle = vehicles[vehicleIndex];
                    if (scope.Contains(vehicle.Controller) && vehicle.Route == segment.Line)
                        segment.Consumers.Add(vehicle.Controller);
                }
            }
            for (int i = missing.Count - 1; i >= 0; i--)
                if (missing[i].Consumers.Count == 0) missing.RemoveAt(i);
        }
        private static RailEtaScopeResult Failed(RailEtaScopeWork work, List<RailEtaBatchRequest> requests, List<RailEtaTicketFailure> failures, RailEtaFailure failure, string detail) => new RailEtaScopeResult
        {
            Mode = work.Mode,
            BatchId = work.BatchId, IndexOriginFrame = work.IndexOriginFrame, Generation = work.Generation,
            Requests = requests, TicketFailures = failures, Failure = failure, Detail = detail,
            Staging = work.Staging, RequestFrameFacts = work.RequestFrameFacts
        };
    }

    internal sealed class RailEtaSnapshotMaterializer
    {
        private sealed class ReservationSemanticComparer : IEqualityComparer<RailEtaScopedLaneRow>
        {
            public static readonly ReservationSemanticComparer Instance = new ReservationSemanticComparer();

            public bool Equals(RailEtaScopedLaneRow x, RailEtaScopedLaneRow y) =>
                x.Lane == y.Lane && x.ReservationBlocker == y.ReservationBlocker && x.ReservationExternalKind == y.ReservationExternalKind &&
                x.PreviousPriority == y.PreviousPriority && x.PreviousOffset == y.PreviousOffset && x.NextPriority == y.NextPriority &&
                x.NextOffset == y.NextOffset && x.UpdateFrameIndex == y.UpdateFrameIndex && x.HasReservation == y.HasReservation && x.HasUpdateFrame == y.HasUpdateFrame;

            public int GetHashCode(RailEtaScopedLaneRow value)
            {
                int hash = value.Lane.GetHashCode();
                hash = CombineHash(hash, value.ReservationBlocker.GetHashCode());
                hash = CombineHash(hash, value.ReservationExternalKind);
                hash = CombineHash(hash, value.PreviousPriority);
                hash = CombineHash(hash, value.PreviousOffset);
                hash = CombineHash(hash, value.NextPriority);
                hash = CombineHash(hash, value.NextOffset);
                hash = CombineHash(hash, unchecked((int)value.UpdateFrameIndex));
                hash = CombineHash(hash, value.HasReservation);
                return CombineHash(hash, value.HasUpdateFrame);
            }
        }

        private sealed class SignalSemanticComparer : IEqualityComparer<RailEtaScopedLaneRow>
        {
            public static readonly SignalSemanticComparer Instance = new SignalSemanticComparer();

            public bool Equals(RailEtaScopedLaneRow x, RailEtaScopedLaneRow y) =>
                x.Lane == y.Lane && x.SignalPetitioner == y.SignalPetitioner && x.SignalBlocker == y.SignalBlocker &&
                x.SignalPetitionerExternalKind == y.SignalPetitionerExternalKind && x.SignalBlockerExternalKind == y.SignalBlockerExternalKind &&
                x.SignalPriority == y.SignalPriority && x.SignalFlags == y.SignalFlags && x.SignalType == y.SignalType;

            public int GetHashCode(RailEtaScopedLaneRow value)
            {
                int hash = value.Lane.GetHashCode();
                hash = CombineHash(hash, value.SignalPetitioner.GetHashCode());
                hash = CombineHash(hash, value.SignalBlocker.GetHashCode());
                hash = CombineHash(hash, value.SignalPetitionerExternalKind);
                hash = CombineHash(hash, value.SignalBlockerExternalKind);
                hash = CombineHash(hash, value.SignalPriority);
                hash = CombineHash(hash, value.SignalFlags);
                return CombineHash(hash, value.SignalType);
            }
        }

        private sealed class OccupancySemanticComparer : IEqualityComparer<RailEtaLaneOccupancyRow>
        {
            public static readonly OccupancySemanticComparer Instance = new OccupancySemanticComparer();

            public bool Equals(RailEtaLaneOccupancyRow x, RailEtaLaneOccupancyRow y) =>
                x.Lane == y.Lane && x.Vehicle == y.Vehicle && x.Start.Equals(y.Start) && x.End.Equals(y.End);

            public int GetHashCode(RailEtaLaneOccupancyRow value)
            {
                int hash = value.Lane.GetHashCode();
                hash = CombineHash(hash, value.Vehicle.GetHashCode());
                hash = CombineHash(hash, value.Start.GetHashCode());
                return CombineHash(hash, value.End.GetHashCode());
            }
        }

        public RailEtaMaterializeResult Materialize(RailEtaMaterializeWork work)
        {
            try
            {
                RailEtaScopedStaging scoped = work.Scope.Staging;
                if (scoped == null || scoped.Overflow.Value != 0)
                    return new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.ScopeTruncated, Detail = "Frozen request-frame staging was incomplete." };
                if (!TryBuildPathPhysicalLaneIndex(scoped.Lanes.AsArray(), out Dictionary<Entity, Entity> pathPhysicalByLane, out string physicalIndexDetail))
                    return new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.SnapshotUnstable, Detail = physicalIndexDetail };
                HashSet<Entity> expected = new HashSet<Entity>(work.Scope.ControllerKeys ?? Array.Empty<Entity>());
                expected.ExceptWith(work.Scope.ExcludedVehicles);
                var requestedTargets = new HashSet<Entity>();
                if (work.Scope.Requests != null) for (int i = 0; i < work.Scope.Requests.Count; i++) requestedTargets.Add(RailEtaEntityId.ToEntity(work.Scope.Requests[i].Descriptor));
                NativeArray<RailEtaScopedVehicleRow> allRows = scoped.Vehicles.AsArray();
                var selectedRows = new List<RailEtaScopedVehicleRow>(expected.Count);
                for (int i = 0; i < allRows.Length; i++) if (expected.Contains(allRows[i].Controller)) selectedRows.Add(allRows[i]);
                var frozenUnits = new List<RailEtaScopedUnitRow>();
                var frozenNavigationLanes = new List<RailEtaFrozenNavigationLaneRow>();
                var frozenPathElements = new List<RailEtaFrozenPathElementRow>();
                var frozenLanes = new List<RailEtaScopedLaneRow>();
                var frozenOccupancies = new List<RailEtaLaneOccupancyRow>();
                var frozenSignalPeers = new List<RailEtaSignalPeerRow>();
                var relevantSignalControllers = new HashSet<Entity>();
                var relevantSignalLanes = new HashSet<Entity>();
                var frozenOverlapTargets = new Dictionary<Entity, List<Entity>>();
                var frozenLineRows = new List<RailEtaLineRouteRow>();
                var frozenRouteSegments = new List<RailEtaRouteSegmentRow>();
                var frozenRoutePaths = new List<RailEtaRoutePathRow>();
                Dictionary<Entity, List<RailEtaScopedLaneRow>> lanesByVehicle = new Dictionary<Entity, List<RailEtaScopedLaneRow>>();
                Dictionary<Entity, Dictionary<int, List<RailEtaScopedLaneRow>>> routeLanes = new Dictionary<Entity, Dictionary<int, List<RailEtaScopedLaneRow>>>();
                Dictionary<Entity, Dictionary<int, Entity>> routeCheckpoints = new Dictionary<Entity, Dictionary<int, Entity>>();
                Dictionary<Entity, List<RailEtaScopedUnitRow>> unitsByVehicle = new Dictionary<Entity, List<RailEtaScopedUnitRow>>();
                NativeArray<RailEtaScopedUnitRow> unitRows = scoped.Units.AsArray();
                for (int i = 0; i < unitRows.Length; i++)
                {
                    if (!expected.Contains(unitRows[i].Controller)) continue;
                    frozenUnits.Add(unitRows[i]);
                    if (!unitsByVehicle.TryGetValue(unitRows[i].Controller, out List<RailEtaScopedUnitRow> list)) unitsByVehicle[unitRows[i].Controller] = list = new List<RailEtaScopedUnitRow>();
                    list.Add(unitRows[i]);
                }
                NativeArray<RailEtaScopedLaneRow> laneRows = scoped.Lanes.AsArray();
                for (int i = 0; i < laneRows.Length; i++)
                {
                    RailEtaScopedLaneRow lane = laneRows[i];
                    bool frozenRelevant = lane.Line != Entity.Null ? work.Scope.Lines != null && work.Scope.Lines.Contains(lane.Line) : expected.Contains(lane.Controller);
                    if (frozenRelevant && lane.Lane != Entity.Null) relevantSignalLanes.Add(lane.Lane);
                    bool freezeLane = frozenRelevant || (lane.Source == 6 && lane.PathPhysicalLane != Entity.Null);
                    if (freezeLane)
                    {
                        frozenLanes.Add(lane);
                        if (lane.Source == 3 && lane.Lane != Entity.Null && lane.OtherLane != Entity.Null)
                        {
                            if (!frozenOverlapTargets.TryGetValue(lane.Lane, out List<Entity> targets))
                                frozenOverlapTargets.Add(lane.Lane, targets = new List<Entity>());
                            targets.Add(lane.OtherLane);
                        }
                    }
                    if (lane.Line != Entity.Null)
                    {
                        if (work.Scope.Lines == null || !work.Scope.Lines.Contains(lane.Line)) continue;
                        if (!routeLanes.TryGetValue(lane.Line, out Dictionary<int, List<RailEtaScopedLaneRow>> bySegment)) routeLanes[lane.Line] = bySegment = new Dictionary<int, List<RailEtaScopedLaneRow>>();
                        if (!bySegment.TryGetValue(lane.RouteSegmentIndex, out List<RailEtaScopedLaneRow> segmentRows)) bySegment[lane.RouteSegmentIndex] = segmentRows = new List<RailEtaScopedLaneRow>();
                        segmentRows.Add(lane);
                        continue;
                    }
                    if (!expected.Contains(lane.Controller)) continue;
                    if (!lanesByVehicle.TryGetValue(lane.Controller, out List<RailEtaScopedLaneRow> list)) lanesByVehicle[lane.Controller] = list = new List<RailEtaScopedLaneRow>();
                    list.Add(lane);
                }
                var pendingSignalLanes = new Queue<Entity>(relevantSignalLanes);
                while (pendingSignalLanes.Count != 0)
                {
                    Entity sourceLane = pendingSignalLanes.Dequeue();
                    if (!frozenOverlapTargets.TryGetValue(sourceLane, out List<Entity> targets)) continue;
                    for (int i = 0; i < targets.Count; i++)
                    {
                        Entity targetLane = targets[i];
                        if (relevantSignalLanes.Add(targetLane)) pendingSignalLanes.Enqueue(targetLane);
                    }
                }
                for (int i = 0; i < laneRows.Length; i++)
                {
                    RailEtaScopedLaneRow lane = laneRows[i];
                    bool railSignalLane = lane.HasTrackLane != 0
                        || (lane.HasConnectionLane != 0 && lane.ConnectionTrackTypes != (uint)Game.Net.TrackTypes.None);
                    if (railSignalLane && lane.SignalController != Entity.Null
                        && relevantSignalLanes.Contains(lane.Lane))
                        relevantSignalControllers.Add(lane.SignalController);
                }
                NativeArray<RailEtaSignalPeerRow> signalPeerRows = scoped.SignalPeers.AsArray();
                for (int i = 0; i < signalPeerRows.Length; i++)
                    if (relevantSignalLanes.Contains(signalPeerRows[i].Lane))
                        relevantSignalControllers.Add(signalPeerRows[i].Controller);
                NativeArray<RailEtaRouteSegmentRow> routeSegmentRows = scoped.Segments.AsArray();
                for (int i = 0; i < routeSegmentRows.Length; i++)
                {
                    RailEtaRouteSegmentRow segment = routeSegmentRows[i];
                    if (work.Scope.Lines != null && work.Scope.Lines.Contains(segment.Line)) frozenRouteSegments.Add(segment);
                    if (!routeCheckpoints.TryGetValue(segment.Line, out Dictionary<int, Entity> bySegment))
                        routeCheckpoints[segment.Line] = bySegment = new Dictionary<int, Entity>();
                    bySegment[segment.SegmentIndex] = segment.ToWaypoint;
                }
                RailVehicleSnapshot[] vehicles = new RailVehicleSnapshot[selectedRows.Count];
                List<RailBlockerSnapshot> blockers = new List<RailBlockerSnapshot>();
                List<RailReservationSnapshot> reservations = new List<RailReservationSnapshot>();
                List<RailSignalSnapshot> signals = new List<RailSignalSnapshot>();
                List<RailLaneOccupancySnapshot> occupancies = new List<RailLaneOccupancySnapshot>();
                HashSet<RailEtaScopedLaneRow> reservationKeys = new HashSet<RailEtaScopedLaneRow>(ReservationSemanticComparer.Instance);
                HashSet<RailEtaScopedLaneRow> signalKeys = new HashSet<RailEtaScopedLaneRow>(SignalSemanticComparer.Instance);
                HashSet<RailEtaLaneOccupancyRow> occupancyKeys = new HashSet<RailEtaLaneOccupancyRow>(OccupancySemanticComparer.Instance);
                NativeArray<RailEtaLaneOccupancyRow> occupancyRows = scoped.Occupancies.AsArray();
                for (int i = 0; i < occupancyRows.Length; i++)
                {
                    RailEtaLaneOccupancyRow occupancy = occupancyRows[i];
                    if (!expected.Contains(occupancy.Vehicle)) continue;
                    frozenOccupancies.Add(occupancy);
                    if (occupancyKeys.Add(occupancy)) occupancies.Add(new RailLaneOccupancySnapshot { LaneId = new RailLaneId(RailEtaEntityId.Pack(occupancy.Lane)), VehicleId = new RailVehicleId(RailEtaEntityId.Pack(occupancy.Vehicle)), StartFraction = occupancy.Start, EndFraction = occupancy.End });
                }
                Dictionary<ulong, RailResourceSnapshot> resources = new Dictionary<ulong, RailResourceSnapshot>();
                NativeArray<RailEtaFrozenNavigationLaneRow> navigationRows = scoped.NavigationLanes.AsArray();
                for (int i = 0; i < navigationRows.Length; i++) if (expected.Contains(navigationRows[i].Controller)) frozenNavigationLanes.Add(navigationRows[i]);
                NativeArray<RailEtaFrozenPathElementRow> pathElementRows = scoped.PathElements.AsArray();
                for (int i = 0; i < pathElementRows.Length; i++) if (expected.Contains(pathElementRows[i].Controller)) frozenPathElements.Add(pathElementRows[i]);
                for (int i = 0; i < laneRows.Length; i++)
                {
                    RailEtaScopedLaneRow lane = laneRows[i];
                    bool relevant = lane.Line != Entity.Null ? work.Scope.Lines != null && work.Scope.Lines.Contains(lane.Line) : expected.Contains(lane.Controller);
                    if (!relevant) continue;
                    if (lane.HasReservation != 0 && reservationKeys.Add(lane))
                        reservations.Add(new RailReservationSnapshot
                        {
                            ResourceId = new RailResourceId(RailEtaEntityId.Pack(lane.Lane)), BlockerVehicleId = new RailVehicleId(RailEtaEntityId.Pack(lane.ReservationBlocker)),
                            ExternalBlockerKind = (RailExternalBlockerKind)lane.ReservationExternalKind,
                            PreviousPriority = lane.PreviousPriority, PreviousOffset = lane.PreviousOffset / 255.0,
                            NextPriority = lane.NextPriority, NextOffset = lane.NextOffset / 255.0, UpdateFrameIndex = lane.UpdateFrameIndex,
                            HasUpdateFrame = lane.HasUpdateFrame != 0
                        });
                    if ((lane.SignalType != 0 || lane.SignalPetitioner != Entity.Null || lane.SignalBlocker != Entity.Null || lane.SignalFlags != 0) && signalKeys.Add(lane))
                        signals.Add(new RailSignalSnapshot { LaneId = new RailLaneId(RailEtaEntityId.Pack(lane.Lane)), PetitionerVehicleId = new RailVehicleId(RailEtaEntityId.Pack(lane.SignalPetitioner)), BlockerVehicleId = new RailVehicleId(RailEtaEntityId.Pack(lane.SignalBlocker)), PetitionerExternalKind = (RailExternalBlockerKind)lane.SignalPetitionerExternalKind, BlockerExternalKind = (RailExternalBlockerKind)lane.SignalBlockerExternalKind, SignalType = (RailLaneSignalType)lane.SignalType, Priority = lane.SignalPriority, Flags = lane.SignalFlags });
                    if (lane.OtherLane != Entity.Null)
                    {
                        ulong key = RailEtaFrameIndex.ResourceKey(lane.Lane, lane.OtherLane);
                        if (!resources.ContainsKey(key)) resources[key] = CreateResourceSnapshot(key, lane);
                    }
                }
                Dictionary<Entity, RailEtaLineRouteRow> lineMetadata = new Dictionary<Entity, RailEtaLineRouteRow>();
                NativeArray<RailEtaLineRouteRow> lineRows = scoped.Lines.AsArray();
                for (int i = 0; i < lineRows.Length; i++) { lineMetadata[lineRows[i].Line] = lineRows[i]; if (work.Scope.Lines != null && work.Scope.Lines.Contains(lineRows[i].Line)) frozenLineRows.Add(lineRows[i]); }
                NativeArray<RailEtaRoutePathRow> routePathRows = scoped.Paths.AsArray();
                for (int i = 0; i < routePathRows.Length; i++) if (work.Scope.Lines != null && work.Scope.Lines.Contains(routePathRows[i].Line)) frozenRoutePaths.Add(routePathRows[i]);
                for (int i = 0; i < signalPeerRows.Length; i++)
                    if (relevantSignalControllers.Contains(signalPeerRows[i].Controller)) frozenSignalPeers.Add(signalPeerRows[i]);
                for (int i = 0; i < selectedRows.Count; i++)
                {
                    RailEtaScopedVehicleRow row = selectedRows[i];
                    List<RailPathSegment> path = new List<RailPathSegment>();
                    List<RailConsistUnitSnapshot> units = new List<RailConsistUnitSnapshot>();
                    double consistLengthMetres = 0;
                    if (unitsByVehicle.TryGetValue(row.Controller, out List<RailEtaScopedUnitRow> capturedUnits))
                    {
                        foreach (RailEtaScopedUnitRow unit in capturedUnits)
                        {
                            units.Add(new RailConsistUnitSnapshot { Entity = Identity(unit.Unit), Prefab = Identity(unit.Prefab), LengthMetres = unit.Length, FrontBogieOffsetMetres = unit.FrontBogieOffset, RearBogieOffsetMetres = unit.RearBogieOffset, FrontAttachOffsetMetres = unit.FrontAttachOffset, RearAttachOffsetMetres = unit.RearAttachOffset });
                            consistLengthMetres += unit.Length;
                        }
                    }
                    if (lanesByVehicle.TryGetValue(row.Controller, out List<RailEtaScopedLaneRow> laneList))
                    {
                        laneList.Sort((a, b) => a.Sequence != b.Sequence ? a.Sequence.CompareTo(b.Sequence) : a.Source.CompareTo(b.Source));
                        bool hasResolvedTargetPath = laneList.Exists(value => value.Source == 7);
                        foreach (RailEtaScopedLaneRow lane in laneList)
                        {
                            if (hasResolvedTargetPath && lane.Source != 7 && lane.Source != 3) continue;
                            if (lane.Source != 3)
                            {
                                if (!TryPhysicalLane(pathPhysicalByLane, lane.Lane, out RailLaneId physicalLane))
                                    return new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.RouteGeometryMissing,
                                        Detail = "Frozen physical-lane mapping is missing for lane " + lane.Lane.Index + ":" + lane.Lane.Version
                                            + " source=" + lane.Source + " hasCurve=" + lane.HasCurve + " hasTrack=" + lane.HasTrackLane
                                            + " hasConnection=" + lane.HasConnectionLane + " pathFlags=" + lane.PathFlags + "." };
                                path.Add(new RailPathSegment { LaneId = new RailLaneId(RailEtaEntityId.Pack(lane.Lane)), PhysicalLaneId = physicalLane, LengthMetres = lane.Length * Math.Abs(lane.CurveEnd - lane.CurveStart), SpeedLimitMetresPerSecond = lane.SpeedLimit, Curviness = lane.Curviness, IsConnectionLane = lane.IsConnectionLane != 0, StartFraction = lane.CurveStart, EndFraction = lane.CurveEnd, NavigationFlags = lane.NavigationFlags, TrackFlags = lane.TrackFlags, Ax = lane.CurveA.x, Ay = lane.CurveA.y, Az = lane.CurveA.z, Bx = lane.CurveB.x, By = lane.CurveB.y, Bz = lane.CurveB.z, Cx = lane.CurveC.x, Cy = lane.CurveC.y, Cz = lane.CurveC.z, Dx = lane.CurveD.x, Dy = lane.CurveD.y, Dz = lane.CurveD.z });
                            }
                            if (lane.HasReservation != 0 && reservationKeys.Add(lane))
                                reservations.Add(new RailReservationSnapshot
                                {
                                    ResourceId = new RailResourceId(RailEtaEntityId.Pack(lane.Lane)), BlockerVehicleId = new RailVehicleId(RailEtaEntityId.Pack(lane.ReservationBlocker)),
                                    ExternalBlockerKind = (RailExternalBlockerKind)lane.ReservationExternalKind,
                                    PreviousPriority = lane.PreviousPriority, PreviousOffset = lane.PreviousOffset / 255.0,
                                    NextPriority = lane.NextPriority, NextOffset = lane.NextOffset / 255.0, UpdateFrameIndex = lane.UpdateFrameIndex,
                                    HasUpdateFrame = lane.HasUpdateFrame != 0
                                });
                            if ((lane.SignalType != 0 || lane.SignalPetitioner != Entity.Null || lane.SignalBlocker != Entity.Null || lane.SignalFlags != 0) && signalKeys.Add(lane))
                                signals.Add(new RailSignalSnapshot { LaneId = new RailLaneId(RailEtaEntityId.Pack(lane.Lane)), PetitionerVehicleId = new RailVehicleId(RailEtaEntityId.Pack(lane.SignalPetitioner)), BlockerVehicleId = new RailVehicleId(RailEtaEntityId.Pack(lane.SignalBlocker)), PetitionerExternalKind = (RailExternalBlockerKind)lane.SignalPetitionerExternalKind, BlockerExternalKind = (RailExternalBlockerKind)lane.SignalBlockerExternalKind, SignalType = (RailLaneSignalType)lane.SignalType, Priority = lane.SignalPriority, Flags = lane.SignalFlags });
                            if (lane.OtherLane != Entity.Null)
                            {
                                ulong key = RailEtaFrameIndex.ResourceKey(lane.Lane, lane.OtherLane);
                                if (!resources.ContainsKey(key)) resources[key] = CreateResourceSnapshot(key, lane);
                            }
                        }
                    }
                    if (path.Count > 0 && row.Target != Entity.Null && row.HasPathInformation != 0 && row.PathDestination == row.Target)
                    {
                        RailPathSegment endpoint = path[path.Count - 1];
                        endpoint.EndCheckpointId = new RailCheckpointId(RailEtaEntityId.Pack(row.Target));
                    }
                    ulong lineSignature = 0;
                    if (!requestedTargets.Contains(row.Controller) && row.Route != Entity.Null && lineMetadata.TryGetValue(row.Route, out RailEtaLineRouteRow lineInfo)
                        && lineInfo.SegmentCount > 0 && routeLanes.TryGetValue(row.Route, out Dictionary<int, List<RailEtaScopedLaneRow>> bySegment))
                    {
                        lineSignature = lineInfo.ChainSignature;
                        if (lineSignature == 0)
                            return new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.RouteGeometryMissing, Detail = "Frozen route signature is missing." };
                        int startSegment = row.TargetSegmentIndex >= 0 ? row.TargetSegmentIndex % lineInfo.SegmentCount : 0;
                        for (int offset = 0; offset < lineInfo.SegmentCount; offset++)
                        {
                            int segmentIndex = (startSegment + offset) % lineInfo.SegmentCount;
                            if (!bySegment.TryGetValue(segmentIndex, out List<RailEtaScopedLaneRow> segmentRows))
                                return new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.RouteGeometryMissing, Detail = "Route segment geometry is missing after path expansion." };
                            segmentRows.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
                            int segmentPathStart = path.Count;
                            for (int p = 0; p < segmentRows.Count; p++)
                            {
                                RailEtaScopedLaneRow lane = segmentRows[p];
                                if (lane.Source == 3 || lane.Lane == Entity.Null) continue;
                                if (!TryPhysicalLane(pathPhysicalByLane, lane.Lane, out RailLaneId physicalLane))
                                    return new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.RouteGeometryMissing,
                                        Detail = "Frozen physical-lane mapping is missing for route lane " + lane.Lane.Index + ":" + lane.Lane.Version
                                            + " segment=" + segmentIndex + " source=" + lane.Source + " hasCurve=" + lane.HasCurve
                                            + " hasTrack=" + lane.HasTrackLane + " hasConnection=" + lane.HasConnectionLane
                                            + " pathFlags=" + lane.PathFlags + "." };
                                path.Add(CreatePathSegment(lane, physicalLane));
                            }
                            if (path.Count == segmentPathStart || !routeCheckpoints.TryGetValue(row.Route, out Dictionary<int, Entity> checkpointBySegment)
                                || !checkpointBySegment.TryGetValue(segmentIndex, out Entity toWaypoint) || toWaypoint == Entity.Null)
                                return new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.RouteGeometryMissing, Detail = "Route segment endpoint checkpoint is missing." };
                            path[path.Count - 1].EndCheckpointId = new RailCheckpointId(RailEtaEntityId.Pack(toWaypoint));
                        }
                    }
                    vehicles[i] = new RailVehicleSnapshot
                    {
                        VehicleId = new RailVehicleId(RailEtaEntityId.Pack(row.Controller)), Entity = Identity(row.Controller), Controller = Identity(row.Controller), Target = Identity(row.Target), Line = Identity(row.Route),
                        SpeedMetresPerSecond = row.Speed, IsBoarding = row.Boarding != 0, DepartureFrame = row.DepartureFrame,
                        PathState = row.PathState, PathElementIndex = row.PathElementIndex, PathSignature = row.PathSignature, ResourceSignature = row.ResourceSignature,
                        VehiclePriority = row.VehiclePriority, LineTrackChainSignature = lineSignature,
                        CurrentLane = new RailCurrentLaneSnapshot { FrontLaneId = new RailLaneId(RailEtaEntityId.Pack(row.FrontLane)), FrontPosition = row.FrontCurveStart, RearLaneId = new RailLaneId(RailEtaEntityId.Pack(row.RearLane)), RearPosition = row.RearCurvePosition, FrontCacheLaneId = new RailLaneId(RailEtaEntityId.Pack(row.FrontCacheLane)), RearCacheLaneId = new RailLaneId(RailEtaEntityId.Pack(row.RearCacheLane)), FrontFlags = row.FrontLaneFlags, RearFlags = row.RearLaneFlags },
                        ExternalBlockerKind = (RailExternalBlockerKind)row.ExternalBlockerKind,
                        Consist = new RailConsistSnapshot { UnitCount = row.UnitCount, LengthMetres = consistLengthMetres, Units = units.ToArray(), Physics = new RailTrainPhysics { MaximumSpeedMetresPerSecond = row.MaximumSpeed, AccelerationMetresPerSecondSquared = row.Acceleration, BrakingMetresPerSecondSquared = row.Braking, TurningLowRadiansPerSecond = row.TurningLow, TurningHighRadiansPerSecond = row.TurningHigh, StopSpeedThresholdMetresPerSecond = 0.1d } },
                        RemainingPath = path.ToArray()
                    };
                    if (row.Blocker != Entity.Null || row.BlockerType != 0)
                        blockers.Add(new RailBlockerSnapshot
                        {
                            VehicleId = vehicles[i].VehicleId,
                            BlockerVehicleId = expected.Contains(row.Blocker) ? new RailVehicleId(RailEtaEntityId.Pack(row.Blocker)) : default,
                            Type = (RailBlockerType)row.BlockerType,
                            MaximumSpeedCode = row.BlockerMaximumSpeed,
                            MaximumSpeedMetresPerSecond = row.BlockerMaximumSpeedMetresPerSecond
                        });
                }
                var topology = new List<RailLineTopologySnapshot>();
                if (work.Scope.Lines != null)
                    foreach (Entity line in work.Scope.Lines)
                        if (lineMetadata.TryGetValue(line, out RailEtaLineRouteRow info) && info.ChainSignature != 0)
                            topology.Add(new RailLineTopologySnapshot { Line = Identity(line), ChainSignature = info.ChainSignature, SegmentCount = info.SegmentCount });
                        else return new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.RouteGeometryMissing, Detail = "Line route metadata is unavailable in the request-frame snapshot." };
                RailEtaWorldSnapshot snapshot = new RailEtaWorldSnapshot { Mode = work.Scope.Mode, OriginFrame = work.OriginFrame, NavigationPhase = work.OriginFrame & 15u, BatchId = work.Scope.BatchId, ServiceGeneration = work.Scope.Generation, ClosureValidated = true, ScopeLineCount = topology.Count, Lines = topology.ToArray(), Vehicles = vehicles, Blockers = blockers.ToArray(), Reservations = reservations.ToArray(), Signals = signals.ToArray(), Occupancies = occupancies.ToArray(), Resources = new List<RailResourceSnapshot>(resources.Values).ToArray() };
                RailEtaMaterializeResult materialized = new RailEtaMaterializeResult
                {
                    Scope = work.Scope,
                    Snapshot = snapshot,
                    FrozenWorld = new RailEtaFrozenWorld { Mode = work.Scope.Mode, OriginFrame = work.OriginFrame, Vehicles = selectedRows.ToArray(), Layout = frozenUnits.ToArray(),
                        NavigationLanes = frozenNavigationLanes.ToArray(), PathElements = frozenPathElements.ToArray(), Lanes = frozenLanes.ToArray(),
                        Occupancies = frozenOccupancies.ToArray(), SignalPeers = frozenSignalPeers.ToArray(), Lines = frozenLineRows.ToArray(), RouteSegments = frozenRouteSegments.ToArray(),
                        RoutePaths = frozenRoutePaths.ToArray(), RuntimeFacts = work.Scope.RequestFrameFacts }
                };
#if RT_DEBUG_TOOLS
                if (RailEtaDebugSettings.DetailedLogsEnabled)
                    materialized.DiagnosticSummary = RailEtaSnapshotDiagnostics.BuildSummary(snapshot, work.Scope.Requests?.Count ?? 0);
#endif
                return materialized;
            }
            finally { }
        }

        /// <summary>
        /// One-shot worker-local index: lane entity -> canonical PathPhysicalLane. Detects conflicting mappings.
        /// </summary>
        private static bool TryBuildPathPhysicalLaneIndex(
            NativeArray<RailEtaScopedLaneRow> rows,
            out Dictionary<Entity, Entity> pathPhysicalByLane,
            out string detail)
        {
            pathPhysicalByLane = new Dictionary<Entity, Entity>();
            detail = null;
            for (int i = 0; i < rows.Length; i++)
            {
                RailEtaScopedLaneRow row = rows[i];
                if (row.Lane == Entity.Null || row.PathPhysicalLane == Entity.Null)
                    continue;
                if (pathPhysicalByLane.TryGetValue(row.Lane, out Entity existing))
                {
                    if (existing != row.PathPhysicalLane)
                    {
                        detail = "canonical path-physical-lane mapping conflict for lane "
                            + row.Lane.Index + ":" + row.Lane.Version
                            + " (" + existing.Index + ":" + existing.Version
                            + " vs " + row.PathPhysicalLane.Index + ":" + row.PathPhysicalLane.Version + ").";
                        pathPhysicalByLane = null;
                        return false;
                    }
                    continue;
                }
                pathPhysicalByLane[row.Lane] = row.PathPhysicalLane;
            }
            return true;
        }

        private static bool TryPhysicalLane(Dictionary<Entity, Entity> pathPhysicalByLane, Entity lane, out RailLaneId physicalLane)
        {
            physicalLane = default;
            if (pathPhysicalByLane == null || lane == Entity.Null)
                return false;
            if (!pathPhysicalByLane.TryGetValue(lane, out Entity mapped) || mapped == Entity.Null)
                return false;
            physicalLane = new RailLaneId(RailEtaEntityId.Pack(mapped));
            return true;
        }
        private static RailPathSegment CreatePathSegment(RailEtaScopedLaneRow lane, RailLaneId physicalLane) => new RailPathSegment
        {
            LaneId = new RailLaneId(RailEtaEntityId.Pack(lane.Lane)), PhysicalLaneId = physicalLane, LengthMetres = lane.Length * Math.Abs(lane.CurveEnd - lane.CurveStart),
            SpeedLimitMetresPerSecond = lane.SpeedLimit, Curviness = lane.Curviness, IsConnectionLane = lane.IsConnectionLane != 0,
            StartFraction = lane.CurveStart, EndFraction = lane.CurveEnd, NavigationFlags = lane.NavigationFlags, TrackFlags = lane.TrackFlags,
            Ax = lane.CurveA.x, Ay = lane.CurveA.y, Az = lane.CurveA.z, Bx = lane.CurveB.x, By = lane.CurveB.y, Bz = lane.CurveB.z,
            Cx = lane.CurveC.x, Cy = lane.CurveC.y, Cz = lane.CurveC.z, Dx = lane.CurveD.x, Dy = lane.CurveD.y, Dz = lane.CurveD.z
        };
        private static RailResourceSnapshot CreateResourceSnapshot(ulong key, RailEtaScopedLaneRow lane)
        {
            RailLaneId first = new RailLaneId(RailEtaEntityId.Pack(lane.Lane));
            RailLaneId second = new RailLaneId(RailEtaEntityId.Pack(lane.OtherLane));
            var firstApproach = new RailResourceApproachSnapshot { LaneId = first, StartFraction = lane.OverlapThisStart / 255.0, EndFraction = lane.OverlapThisEnd / 255.0, OverlapFlags = lane.OverlapFlags, PriorityDelta = -lane.OverlapPriorityDelta };
            var secondApproach = new RailResourceApproachSnapshot { LaneId = second, StartFraction = lane.OverlapOtherStart / 255.0, EndFraction = lane.OverlapOtherEnd / 255.0, OverlapFlags = lane.OverlapFlags, PriorityDelta = 0 };
            if (first.Value > second.Value)
            {
                RailLaneId swapLane = first; first = second; second = swapLane;
                RailResourceApproachSnapshot swapApproach = firstApproach; firstApproach = secondApproach; secondApproach = swapApproach;
            }
            return new RailResourceSnapshot { ResourceId = new RailResourceId(unchecked((long)key)), LaneIds = new[] { first, second }, Approaches = new[] { firstApproach, secondApproach }, PriorityDelta = lane.OverlapPriorityDelta };
        }
        private static RailEntityIdentity Identity(Entity e) => new RailEntityIdentity { Index = e.Index, Version = e.Version };
        private static int CombineHash(int hash, int value) => unchecked((hash * 397) ^ value);

    }

}
