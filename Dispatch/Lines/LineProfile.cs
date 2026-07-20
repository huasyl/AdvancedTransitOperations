using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineProfile
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private NativeHashMap<Entity, ulong> m_LineWaypointSignature;
        private NativeHashMap<Entity, uint> m_LineStableSinceFrame;
        private NativeHashSet<Entity> m_DiagnosedLines;
        private readonly Dictionary<Entity, Entity> m_OriginStopByWaypoint = new Dictionary<Entity, Entity>();

        public LineProfile(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
            m_LineWaypointSignature = new NativeHashMap<Entity, ulong>(64, Allocator.Persistent);
            m_LineStableSinceFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);
            m_DiagnosedLines = new NativeHashSet<Entity>(64, Allocator.Persistent);
        }

        public float DistanceToOrigin(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!TryGetOriginDistanceSquared(vehicle, waypoints, out float distanceSq))
                return float.MaxValue;

            return math.sqrt(distanceSq);
        }

        public bool IsWithinOriginDistance(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints, float radiusMeters)
        {
            return TryGetOriginDistanceSquared(vehicle, waypoints, out float distanceSq)
                && distanceSq <= radiusMeters * radiusMeters;
        }

        private bool TryGetOriginDistanceSquared(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints, out float distanceSq)
        {
            distanceSq = float.MaxValue;
            EntityManager entityManager = m_Runtime.EntityManager;
            if (waypoints.Length == 0 || !entityManager.HasComponent<Game.Objects.Transform>(vehicle))
                return false;

            Entity stop = ResolveOriginStop(waypoints[0].m_Waypoint);
            if (stop == Entity.Null || !entityManager.HasComponent<Game.Objects.Transform>(stop))
                return false;

            float3 vehiclePos = entityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
            float3 stopPos = entityManager.GetComponentData<Game.Objects.Transform>(stop).m_Position;
            distanceSq = math.lengthsq(vehiclePos - stopPos);
            return true;
        }

        private Entity ResolveOriginStop(Entity waypoint)
        {
            if (waypoint == Entity.Null)
                return Entity.Null;

            if (m_OriginStopByWaypoint.TryGetValue(waypoint, out Entity cachedStop))
                return cachedStop;

            Entity stop = waypoint;
            EntityManager entityManager = m_Runtime.EntityManager;
            if (entityManager.HasComponent<Connected>(stop))
                stop = entityManager.GetComponentData<Connected>(stop).m_Connected;

            m_OriginStopByWaypoint[waypoint] = stop;
            return stop;
        }

        public bool ShouldEvaluateOriginSettle(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool atOrigin,
            bool boarding,
            bool lastBoarding,
            int targetMinute)
        {
            bool probeEnabled = RuntimeHotPathProbe.Enabled();
            if (probeEnabled)
                m_Runtime.m_PerfProbeOriginSettleCalls++;
            m_Runtime.m_RuntimeHotPathProbe.CountOriginSettleCall();
            if (vehicle == Entity.Null || waypoints.Length == 0)
                return false;

            if (atOrigin
                || boarding
                || lastBoarding
                || targetMinute >= 0
                || m_Runtime.m_VehicleStateStore.OriginArrivalCandidateSinceFrame.ContainsKey(vehicle))
            {
                if (probeEnabled)
                    m_Runtime.m_PerfProbeOriginSettleFastPathHits++;
                m_Runtime.m_RuntimeHotPathProbe.CountOriginSettleFastPath(m_Runtime.m_VehicleStateStore.OriginArrivalCandidateSinceFrame.ContainsKey(vehicle));
                return true;
            }

            Entity line = m_Runtime.m_Resolve.Line(vehicle);
            if (line == Entity.Null
                || !m_Runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
            {
                return false;
            }

            if (probeEnabled)
                m_Runtime.m_PerfProbeOriginSettleSlowPathEntered++;
            m_Runtime.m_RuntimeHotPathProbe.CountOriginSettleSlowPath();
            if (!m_Runtime.m_TrackProjection.TrySnapshot(
                    vehicle,
                    line,
                    chain.Signature,
                    m_Runtime.m_SimulationSystem.frameIndex,
                    out VehicleTrackCursor cursor))
            {
                if (probeEnabled)
                    m_Runtime.m_PerfProbeOriginSettlePreSnapshotMisses++;
                return false;
            }

            int atomCursorIndex = cursor.AtomCursorIndex;
            if (atomCursorIndex < 0 || atomCursorIndex >= chain.TrackAtoms.Count)
                return false;

            const int originAtomWindow = 2;
            bool inOriginWindow = atomCursorIndex <= originAtomWindow
                || atomCursorIndex >= math.max(0, chain.TrackAtoms.Count - 1 - originAtomWindow);
            if (inOriginWindow)
            {
                if (probeEnabled)
                    m_Runtime.m_PerfProbeOriginSettleWindowHits++;
                m_Runtime.m_RuntimeHotPathProbe.CountOriginSettleWindowHit();
            }
            return inOriginWindow;
        }

        public bool ShouldSettleAtOrigin(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            bool atOrigin,
            bool boarding,
            bool lastBoarding,
            int targetMinute)
        {
            bool waitingAtOrigin = atOrigin
                && (boarding
                    || lastBoarding
                    || targetMinute >= 0
                    || m_Runtime.m_VehicleView.IsInbound(vehicle)
                    || m_Runtime.m_VehicleStateStore.CurrentSlotMinute.ContainsKey(vehicle));

            if (!waitingAtOrigin)
            {
                if (!IsWithinOriginDistance(vehicle, waypoints, DispatchRuntimeSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS))
                {
                    m_Runtime.m_RuntimeController.ClearOriginCandidate(vehicle);
                    return false;
                }

                if (!m_Runtime.m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition))
                {
                    m_Runtime.m_RuntimeController.ClearOriginCandidate(vehicle);
                    return false;
                }

                if (nextWaypointIndex != 0 || segmentPosition < 0.92f)
                {
                    m_Runtime.m_RuntimeController.ClearOriginCandidate(vehicle);
                    return false;
                }
            }

            if (!m_Runtime.m_VehicleView.TryGetOrigin(vehicle, out uint sinceFrame))
            {
                m_Runtime.m_RuntimeController.SetOriginCandidate(vehicle, nowFrame);
                return false;
            }

            return (nowFrame - sinceFrame) >= 180;
        }

        public bool IsBorderlineOriginArrivalCandidate(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!IsWithinOriginDistance(vehicle, waypoints, DispatchRuntimeSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS))
                return false;

            if (!m_Runtime.m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition))
                return false;

            return nextWaypointIndex == 0 && segmentPosition >= 0.92f;
        }

        public bool HasBorderlineOriginArrivalCandidate(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            float slotFramesAway,
            float lineDurationFrames,
            bool lineHasHistory,
            ClockSnapshot clockSnapshot)
        {
            uint waitFrames = clockSnapshot.ToFramesCeil(2d);
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles))
                return false;

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = vehicles[i].m_Vehicle;
                if (!m_Runtime.EntityManager.Exists(vehicle))
                    continue;
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state) || state != VehicleState.Running)
                    continue;
                if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int target) && target >= 0)
                    continue;
                if (m_Runtime.m_VehicleView.TryGetCooldown(vehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                    continue;
                if (!IsBorderlineOriginArrivalCandidate(vehicle, waypoints))
                    continue;

                float etaFrames = m_Runtime.m_LineTimes.Run(vehicle, line, waypoints, nowFrame, lineDurationFrames, lineHasHistory);
                if (etaFrames != float.MaxValue && etaFrames <= slotFramesAway + waitFrames)
                    return true;
            }

            return false;
        }

        public bool ShouldHoldSpawnForNearestRunningCandidate(
            Entity nearestVehicle,
            VehicleState nearestState,
            float nearestEtaFrames,
            DynamicBuffer<RouteWaypoint> waypoints,
            ClockSnapshot clockSnapshot)
        {
            if (nearestVehicle == Entity.Null || nearestState != VehicleState.Running || nearestEtaFrames == float.MaxValue)
                return false;

            uint waitFrames = clockSnapshot.ToFramesCeil(2d);
            if (nearestEtaFrames > waitFrames)
                return false;

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (m_Runtime.m_VehicleView.TryGetCooldown(nearestVehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return false;

            if (IsWithinOriginDistance(nearestVehicle, waypoints, DispatchRuntimeSystem.ORIGIN_CONGESTION_RADIUS_METERS))
                return true;

            if (m_Runtime.m_RouteProgress.Try(nearestVehicle, out int nextWaypointIndex, out float segmentPosition))
                return nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.10f);

            return false;
        }

        public bool HasInboundNearOrigin(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            Entity ignoreVehicle,
            float radiusMeters,
            bool includePreparingVehicles = true)
        {
            EntityManager entityManager = m_Runtime.EntityManager;
            Entity station = waypoints[0].m_Waypoint;
            Entity stop = station != Entity.Null
                && entityManager.Exists(station)
                && entityManager.HasComponent<Connected>(station)
                ? entityManager.GetComponentData<Connected>(station).m_Connected
                : Entity.Null;
            if (stop == Entity.Null || !entityManager.HasComponent<Game.Objects.Transform>(stop))
                return false;

            float3 stationPos = entityManager.GetComponentData<Game.Objects.Transform>(stop).m_Position;
            float radiusSq = radiusMeters * radiusMeters;
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles))
                return false;

            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(vehicles[i].m_Vehicle);
                if (vehicle == ignoreVehicle || !entityManager.Exists(vehicle))
                    continue;
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                    continue;
                bool isPreparing = state == VehicleState.Preparing;
                if (isPreparing && !includePreparingVehicles)
                    continue;
                if (!isPreparing && !m_Runtime.m_VehicleView.IsInbound(vehicle))
                    continue;
                if (!entityManager.HasComponent<Game.Objects.Transform>(vehicle))
                    continue;

                float3 delta = entityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position - stationPos;
                if (math.lengthsq(delta) <= radiusSq)
                    return true;
            }

            return false;
        }

        public bool HasPreparingReachedOrigin(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding,
            int currentWaypointIndex)
        {
            if (vehicle == Entity.Null || waypoints.Length == 0 || currentWaypointIndex != 0)
                return false;

            return boarding;
        }

        public ulong MixSignature(ulong hash, int value)
        {
            return (hash ^ (uint)value) * 1099511628211UL;
        }

        public ulong ComputeWaypointSignature(DynamicBuffer<RouteWaypoint> waypoints)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixSignature(hash, waypoints.Length);
            for (int i = 0; i < waypoints.Length; i++)
            {
                hash = MixSignature(hash, waypoints[i].m_Waypoint.Index);
            }

            return hash;
        }

        public bool IsStable(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            ulong signature = ComputeWaypointSignature(waypoints);
            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;

            if (!m_LineWaypointSignature.TryGetValue(line, out ulong oldSignature) || oldSignature != signature)
            {
                if (RtLog.CacheInvalidationDiagnosticsEnabled)
                {
                    m_Runtime.log.Info("[LineSignatureChanged] line=" + line.Index
                        + " oldSig=" + oldSignature
                        + " newSig=" + signature
                        + " waypoints=" + waypoints.Length
                        + " frame=" + nowFrame
                        + " clearLineTimes=1");
                }
                m_Runtime.m_LineTimes.Clear();
                m_LineWaypointSignature[line] = signature;
                m_LineStableSinceFrame[line] = nowFrame;
                m_DiagnosedLines.Remove(line);
                return false;
            }

            if (!m_LineStableSinceFrame.TryGetValue(line, out uint stableSince))
            {
                m_LineStableSinceFrame[line] = nowFrame;
                return false;
            }

            return nowFrame - stableSince >= DispatchRuntimeSystem.NEW_LINE_STABLE_FRAMES;
        }

        public bool IsDiagnosed(Entity line)
        {
            return line != Entity.Null && m_DiagnosedLines.Contains(line);
        }

        public void MarkDiagnosed(Entity line)
        {
            if (line != Entity.Null)
                m_DiagnosedLines.Add(line);
        }

        public NativeArray<Entity> StabilityKeys(Allocator allocator)
        {
            return m_LineWaypointSignature.GetKeyArray(allocator);
        }

        public void RemoveStability(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_LineWaypointSignature.Remove(line);
            m_LineStableSinceFrame.Remove(line);
            m_DiagnosedLines.Remove(line);
            m_OriginStopByWaypoint.Clear();
        }

        public void ClearStability()
        {
            if (m_LineWaypointSignature.IsCreated) m_LineWaypointSignature.Clear();
            if (m_LineStableSinceFrame.IsCreated) m_LineStableSinceFrame.Clear();
            if (m_DiagnosedLines.IsCreated) m_DiagnosedLines.Clear();
            m_OriginStopByWaypoint.Clear();
        }

        public void Dispose()
        {
            if (m_LineWaypointSignature.IsCreated) m_LineWaypointSignature.Dispose();
            if (m_LineStableSinceFrame.IsCreated) m_LineStableSinceFrame.Dispose();
            if (m_DiagnosedLines.IsCreated) m_DiagnosedLines.Dispose();
        }
    }
}
