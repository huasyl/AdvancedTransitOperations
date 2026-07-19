using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Vehicles;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using TrafficLightState = Game.Net.TrafficLightState;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    /// <summary>
    /// Formal worker-local predictor over the request-frame FrozenWorld. It
    /// advances the single authoritative SimulationState in simulation-frame
    /// order and never reads or writes the live ECS world.
    /// </summary>
    internal sealed class RailPredictionSolver
    {
        private const string BuildId = "eta-vanilla-step4-clock-snapshot";
        internal const uint MaximumPredictionFrames = 54613u;
        internal string Version => BuildId;

        internal RailEtaPrediction Predict(RailEtaFrozenWorld world, RailEtaWorldSnapshot snapshot,
            RailEtaRequest request, RailEtaWorkspace workspace, RailEtaCancellation cancellation)
        {
            if (world == null || snapshot == null || request == null || workspace == null)
                return Failure(request, RailEtaFailure.InvalidInput, "null-frozen-input");
            if (!snapshot.ClosureValidated || world.OriginFrame != snapshot.OriginFrame)
                return Failure(request, RailEtaFailure.SnapshotUnstable, "frozen-world-mismatch");
            if (world.RuntimeFacts == null || world.RuntimeFacts.FramesPerMinute <= 0d)
                return Failure(request, RailEtaFailure.InvalidInput, "request-clock-snapshot-missing");
            if (cancellation.IsCancellationRequested)
                return Failure(request, RailEtaFailure.Cancelled, "cancelled");
            if (world.Vehicles.Length > workspace.MaxVehicles)
                return Failure(request, RailEtaFailure.ScopeTruncated, "frozen-vehicle-limit");

            if (!SimulationState.TryCreate(world, out SimulationState tickStart, out string reason))
                return Failure(request, RailEtaFailure.InvalidInput, reason);
            Entity targetController = RailEtaEntityId.ToEntity(request.VehicleId.Value);
            VehicleState target = tickStart.FindVehicle(targetController);
            if (target == null) return Failure(request, RailEtaFailure.TargetGone, "target-vehicle-missing");
            if (request.ExpectedTarget != null && (request.ExpectedTarget.Index != 0 || request.ExpectedTarget.Version != 0)
                && (target.Target.Index != request.ExpectedTarget.Index || target.Target.Version != request.ExpectedTarget.Version))
                return Failure(request, RailEtaFailure.TargetChanged, "target-changed");

            if (!InitializeTicketEndpoint(tickStart.World, target, out reason))
                return Failure(request, RailEtaFailure.PathIncomplete, reason);

            var targetDiagnostics = new TargetSimulationDiagnostics(workspace.MaxTraceEvents);
            SimulationState scratch = tickStart.CreateBuffer();
            for (uint elapsed = 1; elapsed <= MaximumPredictionFrames; elapsed++)
            {
                if (cancellation.IsCancellationRequested)
                    return Failure(request, RailEtaFailure.Cancelled, "cancelled");
                uint frame = unchecked(world.OriginFrame + elapsed);
                ResetLaneReservationsInPlace(tickStart, frame);
                UpdateFrozenGates(tickStart, tickStart, frame);
                if (!PrepareDepartures(tickStart, frame, targetController, out reason))
                    return Failure(request, RailEtaFailure.PathIncomplete, reason);
                if ((frame & 15u) == 3u)
                {
                    scratch.CopyFrom(tickStart);
                    if (!RunNavigationJobBoundary(tickStart, scratch, targetController, frame, out reason))
                        return Failure(request, RailEtaFailure.PathIncomplete, reason);
                    UpdateTrafficSignalsInPlace(scratch, frame);
                    UpdateFrozenGates(tickStart, scratch, frame);
                    VehicleState advancedTarget = scratch.FindVehicle(targetController);
                    if (advancedTarget == null || !advancedTarget.Active)
                        return Failure(request, RailEtaFailure.PathIncomplete, "target-vehicle-simulation-failed");
                    targetDiagnostics.Observe(scratch, advancedTarget, frame);
                    if (advancedTarget.PendingRouteSegmentIndex < 0 && advancedTarget.PathSwitchFrame != frame
                        && IsStoppedAtPathEnd(advancedTarget) && IsTicketEndpointReached(scratch.World, advancedTarget))
                        return Success(snapshot, request, scratch, frame, targetDiagnostics);
                    if (!BeginStoppedBoarding(scratch, frame, targetController, out reason))
                        return Failure(request, RailEtaFailure.PathIncomplete, reason);
                    SimulationState swap = tickStart;
                    tickStart = scratch;
                    scratch = swap;
                }
                else UpdateTrafficSignalsInPlace(tickStart, frame);
            }
            uint timeoutFrame = unchecked(world.OriginFrame + MaximumPredictionFrames);
            return NotConvergedFailure(request, tickStart, targetController, timeoutFrame, targetDiagnostics);
        }

        private sealed class SimulationState
        {
            internal readonly RailEtaFrozenWorld World;
            internal readonly VehicleState[] Vehicles;
            internal readonly List<LaneState> Lanes;
            internal readonly List<OccupancyState> Occupancies;
            internal readonly List<SignalRequest> SignalRequests;
            internal readonly List<SimulationFailure> Failures;
            private readonly Dictionary<Entity, VehicleState> m_VehiclesByController;
            private readonly Dictionary<Entity, UnitState> m_UnitsByEntity;
            private readonly Dictionary<Entity, LaneState> m_LanesByEntity;
            private readonly Dictionary<Entity, bool> m_EntityExists;
            internal int[] SignalLeaders = Array.Empty<int>();
            internal int[] MutableLaneIndices = Array.Empty<int>();
            internal int[][] ReservationBuckets = Array.Empty<int[]>();

            private SimulationState(RailEtaFrozenWorld world, VehicleState[] vehicles, List<LaneState> lanes,
                List<OccupancyState> occupancies)
            {
                World = world;
                Vehicles = vehicles;
                Lanes = lanes;
                Occupancies = occupancies;
                SignalRequests = new List<SignalRequest>();
                Failures = new List<SimulationFailure>();
                m_VehiclesByController = new Dictionary<Entity, VehicleState>(vehicles.Length);
                m_UnitsByEntity = new Dictionary<Entity, UnitState>();
                for (int i = 0; i < vehicles.Length; i++)
                {
                    VehicleState vehicle = vehicles[i];
                    m_VehiclesByController[vehicle.Controller] = vehicle;
                    for (int j = 0; j < vehicle.Units.Length; j++) m_UnitsByEntity[vehicle.Units[j].Entity] = vehicle.Units[j];
                }
                m_LanesByEntity = new Dictionary<Entity, LaneState>(lanes.Count);
                for (int i = 0; i < lanes.Count; i++) m_LanesByEntity[lanes[i].Entity] = lanes[i];
                m_EntityExists = new Dictionary<Entity, bool>();
                for (int i = 0; i < world.NavigationLanes.Length; i++)
                {
                    RailEtaFrozenNavigationLaneRow row = world.NavigationLanes[i];
                    if (row.Lane == Entity.Null) continue;
                    bool exists = row.LaneExists != 0;
                    if (!m_EntityExists.TryGetValue(row.Lane, out bool known) || exists && !known) m_EntityExists[row.Lane] = exists;
                }
                for (int i = 0; i < world.PathElements.Length; i++)
                {
                    RailEtaFrozenPathElementRow row = world.PathElements[i];
                    if (row.Target == Entity.Null) continue;
                    bool exists = row.TargetExists != 0;
                    if (!m_EntityExists.TryGetValue(row.Target, out bool known) || exists && !known) m_EntityExists[row.Target] = exists;
                }
                foreach (Entity entity in m_VehiclesByController.Keys) m_EntityExists[entity] = true;
                foreach (Entity entity in m_UnitsByEntity.Keys) m_EntityExists[entity] = true;
                foreach (Entity entity in m_LanesByEntity.Keys) m_EntityExists[entity] = true;
            }

            internal static bool TryCreate(RailEtaFrozenWorld world, out SimulationState state, out string reason)
            {
                state = null;
                reason = string.Empty;
                if (world.RuntimeFacts == null)
                {
                    reason = "frozen-runtime-gates-missing";
                    return false;
                }
                foreach (KeyValuePair<Entity, RailControlledHoldSnapshot> pair in world.RuntimeFacts.ControlledHolds)
                {
                    RailControlledHoldSnapshot hold = pair.Value;
                    if (hold == null || hold.Kind != RailControlledHoldKind.BypassYield) continue;
                    if (hold.ReleaseVehicleId.Value == 0 || hold.ReleaseLaneId.Value == 0 || hold.ReleaseDirection == 0)
                    {
                        reason = "frozen-rt-atom-release-incomplete";
                        return false;
                    }
                }
                var vehicles = new VehicleState[world.Vehicles.Length];
                for (int i = 0; i < world.Vehicles.Length; i++)
                {
                    RailEtaScopedVehicleRow row = world.Vehicles[i];
                    var units = new List<UnitState>(row.UnitCount);
                    for (int j = 0; j < world.Layout.Length; j++)
                    {
                        RailEtaScopedUnitRow unit = world.Layout[j];
                        if (unit.Controller != row.Controller) continue;
                        if (unit.HasPrefabTrainData == 0) { reason = "frozen-prefab-train-data-missing"; return false; }
                        if (unit.IsTheory == 0 && unit.HasTransform == 0) { reason = "frozen-transform-missing"; return false; }
                        if (unit.IsTheory == 0 && unit.HasMoving == 0) { reason = "frozen-moving-missing"; return false; }
                        if (unit.IsTheory == 0 && unit.HasTrain == 0) { reason = "frozen-train-missing"; return false; }
                        if (unit.IsTheory == 0 && unit.HasNavigation == 0) { reason = "frozen-train-navigation-missing"; return false; }
                        if (unit.IsTheory == 0 && unit.HasCurrentLane == 0) { reason = "frozen-train-current-lane-missing"; return false; }
                        if (unit.LayoutOrdinal == 0 && unit.HasPrefabGeometryData == 0)
                        { reason = "frozen-object-geometry-missing"; return false; }
                        if (unit.CurrentLane.m_Front.m_Lane == Entity.Null
                            || unit.CurrentLane.m_Rear.m_Lane == Entity.Null
                            || unit.CurrentLane.m_FrontCache.m_Lane == Entity.Null
                            || unit.CurrentLane.m_RearCache.m_Lane == Entity.Null)
                        {
                            reason = "frozen-consist-current-lane-missing";
                            return false;
                        }
                        if (!(unit.PrefabTrainData.m_MaxSpeed > 0f)
                            || !(unit.PrefabTrainData.m_Acceleration > 0f)
                            || !(unit.PrefabTrainData.m_Braking > 0f))
                        {
                            reason = "frozen-consist-train-data-missing";
                            return false;
                        }
                        units.Add(new UnitState(unit));
                    }
                    units.Sort((x, y) => x.LayoutOrdinal.CompareTo(y.LayoutOrdinal));
                    if (units.Count == 0 || units.Count != row.UnitCount)
                    {
                        reason = "frozen-layout-incomplete";
                        return false;
                    }

                    var navigationLanes = new List<TrainNavigationLane>();
                    for (int j = 0; j < world.NavigationLanes.Length; j++)
                    {
                        RailEtaFrozenNavigationLaneRow lane = world.NavigationLanes[j];
                        if (lane.Controller != row.Controller) continue;
                        navigationLanes.Add(new TrainNavigationLane
                        {
                            m_Lane = lane.Lane,
                            m_CurvePosition = lane.CurvePosition,
                            m_Flags = (TrainLaneFlags)lane.Flags
                        });
                    }
                    var pathElements = new List<FrozenPathElement>();
                    for (int j = 0; j < world.PathElements.Length; j++)
                    {
                        RailEtaFrozenPathElementRow element = world.PathElements[j];
                        if (element.Controller != row.Controller) continue;
                        pathElements.Add(new FrozenPathElement(element));
                    }
                    pathElements.Sort((x, y) => x.ElementOrdinal.CompareTo(y.ElementOrdinal));
                    navigationLanes.Capacity = math.max(navigationLanes.Capacity, 16);
                    pathElements.Capacity = math.max(pathElements.Capacity, MaxRoutePathLength(world, row.Route));
                    vehicles[i] = new VehicleState(row, units.ToArray(), navigationLanes, pathElements);
                }
                Array.Sort(vehicles, (x, y) => x.ControllerOrdinal.CompareTo(y.ControllerOrdinal));
                HashSet<Entity> relevantControlLanes = BuildRelevantControlLanes(world, vehicles);
                for (int i = 0; i < world.SignalPeers.Length; i++) relevantControlLanes.Add(world.SignalPeers[i].Lane);
                var lanes = new List<LaneState>();
                var laneEntities = new HashSet<Entity>();
                for (int i = 0; i < world.Lanes.Length; i++)
                {
                    RailEtaScopedLaneRow row = world.Lanes[i];
                    if (row.Source != 6 || !relevantControlLanes.Contains(row.Lane) || !laneEntities.Add(row.Lane)) continue;
                    lanes.Add(new LaneState(row, row.LaneOrdinal));
                }
                int fallbackOrdinal = lanes.Count;
                for (int i = 0; i < world.Lanes.Length; i++)
                {
                    RailEtaScopedLaneRow row = world.Lanes[i];
                    if (row.Source == 3 || !relevantControlLanes.Contains(row.Lane) || !laneEntities.Add(row.Lane)) continue;
                    lanes.Add(new LaneState(row, fallbackOrdinal++));
                }
                for (int i = 0; i < world.SignalPeers.Length; i++)
                {
                    RailEtaSignalPeerRow peer = world.SignalPeers[i];
                    if (peer.Lane == Entity.Null || !relevantControlLanes.Contains(peer.Lane) || !laneEntities.Add(peer.Lane)) continue;
                    LaneSignal signal = peer.Signal;
                    if (!IsScopedSimulationEntity(vehicles, signal.m_Petitioner))
                    {
                        signal.m_Petitioner = Entity.Null;
                        signal.m_Priority = signal.m_Default;
                    }
                    if (!IsScopedSimulationEntity(vehicles, signal.m_Blocker)) signal.m_Blocker = Entity.Null;
                    lanes.Add(new LaneState(peer, signal, fallbackOrdinal++));
                }
                for (int i = 0; i < lanes.Count; i++) lanes[i].ControlRelevant = relevantControlLanes.Contains(lanes[i].Entity);
                AssignOverlapFacts(lanes, world.Lanes);
                for (int i = 0; i < lanes.Count; i++)
                {
                    LaneState lane = lanes[i];
                    if (!lane.ControlRelevant || lane.HasSignal == 0) continue;
                    if (lane.SignalController == Entity.Null) { reason = "frozen-lane-signal-controller-missing"; return false; }
                    if (lane.HasSignalUpdateFrame == 0) { reason = "frozen-lane-signal-update-frame-missing"; return false; }
                }
                AssignSignalPeers(lanes);
                var occupancies = new List<OccupancyState>(math.max(world.Occupancies.Length, world.Layout.Length * 2));
                for (int i = 0; i < world.Occupancies.Length; i++)
                {
                    RailEtaLaneOccupancyRow row = world.Occupancies[i];
                    Entity occupant = row.Unit != Entity.Null ? row.Unit : row.Vehicle;
                    bool duplicate = false;
                    for (int j = 0; j < occupancies.Count; j++)
                    {
                        OccupancyState existing = occupancies[j];
                        if (existing.Lane != row.Lane || existing.Vehicle != occupant) continue;
                        existing.CurvePosition = new float2(row.Start, row.End);
                        occupancies[j] = existing;
                        duplicate = true;
                        break;
                    }
                    if (!duplicate) occupancies.Add(new OccupancyState(row));
                }
                state = new SimulationState(world, vehicles, lanes, occupancies);
                state.BuildLaneControlIndices();
                RebuildCurrentReservationClaimOwners(state);
                for (int i = 0; i < state.Vehicles.Length; i++)
                    if (state.Vehicles[i].Boarding)
                    {
                        VehicleState vehicle = state.Vehicles[i];
                        vehicle.DwellDeadlineFrame = unchecked(world.OriginFrame + DwellFrames(state, vehicle));
                        uint departureDelta = unchecked(vehicle.DepartureFrame - world.OriginFrame);
                        if (departureDelta < 0x80000000u && departureDelta >= 6000u)
                            vehicle.DepartureFrame = vehicle.DwellDeadlineFrame;
                    }
                return true;
            }

            internal SimulationState CreateBuffer()
            {
                var vehicles = new VehicleState[Vehicles.Length];
                for (int i = 0; i < vehicles.Length; i++) vehicles[i] = Vehicles[i].CreateBuffer();
                var lanes = new List<LaneState>(Lanes.Count);
                for (int i = 0; i < Lanes.Count; i++) lanes.Add(Lanes[i].CreateBuffer());
                var occupancies = new List<OccupancyState>(math.max(Occupancies.Capacity, World.Layout.Length * 2));
                var result = new SimulationState(World, vehicles, lanes, occupancies);
                result.SignalLeaders = SignalLeaders;
                result.MutableLaneIndices = MutableLaneIndices;
                result.ReservationBuckets = ReservationBuckets;
                result.SignalRequests.Capacity = math.max(result.SignalRequests.Capacity, Lanes.Count);
                result.Failures.Capacity = math.max(result.Failures.Capacity, Vehicles.Length);
                result.CopyFrom(this);
                return result;
            }

            internal void CopyFrom(SimulationState source)
            {
                for (int i = 0; i < Vehicles.Length; i++) Vehicles[i].CopyFrom(source.Vehicles[i]);
                for (int i = 0; i < MutableLaneIndices.Length; i++)
                {
                    int laneIndex = MutableLaneIndices[i];
                    Lanes[laneIndex].CopyFrom(source.Lanes[laneIndex]);
                }
                Occupancies.Clear();
                Occupancies.AddRange(source.Occupancies);
                SignalRequests.Clear();
                Failures.Clear();
                Failures.AddRange(source.Failures);
            }

            private void BuildLaneControlIndices()
            {
                var mutable = new List<int>();
                var leaders = new List<int>();
                var buckets = new List<int>[16];
                for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();
                for (int i = 0; i < Lanes.Count; i++)
                {
                    LaneState lane = Lanes[i];
                    if (lane.ControlRelevant && (lane.HasReservation != 0 || lane.HasSignal != 0)) mutable.Add(i);
                    if (lane.SignalPeers.Length != 0) leaders.Add(i);
                    if (lane.ControlRelevant && lane.HasReservation != 0 && lane.HasUpdateFrame != 0 && lane.UpdateFrameIndex < 16u)
                        buckets[lane.UpdateFrameIndex].Add(i);
                }
                MutableLaneIndices = mutable.ToArray();
                SignalLeaders = leaders.ToArray();
                ReservationBuckets = new int[16][];
                for (int i = 0; i < ReservationBuckets.Length; i++) ReservationBuckets[i] = buckets[i].ToArray();
            }

            internal VehicleState FindVehicle(Entity controller)
            {
                m_VehiclesByController.TryGetValue(controller, out VehicleState vehicle);
                return vehicle;
            }

            internal UnitState FindUnit(Entity entity)
            {
                m_UnitsByEntity.TryGetValue(entity, out UnitState unit);
                return unit;
            }

            internal UnitState FindOccupant(Entity entity)
            {
                UnitState unit = FindUnit(entity);
                if (unit != null) return unit;
                VehicleState vehicle = FindVehicle(entity);
                return vehicle?.Units[0];
            }

            internal bool IsSameController(Entity entity, Entity controller)
            {
                if (entity == controller) return true;
                UnitState unit = FindUnit(entity);
                return unit != null && unit.Controller == controller;
            }

            internal Entity ResolveController(Entity entity)
            {
                UnitState unit = FindUnit(entity);
                return unit?.Controller ?? entity;
            }

            internal bool TryGetLane(Entity lane, out RailEtaScopedLaneRow result)
            {
                LaneState state = FindLane(lane);
                if (state != null) { result = state.Snapshot(); return true; }
                result = default;
                return false;
            }

            internal LaneState FindLane(Entity lane)
            {
                m_LanesByEntity.TryGetValue(lane, out LaneState state);
                return state;
            }

            internal bool EntityExists(Entity entity)
            {
                if (entity == Entity.Null) return false;
                return m_EntityExists.TryGetValue(entity, out bool exists) && exists;
            }

            private static void AssignSignalPeers(List<LaneState> lanes)
            {
                var groups = new Dictionary<Entity, List<int>>();
                for (int i = 0; i < lanes.Count; i++)
                {
                    LaneState lane = lanes[i];
                    if (!lane.ControlRelevant || lane.HasSignal == 0 || lane.SignalController == Entity.Null) continue;
                    if (!groups.TryGetValue(lane.SignalController, out List<int> peers))
                        groups.Add(lane.SignalController, peers = new List<int>());
                    peers.Add(i);
                }
                foreach (KeyValuePair<Entity, List<int>> pair in groups)
                    lanes[pair.Value[0]].SignalPeers = pair.Value.ToArray();
            }

            private static void AssignOverlapFacts(List<LaneState> lanes, RailEtaScopedLaneRow[] facts)
            {
                var groups = new Dictionary<Entity, List<RailEtaScopedLaneRow>>();
                for (int i = 0; i < facts.Length; i++)
                {
                    RailEtaScopedLaneRow fact = facts[i];
                    if (fact.Source != 3 || fact.Lane == Entity.Null) continue;
                    if (!groups.TryGetValue(fact.Lane, out List<RailEtaScopedLaneRow> overlaps))
                        groups.Add(fact.Lane, overlaps = new List<RailEtaScopedLaneRow>());
                    overlaps.Add(fact);
                }
                for (int i = 0; i < lanes.Count; i++)
                    if (groups.TryGetValue(lanes[i].Entity, out List<RailEtaScopedLaneRow> overlaps))
                        lanes[i].Overlaps = overlaps.ToArray();
            }

            private static HashSet<Entity> BuildRelevantControlLanes(RailEtaFrozenWorld world, VehicleState[] vehicles)
            {
                var result = new HashSet<Entity>();
                for (int i = 0; i < vehicles.Length; i++)
                {
                    VehicleState vehicle = vehicles[i];
                    for (int j = 0; j < vehicle.Units.Length; j++)
                    {
                        TrainCurrentLane current = vehicle.Units[j].CurrentLane;
                        result.Add(current.m_Front.m_Lane);
                        result.Add(current.m_FrontCache.m_Lane);
                        result.Add(current.m_Rear.m_Lane);
                        result.Add(current.m_RearCache.m_Lane);
                    }
                    for (int j = 0; j < vehicle.NavigationLanes.Count; j++) result.Add(vehicle.NavigationLanes[j].m_Lane);
                    for (int j = 0; j < vehicle.PathElements.Count; j++) result.Add(vehicle.PathElements[j].Target);
                }
                for (int i = 0; i < world.RoutePaths.Length; i++) result.Add(world.RoutePaths[i].Lane);
                var overlapTargets = new Dictionary<Entity, List<Entity>>();
                for (int i = 0; i < world.Lanes.Length; i++)
                {
                    RailEtaScopedLaneRow row = world.Lanes[i];
                    if (row.Source != 3 || row.Lane == Entity.Null || row.OtherLane == Entity.Null) continue;
                    if (!overlapTargets.TryGetValue(row.Lane, out List<Entity> targets))
                        overlapTargets.Add(row.Lane, targets = new List<Entity>());
                    targets.Add(row.OtherLane);
                }
                result.Remove(Entity.Null);
                var queue = new Queue<Entity>(result);
                while (queue.Count != 0)
                {
                    Entity lane = queue.Dequeue();
                    if (!overlapTargets.TryGetValue(lane, out List<Entity> targets)) continue;
                    for (int i = 0; i < targets.Count; i++) if (result.Add(targets[i])) queue.Enqueue(targets[i]);
                }
                return result;
            }

            private static bool IsScopedSimulationEntity(VehicleState[] vehicles, Entity entity)
            {
                if (entity == Entity.Null) return false;
                for (int i = 0; i < vehicles.Length; i++)
                {
                    if (vehicles[i].Controller == entity) return true;
                    for (int j = 0; j < vehicles[i].Units.Length; j++) if (vehicles[i].Units[j].Entity == entity) return true;
                }
                return false;
            }

            internal void RequestSignal(Entity petitioner, Entity lane, int priority) =>
                SignalRequests.Add(new SignalRequest(petitioner, lane, priority));
        }

        private readonly struct SimulationFailure
        {
            internal readonly Entity Vehicle;
            internal readonly uint Frame;
            internal readonly string Reason;

            internal SimulationFailure(Entity vehicle, uint frame, string reason)
            {
                Vehicle = vehicle;
                Frame = frame;
                Reason = reason ?? string.Empty;
            }
        }

        private sealed class TargetSimulationDiagnostics
        {
            private readonly Dictionary<BlockerType, uint> m_TypeTicks = new Dictionary<BlockerType, uint>();
            private readonly List<TargetBlockerAggregate> m_Blockers = new List<TargetBlockerAggregate>();
            private readonly List<RailEtaTraceEvent> m_Trace;
            private readonly int m_MaxTraceEvents;
            private uint m_NavigationTicks;
            private uint m_HalfReservationTicks;
            private float m_SpeedSum;
            private float m_MaxSpeed;
            private RailEtaTraceEvent m_ActiveTrace;
            private uint m_LastFrame;
            private int m_EventCount;
            private bool m_TraceTruncated;

            internal TargetSimulationDiagnostics(int maxTraceEvents)
            {
                m_MaxTraceEvents = math.max(0, maxTraceEvents);
                m_Trace = new List<RailEtaTraceEvent>(math.min(64, m_MaxTraceEvents));
            }

            internal void Observe(SimulationState state, VehicleState vehicle, uint frame)
            {
                m_NavigationTicks++;
                float speed = vehicle.Units[0].Navigation.m_Speed;
                m_SpeedSum += speed;
                m_MaxSpeed = math.max(m_MaxSpeed, speed);
                TrainCurrentLane currentLane = vehicle.Units[0].CurrentLane;
                LaneState currentLaneState = state.FindLane(currentLane.m_Front.m_Lane);
                if ((currentLane.m_Front.m_LaneFlags & TrainLaneFlags.Exclusive) == 0
                    && currentLaneState != null && currentLaneState.HasReservation != 0
                    && currentLaneState.Reservation.GetPriority() == 102)
                    m_HalfReservationTicks++;
                BlockerType type = vehicle.Blocker.m_Type;
                m_TypeTicks.TryGetValue(type, out uint typeTicks);
                m_TypeTicks[type] = typeTicks + 1u;
                ObserveTrace(state, vehicle, frame);
                if (vehicle.Blocker.m_Blocker == Entity.Null) return;
                Entity controller = state.ResolveController(vehicle.Blocker.m_Blocker);
                for (int i = 0; i < m_Blockers.Count; i++)
                {
                    TargetBlockerAggregate aggregate = m_Blockers[i];
                    if (aggregate.Controller != controller || aggregate.Type != type) continue;
                    aggregate.Ticks++;
                    m_Blockers[i] = aggregate;
                    return;
                }
                m_Blockers.Add(new TargetBlockerAggregate(controller, type));
            }

            internal void AppendTo(List<RailEtaDiagnosticRecord> diagnostics, uint frame)
            {
                if (diagnostics.Count >= RailEtaLimits.MaxDiagnostics) return;
                diagnostics.Add(new RailEtaDiagnosticRecord
                {
                    Code = "target-simulation-blocker-summary",
                    Severity = RailEtaDiagnosticSeverity.Info,
                    Message = "navigationFrames=" + m_NavigationTicks * 16u
                        + " none=" + Frames(BlockerType.None)
                        + " limit=" + Frames(BlockerType.Limit)
                        + " continuing=" + Frames(BlockerType.Continuing)
                        + " crossing=" + Frames(BlockerType.Crossing)
                        + " signal=" + Frames(BlockerType.Signal)
                        + " temporary=" + Frames(BlockerType.Temporary)
                        + " averageSpeed=" + (m_NavigationTicks == 0 ? 0f : m_SpeedSum / m_NavigationTicks).ToString("F2")
                        + " maxSpeed=" + m_MaxSpeed.ToString("F2")
                        + " halfReservationFrames=" + m_HalfReservationTicks * 16u,
                    Frame = frame
                });
                m_Blockers.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
                int count = math.min(3, m_Blockers.Count);
                for (int i = 0; i < count && diagnostics.Count < RailEtaLimits.MaxDiagnostics; i++)
                {
                    TargetBlockerAggregate aggregate = m_Blockers[i];
                    diagnostics.Add(new RailEtaDiagnosticRecord
                    {
                        Code = "target-simulation-blocker-vehicle",
                        Severity = RailEtaDiagnosticSeverity.Info,
                        Message = "vehicle=" + aggregate.Controller.Index + ":" + aggregate.Controller.Version
                            + " type=" + aggregate.Type + " frames=" + aggregate.Ticks * 16u,
                        Frame = frame
                    });
                }
            }

            internal void AppendFinalBlockerState(SimulationState state,
                List<RailEtaDiagnosticRecord> diagnostics, uint frame)
            {
                if (diagnostics.Count >= RailEtaLimits.MaxDiagnostics || m_Blockers.Count == 0) return;
                m_Blockers.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
                Entity controller = m_Blockers[0].Controller;
                VehicleState blocker = state.FindVehicle(controller);
                if (blocker == null || !blocker.Active || blocker.Units.Length == 0)
                {
                    diagnostics.Add(new RailEtaDiagnosticRecord
                    {
                        Code = "blocker-simulation-final-state",
                        Severity = RailEtaDiagnosticSeverity.Warning,
                        Message = "vehicle=" + controller.Index + ":" + controller.Version + " active=False",
                        Frame = frame,
                        VehicleId = new RailVehicleId(RailEtaEntityId.Pack(controller))
                    });
                    return;
                }
                TrainCurrentLane lane = blocker.Units[0].CurrentLane;
                diagnostics.Add(new RailEtaDiagnosticRecord
                {
                    Code = "blocker-simulation-final-state",
                    Severity = RailEtaDiagnosticSeverity.Warning,
                    Message = "vehicle=" + controller.Index + ":" + controller.Version
                        + " lane=" + lane.m_Front.m_Lane.Index + ":" + lane.m_Front.m_Lane.Version
                        + " pos=" + lane.m_Front.m_CurvePosition.y.ToString("F4")
                        + " speed=" + blocker.Units[0].Navigation.m_Speed.ToString("F3")
                        + " flags=" + (uint)lane.m_Front.m_LaneFlags
                        + " target=" + blocker.Target.Index + ":" + blocker.Target.Version
                        + " reached=" + blocker.TargetReached
                        + " boarding=" + blocker.Boarding + " holdReleased=" + blocker.HoldReleased
                        + " dep=" + blocker.DepartureFrame + " dwell=" + blocker.DwellDeadlineFrame
                        + " pathIndex=" + blocker.PathOwner.m_ElementIndex + " pathState=" + (uint)blocker.PathOwner.m_State
                        + " nav=" + blocker.NavigationLanes.Count + " path=" + blocker.PathElements.Count
                        + " pending=" + blocker.PendingRouteSegmentIndex + " ready=" + blocker.PendingRouteReadyFrame
                        + " blocker=" + blocker.Blocker.m_Blocker.Index + ":" + blocker.Blocker.m_Blocker.Version
                        + " blockerType=" + blocker.Blocker.m_Type + " blockerSource=" + blocker.BlockerSource,
                    Frame = frame,
                    VehicleId = new RailVehicleId(RailEtaEntityId.Pack(controller))
                });
            }

            internal void Finish(uint frame)
            {
                CloseTrace(frame);
            }

            internal RailEtaTraceEvent[] Trace => m_Trace.ToArray();
            internal bool TraceTruncated => m_TraceTruncated;
            internal int EventCount => m_EventCount;

            private void ObserveTrace(SimulationState state, VehicleState vehicle, uint frame)
            {
                BlockerType type = vehicle.Blocker.m_Type;
                bool blocked = type == BlockerType.Continuing || type == BlockerType.Crossing
                    || type == BlockerType.Signal || type == BlockerType.Temporary;
                if (!blocked)
                {
                    CloseTrace(m_LastFrame == 0u ? frame : m_LastFrame);
                    m_LastFrame = frame;
                    return;
                }
                RailEtaBlockerEvidence evidence = BuildEvidence(state, vehicle);
                Entity controller = state.ResolveController(vehicle.Blocker.m_Blocker);
                RailVehicleId other = new RailVehicleId(RailEtaEntityId.Pack(controller));
                string kind = vehicle.BlockerSource == 3 || vehicle.BlockerSource == 4
                    ? "reservation" : type == BlockerType.Signal ? "signal" : type == BlockerType.Temporary ? "temporary" : "following";
                string reason = SourceReason(vehicle.BlockerSource, type);
                bool same = m_ActiveTrace != null
                    && m_ActiveTrace.Kind == kind
                    && m_ActiveTrace.ReasonCode == reason
                    && m_ActiveTrace.OtherVehicleId.Value == other.Value
                    && m_ActiveTrace.StartEvidence != null
                    && m_ActiveTrace.StartEvidence.CheckedLaneId.Value == evidence.CheckedLaneId.Value
                    && m_ActiveTrace.StartEvidence.OtherLaneId.Value == evidence.OtherLaneId.Value;
                if (!same)
                {
                    CloseTrace(m_LastFrame == 0u ? frame : m_LastFrame);
                    m_ActiveTrace = new RailEtaTraceEvent
                    {
                        Kind = kind,
                        VehicleId = new RailVehicleId(RailEtaEntityId.Pack(vehicle.Controller)),
                        OtherVehicleId = other,
                        StartFrame = frame,
                        EndFrame = frame,
                        ReasonCode = reason,
                        StartEvidence = evidence,
                        EndEvidence = evidence
                    };
                }
                else
                {
                    m_ActiveTrace.EndFrame = frame;
                    m_ActiveTrace.EndEvidence = evidence;
                }
                m_LastFrame = frame;
            }

            private void CloseTrace(uint frame)
            {
                if (m_ActiveTrace == null) return;
                m_ActiveTrace.EndFrame = math.max(m_ActiveTrace.StartFrame, frame);
                m_ActiveTrace.DelayFrames = unchecked(m_ActiveTrace.EndFrame - m_ActiveTrace.StartFrame + 16u);
                m_EventCount++;
                if (m_Trace.Count < m_MaxTraceEvents)
                {
                    m_ActiveTrace.Sequence = m_Trace.Count;
                    m_Trace.Add(m_ActiveTrace);
                }
                else m_TraceTruncated = true;
                m_ActiveTrace = null;
            }

            private static RailEtaBlockerEvidence BuildEvidence(SimulationState state, VehicleState target)
            {
                BlockerEvidenceState source = target.BlockerEvidence;
                UnitState blockerUnit = state.FindOccupant(source.BlockerEntity);
                VehicleState blockerVehicle = state.FindVehicle(state.ResolveController(source.BlockerEntity));
                TrainCurrentLane targetLane = target.Units[0].CurrentLane;
                TrainCurrentLane blockerLane = blockerUnit?.CurrentLane ?? default;
                return new RailEtaBlockerEvidence
                {
                    Source = source.Source,
                    BlockerEntityId = (long)RailEtaEntityId.Pack(source.BlockerEntity),
                    TargetLaneId = new RailLaneId(RailEtaEntityId.Pack(targetLane.m_Front.m_Lane)),
                    TargetPosition = targetLane.m_Front.m_CurvePosition.y,
                    CheckedLaneId = new RailLaneId(RailEtaEntityId.Pack(source.CheckedLane)),
                    OtherLaneId = new RailLaneId(RailEtaEntityId.Pack(source.OtherLane)),
                    BlockerFrontLaneId = new RailLaneId(RailEtaEntityId.Pack(blockerLane.m_Front.m_Lane)),
                    BlockerFrontPosition = blockerLane.m_Front.m_CurvePosition.y,
                    BlockerRearLaneId = new RailLaneId(RailEtaEntityId.Pack(blockerLane.m_Rear.m_Lane)),
                    BlockerRearPosition = blockerLane.m_Rear.m_CurvePosition.y,
                    BlockerTargetId = new RailCheckpointId(RailEtaEntityId.Pack(blockerVehicle?.Target ?? Entity.Null)),
                    BlockerBoarding = blockerVehicle != null && blockerVehicle.Boarding,
                    OccupancyStart = source.Occupancy.x,
                    OccupancyEnd = source.Occupancy.y,
                    ReservationPriority = source.ReservationPriority,
                    ReservationOffset = source.ReservationOffset,
                    OverlapFlags = source.OverlapFlags,
                    OverlapThisStart = source.OverlapOffsets.x,
                    OverlapThisEnd = source.OverlapOffsets.y,
                    OverlapOtherStart = source.OverlapOffsets.z,
                    OverlapOtherEnd = source.OverlapOffsets.w,
                    PriorityDelta = source.PriorityDelta,
                    Parallelism = source.Parallelism,
                    Distance = source.Distance,
                    DistanceFactor = source.DistanceFactor,
                    DistanceOffset = source.DistanceOffset,
                    SpeedBefore = source.SpeedBefore,
                    LimitedSpeed = source.LimitedSpeed
                };
            }

            private static string SourceReason(byte source, BlockerType type)
            {
                if (source == 1) return "current-lane-occupancy";
                if (source == 2) return "overlap-occupancy";
                if (source == 3) return "overlap-reservation";
                if (source == 4) return "target-reservation";
                return type == BlockerType.Signal ? "signal" : type == BlockerType.Temporary ? "temporary" : "unknown";
            }

            private uint Frames(BlockerType type) =>
                m_TypeTicks.TryGetValue(type, out uint ticks) ? ticks * 16u : 0u;
        }

        private struct TargetBlockerAggregate
        {
            internal readonly Entity Controller;
            internal readonly BlockerType Type;
            internal uint Ticks;

            internal TargetBlockerAggregate(Entity controller, BlockerType type)
            {
                Controller = controller;
                Type = type;
                Ticks = 1u;
            }
        }

        private struct BlockerEvidenceState
        {
            internal byte Source;
            internal Entity BlockerEntity;
            internal Entity CheckedLane;
            internal Entity OtherLane;
            internal float2 Occupancy;
            internal int ReservationPriority;
            internal float ReservationOffset;
            internal ushort OverlapFlags;
            internal float4 OverlapOffsets;
            internal sbyte PriorityDelta;
            internal float Parallelism;
            internal float Distance;
            internal float DistanceFactor;
            internal float DistanceOffset;
            internal float SpeedBefore;
            internal float LimitedSpeed;
        }

        private sealed class LaneState
        {
            internal readonly int Ordinal;
            internal readonly Entity Entity;
            internal readonly RailEtaScopedLaneRow Frozen;
            internal readonly byte HasReservation;
            internal readonly byte HasSignal;
            internal readonly byte HasUpdateFrame;
            internal readonly uint UpdateFrameIndex;
            internal LaneReservation Reservation;
            internal LaneSignal Signal;
            internal Entity PreviousClaimOwner;
            internal Entity NextClaimOwner;
            internal readonly Entity SignalController;
            internal readonly uint SignalUpdateFrameIndex;
            internal readonly byte HasSignalUpdateFrame;
            internal TrafficLights TrafficLights;
            internal int[] SignalPeers = Array.Empty<int>();
            internal bool ControlRelevant;
            internal RailEtaScopedLaneRow[] Overlaps = Array.Empty<RailEtaScopedLaneRow>();

            internal LaneState(RailEtaScopedLaneRow row, int ordinal)
            {
                Ordinal = ordinal;
                Entity = row.Lane;
                Frozen = row;
                HasReservation = row.HasReservation;
                HasSignal = row.HasSignal;
                HasUpdateFrame = row.HasUpdateFrame;
                UpdateFrameIndex = row.UpdateFrameIndex;
                Reservation = row.Reservation;
                Signal = row.Signal;
                Reservation.m_Blocker = row.ReservationBlocker;
                PreviousClaimOwner = Entity.Null;
                NextClaimOwner = Entity.Null;
                SignalController = row.SignalController;
                SignalUpdateFrameIndex = row.SignalUpdateFrameIndex;
                HasSignalUpdateFrame = row.HasSignalUpdateFrame;
                TrafficLights = row.TrafficLights;
                Signal.m_Petitioner = row.SignalPetitioner;
                Signal.m_Blocker = row.SignalBlocker;
            }

            internal LaneState(RailEtaSignalPeerRow peer, LaneSignal signal, int ordinal)
                : this(new RailEtaScopedLaneRow
                {
                    Lane = peer.Lane,
                    HasSignal = 1,
                    SignalController = peer.Controller,
                    SignalUpdateFrameIndex = peer.UpdateFrameIndex,
                    HasSignalUpdateFrame = 1,
                    TrafficLights = peer.TrafficLights,
                    Signal = signal,
                    SignalPetitioner = signal.m_Petitioner,
                    SignalBlocker = signal.m_Blocker,
                    SignalPriority = signal.m_Priority,
                    SignalDefault = signal.m_Default,
                    SignalType = (byte)signal.m_Signal,
                    SignalGroupMask = signal.m_GroupMask,
                    SignalFlags = (byte)signal.m_Flags
                }, ordinal)
            {
            }

            private LaneState(LaneState source)
            {
                Ordinal = source.Ordinal;
                Entity = source.Entity;
                Frozen = source.Frozen;
                HasReservation = source.HasReservation;
                HasSignal = source.HasSignal;
                HasUpdateFrame = source.HasUpdateFrame;
                UpdateFrameIndex = source.UpdateFrameIndex;
                Reservation = source.Reservation;
                Signal = source.Signal;
                PreviousClaimOwner = source.PreviousClaimOwner;
                NextClaimOwner = source.NextClaimOwner;
                SignalController = source.SignalController;
                SignalUpdateFrameIndex = source.SignalUpdateFrameIndex;
                HasSignalUpdateFrame = source.HasSignalUpdateFrame;
                TrafficLights = source.TrafficLights;
                SignalPeers = source.SignalPeers;
                ControlRelevant = source.ControlRelevant;
                Overlaps = source.Overlaps;
            }

            internal LaneState CreateBuffer() => new LaneState(this);

            internal void CopyFrom(LaneState source)
            {
                Reservation = source.Reservation;
                Signal = source.Signal;
                PreviousClaimOwner = source.PreviousClaimOwner;
                NextClaimOwner = source.NextClaimOwner;
                TrafficLights = source.TrafficLights;
            }

            internal RailEtaScopedLaneRow Snapshot()
            {
                RailEtaScopedLaneRow row = Frozen;
                row.Reservation = Reservation;
                row.ReservationBlocker = Reservation.m_Blocker;
                row.PreviousPriority = Reservation.m_Prev.m_Priority;
                row.PreviousOffset = Reservation.m_Prev.m_Offset;
                row.NextPriority = Reservation.m_Next.m_Priority;
                row.NextOffset = Reservation.m_Next.m_Offset;
                row.Signal = Signal;
                row.SignalPetitioner = Signal.m_Petitioner;
                row.SignalBlocker = Signal.m_Blocker;
                row.SignalPriority = Signal.m_Priority;
                return row;
            }
        }

        private struct OccupancyState
        {
            internal Entity Lane;
            internal readonly Entity Vehicle;
            internal float2 CurvePosition;

            internal OccupancyState(RailEtaLaneOccupancyRow row)
            {
                Lane = row.Lane;
                Vehicle = row.Unit != Entity.Null ? row.Unit : row.Vehicle;
                CurvePosition = new float2(row.Start, row.End);
            }

        }

        // Direct managed equivalent of TrainNavigationHelpers.CurrentLaneCache
        // plus LaneObjectCommandBuffer playback against the advancing truth.
        private readonly struct CurrentLaneCache
        {
            private readonly Entity m_WasCurrentLane1;
            private readonly Entity m_WasCurrentLane2;
            private readonly float2 m_WasCurvePosition1;
            private readonly float2 m_WasCurvePosition2;

            internal CurrentLaneCache(SimulationState state, ref TrainCurrentLane currentLane)
            {
                if (currentLane.m_Front.m_Lane != Entity.Null && state.FindLane(currentLane.m_Front.m_Lane) == null)
                    currentLane.m_Front.m_Lane = Entity.Null;
                if (currentLane.m_Rear.m_Lane != Entity.Null && state.FindLane(currentLane.m_Rear.m_Lane) == null)
                    currentLane.m_Rear.m_Lane = Entity.Null;
                if (currentLane.m_FrontCache.m_Lane != Entity.Null && state.FindLane(currentLane.m_FrontCache.m_Lane) == null)
                    currentLane.m_FrontCache.m_Lane = Entity.Null;
                if (currentLane.m_RearCache.m_Lane != Entity.Null && state.FindLane(currentLane.m_RearCache.m_Lane) == null)
                    currentLane.m_RearCache.m_Lane = Entity.Null;
                m_WasCurrentLane1 = currentLane.m_Front.m_Lane;
                m_WasCurrentLane2 = currentLane.m_Rear.m_Lane;
                GetCurvePositions(ref currentLane, out m_WasCurvePosition1, out m_WasCurvePosition2);
            }

            internal void CheckChanges(SimulationState state, Entity entity, TrainCurrentLane currentLane)
            {
                GetCurvePositions(ref currentLane, out float2 position1, out float2 position2);
                if (currentLane.m_Rear.m_Lane == m_WasCurrentLane1)
                {
                    if (currentLane.m_Front.m_Lane == m_WasCurrentLane2)
                    {
                        if (currentLane.m_Front.m_Lane != Entity.Null && !m_WasCurvePosition2.Equals(position1))
                            UpdateOccupancy(state, currentLane.m_Front.m_Lane, entity, position1);
                        if (currentLane.m_Rear.m_Lane != currentLane.m_Front.m_Lane
                            && currentLane.m_Rear.m_Lane != Entity.Null && !m_WasCurvePosition1.Equals(position2))
                            UpdateOccupancy(state, currentLane.m_Rear.m_Lane, entity, position2);
                        return;
                    }
                    if (currentLane.m_Rear.m_Lane != m_WasCurrentLane2 && m_WasCurrentLane2 != Entity.Null)
                        RemoveOccupancy(state, m_WasCurrentLane2, entity);
                    if (currentLane.m_Rear.m_Lane != Entity.Null && !m_WasCurvePosition1.Equals(position2))
                        UpdateOccupancy(state, currentLane.m_Rear.m_Lane, entity, position2);
                    if (currentLane.m_Front.m_Lane != m_WasCurrentLane1 && currentLane.m_Front.m_Lane != Entity.Null)
                        AddOccupancy(state, currentLane.m_Front.m_Lane, entity, position1);
                    return;
                }
                if (currentLane.m_Front.m_Lane == m_WasCurrentLane2)
                {
                    if (currentLane.m_Front.m_Lane != m_WasCurrentLane1 && m_WasCurrentLane1 != Entity.Null)
                        RemoveOccupancy(state, m_WasCurrentLane1, entity);
                    if (currentLane.m_Front.m_Lane != Entity.Null && !m_WasCurvePosition2.Equals(position1))
                        UpdateOccupancy(state, currentLane.m_Front.m_Lane, entity, position1);
                    if (currentLane.m_Rear.m_Lane != m_WasCurrentLane2 && currentLane.m_Rear.m_Lane != Entity.Null)
                        AddOccupancy(state, currentLane.m_Rear.m_Lane, entity, position2);
                    return;
                }
                if (m_WasCurrentLane1 == m_WasCurrentLane2)
                {
                    if (m_WasCurrentLane1 != Entity.Null) RemoveOccupancy(state, m_WasCurrentLane1, entity);
                    if (currentLane.m_Front.m_Lane != Entity.Null) AddOccupancy(state, currentLane.m_Front.m_Lane, entity, position1);
                    if (currentLane.m_Rear.m_Lane != currentLane.m_Front.m_Lane && currentLane.m_Rear.m_Lane != Entity.Null)
                        AddOccupancy(state, currentLane.m_Rear.m_Lane, entity, position2);
                    return;
                }
                if (currentLane.m_Front.m_Lane == currentLane.m_Rear.m_Lane)
                {
                    if (m_WasCurrentLane1 != Entity.Null) RemoveOccupancy(state, m_WasCurrentLane1, entity);
                    if (m_WasCurrentLane2 != Entity.Null) RemoveOccupancy(state, m_WasCurrentLane2, entity);
                    if (currentLane.m_Front.m_Lane != Entity.Null) AddOccupancy(state, currentLane.m_Front.m_Lane, entity, position1);
                    return;
                }
                if (currentLane.m_Front.m_Lane != m_WasCurrentLane1)
                {
                    if (m_WasCurrentLane1 != Entity.Null) RemoveOccupancy(state, m_WasCurrentLane1, entity);
                    if (currentLane.m_Front.m_Lane != Entity.Null) AddOccupancy(state, currentLane.m_Front.m_Lane, entity, position1);
                }
                else if (m_WasCurrentLane1 != Entity.Null && !m_WasCurvePosition1.Equals(position1))
                    UpdateOccupancy(state, m_WasCurrentLane1, entity, position1);
                if (currentLane.m_Rear.m_Lane != m_WasCurrentLane2)
                {
                    if (m_WasCurrentLane2 != Entity.Null) RemoveOccupancy(state, m_WasCurrentLane2, entity);
                    if (currentLane.m_Rear.m_Lane != Entity.Null) AddOccupancy(state, currentLane.m_Rear.m_Lane, entity, position2);
                }
                else if (m_WasCurrentLane2 != Entity.Null && !m_WasCurvePosition2.Equals(position2))
                    UpdateOccupancy(state, m_WasCurrentLane2, entity, position2);
            }
        }

        private static void GetCurvePositions(ref TrainCurrentLane currentLane, out float2 position1, out float2 position2)
        {
            position1 = currentLane.m_Front.m_CurvePosition.yz;
            position2 = currentLane.m_Rear.m_CurvePosition.yz;
            if (currentLane.m_Front.m_Lane == currentLane.m_Rear.m_Lane)
            {
                if (position1.y < position1.x)
                { position1.y = math.min(position1.y, position2.y); position1.x = math.max(position1.x, position2.x); }
                else
                { position1.x = math.min(position1.x, position2.x); position1.y = math.max(position1.y, position2.y); }
                position2 = position1;
            }
            else if (position1.y < position1.x)
                position1.x = math.max(position1.x, currentLane.m_Front.m_CurvePosition.x);
            else position1.x = math.min(position1.x, currentLane.m_Front.m_CurvePosition.x);
            if (currentLane.m_Rear.m_Lane != currentLane.m_RearCache.m_Lane)
            {
                if (position2.y < position2.x)
                    position2.x = math.max(position2.x, currentLane.m_Rear.m_CurvePosition.x);
                else position2.x = math.min(position2.x, currentLane.m_Rear.m_CurvePosition.x);
            }
        }

        private static void RemoveOccupancy(SimulationState state, Entity lane, Entity vehicle)
        {
            for (int i = state.Occupancies.Count - 1; i >= 0; i--)
                if (state.Occupancies[i].Lane == lane && state.Occupancies[i].Vehicle == vehicle)
                    state.Occupancies.RemoveAt(i);
        }

        private static void UpdateOccupancy(SimulationState state, Entity lane, Entity vehicle, float2 position)
        {
            for (int i = 0; i < state.Occupancies.Count; i++)
            {
                OccupancyState occupancy = state.Occupancies[i];
                if (occupancy.Lane != lane || occupancy.Vehicle != vehicle) continue;
                occupancy.CurvePosition = position;
                state.Occupancies[i] = occupancy;
                return;
            }
            AddOccupancy(state, lane, vehicle, position);
        }

        private static void AddOccupancy(SimulationState state, Entity lane, Entity vehicle, float2 position)
        {
            for (int i = 0; i < state.Occupancies.Count; i++)
            {
                OccupancyState occupancy = state.Occupancies[i];
                if (occupancy.Lane != lane || occupancy.Vehicle != vehicle) continue;
                occupancy.CurvePosition = position;
                state.Occupancies[i] = occupancy;
                return;
            }
            state.Occupancies.Add(new OccupancyState(new RailEtaLaneOccupancyRow
            { Lane = lane, Vehicle = vehicle, Start = position.x, End = position.y }));
        }

        private readonly struct FrozenPathElement
        {
            internal readonly int ElementOrdinal;
            internal readonly Entity Target;
            internal readonly float2 TargetDelta;
            internal readonly PathElementFlags Flags;
            internal readonly bool TargetExists;

            internal FrozenPathElement(RailEtaFrozenPathElementRow row)
            {
                ElementOrdinal = row.ElementOrdinal;
                Target = row.Target;
                TargetDelta = row.TargetDelta;
                Flags = (PathElementFlags)row.Flags;
                TargetExists = row.TargetExists != 0;
            }

            internal FrozenPathElement(int ordinal, RailEtaRoutePathRow row)
            {
                ElementOrdinal = ordinal;
                Target = row.Lane;
                TargetDelta = new float2(row.Start, row.End);
                Flags = (PathElementFlags)row.PathFlags;
                TargetExists = row.Lane != Entity.Null;
            }

            internal FrozenPathElement(int ordinal, Entity target, float2 targetDelta,
                PathElementFlags flags, bool targetExists)
            {
                ElementOrdinal = ordinal;
                Target = target;
                TargetDelta = targetDelta;
                Flags = flags;
                TargetExists = targetExists;
            }
        }

        private readonly struct SignalRequest
        {
            internal readonly Entity Petitioner;
            internal readonly Entity Lane;
            internal readonly int Priority;

            internal SignalRequest(Entity petitioner, Entity lane, int priority)
            {
                Petitioner = petitioner;
                Lane = lane;
                Priority = priority;
            }
        }

        private sealed class VehicleState
        {
            internal readonly Entity Controller;
            internal readonly int ControllerOrdinal;
            internal readonly Entity Route;
            internal readonly int Priority;
            internal readonly bool HasOdometer;
            internal readonly UnitState[] Units;
            internal readonly CurrentLaneCache[] LaneCaches;
            internal readonly List<TrainNavigationLane> NavigationLanes;
            internal readonly List<FrozenPathElement> PathElements;
            internal Entity Target;
            internal int TargetSegmentIndex;
            internal uint DepartureFrame;
            internal bool Active;
            internal bool Boarding;
            internal uint DwellDeadlineFrame;
            internal bool HoldReleased;
            internal uint PathSwitchFrame;
            internal int PendingRouteSegmentIndex;
            internal uint PendingRouteReadyFrame;
            internal Entity TicketEndpoint;
            internal bool TicketEndpointIsLifecycle;
            internal bool TargetReached;
            internal PathOwner PathOwner;
            internal Blocker Blocker;
            internal byte BlockerSource;
            internal BlockerEvidenceState BlockerEvidence;
            internal Odometer Odometer;

            internal VehicleState(RailEtaScopedVehicleRow row, UnitState[] units, List<TrainNavigationLane> navigationLanes,
                List<FrozenPathElement> pathElements)
            {
                Controller = row.Controller;
                ControllerOrdinal = row.ControllerOrdinal;
                Target = row.Target;
                Route = row.Route;
                TargetSegmentIndex = row.TargetSegmentIndex;
                Priority = row.VehiclePriority;
                HasOdometer = row.HasOdometer != 0;
                DepartureFrame = row.DepartureFrame;
                Active = true;
                Boarding = row.Boarding != 0 && units[0].Navigation.m_Speed < 0.1f;
                DwellDeadlineFrame = 0u;
                HoldReleased = false;
                PathSwitchFrame = 0u;
                PendingRouteSegmentIndex = -1;
                PendingRouteReadyFrame = 0u;
                TicketEndpoint = Entity.Null;
                TicketEndpointIsLifecycle = false;
                TargetReached = Boarding;
                Units = units;
                LaneCaches = new CurrentLaneCache[units.Length];
                NavigationLanes = navigationLanes;
                PathElements = pathElements;
                PathOwner = new PathOwner { m_ElementIndex = row.PathElementIndex, m_State = (PathFlags)row.PathState };
                Blocker = new Blocker
                {
                    m_Blocker = row.Blocker,
                    m_Type = (BlockerType)row.BlockerType,
                    m_MaxSpeed = row.BlockerMaximumSpeed
                };
                BlockerSource = 0;
                BlockerEvidence = default;
                Odometer = new Odometer { m_Distance = row.OdometerDistance };
            }

            private VehicleState(VehicleState source)
            {
                Controller = source.Controller;
                ControllerOrdinal = source.ControllerOrdinal;
                Target = source.Target;
                Route = source.Route;
                TargetSegmentIndex = source.TargetSegmentIndex;
                Priority = source.Priority;
                HasOdometer = source.HasOdometer;
                DepartureFrame = source.DepartureFrame;
                Active = source.Active;
                Boarding = source.Boarding;
                DwellDeadlineFrame = source.DwellDeadlineFrame;
                HoldReleased = source.HoldReleased;
                PathSwitchFrame = source.PathSwitchFrame;
                PendingRouteSegmentIndex = source.PendingRouteSegmentIndex;
                PendingRouteReadyFrame = source.PendingRouteReadyFrame;
                TicketEndpoint = source.TicketEndpoint;
                TicketEndpointIsLifecycle = source.TicketEndpointIsLifecycle;
                TargetReached = source.TargetReached;
                Units = new UnitState[source.Units.Length];
                for (int i = 0; i < Units.Length; i++) Units[i] = source.Units[i].CreateBuffer();
                LaneCaches = new CurrentLaneCache[source.Units.Length];
                NavigationLanes = new List<TrainNavigationLane>(source.NavigationLanes.Capacity);
                PathElements = new List<FrozenPathElement>(source.PathElements.Capacity);
                CopyFrom(source);
            }

            internal VehicleState CreateBuffer() => new VehicleState(this);

            internal void CopyFrom(VehicleState source)
            {
                Target = source.Target;
                TargetSegmentIndex = source.TargetSegmentIndex;
                DepartureFrame = source.DepartureFrame;
                Active = source.Active;
                Boarding = source.Boarding;
                DwellDeadlineFrame = source.DwellDeadlineFrame;
                HoldReleased = source.HoldReleased;
                PathSwitchFrame = source.PathSwitchFrame;
                PendingRouteSegmentIndex = source.PendingRouteSegmentIndex;
                PendingRouteReadyFrame = source.PendingRouteReadyFrame;
                TicketEndpoint = source.TicketEndpoint;
                TicketEndpointIsLifecycle = source.TicketEndpointIsLifecycle;
                TargetReached = source.TargetReached;
                PathOwner = source.PathOwner;
                Blocker = source.Blocker;
                BlockerSource = source.BlockerSource;
                BlockerEvidence = source.BlockerEvidence;
                Odometer = source.Odometer;
                for (int i = 0; i < Units.Length; i++) Units[i].CopyFrom(source.Units[i]);
                NavigationLanes.Clear();
                NavigationLanes.AddRange(source.NavigationLanes);
                PathElements.Clear();
                PathElements.AddRange(source.PathElements);
            }
        }

        private sealed class UnitState
        {
            internal readonly int LayoutOrdinal;
            internal readonly Entity Controller;
            internal readonly Entity Entity;
            internal readonly Entity Prefab;
            internal Game.Objects.Transform Transform;
            internal Moving Moving;
            internal Train Train;
            internal TrainNavigation Navigation;
            internal TrainCurrentLane CurrentLane;
            internal TrainData PrefabTrain;
            internal ObjectGeometryData PrefabGeometry;

            internal UnitState(RailEtaScopedUnitRow row)
            {
                LayoutOrdinal = row.LayoutOrdinal;
                Controller = row.Controller;
                Entity = row.Unit;
                Prefab = row.Prefab;
                Transform = row.Transform;
                Moving = row.Moving;
                Train = row.Train;
                Navigation = row.Navigation;
                CurrentLane = row.CurrentLane;
                PrefabTrain = row.PrefabTrainData;
                PrefabGeometry = row.PrefabGeometryData;
            }

            private UnitState(UnitState source)
            {
                LayoutOrdinal = source.LayoutOrdinal;
                Controller = source.Controller;
                Entity = source.Entity;
                Prefab = source.Prefab;
                Transform = source.Transform;
                Moving = source.Moving;
                Train = source.Train;
                Navigation = source.Navigation;
                CurrentLane = source.CurrentLane;
                PrefabTrain = source.PrefabTrain;
                PrefabGeometry = source.PrefabGeometry;
            }

            internal UnitState CreateBuffer() => new UnitState(this);

            internal void CopyFrom(UnitState source)
            {
                Transform = source.Transform;
                Moving = source.Moving;
                Train = source.Train;
                Navigation = source.Navigation;
                CurrentLane = source.CurrentLane;
                PrefabTrain = source.PrefabTrain;
                PrefabGeometry = source.PrefabGeometry;
            }
        }

        private static uint DwellFrames(SimulationState state, VehicleState vehicle)
        {
            int dwellMinutes = 10;
            RailEtaRequestFrameFacts facts = state.World.RuntimeFacts;
            if (facts != null && facts.LineMaxDwellMinutes.TryGetValue(vehicle.Route, out int configured) && configured > 0)
                dwellMinutes = configured;
            return (uint)math.max(1, Mathf.RoundToInt((float)(dwellMinutes * facts.FramesPerMinute)));
        }

        private static bool FrameReached(uint frame, uint deadline) => unchecked(frame - deadline) < 0x80000000u;

        private static bool InitializeTicketEndpoint(RailEtaFrozenWorld world, VehicleState target, out string reason)
        {
            reason = string.Empty;
            if (target.Route == Entity.Null || target.TargetSegmentIndex < 0)
            {
                target.TicketEndpoint = target.Target;
                target.TicketEndpointIsLifecycle = true;
                return target.TicketEndpoint != Entity.Null;
            }
            if (!target.Boarding && IsBoardingWaypoint(world, target))
            {
                target.TicketEndpoint = target.Target;
                return true;
            }
            int segmentIndex = target.TargetSegmentIndex;
            int maximum = RouteSegmentCount(world, target.Route);
            for (int i = 0; i < maximum; i++)
            {
                if (TryGetRouteSegment(world, target.Route, segmentIndex, out RailEtaRouteSegmentRow segment)
                    && segment.ToWaypoint != Entity.Null && segment.ToWaypointBoarding != 0)
                {
                    target.TicketEndpoint = segment.ToWaypoint;
                    return true;
                }
                segmentIndex = NextWaypointIndex(world, target.Route, segmentIndex);
            }
            reason = "next-boarding-route-target-missing";
            return false;
        }

        private static bool IsTicketEndpointReached(RailEtaFrozenWorld world, VehicleState vehicle) =>
            vehicle.Target == vehicle.TicketEndpoint
            && (vehicle.TicketEndpointIsLifecycle || IsBoardingWaypoint(world, vehicle));

        private static int RouteSegmentCount(RailEtaFrozenWorld world, Entity route)
        {
            for (int i = 0; i < world.Lines.Length; i++)
                if (world.Lines[i].Line == route) return world.Lines[i].SegmentCount;
            return 0;
        }

        private static bool IsStoppedAtPathEnd(VehicleState vehicle)
        {
            if (!vehicle.Active || !vehicle.TargetReached || vehicle.Units.Length == 0
                || !(vehicle.Units[0].Navigation.m_Speed < 0.1f)) return false;
            TrainLaneFlags flags = vehicle.Units[0].CurrentLane.m_Front.m_LaneFlags;
            return (flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached)) != 0;
        }

        private static bool BeginStoppedBoarding(SimulationState state, uint frame, Entity targetController,
            out string targetFailure)
        {
            targetFailure = string.Empty;
            for (int i = 0; i < state.Vehicles.Length; i++)
            {
                VehicleState vehicle = state.Vehicles[i];
                if (!vehicle.Active || vehicle.Boarding || vehicle.PendingRouteSegmentIndex >= 0
                    || vehicle.PathSwitchFrame == frame
                    || !IsStoppedAtPathEnd(vehicle)) continue;
                if (!IsBoardingWaypoint(state.World, vehicle))
                {
                    if (!TryBeginRoutePathRequest(state, vehicle, frame, out string reason))
                    {
                        if (vehicle.Controller == targetController)
                        {
                            targetFailure = reason;
                            return false;
                        }
                        RemoveFailedVehicle(state, vehicle, frame, reason);
                    }
                    continue;
                }
                vehicle.Boarding = true;
                vehicle.DwellDeadlineFrame = unchecked(frame + DwellFrames(state, vehicle));
            }
            return true;
        }

        private static bool IsBoardingWaypoint(RailEtaFrozenWorld world, VehicleState vehicle)
        {
            if (vehicle.Route == Entity.Null || vehicle.Target == Entity.Null) return false;
            for (int i = 0; i < world.RouteSegments.Length; i++)
            {
                RailEtaRouteSegmentRow segment = world.RouteSegments[i];
                if (segment.Line == vehicle.Route && segment.ToWaypoint == vehicle.Target)
                    return segment.ToWaypointBoarding != 0;
            }
            return false;
        }

        private static void UpdateFrozenGates(SimulationState previous, SimulationState current, uint frame)
        {
            RailEtaRequestFrameFacts facts = current.World.RuntimeFacts;
            for (int i = 0; i < current.Vehicles.Length; i++)
            {
                VehicleState vehicle = current.Vehicles[i];
                if (!vehicle.Active || vehicle.HoldReleased) continue;
                if (facts == null || !facts.ControlledHolds.TryGetValue(vehicle.Controller, out RailControlledHoldSnapshot hold)
                    || hold == null || hold.Kind == RailControlledHoldKind.None)
                {
                    vehicle.HoldReleased = true;
                    continue;
                }
                if (hold.Kind == RailControlledHoldKind.OriginScheduled)
                {
                    vehicle.HoldReleased = FrameReached(frame, hold.EarliestReleaseFrame);
                    continue;
                }
                if (hold.Kind != RailControlledHoldKind.BypassYield) continue;
                Entity releaseController = RailEtaEntityId.ToEntity(hold.ReleaseVehicleId.Value);
                VehicleState release = current.FindVehicle(releaseController);
                if (release == null || !release.Active)
                {
                    vehicle.HoldReleased = true;
                    continue;
                }
                VehicleState priorRelease = previous.FindVehicle(releaseController);
                bool wasOnLane = TryGetFrontPhysicalPosition(previous, priorRelease, hold.ReleaseLaneId, out float priorPosition);
                bool isOnLane = TryGetFrontPhysicalPosition(current, release, hold.ReleaseLaneId, out float currentPosition);
                if (isOnLane && PassedFraction(currentPosition, hold.ReleaseLaneFraction, hold.ReleaseDirection))
                    vehicle.HoldReleased = true;
                else if (wasOnLane && !isOnLane && !PassedFraction(priorPosition, hold.ReleaseLaneFraction, hold.ReleaseDirection))
                    vehicle.HoldReleased = true;
            }
        }

        private static bool TryGetFrontPhysicalPosition(SimulationState state, VehicleState vehicle,
            RailLaneId releaseLane, out float position)
        {
            position = 0f;
            if (vehicle == null || !vehicle.Active || vehicle.Units.Length == 0 || releaseLane.Value == 0) return false;
            TrainBogieLane front = vehicle.Units[0].CurrentLane.m_Front;
            LaneState lane = state.FindLane(front.m_Lane);
            if (lane == null) return false;
            Entity physical = lane.Frozen.PathPhysicalLane != Entity.Null ? lane.Frozen.PathPhysicalLane : lane.Entity;
            if (RailEtaEntityId.Pack(physical) != releaseLane.Value) return false;
            position = front.m_CurvePosition.y;
            return true;
        }

        private static bool PassedFraction(float position, double releaseFraction, int direction) =>
            direction < 0 ? position <= (float)releaseFraction : position >= (float)releaseFraction;

        private static bool PrepareDepartures(SimulationState state, uint frame, Entity targetController,
            out string reason)
        {
            reason = string.Empty;
            for (int i = 0; i < state.Vehicles.Length; i++)
            {
                VehicleState vehicle = state.Vehicles[i];
                if (!vehicle.Active || !vehicle.Boarding) continue;
                if (vehicle.PendingRouteSegmentIndex >= 0) continue;
                if (!vehicle.HoldReleased) continue;
                if (!FrameReached(frame, vehicle.DwellDeadlineFrame) || !FrameReached(frame, vehicle.DepartureFrame)) continue;
                if (TryBeginRoutePathRequest(state, vehicle, frame, out reason)) continue;
                if (vehicle.Controller == targetController) return false;
                RemoveFailedVehicle(state, vehicle, frame, reason);
            }
            return true;
        }

        private static bool TryBeginRoutePathRequest(SimulationState state, VehicleState vehicle, uint frame,
            out string reason)
        {
            reason = string.Empty;
            if (vehicle.PendingRouteSegmentIndex >= 0) return true;
            int segmentIndex = vehicle.TargetSegmentIndex;
            if (!TryGetRouteSegment(state.World, vehicle.Route, segmentIndex, out RailEtaRouteSegmentRow segment)
                || segment.ToWaypoint == Entity.Null)
            { reason = "next-route-target-missing"; return false; }
            if (segment.PathfindDelayKnown == 0)
            { reason = "next-route-path-latency-missing"; return false; }
            vehicle.Target = segment.ToWaypoint;
            vehicle.TargetSegmentIndex = NextWaypointIndex(state.World, vehicle.Route, segmentIndex);
            vehicle.PendingRouteSegmentIndex = segmentIndex;
            vehicle.PendingRouteReadyFrame = unchecked(frame + segment.PathfindDelayFrames);
            vehicle.PathOwner.m_State = PathFlags.Pending | PathFlags.Append;
            vehicle.TargetReached = false;
            if (segment.PathfindDelayFrames == 0u)
                return CompletePendingRoutePath(state, vehicle, frame, out reason);
            return true;
        }

        private static bool CheckNonBoardingNavigationLanes(SimulationState state, VehicleState vehicle,
            uint frame, out string reason)
        {
            reason = string.Empty;
            if (vehicle.PendingRouteSegmentIndex >= 0) return true;

            int count = vehicle.NavigationLanes.Count;
            if (count == 0 || count >= 10) return true;
            TrainNavigationLane last = vehicle.NavigationLanes[count - 1];
            if ((last.m_Flags & TrainLaneFlags.EndOfPath) == 0 || IsBoardingWaypoint(state.World, vehicle)) return true;
            if ((vehicle.PathOwner.m_State & (PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete)) != 0)
            { reason = "non-boarding-route-path-state-invalid"; return false; }

            return TryBeginRoutePathRequest(state, vehicle, frame, out reason);
        }

        private static bool CompletePendingRoutePath(SimulationState state, VehicleState vehicle,
            uint frame, out string reason)
        {
            reason = string.Empty;
            if (vehicle.PendingRouteSegmentIndex < 0 || !FrameReached(frame, vehicle.PendingRouteReadyFrame)) return true;
            int pendingSegment = vehicle.PendingRouteSegmentIndex;
            bool departingBoardingStop = vehicle.Boarding;
            if (departingBoardingStop)
            {
                vehicle.PathElements.Clear();
                vehicle.NavigationLanes.Clear();
                vehicle.PathOwner.m_ElementIndex = 0;
            }
            int appendStart = vehicle.PathElements.Count;
            if (!AppendRoutePath(state.World, vehicle.Route, pendingSegment, vehicle.PathElements))
            { reason = "non-boarding-next-path-missing"; return false; }
            if (departingBoardingStop
                && !TrimRoutePathStartAtCurrentTrain(vehicle, appendStart))
            { reason = "route-path-current-train-origin-missing"; return false; }
            if (!TryGetRouteSegment(state.World, vehicle.Route, pendingSegment, out RailEtaRouteSegmentRow segment))
            { reason = "route-path-segment-metadata-missing"; return false; }
            if (segment.ToWaypointBoarding != 0
                && !ExtendRoutePathForBoardingStop(state, vehicle, pendingSegment))
            { reason = "route-path-boarding-extension-incomplete"; return false; }
            vehicle.PathOwner.m_State = PathFlags.Append;
            vehicle.PendingRouteSegmentIndex = -1;
            vehicle.PendingRouteReadyFrame = 0u;
            vehicle.DepartureFrame = 0u;
            vehicle.Boarding = false;
            vehicle.DwellDeadlineFrame = 0u;
            ClearRoutePathEnd(vehicle);
            return true;
        }

        private static void ClearRoutePathEnd(VehicleState vehicle)
        {
            if (vehicle.NavigationLanes.Count > 0)
            {
                int lastIndex = vehicle.NavigationLanes.Count - 1;
                TrainNavigationLane last = vehicle.NavigationLanes[lastIndex];
                last.m_Flags &= ~TrainLaneFlags.EndOfPath;
                vehicle.NavigationLanes[lastIndex] = last;
            }
            for (int i = 0; i < vehicle.Units.Length; i++)
            {
                TrainCurrentLane current = vehicle.Units[i].CurrentLane;
                current.m_Front.m_LaneFlags &= ~(TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached);
                current.m_Rear.m_LaneFlags &= ~(TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached);
                vehicle.Units[i].CurrentLane = current;
            }
            vehicle.TargetReached = false;
        }

        private static bool TryGetRouteSegment(RailEtaFrozenWorld world, Entity route, int segmentIndex,
            out RailEtaRouteSegmentRow result)
        {
            for (int i = 0; i < world.RouteSegments.Length; i++)
            {
                RailEtaRouteSegmentRow segment = world.RouteSegments[i];
                if (segment.Line != route || segment.SegmentIndex != segmentIndex) continue;
                result = segment;
                return true;
            }
            result = default;
            return false;
        }

        private static bool AppendRoutePath(RailEtaFrozenWorld world, Entity route, int segmentIndex,
            List<FrozenPathElement> target)
        {
            int ordinal = 0;
            for (int i = 0; i < world.RoutePaths.Length; i++)
            {
                RailEtaRoutePathRow row = world.RoutePaths[i];
                if (row.Line != route || row.SegmentIndex != segmentIndex) continue;
                target.Add(new FrozenPathElement(ordinal++, row));
            }
            return ordinal != 0;
        }

        // Vanilla TransportTrainAISystem post-processes every completed train
        // path before navigation consumes it. A train starting from a stop uses
        // its current front-bogie path endpoint as the pathfind origin, rather
        // than replaying the line segment from the waypoint anchor.
        private static bool TrimRoutePathStartAtCurrentTrain(VehicleState vehicle, int appendStart)
        {
            if (vehicle.Units.Length == 0 || appendStart < 0 || appendStart >= vehicle.PathElements.Count)
                return false;
            TrainBogieLane front = vehicle.Units[0].CurrentLane.m_Front;
            if (front.m_Lane == Entity.Null) return false;
            float origin = front.m_CurvePosition.w;
            float direction = front.m_CurvePosition.w - front.m_CurvePosition.x;
            if (math.abs(direction) < 0.0001f) direction = front.m_CurvePosition.w - front.m_CurvePosition.y;
            int match = -1;
            for (int i = appendStart; i < vehicle.PathElements.Count; i++)
            {
                FrozenPathElement element = vehicle.PathElements[i];
                if (element.Target != front.m_Lane) continue;
                float2 delta = element.TargetDelta;
                float minimum = math.min(delta.x, delta.y) - 0.0001f;
                float maximum = math.max(delta.x, delta.y) + 0.0001f;
                if (origin < minimum || origin > maximum) continue;
                float elementDirection = delta.y - delta.x;
                if (math.abs(direction) >= 0.0001f && math.abs(elementDirection) >= 0.0001f
                    && math.sign(direction) != math.sign(elementDirection)) continue;
                match = i;
                break;
            }
            if (match < 0) return false;
            if (match > appendStart) vehicle.PathElements.RemoveRange(appendStart, match - appendStart);
            FrozenPathElement first = vehicle.PathElements[appendStart];
            float clampedOrigin = math.clamp(origin,
                math.min(first.TargetDelta.x, first.TargetDelta.y),
                math.max(first.TargetDelta.x, first.TargetDelta.y));
            vehicle.PathElements[appendStart] = new FrozenPathElement(first.ElementOrdinal, first.Target,
                new float2(clampedOrigin, first.TargetDelta.y), first.Flags, first.TargetExists);
            return true;
        }

        // Direct DTO adaptation of VehicleUtils.CalculateLength followed by
        // PathUtils.ExtendPath(path, length / 2) for a boarding destination.
        // The ordered next route-segment path is the frozen equivalent of
        // vanilla walking connected lanes beyond the waypoint.
        private static bool ExtendRoutePathForBoardingStop(SimulationState state, VehicleState vehicle,
            int completedSegment)
        {
            float remaining = CalculateConsistLength(vehicle) * 0.5f;
            if (!(remaining > 0f)) return true;
            int segmentCount = RouteSegmentCount(state.World, vehicle.Route);
            if (segmentCount <= 0) return false;
            int segmentIndex = NextWaypointIndex(state.World, vehicle.Route, completedSegment);
            for (int segmentOffset = 0; segmentOffset < segmentCount && remaining > 0.001f; segmentOffset++)
            {
                bool found = false;
                for (int i = 0; i < state.World.RoutePaths.Length; i++)
                {
                    RailEtaRoutePathRow row = state.World.RoutePaths[i];
                    if (row.Line != vehicle.Route || row.SegmentIndex != segmentIndex) continue;
                    found = true;
                    if (!state.TryGetLane(row.Lane, out RailEtaScopedLaneRow lane) || lane.HasCurve == 0)
                        return false;
                    float pathLength = lane.Curve.m_Length * math.abs(row.End - row.Start);
                    if (!(pathLength > 0.001f)) continue;
                    float2 delta = new float2(row.Start, row.End);
                    if (pathLength > remaining)
                    {
                        delta.y = math.lerp(delta.x, delta.y, remaining / pathLength);
                        vehicle.PathElements.Add(new FrozenPathElement(vehicle.PathElements.Count,
                            row.Lane, delta, (PathElementFlags)row.PathFlags, row.Lane != Entity.Null));
                        return true;
                    }
                    vehicle.PathElements.Add(new FrozenPathElement(vehicle.PathElements.Count,
                        row.Lane, delta, (PathElementFlags)row.PathFlags, row.Lane != Entity.Null));
                    remaining -= pathLength;
                }
                if (!found) return false;
                segmentIndex = NextWaypointIndex(state.World, vehicle.Route, segmentIndex);
            }
            return remaining <= 0.001f;
        }

        private static float CalculateConsistLength(VehicleState vehicle)
        {
            float result = 0f;
            for (int i = 0; i < vehicle.Units.Length; i++)
                result += math.csum(vehicle.Units[i].PrefabTrain.m_AttachOffsets);
            return result;
        }

        private static int MaxRoutePathLength(RailEtaFrozenWorld world, Entity route)
        {
            int maximum = 0;
            for (int i = 0; i < world.RouteSegments.Length; i++)
            {
                RailEtaRouteSegmentRow segment = world.RouteSegments[i];
                if (segment.Line != route) continue;
                int count = 0;
                for (int j = 0; j < world.RoutePaths.Length; j++)
                    if (world.RoutePaths[j].Line == route && world.RoutePaths[j].SegmentIndex == segment.SegmentIndex) count++;
                maximum = math.max(maximum, count);
            }
            return maximum;
        }

        private static int NextWaypointIndex(RailEtaFrozenWorld world, Entity route, int current)
        {
            for (int i = 0; i < world.Lines.Length; i++)
                if (world.Lines[i].Line == route && world.Lines[i].WaypointCount > 0)
                    return (current + 1) % world.Lines[i].WaypointCount;
            return current + 1;
        }

        private static void RemoveFailedVehicle(SimulationState state, VehicleState vehicle, uint frame, string reason)
        {
            vehicle.Active = false;
            vehicle.Boarding = false;
            state.Failures.Add(new SimulationFailure(vehicle.Controller, frame, reason));
            for (int i = state.Occupancies.Count - 1; i >= 0; i--)
                if (state.IsSameController(state.Occupancies[i].Vehicle, vehicle.Controller)) state.Occupancies.RemoveAt(i);
            for (int i = 0; i < state.MutableLaneIndices.Length; i++)
            {
                LaneState lane = state.Lanes[state.MutableLaneIndices[i]];
                LaneReservation reservation = lane.Reservation;
                bool clearPrevious = state.IsSameController(lane.PreviousClaimOwner, vehicle.Controller);
                bool clearNext = state.IsSameController(lane.NextClaimOwner, vehicle.Controller);
                if (clearPrevious)
                {
                    Entity replacement = ReplayReservationClaimOwner(state, lane.Entity, reservation.m_Prev.m_Priority);
                    if (replacement == Entity.Null) reservation.m_Prev = default;
                    lane.PreviousClaimOwner = replacement;
                }
                if (clearNext)
                {
                    Entity replacement = ReplayReservationClaimOwner(state, lane.Entity, reservation.m_Next.m_Priority);
                    if (replacement == Entity.Null) reservation.m_Next = default;
                    lane.NextClaimOwner = replacement;
                }
                if (state.IsSameController(reservation.m_Blocker, vehicle.Controller))
                {
                    reservation.m_Blocker = reservation.m_Next.m_Priority >= reservation.m_Prev.m_Priority
                        ? lane.NextClaimOwner : lane.PreviousClaimOwner;
                }
                lane.Reservation = reservation;
                LaneSignal signal = lane.Signal;
                if (state.IsSameController(signal.m_Petitioner, vehicle.Controller))
                {
                    signal.m_Petitioner = Entity.Null;
                    signal.m_Priority = signal.m_Default;
                }
                if (state.IsSameController(signal.m_Blocker, vehicle.Controller)) signal.m_Blocker = Entity.Null;
                lane.Signal = signal;
            }
            for (int i = 0; i < state.Vehicles.Length; i++)
                if (state.IsSameController(state.Vehicles[i].Blocker.m_Blocker, vehicle.Controller))
                    state.Vehicles[i].Blocker = default;
            for (int i = state.SignalRequests.Count - 1; i >= 0; i--)
                if (state.IsSameController(state.SignalRequests[i].Petitioner, vehicle.Controller))
                    state.SignalRequests.RemoveAt(i);
            RailEtaRequestFrameFacts facts = state.World.RuntimeFacts;
            if (facts != null)
                for (int i = 0; i < state.Vehicles.Length; i++)
                    if (facts.ControlledHolds.TryGetValue(state.Vehicles[i].Controller, out RailControlledHoldSnapshot hold)
                        && hold != null && hold.ReleaseVehicleId.Value == RailEtaEntityId.Pack(vehicle.Controller))
                        state.Vehicles[i].HoldReleased = true;
            vehicle.Blocker = default;
            vehicle.NavigationLanes.Clear();
            vehicle.PathElements.Clear();
            for (int i = 0; i < vehicle.Units.Length; i++)
            {
                vehicle.Units[i].CurrentLane = default;
                vehicle.Units[i].Navigation = default;
                Moving moving = vehicle.Units[i].Moving;
                moving.m_Velocity = float3.zero;
                moving.m_AngularVelocity = float3.zero;
                vehicle.Units[i].Moving = moving;
            }
        }

        private static bool RunNavigationJobBoundary(SimulationState tickStart, SimulationState nextState,
            Entity targetController, uint frame, out string reason)
        {
            reason = string.Empty;
            for (int i = 0; i < nextState.Vehicles.Length; i++)
            {
                VehicleState vehicle = nextState.Vehicles[i];
                if (vehicle.Active && vehicle.PendingRouteSegmentIndex >= 0
                    && !CompletePendingRoutePath(nextState, vehicle, frame, out reason))
                {
                    if (vehicle.Controller == targetController) return false;
                    RemoveFailedVehicle(nextState, vehicle, frame, reason);
                    continue;
                }
                if (!vehicle.Active || vehicle.Boarding || vehicle.PathSwitchFrame == frame) continue;
                CurrentLaneCache[] laneCaches = vehicle.LaneCaches;
                for (int j = 0; j < vehicle.Units.Length; j++)
                {
                    TrainCurrentLane currentLane = vehicle.Units[j].CurrentLane;
                    laneCaches[j] = new CurrentLaneCache(tickStart, ref currentLane);
                    vehicle.Units[j].CurrentLane = currentLane;
                }
                UpdateTrainLimits(vehicle, out TrainData prefabTrainData);
                if (!UpdateNavigationLanes(tickStart, vehicle, prefabTrainData, out reason))
                {
                    if (vehicle.Controller == targetController) return false;
                    RemoveFailedVehicle(nextState, vehicle, frame, reason);
                    continue;
                }
                UpdateNavigationTarget(tickStart, nextState, vehicle, prefabTrainData);
                if (!TryReserveNavigationLanes(nextState, vehicle, prefabTrainData, out reason))
                {
                    if (vehicle.Controller == targetController) return false;
                    RemoveFailedVehicle(nextState, vehicle, frame, reason);
                    continue;
                }
                for (int j = 0; j < vehicle.Units.Length; j++)
                    laneCaches[j].CheckChanges(nextState, vehicle.Units[j].Entity, vehicle.Units[j].CurrentLane);
            }
            UpdateLaneReservations(nextState);
            CommitLaneSignals(nextState);
            for (int i = 0; i < nextState.Vehicles.Length; i++) SyncTrainMoveState(nextState.Vehicles[i]);
            for (int i = 0; i < nextState.Vehicles.Length; i++)
            {
                VehicleState vehicle = nextState.Vehicles[i];
                if (!vehicle.Active || vehicle.Boarding || vehicle.PathSwitchFrame == frame) continue;
                if (CheckNonBoardingNavigationLanes(nextState, vehicle, frame, out reason)) continue;
                if (vehicle.Controller == targetController) return false;
                RemoveFailedVehicle(nextState, vehicle, frame, reason);
            }
            return true;
        }

        // TrainMoveSystem.UpdateTrainMovementJob fields needed by the following
        // navigation tick: Transform and Moving only (frame buffers are outputs).
        private static void SyncTrainMoveState(VehicleState vehicle)
        {
            for (int i = 0; i < vehicle.Units.Length; i++)
            {
                UnitState unit = vehicle.Units[i];
                TrainData prefabTrainData = unit.PrefabTrain;
                Game.Objects.Transform transform = unit.Transform;
                Moving moving = unit.Moving;
                VehicleUtils.CalculateTrainNavigationPivots(transform, prefabTrainData, out float3 pivot, out float3 rearPivot);
                float3 offset = unit.Navigation.m_Rear.m_Position - unit.Navigation.m_Front.m_Position;
                bool reversed = (unit.Train.m_Flags & Game.Vehicles.TrainFlags.Reversed) != 0;
                if (reversed)
                {
                    Swap(ref pivot, ref rearPivot);
                    prefabTrainData.m_BogieOffsets = prefabTrainData.m_BogieOffsets.yx;
                }
                if (!MathUtils.TryNormalize(ref offset, prefabTrainData.m_BogieOffsets.x))
                    offset = transform.m_Position - pivot;
                transform.m_Position = unit.Navigation.m_Front.m_Position + offset;
                float3 direction = math.select(-offset, offset, reversed);
                if (MathUtils.TryNormalize(ref direction)) transform.m_Rotation = quaternion.LookRotationSafe(direction, math.up());
                moving.m_Velocity = unit.Navigation.m_Front.m_Direction + unit.Navigation.m_Rear.m_Direction;
                MathUtils.TryNormalize(ref moving.m_Velocity, unit.Navigation.m_Speed);
                unit.Transform = transform;
                unit.Moving = moving;
            }
        }

        private static void UpdateTrainLimits(VehicleState vehicle, out TrainData prefabTrainData)
        {
            prefabTrainData = vehicle.Units[0].PrefabTrain;
            for (int i = 1; i < vehicle.Units.Length; i++)
            {
                TrainData trainData = vehicle.Units[i].PrefabTrain;
                prefabTrainData.m_MaxSpeed = math.min(prefabTrainData.m_MaxSpeed, trainData.m_MaxSpeed);
                prefabTrainData.m_Acceleration = math.min(prefabTrainData.m_Acceleration, trainData.m_Acceleration);
                prefabTrainData.m_Braking = math.min(prefabTrainData.m_Braking, trainData.m_Braking);
            }
        }

        private static bool UpdateNavigationLanes(SimulationState tickStart, VehicleState vehicle,
            TrainData prefabTrainData, out string reason)
        {
            reason = string.Empty;
            int invalidPath = 0;
            TrainCurrentLane currentLane = vehicle.Units[0].CurrentLane;
            PathOwner pathOwner = vehicle.PathOwner;
            if (!HasValidLanes(currentLane))
            {
                // Vanilla calls TryFindCurrentLane here. The frozen scope has no
                // spatial search tree, so this exact branch must stay closed.
                reason = "frozen-current-lane-search-required";
                return false;
            }
            if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Updated)) != 0
                && (pathOwner.m_State & PathFlags.Append) == 0)
            {
                vehicle.NavigationLanes.Clear();
                currentLane.m_Front.m_LaneFlags &= ~TrainLaneFlags.Return;
            }
            else if ((pathOwner.m_State & PathFlags.Updated) == 0)
            {
                FillNavigationPaths(tickStart, vehicle, ref currentLane, ref pathOwner, ref invalidPath);
            }
            for (int i = 1; i < vehicle.Units.Length; i++)
            {
                if (!HasValidLanes(vehicle.Units[i].CurrentLane))
                {
                    reason = "frozen-consist-lane-search-required";
                    return false;
                }
            }
            if (invalidPath != 0)
            {
                vehicle.NavigationLanes.Clear();
                vehicle.PathElements.Clear();
                pathOwner.m_ElementIndex = 0;
                pathOwner.m_State |= PathFlags.Obsolete;
                currentLane.m_Front.m_LaneFlags &= ~TrainLaneFlags.Return;
            }
            vehicle.Units[0].CurrentLane = currentLane;
            vehicle.PathOwner = pathOwner;
            return true;
        }

        private static bool HasValidLanes(TrainCurrentLane currentLane) =>
            currentLane.m_Front.m_Lane != Entity.Null
            && currentLane.m_Rear.m_Lane != Entity.Null
            && currentLane.m_FrontCache.m_Lane != Entity.Null
            && currentLane.m_RearCache.m_Lane != Entity.Null
            && (currentLane.m_Front.m_LaneFlags & TrainLaneFlags.Obsolete) == 0;

        private static void FillNavigationPaths(SimulationState tickStart, VehicleState vehicle,
            ref TrainCurrentLane currentLane, ref PathOwner pathOwner, ref int invalidPath)
        {
            if ((currentLane.m_Front.m_LaneFlags & TrainLaneFlags.EndOfPath) != 0) return;
            for (int i = 0; i < 10000; i++)
            {
                TrainNavigationLane element;
                if (i >= vehicle.NavigationLanes.Count)
                {
                    if (pathOwner.m_ElementIndex >= vehicle.PathElements.Count
                        || (pathOwner.m_ElementIndex + 1 >= vehicle.PathElements.Count
                            && (pathOwner.m_State & PathFlags.Pending) != 0)) break;
                    FrozenPathElement pathElement = vehicle.PathElements[pathOwner.m_ElementIndex++];
                    element = new TrainNavigationLane { m_Lane = pathElement.Target, m_CurvePosition = pathElement.TargetDelta };
                    bool hasLaneRow = tickStart.TryGetLane(element.m_Lane, out RailEtaScopedLaneRow laneRow);
                    if (hasLaneRow && laneRow.HasTrackLane != 0)
                    {
                        Game.Net.TrackLane trackLane = laneRow.TrackLane;
                        if (pathOwner.m_ElementIndex >= vehicle.PathElements.Count) element.m_Flags |= TrainLaneFlags.EndOfPath;
                        else
                        {
                            if ((pathElement.Flags & PathElementFlags.Return) != 0) element.m_Flags |= TrainLaneFlags.Return;
                            if (((trackLane.m_Flags & (TrackLaneFlags.Twoway | TrackLaneFlags.Switch | TrackLaneFlags.DiamondCrossing | TrackLaneFlags.CrossingTraffic)) != 0
                                && (trackLane.m_Flags & TrackLaneFlags.MergingTraffic) == 0)
                                || (pathElement.Flags & PathElementFlags.Reverse) != 0)
                                element.m_Flags |= TrainLaneFlags.KeepClear;
                        }
                        if ((trackLane.m_Flags & TrackLaneFlags.Exclusive) != 0) element.m_Flags |= TrainLaneFlags.Exclusive;
                        if ((trackLane.m_Flags & TrackLaneFlags.TurnLeft) != 0) element.m_Flags |= TrainLaneFlags.TurnLeft;
                        if ((trackLane.m_Flags & TrackLaneFlags.TurnRight) != 0) element.m_Flags |= TrainLaneFlags.TurnRight;
                        vehicle.NavigationLanes.Add(element);
                    }
                    else if (!hasLaneRow || laneRow.HasConnectionLane == 0)
                    {
                        if (pathElement.TargetExists)
                        {
                            if (pathOwner.m_ElementIndex >= vehicle.PathElements.Count)
                            {
                                if (vehicle.NavigationLanes.Count > 0)
                                {
                                    TrainNavigationLane previous = vehicle.NavigationLanes[vehicle.NavigationLanes.Count - 1];
                                    previous.m_Flags |= TrainLaneFlags.EndOfPath;
                                    vehicle.NavigationLanes[vehicle.NavigationLanes.Count - 1] = previous;
                                }
                                else currentLane.m_Front.m_LaneFlags |= TrainLaneFlags.EndOfPath;
                                element.m_Flags |= TrainLaneFlags.ParkingSpace;
                                vehicle.NavigationLanes.Add(element);
                                break;
                            }
                            continue;
                        }
                        invalidPath++;
                        break;
                    }
                    else
                    {
                        element.m_Flags |= TrainLaneFlags.Connection;
                        if (pathOwner.m_ElementIndex >= vehicle.PathElements.Count) element.m_Flags |= TrainLaneFlags.EndOfPath;
                        vehicle.NavigationLanes.Add(element);
                    }
                }
                else
                {
                    element = vehicle.NavigationLanes[i];
                    if (!tickStart.EntityExists(element.m_Lane)) { invalidPath++; break; }
                }
                if ((element.m_Flags & TrainLaneFlags.EndOfPath) != 0
                    || (element.m_Flags & (TrainLaneFlags.Reserved | TrainLaneFlags.KeepClear | TrainLaneFlags.Connection)) == 0) break;
            }
        }

        // Direct port of TrainNavigationSystem.TryReserveNavigationLanes. This
        // stage only marks the navigation buffer; reservation components are
        // written by UpdateLaneReservations at the following vanilla job boundary.
        private static bool TryReserveNavigationLanes(SimulationState state, VehicleState vehicle,
            TrainData prefabTrainData, out string reason)
        {
            reason = string.Empty;
            const float timeStep = 4f / 15f;
            UnitState lead = vehicle.Units[0];
            if ((lead.Train.m_Flags & Game.Vehicles.TrainFlags.Reversed) != 0)
            {
                prefabTrainData.m_BogieOffsets = prefabTrainData.m_BogieOffsets.yx;
                prefabTrainData.m_AttachOffsets = prefabTrainData.m_AttachOffsets.yx;
            }
            TrainCurrentLane currentLane = lead.CurrentLane;
            if (currentLane.m_Front.m_Lane == Entity.Null) return true;
            if (!state.TryGetLane(currentLane.m_Front.m_Lane, out RailEtaScopedLaneRow currentRow)
                || currentRow.HasCurve == 0)
            {
                reason = "frozen-reservation-current-curve-missing";
                return false;
            }
            float brakingDistance = VehicleUtils.GetBrakingDistance(prefabTrainData, lead.Navigation.m_Speed, timeStep);
            brakingDistance = math.max(0f, brakingDistance - 0.01f);
            float remaining = brakingDistance;
            float signalDistance = prefabTrainData.m_AttachOffsets.x - prefabTrainData.m_BogieOffsets.x + 2f;
            signalDistance += VehicleUtils.GetSignalDistance(prefabTrainData, lead.Navigation.m_Speed);
            Curve curve = currentRow.Curve;
            if (currentLane.m_Front.m_CurvePosition.w > currentLane.m_Front.m_CurvePosition.x)
            {
                currentLane.m_Front.m_CurvePosition.z = currentLane.m_Front.m_CurvePosition.y
                    + remaining / math.max(1E-06f, curve.m_Length);
                currentLane.m_Front.m_CurvePosition.z = math.min(currentLane.m_Front.m_CurvePosition.z,
                    currentLane.m_Front.m_CurvePosition.w);
            }
            else
            {
                currentLane.m_Front.m_CurvePosition.z = currentLane.m_Front.m_CurvePosition.y
                    - remaining / math.max(1E-06f, curve.m_Length);
                currentLane.m_Front.m_CurvePosition.z = math.max(currentLane.m_Front.m_CurvePosition.z,
                    currentLane.m_Front.m_CurvePosition.w);
            }
            remaining -= curve.m_Length * math.abs(currentLane.m_Front.m_CurvePosition.w
                - currentLane.m_Front.m_CurvePosition.y);
            int index = 0;
            bool full = remaining > 0f;
            bool reserve = remaining + signalDistance > 0f
                || (currentLane.m_Front.m_LaneFlags & TrainLaneFlags.KeepClear) != 0;
            while (reserve && index < vehicle.NavigationLanes.Count)
            {
                TrainNavigationLane lane = vehicle.NavigationLanes[index];
                if ((lane.m_Flags & TrainLaneFlags.ParkingSpace) != 0) break;
                if (state.TryGetLane(lane.m_Lane, out RailEtaScopedLaneRow row) && row.HasTrackLane != 0)
                {
                    lane.m_Flags |= TrainLaneFlags.TryReserve;
                    if (full) lane.m_Flags |= TrainLaneFlags.FullReserve;
                    else lane.m_Flags &= ~TrainLaneFlags.FullReserve;
                    vehicle.NavigationLanes[index] = lane;
                }
                if (!state.TryGetLane(lane.m_Lane, out RailEtaScopedLaneRow curveRow) || curveRow.HasCurve == 0)
                {
                    reason = "frozen-reservation-navigation-curve-missing";
                    return false;
                }
                remaining -= curveRow.Curve.m_Length * math.abs(lane.m_CurvePosition.y - lane.m_CurvePosition.x);
                full = remaining > 0f;
                reserve = remaining + signalDistance > 0f || (lane.m_Flags & TrainLaneFlags.KeepClear) != 0;
                index++;
            }
            lead.CurrentLane = currentLane;
            return true;
        }

        // Direct port of NetLaneReservationSystem.ResetLaneReservationsJob.
        // The outer frame loop calls this on the matching UpdateFrame bucket.
        private static void RebuildCurrentReservationClaimOwners(SimulationState state)
        {
            for (int i = 0; i < state.MutableLaneIndices.Length; i++)
            {
                LaneState lane = state.Lanes[state.MutableLaneIndices[i]];
                if (lane.HasReservation == 0) continue;
                lane.PreviousClaimOwner = Entity.Null;
                uint navigationDelta = (state.World.OriginFrame - 3u) & 15u;
                uint resetDelta = (state.World.OriginFrame - lane.UpdateFrameIndex) & 15u;
                lane.NextClaimOwner = navigationDelta <= resetDelta
                    ? ReplayReservationClaimOwner(state, lane.Entity, lane.Reservation.m_Next.m_Priority)
                    : Entity.Null;
            }
        }

        private static Entity ReplayReservationClaimOwner(SimulationState state, Entity lane, int desiredPriority)
        {
            if (desiredPriority == 0) return Entity.Null;
            Entity winner = Entity.Null;
            int winnerPriority = 0;
            for (int i = 0; i < state.Vehicles.Length; i++)
            {
                VehicleState vehicle = state.Vehicles[i];
                if (!vehicle.Active) continue;
                Entity previousLane = Entity.Null;
                for (int j = 0; j < vehicle.Units.Length; j++)
                {
                    TrainCurrentLane current = vehicle.Units[j].CurrentLane;
                    ReplayCurrentClaim(vehicle.Controller, current.m_Front.m_Lane, previousLane, lane,
                        98, ref winner, ref winnerPriority);
                    ReplayCurrentClaim(vehicle.Controller, current.m_FrontCache.m_Lane, current.m_Front.m_Lane,
                        lane, 98, ref winner, ref winnerPriority);
                    ReplayCurrentClaim(vehicle.Controller, current.m_Rear.m_Lane, current.m_FrontCache.m_Lane,
                        lane, 98, ref winner, ref winnerPriority);
                    ReplayCurrentClaim(vehicle.Controller, current.m_RearCache.m_Lane, current.m_Rear.m_Lane,
                        lane, 98, ref winner, ref winnerPriority);
                    previousLane = current.m_RearCache.m_Lane;
                }
            }
            for (int i = 0; i < state.Vehicles.Length; i++)
            {
                VehicleState vehicle = state.Vehicles[i];
                if (!vehicle.Active || vehicle.Units.Length == 0) continue;
                Entity previousLane = vehicle.Units[0].CurrentLane.m_Front.m_Lane;
                for (int j = 0; j < vehicle.NavigationLanes.Count; j++)
                {
                    TrainNavigationLane navigation = vehicle.NavigationLanes[j];
                    if ((navigation.m_Flags & (TrainLaneFlags.Reserved | TrainLaneFlags.TryReserve | TrainLaneFlags.Connection)) == 0) break;
                    if (navigation.m_Lane != previousLane)
                    {
                        bool full = (navigation.m_Flags & (TrainLaneFlags.TryReserve | TrainLaneFlags.FullReserve))
                            == (TrainLaneFlags.TryReserve | TrainLaneFlags.FullReserve);
                        int priority = full || (desiredPriority != 98 && desiredPriority == vehicle.Priority)
                            ? vehicle.Priority : 98;
                        ReplayCurrentClaim(vehicle.Controller, navigation.m_Lane, Entity.Null, lane,
                            priority, ref winner, ref winnerPriority);
                    }
                    previousLane = navigation.m_Lane;
                    if ((navigation.m_Flags & TrainLaneFlags.BlockReserve) != 0) break;
                }
            }
            return winnerPriority == desiredPriority ? winner : Entity.Null;
        }

        private static void ReplayCurrentClaim(Entity controller, Entity candidate, Entity duplicate,
            Entity lane, int priority, ref Entity winner, ref int winnerPriority)
        {
            if (candidate != lane || candidate == duplicate || priority <= winnerPriority) return;
            winner = controller;
            winnerPriority = priority;
        }

        private static void ResetLaneReservationsInPlace(SimulationState state, uint frameIndex)
        {
            int[] bucket = state.ReservationBuckets[(int)(frameIndex & 15u)];
            for (int i = 0; i < bucket.Length; i++)
            {
                LaneState lane = state.Lanes[bucket[i]];
                LaneReservation reservation = lane.Reservation;
                if (reservation.m_Next.m_Priority < reservation.m_Prev.m_Priority)
                    reservation.m_Blocker = Entity.Null;
                reservation.m_Prev = reservation.m_Next;
                reservation.m_Next = default(ReservationData);
                lane.Reservation = reservation;
                lane.PreviousClaimOwner = lane.NextClaimOwner;
                lane.NextClaimOwner = Entity.Null;
            }
        }

        // Direct port of TrainNavigationSystem.UpdateLaneReservationsJob. The
        // two passes and their controller/layout/lane order are preserved.
        private static void UpdateLaneReservations(SimulationState state)
        {
            for (int i = 0; i < state.Vehicles.Length; i++)
                if (state.Vehicles[i].Active) ReserveCurrentLanes(state, state.Vehicles[i]);
            for (int i = 0; i < state.Vehicles.Length; i++)
                if (state.Vehicles[i].Active) TryReserveNavigationLanes(state, state.Vehicles[i]);
        }

        private static void ReserveCurrentLanes(SimulationState state, VehicleState vehicle)
        {
            Entity previousLane = Entity.Null;
            for (int i = 0; i < vehicle.Units.Length; i++)
                ReserveCurrentLanes(state, vehicle.Units[i].Entity, vehicle.Units[i].CurrentLane, ref previousLane, 98);
        }

        private static void ReserveCurrentLanes(SimulationState state, Entity entity,
            TrainCurrentLane currentLane, ref Entity previousLane, int priority)
        {
            if (currentLane.m_Front.m_Lane != Entity.Null && currentLane.m_Front.m_Lane != previousLane)
                ReserveLane(state, entity, currentLane.m_Front.m_Lane, priority);
            if (currentLane.m_FrontCache.m_Lane != Entity.Null
                && currentLane.m_FrontCache.m_Lane != currentLane.m_Front.m_Lane)
                ReserveLane(state, entity, currentLane.m_FrontCache.m_Lane, priority);
            if (currentLane.m_Rear.m_Lane != Entity.Null
                && currentLane.m_Rear.m_Lane != currentLane.m_FrontCache.m_Lane)
                ReserveLane(state, entity, currentLane.m_Rear.m_Lane, priority);
            if (currentLane.m_RearCache.m_Lane != Entity.Null
                && currentLane.m_RearCache.m_Lane != currentLane.m_Rear.m_Lane)
                ReserveLane(state, entity, currentLane.m_RearCache.m_Lane, priority);
            previousLane = currentLane.m_RearCache.m_Lane;
        }

        private static void ReserveLane(SimulationState state, Entity entity, Entity lane, int priority)
        {
            LaneState laneState = state.FindLane(lane);
            if (laneState == null || laneState.HasReservation == 0) return;
            LaneReservation reservation = laneState.Reservation;
            if (priority > reservation.m_Next.m_Priority)
            {
                if (priority >= reservation.m_Prev.m_Priority) reservation.m_Blocker = entity;
                reservation.m_Next.m_Priority = (byte)priority;
                laneState.Reservation = reservation;
                laneState.NextClaimOwner = entity;
            }
        }

        private static void TryReserveNavigationLanes(SimulationState state, VehicleState vehicle)
        {
            if (vehicle.Units.Length < 1) return;
            Entity previousLane = vehicle.Units[0].CurrentLane.m_Front.m_Lane;
            Entity initialLane = previousLane;
            int contiguous = -1;
            int last = -1;
            for (int i = 0; i < vehicle.NavigationLanes.Count; i++)
            {
                TrainNavigationLane lane = vehicle.NavigationLanes[i];
                if ((lane.m_Flags & (TrainLaneFlags.Reserved | TrainLaneFlags.TryReserve | TrainLaneFlags.Connection)) == 0) break;
                if ((lane.m_Flags & (TrainLaneFlags.Reserved | TrainLaneFlags.Connection)) != 0)
                {
                    contiguous = i;
                    last = i;
                }
                else
                {
                    if (lane.m_Lane != previousLane && (lane.m_Flags & TrainLaneFlags.Exclusive) != 0
                        && !CanReserveLane(state, lane.m_Lane, vehicle))
                    {
                        lane.m_Flags |= TrainLaneFlags.BlockReserve;
                        vehicle.NavigationLanes[i] = lane;
                        last = contiguous;
                        break;
                    }
                    lane.m_Flags &= ~TrainLaneFlags.BlockReserve;
                    contiguous = math.select(contiguous, i, contiguous == i - 1 && lane.m_Lane == previousLane);
                    last = i;
                    vehicle.NavigationLanes[i] = lane;
                }
                previousLane = lane.m_Lane;
            }
            previousLane = initialLane;
            for (int i = 0; i <= last; i++)
            {
                TrainNavigationLane lane = vehicle.NavigationLanes[i];
                if (lane.m_Lane != previousLane)
                {
                    bool full = (lane.m_Flags & (TrainLaneFlags.TryReserve | TrainLaneFlags.FullReserve))
                        == (TrainLaneFlags.TryReserve | TrainLaneFlags.FullReserve);
                    ReserveLane(state, vehicle.Units[0].Entity, lane.m_Lane, full ? vehicle.Priority : 98);
                }
                if ((lane.m_Flags & TrainLaneFlags.TryReserve) != 0)
                {
                    lane.m_Flags &= ~(TrainLaneFlags.TryReserve | TrainLaneFlags.FullReserve);
                    lane.m_Flags |= TrainLaneFlags.Reserved;
                    vehicle.NavigationLanes[i] = lane;
                }
                previousLane = lane.m_Lane;
            }
        }

        private static bool CanReserveLane(SimulationState state, Entity lane, VehicleState vehicle)
        {
            LaneState laneState = state.FindLane(lane);
            if (laneState != null && laneState.HasReservation != 0 && laneState.Reservation.GetPriority() != 0)
            {
                for (int i = 0; i < vehicle.Units.Length; i++)
                {
                    TrainCurrentLane current = vehicle.Units[i].CurrentLane;
                    if (current.m_Front.m_Lane == lane || current.m_FrontCache.m_Lane == lane
                        || current.m_Rear.m_Lane == lane || current.m_RearCache.m_Lane == lane) return true;
                }
                return false;
            }
            return true;
        }

        private static void CommitLaneSignals(SimulationState state)
        {
            for (int i = 0; i < state.SignalRequests.Count; i++)
            {
                SignalRequest request = state.SignalRequests[i];
                LaneState lane = state.FindLane(request.Lane);
                if (lane == null || lane.HasSignal == 0) continue;
                LaneSignal signal = lane.Signal;
                if (request.Priority > signal.m_Priority)
                {
                    signal.m_Petitioner = request.Petitioner;
                    signal.m_Priority = (sbyte)request.Priority;
                    lane.Signal = signal;
                }
            }
        }

        private static void UpdateTrafficSignalsInPlace(SimulationState state, uint frame)
        {
            uint bucket = (frame / 4u) & 15u;
            for (int leaderIndex = 0; leaderIndex < state.SignalLeaders.Length; leaderIndex++)
            {
                LaneState leader = state.Lanes[state.SignalLeaders[leaderIndex]];
                if (leader.HasSignal == 0 || leader.SignalController == Entity.Null
                    || leader.SignalPeers.Length == 0 || leader.SignalUpdateFrameIndex != bucket) continue;
                TrafficLights lights = leader.TrafficLights;
                bool changed = AdvanceTrafficLightState(state, leader.SignalPeers, ref lights);
                for (int j = 0; j < leader.SignalPeers.Length; j++)
                {
                    LaneState lane = state.Lanes[leader.SignalPeers[j]];
                    lane.TrafficLights = lights;
                    LaneSignal signal = lane.Signal;
                    if (changed) UpdateLaneSignal(lights, ref signal);
                    signal.m_Petitioner = Entity.Null;
                    signal.m_Priority = signal.m_Default;
                    lane.Signal = signal;
                }
            }
        }

        private static bool AdvanceTrafficLightState(SimulationState state, int[] peers,
            ref TrafficLights lights)
        {
            switch (lights.m_State)
            {
                case TrafficLightState.None:
                    if (++lights.m_Timer >= 1)
                    {
                        lights.m_State = TrafficLightState.Beginning;
                        lights.m_CurrentSignalGroup = 0;
                        lights.m_NextSignalGroup = (byte)GetNextSignalGroup(state, peers, lights, true, out _);
                        lights.m_Timer = 0;
                        return true;
                    }
                    break;
                case TrafficLightState.Beginning:
                    if (++lights.m_Timer >= 1)
                    {
                        lights.m_State = TrafficLightState.Ongoing;
                        lights.m_CurrentSignalGroup = lights.m_NextSignalGroup;
                        lights.m_NextSignalGroup = 0;
                        lights.m_Timer = 0;
                        return true;
                    }
                    break;
                case TrafficLightState.Ongoing:
                    if (++lights.m_Timer >= 2)
                    {
                        int next = GetNextSignalGroup(state, peers, lights, lights.m_Timer >= 6, out bool canExtend);
                        if (next != lights.m_CurrentSignalGroup)
                        {
                            lights.m_State = canExtend ? TrafficLightState.Extending : TrafficLightState.Ending;
                            lights.m_NextSignalGroup = (byte)next;
                            lights.m_Timer = 0;
                            return true;
                        }
                        return false;
                    }
                    break;
                case TrafficLightState.Extending:
                    if (++lights.m_Timer >= 2)
                    {
                        int next = GetNextSignalGroup(state, peers, lights, true, out bool canExtend);
                        if (next == lights.m_CurrentSignalGroup)
                        {
                            lights.m_State = TrafficLightState.Beginning;
                            lights.m_CurrentSignalGroup = 0;
                        }
                        else lights.m_State = canExtend ? TrafficLightState.Extended : TrafficLightState.Ending;
                        lights.m_NextSignalGroup = (byte)next;
                        lights.m_Timer = 0;
                        return true;
                    }
                    break;
                case TrafficLightState.Extended:
                    if (++lights.m_Timer >= 2)
                    {
                        int next = GetNextSignalGroup(state, peers, lights, true, out bool canExtend);
                        if (next == lights.m_CurrentSignalGroup)
                        {
                            lights.m_State = TrafficLightState.Beginning;
                            lights.m_CurrentSignalGroup = 0;
                            lights.m_NextSignalGroup = (byte)next;
                            lights.m_Timer = 0;
                            return true;
                        }
                        if (lights.m_Timer >= 4 || !canExtend)
                        {
                            lights.m_State = TrafficLightState.Ending;
                            lights.m_NextSignalGroup = (byte)next;
                            lights.m_Timer = 0;
                            return true;
                        }
                        return false;
                    }
                    break;
                case TrafficLightState.Ending:
                    if (++lights.m_Timer >= 2)
                    {
                        int next = GetNextSignalGroup(state, peers, lights, true, out _);
                        if (next != lights.m_NextSignalGroup)
                        {
                            if (RequireEnding(state, peers, next)) lights.m_CurrentSignalGroup = lights.m_NextSignalGroup;
                            else lights.m_State = TrafficLightState.Changing;
                            lights.m_NextSignalGroup = (byte)next;
                        }
                        else lights.m_State = TrafficLightState.Changing;
                        lights.m_Timer = 0;
                        return true;
                    }
                    break;
                case TrafficLightState.Changing:
                    if (++lights.m_Timer >= 1)
                    {
                        int next = GetNextSignalGroup(state, peers, lights, true, out _);
                        if (next != lights.m_NextSignalGroup)
                        {
                            if (RequireEnding(state, peers, next))
                            {
                                lights.m_CurrentSignalGroup = lights.m_NextSignalGroup;
                                lights.m_State = TrafficLightState.Ending;
                            }
                            else lights.m_State = TrafficLightState.Beginning;
                            lights.m_NextSignalGroup = (byte)next;
                        }
                        else lights.m_State = TrafficLightState.Beginning;
                        lights.m_Timer = 0;
                        return true;
                    }
                    break;
            }
            return false;
        }

        private static bool RequireEnding(SimulationState state, int[] peers, int nextGroup)
        {
            int mask = nextGroup > 0 ? 1 << nextGroup - 1 : 0;
            for (int i = 0; i < peers.Length; i++)
            {
                LaneState lane = state.Lanes[peers[i]];
                if (lane.Signal.m_Signal == LaneSignalType.Go && (lane.Signal.m_GroupMask & mask) == 0) return true;
            }
            return false;
        }

        private static int GetNextSignalGroup(SimulationState state, int[] peers, TrafficLights lights,
            bool preferChange, out bool canExtend)
        {
            Entity petitioner = Entity.Null;
            Entity blocker = Entity.Null;
            int priority = 0;
            int groups = 0;
            int extendGroups = 0;
            int negativeGroups = 0;
            for (int i = 0; i < peers.Length; i++)
            {
                LaneState lane = state.Lanes[peers[i]];
                LaneSignal signal = lane.Signal;
                int value = math.min(signal.m_Priority, 127);
                if (value > priority)
                {
                    petitioner = signal.m_Petitioner;
                    priority = value;
                    groups = signal.m_GroupMask;
                    extendGroups = (signal.m_Flags & LaneSignalFlags.CanExtend) != 0 ? signal.m_GroupMask : 0;
                }
                else if (value == priority)
                {
                    groups |= signal.m_GroupMask;
                    if ((signal.m_Flags & LaneSignalFlags.CanExtend) != 0) extendGroups |= signal.m_GroupMask;
                }
                else if (value < 0) negativeGroups |= signal.m_GroupMask;
                if (signal.m_Blocker != Entity.Null) blocker = signal.m_Blocker;
            }
            if (petitioner != blocker)
            {
                for (int i = 0; i < peers.Length; i++)
                {
                    LaneState lane = state.Lanes[peers[i]];
                    LaneSignal signal = lane.Signal;
                    signal.m_Blocker = (groups & signal.m_GroupMask) != 0 ? Entity.Null : petitioner;
                    lane.Signal = signal;
                }
            }
            if (priority == 0)
            {
                preferChange = false;
                groups &= ~negativeGroups;
            }
            int nextDefault = lights.m_CurrentSignalGroup >= lights.m_SignalGroupCount
                ? 1 : lights.m_CurrentSignalGroup + 1;
            int start = preferChange ? nextDefault : math.max(1, lights.m_CurrentSignalGroup);
            int end = preferChange ? lights.m_CurrentSignalGroup : lights.m_CurrentSignalGroup - 1;
            canExtend = preferChange && lights.m_CurrentSignalGroup >= 1
                && (extendGroups & (1 << lights.m_CurrentSignalGroup - 1)) != 0;
            for (int i = start; i <= lights.m_SignalGroupCount; i++)
                if ((groups & (1 << i - 1)) != 0) return i;
            for (int i = 1; i <= end; i++)
                if ((groups & (1 << i - 1)) != 0) return i;
            return lights.m_CurrentSignalGroup;
        }

        private static void UpdateLaneSignal(TrafficLights lights, ref LaneSignal signal)
        {
            int current = lights.m_CurrentSignalGroup > 0 ? 1 << lights.m_CurrentSignalGroup - 1 : 0;
            int next = lights.m_NextSignalGroup > 0 ? 1 << lights.m_NextSignalGroup - 1 : 0;
            switch (lights.m_State)
            {
                case TrafficLightState.Beginning:
                    signal.m_Signal = (signal.m_GroupMask & next) != 0
                        ? (signal.m_Signal == LaneSignalType.Go ? LaneSignalType.Go : LaneSignalType.Yield)
                        : LaneSignalType.Stop;
                    break;
                case TrafficLightState.Ongoing:
                    signal.m_Signal = (signal.m_GroupMask & current) != 0 ? LaneSignalType.Go : LaneSignalType.Stop;
                    break;
                case TrafficLightState.Extending:
                    if ((signal.m_Flags & LaneSignalFlags.CanExtend) != 0)
                        signal.m_Signal = (signal.m_GroupMask & current) != 0 ? LaneSignalType.Go : LaneSignalType.Stop;
                    else if (signal.m_Signal == LaneSignalType.Go && (signal.m_GroupMask & next) == 0)
                        signal.m_Signal = LaneSignalType.SafeStop;
                    else if (signal.m_Signal != LaneSignalType.Go) signal.m_Signal = LaneSignalType.Stop;
                    break;
                case TrafficLightState.Extended:
                    signal.m_Signal = (signal.m_Flags & LaneSignalFlags.CanExtend) != 0
                        && (signal.m_GroupMask & current) != 0 ? LaneSignalType.Go : LaneSignalType.Stop;
                    break;
                case TrafficLightState.Ending:
                    if (signal.m_Signal == LaneSignalType.Go && (signal.m_GroupMask & next) == 0)
                        signal.m_Signal = LaneSignalType.SafeStop;
                    else if (signal.m_Signal != LaneSignalType.Go) signal.m_Signal = LaneSignalType.Stop;
                    break;
                case TrafficLightState.Changing:
                    if (signal.m_Signal != LaneSignalType.Go || (signal.m_GroupMask & next) == 0)
                        signal.m_Signal = LaneSignalType.Stop;
                    break;
                default:
                    signal.m_Signal = LaneSignalType.None;
                    break;
            }
        }

        // Direct port of TrainNavigationSystem.UpdateNavigationTarget. All reads
        // of other vehicles/lane control facts come from tickStart; writes go to
        // the target vehicle in nextState.
        private static void UpdateNavigationTarget(SimulationState tickStart, SimulationState nextState,
            VehicleState targetState, TrainData prefabTrainData)
        {
            VehicleState startVehicle = tickStart.FindVehicle(targetState.Controller);
            UnitState lead = targetState.Units[0];
            UnitState startLead = startVehicle.Units[0];
            Odometer odometer = targetState.HasOdometer ? targetState.Odometer : default;

            float timeStep = 4f / 15f;
            float previousSpeed = startLead.Navigation.m_Speed;
            TrainCurrentLane currentLane = lead.CurrentLane;
            TrainNavigation navigation = startLead.Navigation;
            bool connection = HasConnection(currentLane);
            for (int i = 1; i < startVehicle.Units.Length; i++) connection |= HasConnection(startVehicle.Units[i].CurrentLane);
            if (connection)
            {
                prefabTrainData.m_MaxSpeed = 277.77777f;
                prefabTrainData.m_Acceleration = 277.77777f;
                prefabTrainData.m_Braking = 277.77777f;
            }
            else previousSpeed = math.min(previousSpeed, prefabTrainData.m_MaxSpeed);

            Bounds1 speedRange = !connection && (currentLane.m_Front.m_LaneFlags & TrainLaneFlags.ResetSpeed) == 0
                ? VehicleUtils.CalculateSpeedRange(prefabTrainData, previousSpeed, timeStep)
                : new Bounds1(0f, prefabTrainData.m_MaxSpeed);
            VehicleUtils.CalculateTrainNavigationPivots(startLead.Transform, prefabTrainData, out float3 pivot, out float3 rearPivot);
            if ((startLead.Train.m_Flags & Game.Vehicles.TrainFlags.Reversed) != 0)
            {
                Swap(ref pivot, ref rearPivot);
                prefabTrainData.m_BogieOffsets = prefabTrainData.m_BogieOffsets.yx;
                prefabTrainData.m_AttachOffsets = prefabTrainData.m_AttachOffsets.yx;
            }

            bool wasTemporary = targetState.Blocker.m_Type == BlockerType.Temporary;
            var iterator = new FrozenTrainLaneSpeedIterator(tickStart, targetState.Controller, targetState.Priority,
                timeStep, previousSpeed, speedRange, rearPivot,
                (currentLane.m_Front.m_LaneFlags & TrainLaneFlags.PushBlockers) != 0, pivot);
            if (currentLane.m_Front.m_Lane == Entity.Null)
            {
                navigation.m_Speed = math.max(0f, previousSpeed - prefabTrainData.m_Braking * timeStep);
                targetState.Blocker = new Blocker { m_Blocker = Entity.Null, m_Type = BlockerType.None, m_MaxSpeed = byte.MaxValue };
                targetState.BlockerSource = 0;
                targetState.BlockerEvidence = default;
                lead.Navigation = navigation;
                if (targetState.HasOdometer) targetState.Odometer = odometer;
                return;
            }
            if ((currentLane.m_Front.m_LaneFlags & TrainLaneFlags.HighBeams) == 0
                && prefabTrainData.m_TrackType != TrackTypes.Tram
                && tickStart.TryGetLane(currentLane.m_Front.m_Lane, out RailEtaScopedLaneRow currentLaneRow)
                && (currentLaneRow.TrackLane.m_Flags & TrackLaneFlags.Station) == 0)
                currentLane.m_Front.m_LaneFlags |= TrainLaneFlags.HighBeams;

            bool atTarget = false;
            for (int i = startVehicle.Units.Length - 1; i >= 1; i--)
            {
                UnitState unit = startVehicle.Units[i];
                iterator.PrefabTrain = unit.PrefabTrain;
                RequestSignal(nextState, targetState, unit.CurrentLane.m_RearCache.m_Lane,
                    iterator.IteratePrevLane(unit.CurrentLane.m_RearCache.m_Lane));
                RequestSignal(nextState, targetState, unit.CurrentLane.m_Rear.m_Lane,
                    iterator.IteratePrevLane(unit.CurrentLane.m_Rear.m_Lane));
                RequestSignal(nextState, targetState, unit.CurrentLane.m_FrontCache.m_Lane,
                    iterator.IteratePrevLane(unit.CurrentLane.m_FrontCache.m_Lane));
                RequestSignal(nextState, targetState, unit.CurrentLane.m_Front.m_Lane,
                    iterator.IteratePrevLane(unit.CurrentLane.m_Front.m_Lane));
            }
            bool exclusive = (currentLane.m_Front.m_LaneFlags & TrainLaneFlags.Exclusive) != 0;
            bool skipCurrent = !exclusive && targetState.NavigationLanes.Count != 0
                && (targetState.NavigationLanes[0].m_Flags & (TrainLaneFlags.Reserved | TrainLaneFlags.Exclusive))
                == (TrainLaneFlags.Reserved | TrainLaneFlags.Exclusive);
            iterator.PrefabTrain = prefabTrainData;
            iterator.PrefabGeometry = startLead.PrefabGeometry;
            RequestSignal(nextState, targetState, currentLane.m_RearCache.m_Lane,
                iterator.IteratePrevLane(currentLane.m_RearCache.m_Lane));
            RequestSignal(nextState, targetState, currentLane.m_Rear.m_Lane,
                iterator.IteratePrevLane(currentLane.m_Rear.m_Lane));
            RequestSignal(nextState, targetState, currentLane.m_FrontCache.m_Lane,
                iterator.IteratePrevLane(currentLane.m_FrontCache.m_Lane));
            bool stop = iterator.IterateFirstLane(currentLane.m_Front.m_Lane,
                currentLane.m_Front.m_CurvePosition, exclusive, connection, skipCurrent, out bool needSignal);
            RequestSignal(nextState, targetState, currentLane.m_Front.m_Lane, needSignal);
            if (!stop)
            {
                if ((currentLane.m_Front.m_LaneFlags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return)) == 0)
                {
                    int laneIndex = 0;
                    while (laneIndex < targetState.NavigationLanes.Count)
                    {
                        TrainNavigationLane lane = targetState.NavigationLanes[laneIndex];
                        currentLane.m_Front.m_LaneFlags |= lane.m_Flags & (TrainLaneFlags.TurnLeft | TrainLaneFlags.TurnRight);
                        bool sameLane = lane.m_Lane == currentLane.m_Front.m_Lane;
                        if ((lane.m_Flags & (TrainLaneFlags.Reserved | TrainLaneFlags.Connection)) == 0)
                        {
                            while ((lane.m_Flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.BlockReserve)) == 0
                                && ++laneIndex < targetState.NavigationLanes.Count)
                                lane = targetState.NavigationLanes[laneIndex];
                            iterator.IterateTarget(lane.m_Lane, sameLane);
                        }
                        else
                        {
                            if ((lane.m_Flags & TrainLaneFlags.Connection) != 0)
                            {
                                TrainData connectionTrain = iterator.PrefabTrain;
                                connectionTrain.m_MaxSpeed = 277.77777f;
                                connectionTrain.m_Acceleration = 277.77777f;
                                connectionTrain.m_Braking = 277.77777f;
                                iterator.PrefabTrain = connectionTrain;
                                iterator.SpeedRange = new Bounds1(0f, 277.77777f);
                            }
                            float minOffset = math.select(-1f, currentLane.m_Front.m_CurvePosition.z, sameLane);
                            if (!iterator.IterateNextLane(lane.m_Lane, lane.m_CurvePosition, minOffset,
                                (lane.m_Flags & TrainLaneFlags.Exclusive) != 0, sameLane || connection, out needSignal))
                            {
                                RequestSignal(nextState, targetState, lane.m_Lane, needSignal);
                                if ((lane.m_Flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return)) != 0) break;
                                laneIndex++;
                                continue;
                            }
                            RequestSignal(nextState, targetState, lane.m_Lane, needSignal);
                        }
                        break;
                    }
                }
                else atTarget = iterator.IterateTarget();
            }

            navigation.m_Speed = iterator.MaxSpeed;
            float speedCode = math.select(1.8360001f, 2.2949998f, (prefabTrainData.m_TrackType & TrackTypes.Tram) != 0);
            targetState.Blocker = new Blocker
            {
                m_Blocker = iterator.Blocker,
                m_Type = iterator.BlockerType,
                m_MaxSpeed = (byte)math.clamp(Mathf.RoundToInt(iterator.MaxSpeed * speedCode), 0, 255)
            };
            targetState.BlockerSource = iterator.BlockerSource;
            targetState.BlockerEvidence = iterator.BlockerEvidence;
            bool isTemporary = targetState.Blocker.m_Type == BlockerType.Temporary;
            if (isTemporary != wasTemporary || currentLane.m_Duration >= 30f)
            {
                currentLane.m_Duration = 0f;
                currentLane.m_Distance = 0f;
            }
            if (isTemporary)
            {
                if (currentLane.m_Duration >= 5f) currentLane.m_Front.m_LaneFlags |= TrainLaneFlags.PushBlockers;
            }
            else if (currentLane.m_Duration >= 5f) currentLane.m_Front.m_LaneFlags &= ~TrainLaneFlags.PushBlockers;

            float distance = previousSpeed * timeStep;
            currentLane.m_Duration += timeStep;
            currentLane.m_Distance += distance;
            odometer.m_Distance += distance;
            TrainLaneFlags endFlags = TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached;
            if ((currentLane.m_Front.m_LaneFlags & endFlags) == endFlags)
            {
                lead.CurrentLane = currentLane;
                lead.Navigation = navigation;
                if (targetState.HasOdometer) targetState.Odometer = odometer;
                return;
            }

            float moveDistance = navigation.m_Speed * timeStep;
            TrainBogieCache tempCache = default;
            bool resetCache = ShouldResetCache(currentLane.m_Front, currentLane.m_FrontCache);
            while (true)
            {
                if (!tickStart.TryGetLane(currentLane.m_Front.m_Lane, out RailEtaScopedLaneRow laneRow)) break;
                Curve curve = laneRow.Curve;
                bool nonZeroCurve = curve.m_Length > 0.1f;
                if (nonZeroCurve && MoveTarget(pivot, ref navigation.m_Front, moveDistance,
                    curve.m_Bezier, ref currentLane.m_Front.m_CurvePosition))
                {
                    if (!atTarget || !(navigation.m_Speed < 0.1f) || !(previousSpeed < 0.1f)) break;
                    currentLane.m_Front.m_LaneFlags |= TrainLaneFlags.EndReached;
                    targetState.TargetReached = true;
                    if ((currentLane.m_Front.m_LaneFlags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return)) == 0)
                    {
                        for (int i = 0; i < targetState.NavigationLanes.Count; i++)
                        {
                            TrainLaneFlags flags = targetState.NavigationLanes[i].m_Flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return);
                            if (flags == 0) continue;
                            currentLane.m_Front.m_LaneFlags |= flags;
                            targetState.NavigationLanes.RemoveRange(0, i + 1);
                            break;
                        }
                    }
                    break;
                }
                if (targetState.NavigationLanes.Count == 0
                    || (currentLane.m_Front.m_LaneFlags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return)) != 0)
                {
                    if (atTarget && navigation.m_Speed < 0.1f && previousSpeed < 0.1f)
                    {
                        currentLane.m_Front.m_LaneFlags |= TrainLaneFlags.EndReached;
                        targetState.TargetReached = true;
                    }
                    break;
                }
                TrainNavigationLane navLane = targetState.NavigationLanes[0];
                if ((navLane.m_Flags & (TrainLaneFlags.Reserved | TrainLaneFlags.Connection)) == 0
                    || !tickStart.TryGetLane(navLane.m_Lane, out RailEtaScopedLaneRow nextLaneRow)) break;
                if (connection && (navLane.m_Flags & TrainLaneFlags.Connection) == 0) navLane.m_Flags |= TrainLaneFlags.ResetSpeed;
                if ((currentLane.m_Front.m_LaneFlags & TrainLaneFlags.HighBeams) != 0
                    && prefabTrainData.m_TrackType != TrackTypes.Tram
                    && (nextLaneRow.TrackLane.m_Flags & TrackLaneFlags.Station) == 0)
                    navLane.m_Flags |= TrainLaneFlags.HighBeams;
                if (nonZeroCurve)
                {
                    tempCache = currentLane.m_FrontCache;
                    currentLane.m_FrontCache = new TrainBogieCache(currentLane.m_Front);
                }
                TrainLaneFlags pushBlockers = currentLane.m_Front.m_LaneFlags & TrainLaneFlags.PushBlockers;
                currentLane.m_Duration = 0f;
                currentLane.m_Distance = 0f;
                currentLane.m_Front = new TrainBogieLane(navLane);
                currentLane.m_Front.m_LaneFlags |= pushBlockers;
                targetState.NavigationLanes.RemoveAt(0);
            }
            ClampPosition(ref navigation.m_Front.m_Position, pivot, moveDistance);
            navigation.m_Front.m_Direction = math.normalizesafe(navigation.m_Front.m_Direction);
            float3 followPosition = navigation.m_Front.m_Position;
            float followDistance = math.csum(prefabTrainData.m_BogieOffsets);
            currentLane.m_Front.m_CurvePosition.z = currentLane.m_Front.m_CurvePosition.y;
            UpdateFollowerBogie(tickStart, ref currentLane.m_Rear, ref currentLane.m_RearCache,
                ref navigation.m_Rear, ref resetCache, ref tempCache, ref currentLane.m_FrontCache,
                currentLane.m_Front, followPosition, followDistance);
            if (targetState.Units.Length == 1) currentLane.m_RearCache = new TrainBogieCache(currentLane.m_Rear);
            else
            {
                followPosition = navigation.m_Rear.m_Position;
                followDistance = prefabTrainData.m_AttachOffsets.y - prefabTrainData.m_BogieOffsets.y;
            }
            TrainCurrentLane previousLane = currentLane;
            lead.CurrentLane = currentLane;
            lead.Navigation = navigation;
            for (int i = 1; i < targetState.Units.Length; i++)
            {
                UnitState unit = targetState.Units[i];
                UnitState startUnit = startVehicle.Units[i];
                TrainCurrentLane unitLane = startUnit.CurrentLane;
                TrainNavigation unitNavigation = startUnit.Navigation;
                TrainData unitTrain = unit.PrefabTrain;
                if ((startUnit.Train.m_Flags & Game.Vehicles.TrainFlags.Reversed) != 0)
                {
                    unitTrain.m_BogieOffsets = unitTrain.m_BogieOffsets.yx;
                    unitTrain.m_AttachOffsets = unitTrain.m_AttachOffsets.yx;
                }
                unitNavigation.m_Speed = navigation.m_Speed;
                unitLane.m_Duration += timeStep;
                unitLane.m_Distance += distance;
                Entity priorLaneEntity = unitLane.m_Front.m_Lane;
                followDistance += unitTrain.m_AttachOffsets.x - unitTrain.m_BogieOffsets.x;
                UpdateFollowerBogie(tickStart, ref unitLane.m_Front, ref unitLane.m_FrontCache,
                    ref unitNavigation.m_Front, ref resetCache, ref tempCache, ref previousLane.m_RearCache,
                    previousLane.m_Rear, followPosition, followDistance);
                if (unitLane.m_Front.m_Lane != priorLaneEntity || unitLane.m_Duration >= 30f)
                {
                    unitLane.m_Duration = 0f;
                    unitLane.m_Distance = 0f;
                }
                followPosition = unitNavigation.m_Front.m_Position;
                followDistance = math.csum(unitTrain.m_BogieOffsets);
                UpdateFollowerBogie(tickStart, ref unitLane.m_Rear, ref unitLane.m_RearCache,
                    ref unitNavigation.m_Rear, ref resetCache, ref tempCache, ref unitLane.m_FrontCache,
                    unitLane.m_Front, followPosition, followDistance);
                targetState.Units[i - 1].CurrentLane = previousLane;
                if (i == targetState.Units.Length - 1) unitLane.m_RearCache = new TrainBogieCache(unitLane.m_Rear);
                else
                {
                    followPosition = unitNavigation.m_Rear.m_Position;
                    followDistance = unitTrain.m_AttachOffsets.y - unitTrain.m_BogieOffsets.y;
                }
                previousLane = unitLane;
                unit.CurrentLane = unitLane;
                unit.Navigation = unitNavigation;
            }
            targetState.Units[targetState.Units.Length - 1].CurrentLane = previousLane;
            if (targetState.HasOdometer) targetState.Odometer = odometer;
        }

        private static void RequestSignal(SimulationState nextState, VehicleState vehicle,
            Entity lane, bool needed)
        {
            if (needed) nextState.RequestSignal(vehicle.Controller, lane, vehicle.Priority);
        }

        private struct FrozenTrainLaneSpeedIterator
        {
            private readonly SimulationState m_State;
            private readonly Entity m_Controller;
            private readonly int m_Priority;
            private readonly float m_TimeStep;
            private readonly float m_SafeTimeStep;
            private readonly float m_CurrentSpeed;
            private readonly float3 m_RearPosition;
            private readonly bool m_PushBlockers;
            private Entity m_Lane;
            private Curve m_Curve;
            private float2 m_CurveOffset;
            private float3 m_PrevPosition;
            private float m_PrevDistance;
            private float3 m_CurrentPosition;
            private float m_Distance;

            internal TrainData PrefabTrain;
            internal ObjectGeometryData PrefabGeometry;
            internal Bounds1 SpeedRange;
            internal float MaxSpeed;
            internal Entity Blocker;
            internal BlockerType BlockerType;
            internal byte BlockerSource;
            internal BlockerEvidenceState BlockerEvidence;

            internal FrozenTrainLaneSpeedIterator(SimulationState state, Entity controller, int priority,
                float timeStep, float currentSpeed, Bounds1 speedRange, float3 rearPosition,
                bool pushBlockers, float3 currentPosition)
            {
                this = default;
                m_State = state;
                m_Controller = controller;
                m_Priority = priority;
                m_TimeStep = timeStep;
                m_SafeTimeStep = timeStep + 0.5f;
                m_CurrentSpeed = currentSpeed;
                m_RearPosition = rearPosition;
                m_PushBlockers = pushBlockers;
                m_CurrentPosition = currentPosition;
                SpeedRange = speedRange;
                MaxSpeed = speedRange.max;
            }

            internal bool IterateFirstLane(Entity lane, float4 curveOffset, bool exclusive,
                bool ignoreObstacles, bool skipCurrent, out bool needSignal)
            {
                if (!m_State.TryGetLane(lane, out RailEtaScopedLaneRow row)) { needSignal = false; return false; }
                Curve curve = row.Curve;
                needSignal = false;
                float3 position = MathUtils.Position(curve.m_Bezier, curveOffset.y);
                m_PrevPosition = m_CurrentPosition;
                m_PrevDistance = 0f - (PrefabTrain.m_AttachOffsets.x - PrefabTrain.m_BogieOffsets.x);
                m_Distance = math.distance(m_CurrentPosition, position);
                m_Distance = math.min(m_Distance, math.distance(m_RearPosition, position) - math.max(1f, math.csum(PrefabTrain.m_BogieOffsets)));
                m_Distance -= PrefabTrain.m_AttachOffsets.x - PrefabTrain.m_BogieOffsets.x;
                if (row.HasTrackLane != 0)
                {
                    needSignal = row.HasSignal != 0 && lane != m_Lane;
                    m_Lane = lane;
                    m_Curve = curve;
                    m_CurveOffset = curveOffset.yw;
                    m_CurrentPosition = position;
                    int yieldOverride = row.SignalType == (byte)LaneSignalType.Stop ? -1 : row.SignalType == (byte)LaneSignalType.Yield ? 1 : 0;
                    float speed = VehicleUtils.GetMaxDriveSpeed(PrefabTrain, row.TrackLane);
                    if (!exclusive && row.HasReservation != 0 && row.Reservation.GetPriority() == 102) speed *= 0.5f;
                    ApplyLimit(speed, Entity.Null, BlockerType.Limit);
                    if (!ignoreObstacles)
                    {
                        if (!exclusive && !skipCurrent) CheckCurrentLane(m_Distance, curveOffset.yz, exclusive);
                        CheckOverlappingLanes(m_Distance, curveOffset.z, yieldOverride, exclusive);
                    }
                }
                float3 end = MathUtils.Position(curve.m_Bezier, curveOffset.w);
                float delta = math.abs(curveOffset.w - curveOffset.y);
                float length = math.max(0.001f, math.lerp(math.distance(position, end), curve.m_Length * delta, delta));
                if (length > 1f) { m_PrevPosition = m_CurrentPosition; m_PrevDistance = m_Distance; }
                m_CurrentPosition = end;
                m_Distance += length;
                float brakingDistance = VehicleUtils.GetBrakingDistance(PrefabTrain, MaxSpeed, m_SafeTimeStep)
                    + VehicleUtils.GetSignalDistance(PrefabTrain, MaxSpeed);
                return (m_Distance - 10f >= brakingDistance) | (MaxSpeed == SpeedRange.min);
            }

            internal bool IteratePrevLane(Entity lane)
            {
                if (lane == m_Lane || !m_State.TryGetLane(lane, out RailEtaScopedLaneRow row)
                    || row.HasTrackLane == 0) return false;
                bool needSignal = row.HasSignal != 0;
                m_Lane = lane;
                ApplyLimit(VehicleUtils.GetMaxDriveSpeed(PrefabTrain, row.TrackLane), Entity.Null, BlockerType.Limit);
                return needSignal;
            }

            internal bool IterateNextLane(Entity lane, float2 curveOffset, float minOffset,
                bool exclusive, bool ignoreObstacles, out bool needSignal)
            {
                needSignal = false;
                if (!m_State.TryGetLane(lane, out RailEtaScopedLaneRow row) || row.HasCurve == 0) return false;
                if (row.HasTrackLane == 0)
                {
                    float3 connectionEnd = MathUtils.Position(row.Curve.m_Bezier, curveOffset.y);
                    float connectionDelta = math.abs(curveOffset.y - curveOffset.x);
                    float connectionLength = math.max(0.001f, math.lerp(math.distance(m_CurrentPosition, connectionEnd), row.Curve.m_Length * connectionDelta, connectionDelta));
                    if (connectionLength > 1f) { m_PrevPosition = m_CurrentPosition; m_PrevDistance = m_Distance; }
                    m_CurrentPosition = connectionEnd;
                    m_Distance += connectionLength;
                    float connectionBrakingDistance = VehicleUtils.GetBrakingDistance(PrefabTrain, MaxSpeed, m_SafeTimeStep)
                        + VehicleUtils.GetSignalDistance(PrefabTrain, MaxSpeed);
                    return (m_Distance - 10f >= connectionBrakingDistance) | (MaxSpeed == SpeedRange.min);
                }
                float speed = VehicleUtils.GetMaxDriveSpeed(PrefabTrain, row.TrackLane);
                int yieldOverride = 0;
                Entity blocker = Entity.Null;
                BlockerType blockerType = BlockerType.Limit;
                if (row.HasSignal != 0)
                {
                    needSignal = true;
                    LaneSignalType signal = (LaneSignalType)row.SignalType;
                    if (signal == LaneSignalType.Stop)
                    {
                        if ((m_Priority < 108 || ((LaneSignalFlags)row.SignalFlags & LaneSignalFlags.Physical) != 0)
                            && VehicleUtils.GetBrakingDistance(PrefabTrain, m_CurrentSpeed, 0f) <= m_Distance + 1f)
                        { speed = 0f; blocker = row.SignalBlocker; blockerType = BlockerType.Signal; yieldOverride = 1; }
                        else yieldOverride = -1;
                    }
                    else if (signal == LaneSignalType.SafeStop
                        && (m_Priority < 108 || ((LaneSignalFlags)row.SignalFlags & LaneSignalFlags.Physical) != 0)
                        && VehicleUtils.GetBrakingDistance(PrefabTrain, m_CurrentSpeed, 0f) <= m_Distance)
                    { speed = 0f; blocker = row.SignalBlocker; blockerType = BlockerType.Signal; }
                    else if (signal == LaneSignalType.Yield) yieldOverride = 1;
                }
                float brakingSpeed = speed == 0f
                    ? VehicleUtils.GetMaxBrakingSpeed(PrefabTrain,
                        math.max(0f, m_Distance - math.select(10f, 0.5f, (PrefabTrain.m_TrackType & TrackTypes.Tram) != 0)), m_SafeTimeStep)
                    : math.max(speed, VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, m_Distance, speed, m_TimeStep));
                ApplyLimit(brakingSpeed, blocker, blockerType);
                if (!ignoreObstacles)
                {
                    m_Lane = lane; m_Curve = row.Curve; m_CurveOffset = curveOffset;
                    CheckCurrentLane(m_Distance, minOffset, exclusive);
                    CheckOverlappingLanes(m_Distance, minOffset, yieldOverride, exclusive);
                }
                float3 end = MathUtils.Position(row.Curve.m_Bezier, curveOffset.y);
                float delta = math.abs(curveOffset.y - curveOffset.x);
                float length = math.max(0.001f, math.lerp(math.distance(m_CurrentPosition, end), row.Curve.m_Length * delta, delta));
                if (length > 1f) { m_PrevPosition = m_CurrentPosition; m_PrevDistance = m_Distance; }
                m_CurrentPosition = end;
                m_Distance += length;
                float brakingDistance = VehicleUtils.GetBrakingDistance(PrefabTrain, MaxSpeed, m_SafeTimeStep)
                    + VehicleUtils.GetSignalDistance(PrefabTrain, MaxSpeed);
                return (m_Distance - 10f >= brakingDistance) | (MaxSpeed == SpeedRange.min);
            }

            internal bool IterateTarget(Entity lane, bool ignoreObstacles)
            {
                float speed = VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, m_Distance, m_TimeStep);
                if (speed >= MaxSpeed) return false;
                Entity blocker = Entity.Null;
                BlockerType type = BlockerType.None;
                byte source = 0;
                if (m_State.TryGetLane(lane, out RailEtaScopedLaneRow row) && row.HasReservation != 0
                    && !ignoreObstacles && !m_State.IsSameController(row.ReservationBlocker, m_Controller))
                {
                    blocker = row.ReservationBlocker;
                    type = BlockerType.Continuing;
                    source = 4;
                    BlockerEvidence = new BlockerEvidenceState
                    {
                        Source = source,
                        BlockerEntity = blocker,
                        CheckedLane = lane,
                        ReservationPriority = row.Reservation.GetPriority(),
                        ReservationOffset = row.Reservation.GetOffset(),
                        Distance = m_Distance,
                        SpeedBefore = MaxSpeed,
                        LimitedSpeed = MathUtils.Clamp(speed, SpeedRange)
                    };
                }
                MaxSpeed = MathUtils.Clamp(speed, SpeedRange);
                Blocker = blocker;
                BlockerType = type;
                BlockerSource = source;
                if (source == 0) BlockerEvidence = default;
                return true;
            }

            internal bool IterateTarget()
            {
                float speed = VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, m_Distance, m_TimeStep);
                if (speed >= MaxSpeed) return false;
                MaxSpeed = MathUtils.Clamp(speed, SpeedRange);
                Blocker = Entity.Null;
                BlockerType = BlockerType.None;
                BlockerSource = 0;
                BlockerEvidence = default;
                return true;
            }

            private void CheckCurrentLane(float distance, float2 minOffset, bool exclusive)
            {
                distance -= exclusive ? 10f : 1f;
                List<OccupancyState> occupancies = m_State.Occupancies;
                for (int i = 0; i < occupancies.Count; i++)
                {
                    OccupancyState occupancy = occupancies[i];
                    if (occupancy.Lane != m_Lane || m_State.IsSameController(occupancy.Vehicle, m_Controller)) continue;
                    var evidence = new BlockerEvidenceState
                    {
                        CheckedLane = m_Lane,
                        Occupancy = occupancy.CurvePosition,
                        Distance = distance
                    };
                    if (exclusive)
                    {
                        ApplyLimit(VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, distance, m_SafeTimeStep),
                            occupancy.Vehicle, BlockerType.Continuing, 1, evidence);
                    }
                    else if (!(occupancy.CurvePosition.y <= minOffset.y)
                        || (!(occupancy.CurvePosition.y < 1f) && !(occupancy.CurvePosition.x <= minOffset.x)))
                    {
                        UpdateMaxSpeed(occupancy.Vehicle, BlockerType.Continuing,
                            GetObjectSpeed(occupancy.Vehicle, occupancy.CurvePosition.x), occupancy.CurvePosition.x,
                            1f, distance, 0f, 1, evidence);
                    }
                }
            }

            private void CheckOverlappingLanes(float originalDistance, float originalMinOffset,
                int yieldOverride, bool exclusive)
            {
                float distance = originalDistance - 10f;
                originalDistance -= 1f;
                Bezier4x3 bezier = m_Curve.m_Bezier;
                float2 curveOffset = m_CurveOffset;
                float length = m_Curve.m_Length;
                int priority = m_Priority;
                if (m_State.TryGetLane(m_Lane, out RailEtaScopedLaneRow ownRow)
                    && ownRow.HasReservation != 0 && ownRow.Reservation.GetPriority() >= 108 && priority < 106) priority = 106;
                Entity sourceLane = m_Lane;
                LaneState sourceState = m_State.FindLane(sourceLane);
                RailEtaScopedLaneRow[] overlaps = sourceState?.Overlaps ?? Array.Empty<RailEtaScopedLaneRow>();
                for (int i = 0; i < overlaps.Length; i++)
                {
                    RailEtaScopedLaneRow overlap = overlaps[i];
                    if (((OverlapFlags)overlap.OverlapFlags & OverlapFlags.Water) != 0) continue;
                    float4 offsets = new float4(overlap.OverlapThisStart, overlap.OverlapThisEnd,
                        overlap.OverlapOtherStart, overlap.OverlapOtherEnd) * 0.003921569f;
                    if (offsets.y <= curveOffset.x || !m_State.TryGetLane(overlap.OtherLane, out RailEtaScopedLaneRow other)) continue;
                    BlockerType type = (((OverlapFlags)overlap.OverlapFlags & (OverlapFlags.MergeEnd | OverlapFlags.MergeMiddleEnd)) != 0)
                        ? BlockerType.Continuing : BlockerType.Crossing;
                    var overlapEvidence = new BlockerEvidenceState
                    {
                        CheckedLane = sourceLane,
                        OtherLane = overlap.OtherLane,
                        OverlapFlags = overlap.OverlapFlags,
                        OverlapOffsets = offsets,
                        PriorityDelta = overlap.OverlapPriorityDelta,
                        Parallelism = overlap.OverlapParallelism * (1f / 128f)
                    };
                    if (exclusive && (other.TrackLane.m_Flags & TrackLaneFlags.Exclusive) != 0)
                    {
                        if (other.HasReservation != 0 && other.Reservation.GetPriority() >= m_Priority)
                        {
                            BlockerEvidenceState reservationEvidence = overlapEvidence;
                            reservationEvidence.ReservationPriority = other.Reservation.GetPriority();
                            reservationEvidence.ReservationOffset = other.Reservation.GetOffset();
                            ApplyLimit(VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, distance, m_SafeTimeStep),
                                other.ReservationBlocker, type, 3, reservationEvidence);
                        }
                        ApplyExclusiveOccupants(overlap.OtherLane, distance, type, overlapEvidence);
                        continue;
                    }
                    m_Lane = overlap.OtherLane; m_Curve = other.Curve; m_CurveOffset = offsets.zw;
                    float otherMinimum = math.max(0f, originalMinOffset - offsets.x) + offsets.z;
                    float overlapDistance = length * (offsets.x - curveOffset.x);
                    float distanceOffset = originalDistance + overlapDistance;
                    float distanceFactor = overlap.OverlapParallelism * (1f / 128f);
                    int otherPriority = priority;
                    if ((((OverlapFlags)overlap.OverlapFlags & (OverlapFlags.MergeStart | OverlapFlags.MergeMiddleStart)) == 0)
                        && offsets.x > originalMinOffset)
                    {
                        int signalOverride = yieldOverride;
                        if (other.SignalType == (byte)LaneSignalType.Stop) signalOverride++;
                        else if (other.SignalType == (byte)LaneSignalType.Yield) signalOverride--;
                        int delta = signalOverride != 0 ? signalOverride : overlap.OverlapPriorityDelta;
                        otherPriority -= delta;
                        if (other.HasReservation != 0
                            && (other.Reservation.GetOffset() > math.max(otherMinimum, m_CurveOffset.x)
                                || other.Reservation.GetPriority() > otherPriority))
                        {
                            BlockerEvidenceState reservationEvidence = overlapEvidence;
                            reservationEvidence.ReservationPriority = other.Reservation.GetPriority();
                            reservationEvidence.ReservationOffset = other.Reservation.GetOffset();
                            ApplyLimit(VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, distanceOffset, m_SafeTimeStep),
                                other.ReservationBlocker, type, 3, reservationEvidence);
                        }
                    }
                    m_CurrentPosition = MathUtils.Position(m_Curve.m_Bezier, m_CurveOffset.x);
                    List<OccupancyState> occupancies = m_State.Occupancies;
                    for (int j = 0; j < occupancies.Count; j++)
                    {
                        OccupancyState occupancy = occupancies[j];
                        if (occupancy.Lane != overlap.OtherLane || m_State.IsSameController(occupancy.Vehicle, m_Controller)) continue;
                        float2 position = occupancy.CurvePosition;
                        float objectSpeed = GetObjectSpeed(occupancy.Vehicle, position.x);
                        if ((((OverlapFlags)overlap.OverlapFlags & (OverlapFlags.MergeStart | OverlapFlags.MergeMiddleStart)) == 0)
                            && (offsets.x >= originalMinOffset || position.y > offsets.z))
                        {
                            VehicleState otherVehicle = m_State.FindVehicle(occupancy.Vehicle);
                            int occupantPriority = otherVehicle?.Priority ?? 0;
                            if (occupantPriority - otherPriority > 0)
                                position.y += objectSpeed * 2f / math.max(1f, m_Curve.m_Length);
                        }
                        if (position.y > otherMinimum)
                        {
                            BlockerEvidenceState occupancyEvidence = overlapEvidence;
                            occupancyEvidence.Occupancy = occupancy.CurvePosition;
                            UpdateMaxSpeed(occupancy.Vehicle, type, objectSpeed, position.x,
                                distanceFactor, distanceOffset, overlapDistance, 2, occupancyEvidence);
                        }
                    }
                }
            }

            private void ApplyExclusiveOccupants(Entity lane, float distance, BlockerType type,
                BlockerEvidenceState evidence)
            {
                List<OccupancyState> occupancies = m_State.Occupancies;
                for (int i = 0; i < occupancies.Count; i++)
                    if (occupancies[i].Lane == lane && !m_State.IsSameController(occupancies[i].Vehicle, m_Controller))
                    {
                        BlockerEvidenceState occupancyEvidence = evidence;
                        occupancyEvidence.Occupancy = occupancies[i].CurvePosition;
                        ApplyLimit(VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, distance, m_SafeTimeStep),
                            occupancies[i].Vehicle, type, 2, occupancyEvidence);
                    }
            }

            private float GetObjectSpeed(Entity entity, float curveOffset)
            {
                UnitState unit = m_State.FindOccupant(entity);
                if (unit == null) return 0f;
                return math.dot(unit.Moving.m_Velocity,
                    math.normalizesafe(MathUtils.Tangent(m_Curve.m_Bezier, curveOffset)));
            }

            private void UpdateMaxSpeed(Entity entity, BlockerType blockerType, float objectSpeed,
                float laneOffset, float distanceFactor, float distanceOffset, float overlapOffset, byte source = 0,
                BlockerEvidenceState evidence = default)
            {
                UnitState unit = m_State.FindOccupant(entity);
                if (unit == null) return;
                TrainData otherTrain = unit.PrefabTrain;
                float2 attach = otherTrain.m_AttachOffsets - otherTrain.m_BogieOffsets;
                float frontOffset = math.select(attach.y, attach.x,
                    (unit.Train.m_Flags & Game.Vehicles.TrainFlags.Reversed) != 0);
                if ((laneOffset - m_CurveOffset.y) * m_Curve.m_Length >= frontOffset) return;
                float distance = math.distance(MathUtils.Position(m_Curve.m_Bezier,
                    math.max(m_CurveOffset.x, laneOffset)), m_CurrentPosition);
                distance -= math.max(0f, m_CurveOffset.x - laneOffset) * m_Curve.m_Length;
                distance = math.dot(unit.Transform.m_Position - m_CurrentPosition,
                    m_CurrentPosition - m_PrevPosition) < 0f
                    ? math.min(distance, math.distance(unit.Transform.m_Position, m_PrevPosition)
                        + m_PrevDistance - m_Distance - math.min(0f, overlapOffset))
                    : math.min(distance, math.distance(unit.Transform.m_Position, m_CurrentPosition));
                distance = (distance - frontOffset) * distanceFactor + distanceOffset;
                float maxSpeed;
                if (objectSpeed > 0.001f)
                {
                    objectSpeed = math.max(0f, objectSpeed - otherTrain.m_Braking * m_TimeStep * 2f) * distanceFactor;
                    if (PrefabTrain.m_Braking >= otherTrain.m_Braking)
                    {
                        distance += objectSpeed * m_SafeTimeStep;
                        maxSpeed = VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, distance, objectSpeed, m_SafeTimeStep);
                    }
                    else
                    {
                        distance += VehicleUtils.GetBrakingDistance(otherTrain, objectSpeed, m_SafeTimeStep);
                        maxSpeed = VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, distance, m_SafeTimeStep);
                    }
                }
                else maxSpeed = VehicleUtils.GetMaxBrakingSpeed(PrefabTrain, distance, m_SafeTimeStep);
                evidence.Distance = distance;
                evidence.DistanceFactor = distanceFactor;
                evidence.DistanceOffset = distanceOffset;
                ApplyLimit(maxSpeed, entity, blockerType, source, evidence);
            }

            private void ApplyLimit(float speed, Entity blocker, BlockerType type, byte source = 0,
                BlockerEvidenceState evidence = default)
            {
                speed = MathUtils.Clamp(speed, SpeedRange);
                if (speed >= MaxSpeed) return;
                evidence.Source = source;
                evidence.BlockerEntity = blocker;
                evidence.SpeedBefore = MaxSpeed;
                evidence.LimitedSpeed = speed;
                MaxSpeed = speed;
                Blocker = blocker;
                BlockerType = type;
                BlockerSource = source;
                BlockerEvidence = evidence;
            }
        }

        private static bool HasConnection(TrainCurrentLane lane) =>
            ((lane.m_Front.m_LaneFlags | lane.m_FrontCache.m_LaneFlags
              | lane.m_Rear.m_LaneFlags | lane.m_RearCache.m_LaneFlags) & TrainLaneFlags.Connection) != 0;

        private static void Swap(ref float3 x, ref float3 y) { float3 value = x; x = y; y = value; }

        private static void ClampPosition(ref float3 position, float3 original, float maxDistance) =>
            position = original + MathUtils.ClampLength(position - original, maxDistance);

        private static bool ShouldResetCache(TrainBogieLane bogie, TrainBogieCache cache)
        {
            if (math.all(bogie.m_CurvePosition == bogie.m_CurvePosition.x)
                && math.all(cache.m_CurvePosition == bogie.m_CurvePosition.x)) return cache.m_Lane == bogie.m_Lane;
            return false;
        }

        private static void UpdateFollowerBogie(SimulationState state, ref TrainBogieLane bogie,
            ref TrainBogieCache cache, ref TrainBogiePosition position, ref bool resetCache,
            ref TrainBogieCache tempCache, ref TrainBogieCache nextCache, TrainBogieLane nextBogie,
            float3 followPosition, float followDistance)
        {
            TrainBogieCache oldCache = default;
            float3 oldDirection = position.m_Position - followPosition;
            if (resetCache)
            {
                if (bogie.m_Lane == nextBogie.m_Lane)
                {
                    tempCache = default;
                    nextCache = new TrainBogieCache(nextBogie);
                    nextCache.m_CurvePosition.x = bogie.m_CurvePosition.w;
                }
                else if (bogie.m_Lane != Entity.Null && nextBogie.m_Lane != Entity.Null
                    && state.TryGetLane(bogie.m_Lane, out RailEtaScopedLaneRow bogieLane)
                    && state.TryGetLane(nextBogie.m_Lane, out RailEtaScopedLaneRow nextLane))
                {
                    tempCache = new TrainBogieCache(bogie);
                    nextCache = new TrainBogieCache(nextBogie);
                    float3 bogiePosition = MathUtils.Position(bogieLane.Curve.m_Bezier, bogie.m_CurvePosition.w);
                    MathUtils.Distance(bogieLane.Curve.m_Bezier,
                        MathUtils.Position(nextLane.Curve.m_Bezier, nextBogie.m_CurvePosition.x), out tempCache.m_CurvePosition.y);
                    MathUtils.Distance(nextLane.Curve.m_Bezier, bogiePosition, out nextCache.m_CurvePosition.x);
                }
            }
            resetCache = ShouldResetCache(bogie, cache);
            while (true)
            {
                if (bogie.m_Lane != Entity.Null && state.TryGetLane(bogie.m_Lane, out RailEtaScopedLaneRow lane))
                {
                    if (bogie.m_Lane == nextBogie.m_Lane && bogie.m_CurvePosition.w == nextBogie.m_CurvePosition.w)
                    {
                        float end = bogie.m_CurvePosition.w;
                        bogie.m_CurvePosition.zw = nextBogie.m_CurvePosition.y;
                        if (MoveFollowerTarget(followPosition, ref position, followDistance,
                            lane.Curve.m_Bezier, ref bogie.m_CurvePosition)) { bogie.m_CurvePosition.w = end; break; }
                        bogie.m_CurvePosition.w = end;
                    }
                    else
                    {
                        bogie.m_CurvePosition.z = bogie.m_CurvePosition.w;
                        if (MoveFollowerTarget(followPosition, ref position, followDistance,
                            lane.Curve.m_Bezier, ref bogie.m_CurvePosition)) break;
                    }
                }
                if (nextBogie.m_Lane == bogie.m_Lane && nextBogie.m_CurvePosition.xw.Equals(bogie.m_CurvePosition.xw)) break;
                oldCache = cache;
                cache = new TrainBogieCache(bogie);
                if (tempCache.m_Lane != Entity.Null)
                {
                    bogie = new TrainBogieLane(tempCache);
                    tempCache = default;
                }
                else
                {
                    bogie = new TrainBogieLane(nextCache);
                    nextCache = new TrainBogieCache(nextBogie);
                }
            }
            float3 direction = position.m_Position - followPosition;
            if (math.dot(direction, oldDirection) <= 0f) { direction = oldDirection; position.m_Direction = -oldDirection; }
            if (MathUtils.TryNormalize(ref direction, followDistance))
            {
                position.m_Position = followPosition + direction;
                position.m_Direction = math.normalizesafe(position.m_Direction);
            }
            tempCache = oldCache;
        }

        private static bool MoveTarget(float3 comparePosition, ref TrainBogiePosition targetPosition,
            float minDistance, Bezier4x3 curve, ref float4 curveDelta)
        {
            float3 end = MathUtils.Position(curve, curveDelta.w);
            if (math.distance(comparePosition, end) < minDistance)
            {
                float middle = math.lerp(curveDelta.y, curveDelta.w, 0.5f);
                if (math.distance(comparePosition, MathUtils.Position(curve, middle)) < minDistance)
                {
                    curveDelta.y = curveDelta.w;
                    targetPosition.m_Position = end;
                    targetPosition.m_Direction = MathUtils.Tangent(curve, curveDelta.w) * math.sign(curveDelta.w - curveDelta.x);
                    return false;
                }
            }
            float3 start = MathUtils.Position(curve, curveDelta.y);
            if (math.distance(comparePosition, start) >= minDistance)
            {
                targetPosition.m_Position = start;
                targetPosition.m_Direction = MathUtils.Tangent(curve, curveDelta.y) * math.sign(curveDelta.w - curveDelta.x);
                return true;
            }
            float2 range = curveDelta.yw;
            for (int i = 0; i < 8; i++)
            {
                float middle = math.lerp(range.x, range.y, 0.5f);
                if (math.distance(comparePosition, MathUtils.Position(curve, middle)) < minDistance) range.x = middle;
                else range.y = middle;
            }
            curveDelta.y = range.y;
            targetPosition.m_Position = MathUtils.Position(curve, range.y);
            targetPosition.m_Direction = MathUtils.Tangent(curve, range.y) * math.sign(curveDelta.w - curveDelta.x);
            return true;
        }

        private static bool MoveFollowerTarget(float3 comparePosition, ref TrainBogiePosition targetPosition,
            float maxDistance, Bezier4x3 curve, ref float4 curveDelta)
        {
            float3 end = MathUtils.Position(curve, curveDelta.w);
            if (math.distance(comparePosition, end) > maxDistance)
            {
                curveDelta.y = curveDelta.w;
                targetPosition.m_Position = end;
                targetPosition.m_Direction = MathUtils.Tangent(curve, curveDelta.w) * math.sign(curveDelta.w - curveDelta.x);
                return false;
            }
            float2 range = curveDelta.yw;
            for (int i = 0; i < 8; i++)
            {
                float middle = math.lerp(range.x, range.y, 0.5f);
                if (math.distance(comparePosition, MathUtils.Position(curve, middle)) > maxDistance) range.x = middle;
                else range.y = middle;
            }
            curveDelta.y = range.x;
            targetPosition.m_Position = MathUtils.Position(curve, range.x);
            targetPosition.m_Direction = MathUtils.Tangent(curve, range.x) * math.sign(curveDelta.w - curveDelta.x);
            return true;
        }

        private static RailEtaPrediction Failure(RailEtaRequest request, RailEtaFailure failure, string reason)
        {
            return new RailEtaPrediction
            {
                RequestId = request?.RequestId ?? string.Empty,
                Confidence = RailEtaConfidence.Unknown,
                Failure = failure,
                PredictorSource = "hot",
                PredictorBuildId = BuildId,
                Reason = reason
            };
        }

        private static RailEtaPrediction NotConvergedFailure(RailEtaRequest request, SimulationState state,
            Entity targetController, uint frame, TargetSimulationDiagnostics targetDiagnostics)
        {
            targetDiagnostics.Finish(frame);
            var diagnostics = new List<RailEtaDiagnosticRecord>();
            for (int i = 0; i < state.Failures.Count && diagnostics.Count < RailEtaLimits.MaxDiagnostics; i++)
            {
                SimulationFailure failure = state.Failures[i];
                diagnostics.Add(new RailEtaDiagnosticRecord
                {
                    Code = "simulation-non-target-failure",
                    Severity = RailEtaDiagnosticSeverity.Warning,
                    Message = failure.Vehicle.Index + ":" + failure.Vehicle.Version + " " + failure.Reason,
                    Frame = failure.Frame
                });
            }
            targetDiagnostics.AppendTo(diagnostics, frame);
            targetDiagnostics.AppendFinalBlockerState(state, diagnostics, frame);
            VehicleState target = state.FindVehicle(targetController);
            if (target != null && target.Units.Length > 0 && diagnostics.Count < RailEtaLimits.MaxDiagnostics)
            {
                TrainCurrentLane lane = target.Units[0].CurrentLane;
                diagnostics.Add(new RailEtaDiagnosticRecord
                {
                    Code = "target-simulation-final-state",
                    Severity = RailEtaDiagnosticSeverity.Warning,
                    Message = "lane=" + lane.m_Front.m_Lane.Index + ":" + lane.m_Front.m_Lane.Version
                        + " pos=" + lane.m_Front.m_CurvePosition.y.ToString("F4")
                        + " speed=" + target.Units[0].Navigation.m_Speed.ToString("F3")
                        + " flags=" + (uint)lane.m_Front.m_LaneFlags
                        + " target=" + target.Target.Index + ":" + target.Target.Version
                        + " endpoint=" + target.TicketEndpoint.Index + ":" + target.TicketEndpoint.Version
                        + " reached=" + target.TargetReached
                        + " boarding=" + target.Boarding + " holdReleased=" + target.HoldReleased
                        + " dep=" + target.DepartureFrame + " dwell=" + target.DwellDeadlineFrame
                        + " pathIndex=" + target.PathOwner.m_ElementIndex + " pathState=" + (uint)target.PathOwner.m_State
                        + " nav=" + target.NavigationLanes.Count + " path=" + target.PathElements.Count
                        + " pending=" + target.PendingRouteSegmentIndex + " ready=" + target.PendingRouteReadyFrame
                        + " switch=" + target.PathSwitchFrame
                        + " blocker=" + target.Blocker.m_Blocker.Index + ":" + target.Blocker.m_Blocker.Version
                        + " blockerType=" + target.Blocker.m_Type + " blockerSource=" + target.BlockerSource,
                    Frame = frame,
                    VehicleId = new RailVehicleId(RailEtaEntityId.Pack(target.Controller))
                });
            }
            return new RailEtaPrediction
            {
                RequestId = request?.RequestId ?? string.Empty,
                Confidence = RailEtaConfidence.Unknown,
                Failure = RailEtaFailure.NotConverged,
                Trace = targetDiagnostics.Trace,
                TraceTruncated = targetDiagnostics.TraceTruncated,
                EventCount = targetDiagnostics.EventCount,
                PredictorSource = "hot",
                PredictorBuildId = BuildId,
                Diagnostics = diagnostics.ToArray(),
                Reason = "prediction-exceeded-300-game-minutes"
            };
        }

        private static RailEtaPrediction Success(RailEtaWorldSnapshot snapshot, RailEtaRequest request,
            SimulationState state, uint frame, TargetSimulationDiagnostics targetDiagnostics)
        {
            targetDiagnostics.Finish(frame);
            var diagnostics = new List<RailEtaDiagnosticRecord>();
            for (int i = 0; i < state.Failures.Count; i++)
            {
                if (diagnostics.Count >= RailEtaLimits.MaxDiagnostics) break;
                SimulationFailure failure = state.Failures[i];
                diagnostics.Add(new RailEtaDiagnosticRecord
                {
                    Code = "simulation-non-target-failure",
                    Severity = RailEtaDiagnosticSeverity.Warning,
                    Message = failure.Vehicle.Index + ":" + failure.Vehicle.Version + " " + failure.Reason,
                    Frame = failure.Frame
                });
            }
            targetDiagnostics.AppendTo(diagnostics, frame);
            return new RailEtaPrediction
            {
                RequestId = request.RequestId,
                Confidence = RailEtaConfidence.Full,
                Failure = RailEtaFailure.None,
                PredictedArrivalFrame = frame,
                Checkpoints = new[]
                {
                    new RailEtaCheckpointPrediction
                    {
                        CheckpointId = request.TargetCheckpointId,
                        ArrivalFrame = frame
                    }
                },
                Trace = targetDiagnostics.Trace,
                TraceTruncated = targetDiagnostics.TraceTruncated,
                EventCount = targetDiagnostics.EventCount,
                PredictorSource = "hot",
                PredictorBuildId = BuildId,
                Diagnostics = diagnostics.ToArray(),
                Reason = state.Failures.Count == 0 ? "vanilla-frame-simulation-complete" : "vanilla-frame-simulation-incomplete"
            };
        }
    }
}
