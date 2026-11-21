using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineProfile
    {
        private const uint NEW_LINE_STABLE_FRAMES = 300;

        internal sealed class RoadRouteSnapshot
        {
            internal Entity[] Waypoints = System.Array.Empty<Entity>();
            internal Entity[] Stops = System.Array.Empty<Entity>();
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private NativeHashMap<Entity, ulong> m_LineWaypointSignature;
        private NativeHashMap<Entity, uint> m_LineStableSinceFrame;
        private NativeHashSet<Entity> m_DiagnosedLines;
        private readonly Dictionary<Entity, Entity> m_OriginStopByWaypoint = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, Entity> m_OriginWaypointByLine = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, RoadRouteSnapshot> m_RoadSnapshots = new Dictionary<Entity, RoadRouteSnapshot>();
        private readonly Dictionary<Entity, uint> m_LifecycleFailureFrames = new Dictionary<Entity, uint>();

        private const uint LIFECYCLE_FAILURE_LOG_COOLDOWN_FRAMES = 1800;

        public LineProfile(ModRuntimeHostSystem runtime)
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

        private bool TryGetOriginDistanceSquared(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints, out float distanceSq)
        {
            distanceSq = float.MaxValue;
            EntityManager entityManager = m_Runtime.EntityManager;
            if (waypoints.Length == 0 || !entityManager.HasComponent<Game.Objects.Transform>(vehicle))
                return false;

            Entity line = m_Runtime.m_Resolve.Line(vehicle);
            Entity stop = ResolveOriginStop(line, waypoints[0].m_Waypoint);
            if (stop == Entity.Null || !entityManager.HasComponent<Game.Objects.Transform>(stop))
                return false;

            float3 vehiclePos = entityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
            float3 stopPos = entityManager.GetComponentData<Game.Objects.Transform>(stop).m_Position;
            distanceSq = math.lengthsq(vehiclePos - stopPos);
            return true;
        }

        private Entity ResolveOriginStop(Entity line, Entity waypoint)
        {
            if (waypoint == Entity.Null)
                return Entity.Null;

            if (line != Entity.Null)
            {
                if (m_OriginWaypointByLine.TryGetValue(line, out Entity oldWaypoint)
                    && oldWaypoint != waypoint)
                    m_OriginStopByWaypoint.Remove(oldWaypoint);
                m_OriginWaypointByLine[line] = waypoint;
            }

            if (m_OriginStopByWaypoint.TryGetValue(waypoint, out Entity cachedStop))
                return cachedStop;

            Entity stop = waypoint;
            EntityManager entityManager = m_Runtime.EntityManager;
            if (entityManager.HasComponent<Connected>(stop))
                stop = entityManager.GetComponentData<Connected>(stop).m_Connected;

            m_OriginStopByWaypoint[waypoint] = stop;
            return stop;
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

        public bool Stable(Entity line)
        {
            return line != Entity.Null
                && !m_Runtime.m_LineStructureInvalidator.IsLinePending(line)
                && m_LineStableSinceFrame.TryGetValue(line, out uint stableSince)
                && m_Runtime.m_SimulationSystem.frameIndex - stableSince >= NEW_LINE_STABLE_FRAMES;
        }

        public bool IsStable(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle))
            {
                LogLifecycleFailure(line);
                return false;
            }

            if (lifecycle == LifecycleKind.Road)
                return ObserveRoadRouteSnapshot(line, waypoints);
            if (lifecycle != LifecycleKind.Rail)
                return false;
            if (m_Runtime.m_LineStructureInvalidator.IsLinePending(line))
                return false;

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
                        + " clearLineTimes=0");
                }
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

            return nowFrame - stableSince >= NEW_LINE_STABLE_FRAMES;
        }

        internal void BaselineLoadedLines()
        {
            uint stableSince = m_Runtime.m_SimulationSystem.frameIndex - NEW_LINE_STABLE_FRAMES;
            using NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle))
                    continue;
                DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
                if (lifecycle == LifecycleKind.Rail)
                {
                    m_LineWaypointSignature[line] = ComputeWaypointSignature(waypoints);
                }
                else if (lifecycle == LifecycleKind.Road)
                {
                    m_RoadSnapshots[line] = CaptureRoadRoute(waypoints);
                    m_LineWaypointSignature[line] = 0;
                }
                else
                {
                    continue;
                }
                m_LineStableSinceFrame[line] = stableSince;
            }
        }

        internal void ConfirmLoadedRoutes()
        {
            using NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle)
                    || lifecycle != LifecycleKind.Road
                    || !m_RoadSnapshots.TryGetValue(line, out RoadRouteSnapshot previous))
                {
                    continue;
                }
                DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
                if (!MatchesRoadRoute(previous, waypoints))
                    ReplaceRoadRoute(line, previous, waypoints);
            }
        }

        private void ReplaceRoadRoute(
            Entity line,
            RoadRouteSnapshot previous,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            RoadRouteSnapshot current = CaptureRoadRoute(waypoints);
            m_Runtime.m_LineStructureInvalidator.RequestRoadRoute(line, previous, current);
            m_RoadSnapshots[line] = current;
            m_LineStableSinceFrame[line] = m_Runtime.m_SimulationSystem.frameIndex;
            m_DiagnosedLines.Remove(line);
        }

        internal void ResetRailStructure(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (line == Entity.Null)
                return;

            ulong signature = ComputeWaypointSignature(waypoints);
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            bool signatureChanged = !m_LineWaypointSignature.TryGetValue(line, out ulong oldSignature)
                || oldSignature != signature;
            RemoveOriginBinding(line);
            m_LineWaypointSignature[line] = signature;
            if (signatureChanged || !m_LineStableSinceFrame.ContainsKey(line))
                m_LineStableSinceFrame[line] = frame;
            m_DiagnosedLines.Remove(line);
            for (int i = 0; i < waypoints.Length; i++)
                m_OriginStopByWaypoint.Remove(waypoints[i].m_Waypoint);
            if (waypoints.Length > 0)
                ResolveOriginStop(line, waypoints[0].m_Waypoint);
        }

        private void RemoveOriginBinding(Entity line)
        {
            if (line == Entity.Null || !m_OriginWaypointByLine.TryGetValue(line, out Entity waypoint))
                return;

            m_OriginStopByWaypoint.Remove(waypoint);
            m_OriginWaypointByLine.Remove(line);
        }

        private void LogLifecycleFailure(Entity line)
        {
            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (m_LifecycleFailureFrames.TryGetValue(line, out uint lastFrame)
                && nowFrame - lastFrame < LIFECYCLE_FAILURE_LOG_COOLDOWN_FRAMES)
            {
                return;
            }

            m_LifecycleFailureFrames[line] = nowFrame;
            m_Runtime.log.Info("[LineProfile] line=" + line.Index + " lifecycle resolution failed; stability check skipped");
        }

        private bool ObserveRoadRouteSnapshot(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (!m_RoadSnapshots.TryGetValue(line, out RoadRouteSnapshot previous))
            {
                m_RoadSnapshots[line] = CaptureRoadRoute(waypoints);
                m_LineWaypointSignature[line] = 0;
                m_LineStableSinceFrame[line] = nowFrame;
                m_DiagnosedLines.Remove(line);
                return false;
            }

            if (!MatchesRoadRoute(previous, waypoints))
            {
                ReplaceRoadRoute(line, previous, waypoints);
                return false;
            }

            if (!m_LineStableSinceFrame.TryGetValue(line, out uint stableSince))
            {
                m_LineStableSinceFrame[line] = nowFrame;
                return false;
            }

            return nowFrame - stableSince >= NEW_LINE_STABLE_FRAMES;
        }

        internal bool TryReadRoadRoute(Entity line, out RoadRouteSnapshot snapshot)
        {
            snapshot = null;
            if (line == Entity.Null || !m_RoadSnapshots.TryGetValue(line, out RoadRouteSnapshot stored))
                return false;

            snapshot = new RoadRouteSnapshot
            {
                Waypoints = (Entity[])stored.Waypoints.Clone(),
                Stops = (Entity[])stored.Stops.Clone()
            };
            return true;
        }

        private RoadRouteSnapshot CaptureRoadRoute(DynamicBuffer<RouteWaypoint> waypoints)
        {
            var snapshot = new RoadRouteSnapshot
            {
                Waypoints = new Entity[waypoints.Length],
                Stops = new Entity[waypoints.Length]
            };
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                snapshot.Waypoints[i] = waypoint;
                snapshot.Stops[i] = waypoint == Entity.Null
                    ? Entity.Null
                    : m_Runtime.m_Resolve.Stop(waypoint);
            }

            return snapshot;
        }

        private bool MatchesRoadRoute(RoadRouteSnapshot snapshot, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (snapshot.Waypoints.Length != waypoints.Length)
                return false;

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (snapshot.Waypoints[i] != waypoint)
                    return false;

                Entity stop = waypoint == Entity.Null
                    ? Entity.Null
                    : m_Runtime.m_Resolve.Stop(waypoint);
                if (snapshot.Stops[i] != stop)
                    return false;
            }

            return true;
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
            RemoveOriginBinding(line);
            m_RoadSnapshots.Remove(line);
            m_LifecycleFailureFrames.Remove(line);
        }

        internal void RemoveStability(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            RemoveStability(line);
            for (int i = 0; i < waypoints.Length; i++)
                m_OriginStopByWaypoint.Remove(waypoints[i].m_Waypoint);
        }

        public void ClearStability()
        {
            if (m_LineWaypointSignature.IsCreated) m_LineWaypointSignature.Clear();
            if (m_LineStableSinceFrame.IsCreated) m_LineStableSinceFrame.Clear();
            if (m_DiagnosedLines.IsCreated) m_DiagnosedLines.Clear();
            m_RoadSnapshots.Clear();
            m_LifecycleFailureFrames.Clear();
            m_OriginStopByWaypoint.Clear();
            m_OriginWaypointByLine.Clear();
        }

        public void Dispose()
        {
            if (m_LineWaypointSignature.IsCreated) m_LineWaypointSignature.Dispose();
            if (m_LineStableSinceFrame.IsCreated) m_LineStableSinceFrame.Dispose();
            if (m_DiagnosedLines.IsCreated) m_DiagnosedLines.Dispose();
        }
    }
}
