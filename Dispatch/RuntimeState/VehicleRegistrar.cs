using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal sealed class VehicleRegistrar
    {
        private struct VehicleCandidate
        {
            public Entity Line;
            public Entity Vehicle;
            public int LineIndex;
            public int VehicleIndex;
        }

        private struct StartupCandidate
        {
            public Entity Line;
            public Entity Vehicle;
            public int Order;
        }

        private enum StartupLineState
        {
            Ineligible,
            WaitingStable,
            Stable
        }

        private const int STARTUP_BUCKET_COUNT = 16;
        private const int STARTUP_BUCKET_MASK = STARTUP_BUCKET_COUNT - 1;

        [BurstCompile]
        private struct FilterVehiclesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Lines;
            [ReadOnly] public BufferLookup<RouteVehicle> RouteVehicles;
            [ReadOnly] public ComponentLookup<Controller> Controllers;
            [ReadOnly] public ComponentLookup<Owner> Owners;
            [ReadOnly] public ComponentLookup<PublicTransport> PublicTransports;
            [ReadOnly] public BufferLookup<LayoutElement> Layouts;
            public NativeParallelHashSet<Entity>.ParallelWriter Seen;
            public NativeList<VehicleCandidate>.ParallelWriter Candidates;

            public void Execute(int index)
            {
                Entity line = Lines[index];
                if (!RouteVehicles.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles)) return;
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = Resolve(vehicles[i].m_Vehicle);
                    if (vehicle == Entity.Null) continue;
                    if (!Seen.Add(vehicle)) continue;
                    PublicTransport publicTransport = PublicTransports[vehicle];
                    if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0) continue;
                    Candidates.AddNoResize(new VehicleCandidate
                    {
                        Line = line,
                        Vehicle = vehicle,
                        LineIndex = index,
                        VehicleIndex = i
                    });
                }
            }

            private Entity Resolve(Entity vehicle)
            {
                Entity current = vehicle;
                Entity fallback = Entity.Null;
                for (int i = 0; current != Entity.Null && i < 16; i++)
                {
                    bool hasPublicTransport = PublicTransports.HasComponent(current);
                    if (hasPublicTransport) fallback = current;
                    if (hasPublicTransport && Layouts.HasBuffer(current)) return current;
                    if (Controllers.HasComponent(current))
                    {
                        Entity controller = Controllers[current].m_Controller;
                        if (controller != Entity.Null && controller != current)
                        {
                            current = controller;
                            continue;
                        }
                    }
                    if (!Owners.HasComponent(current)) break;
                    Entity owner = Owners[current].m_Owner;
                    if (owner == Entity.Null || owner == current) break;
                    current = owner;
                }
                return fallback;
            }
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly List<Entity> m_DisabledLineLateSpawnRetireQueue = new List<Entity>();
        private readonly HashSet<Entity> m_DisabledLineLateSpawnRetireQueueSeen = new HashSet<Entity>();
        private readonly HashSet<Entity> m_DisabledLineLateSpawnHandledLines = new HashSet<Entity>();
        // 跨来源帧候选：第二步只保留完整 Entity，第三步才由 Register 唯一消费。
        private readonly HashSet<Entity> m_PendingRebindCandidates = new HashSet<Entity>();
        private readonly Dictionary<Entity, StopFact> m_DeferredRestoredStops = new Dictionary<Entity, StopFact>();
        // 启动期索引只保存当前批次的实体、线路和稳定线路序号。
        private readonly List<StartupCandidate> m_StartupCandidates = new List<StartupCandidate>();
        private readonly List<Entity> m_StartupLines = new List<Entity>();
        private readonly List<Entity> m_StartupWaitingLines = new List<Entity>();
        private readonly List<Entity> m_StartupOrderedLines = new List<Entity>();
        private readonly List<Entity> m_StartupOrderedVehicles = new List<Entity>();
        private readonly HashSet<Entity> m_StartupSeenVehicles = new HashSet<Entity>();
        private readonly List<StartupCandidate> m_StartupCheckCandidates = new List<StartupCandidate>();
        private readonly List<Entity> m_StartupCheckLines = new List<Entity>();
        private bool m_StartupGate;
        private bool m_StartupAwaitingStable;
        private bool m_StartupAwaitingBaseline;
        private int m_StartupBucket;
        private readonly System.Action<StopFact> m_PublishStopFact;
        private readonly System.Action<Entity, int, StopControlResult> m_ApplyStopControl;

        public VehicleRegistrar(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
            m_PublishStopFact = runtime.PublishStopFact;
            m_ApplyStopControl = runtime.ApplyStopControl;
        }

        internal IReadOnlyList<Entity> DisabledLineLateSpawnRetireQueue => m_DisabledLineLateSpawnRetireQueue;
        internal IReadOnlyCollection<Entity> PendingRebindCandidates => m_PendingRebindCandidates;
        internal bool StartupGateActive => m_StartupGate;
        internal bool IsStartupActivationFrame(uint frame) => m_StartupGate
            && !m_StartupAwaitingStable
            && m_StartupAwaitingBaseline
            && (frame & 15u) == 3u;

        internal void ClearDisabledLineLateSpawnRetireQueue()
        {
            m_DisabledLineLateSpawnRetireQueue.Clear();
            m_DisabledLineLateSpawnRetireQueueSeen.Clear();
            m_DisabledLineLateSpawnHandledLines.Clear();
        }

        internal void ClearPendingRebindCandidates() => m_PendingRebindCandidates.Clear();

        internal void ClearStartupGate()
        {
            m_StartupCandidates.Clear();
            m_StartupLines.Clear();
            m_StartupWaitingLines.Clear();
            m_StartupOrderedLines.Clear();
            m_StartupOrderedVehicles.Clear();
            m_StartupSeenVehicles.Clear();
            m_StartupCheckCandidates.Clear();
            m_StartupCheckLines.Clear();
            m_StartupGate = false;
            m_StartupAwaitingStable = false;
            m_StartupAwaitingBaseline = false;
            m_StartupBucket = 0;
        }

        internal void BeginStartupGate()
        {
            ClearStartupGate();
            m_StartupAwaitingStable = !CollectStartupIndex(m_StartupCandidates, m_StartupLines);
            m_StartupGate = true;
        }

        internal void TickStartupGate()
        {
            if (!m_StartupGate || m_StartupAwaitingBaseline || m_StartupBucket >= STARTUP_BUCKET_COUNT)
                return;

            if (m_StartupAwaitingStable)
            {
                uint frame = m_Runtime.m_SimulationSystem.frameIndex;
                if ((frame & 15u) != 3u)
                    return;
                if (!CollectStartupIndex(m_StartupCandidates, m_StartupLines))
                    return;
                m_StartupAwaitingStable = false;
                m_StartupBucket = 0;
            }

            int bucket = m_StartupBucket;
            for (int i = 0; i < m_StartupCandidates.Count; i++)
            {
                StartupCandidate candidate = m_StartupCandidates[i];
                if ((candidate.Order & STARTUP_BUCKET_MASK) != bucket)
                    continue;

                if (!TryStartupRoute(candidate.Line, candidate.Vehicle, out DynamicBuffer<RouteWaypoint> waypoints))
                    continue;

                EnsureStartupVehicle(candidate.Line, candidate.Vehicle, waypoints);
            }

            m_StartupBucket++;
            if (m_StartupBucket == STARTUP_BUCKET_COUNT)
                m_StartupAwaitingBaseline = true;
        }

        internal bool TryActivateStartup(uint frame)
        {
            if (!IsStartupActivationFrame(frame))
                return false;

            bool currentIndexReady = CollectStartupIndex(m_StartupCheckCandidates, m_StartupCheckLines);
            if (!currentIndexReady)
            {
                PruneStartupVehicles(m_StartupCheckCandidates);
                m_StartupCandidates.Clear();
                m_StartupLines.Clear();
                m_StartupBucket = 0;
                m_StartupAwaitingStable = true;
                m_StartupAwaitingBaseline = false;
                return false;
            }

            if (!StartupIndexMatches(m_StartupCheckCandidates, m_StartupCheckLines))
            {
                PruneStartupVehicles(m_StartupCheckCandidates);
                ReplaceStartupIndex(m_StartupCheckCandidates, m_StartupCheckLines);
                m_StartupBucket = 0;
                m_StartupAwaitingStable = false;
                m_StartupAwaitingBaseline = false;
                return false;
            }

            var railVehicles = new List<Entity>(m_StartupCandidates.Count);
            var roadVehicles = new List<Entity>(m_StartupCandidates.Count);
            for (int i = 0; i < m_StartupCandidates.Count; i++)
            {
                StartupCandidate candidate = m_StartupCandidates[i];
                if (!m_Runtime.m_VehicleView.TryGetLine(candidate.Vehicle, out Entity line)
                    || line != candidate.Line)
                {
                    m_StartupBucket = 0;
                    m_StartupAwaitingStable = false;
                    m_StartupAwaitingBaseline = false;
                    return false;
                }
                if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, candidate.Line, out LifecycleKind lifecycle))
                    return false;

                if (lifecycle == LifecycleKind.Rail)
                    railVehicles.Add(candidate.Vehicle);
                else if (lifecycle == LifecycleKind.Road)
                    roadVehicles.Add(candidate.Vehicle);
            }

            if (railVehicles.Count > 0 && !m_Runtime.m_RailEventSource.RebaselineStartup(railVehicles, frame))
            {
                return false;
            }
            if (roadVehicles.Count > 0 && !m_Runtime.m_RoadEventSource.RebaselineStartup(roadVehicles, frame))
            {
                return false;
            }

            for (int i = 0; i < m_StartupCandidates.Count; i++)
            {
                StartupCandidate candidate = m_StartupCandidates[i];
                if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, candidate.Line, out LifecycleKind lifecycle)
                    || lifecycle != LifecycleKind.Rail)
                {
                    continue;
                }
                if (!m_Runtime.m_RailEventSource.TryGetStartupSource(
                        candidate.Vehicle,
                        candidate.Line,
                        frame,
                        out _,
                        out _,
                        out _))
                {
                    return false;
                }
            }

            for (int i = 0; i < m_StartupCandidates.Count; i++)
            {
                StartupCandidate candidate = m_StartupCandidates[i];
                if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, candidate.Line, out LifecycleKind lifecycle))
                    return false;

                bool prepared = lifecycle == LifecycleKind.Rail
                    ? m_Runtime.m_RailEventSource.PrepareStartupStopSource(
                        candidate.Vehicle,
                        candidate.Line,
                        frame,
                        out bool boarding,
                        out int waypoint,
                        out _)
                    : m_Runtime.m_RoadEventSource.PrepareStartupStopSource(
                        candidate.Vehicle,
                        candidate.Line,
                        frame,
                        out boarding,
                        out waypoint,
                        out _);
                if (!prepared)
                {
                    return false;
                }
                m_Runtime.m_VehicleRegistry.PublishStartupRestore(
                    candidate.Vehicle,
                    candidate.Line);
                m_Runtime.m_VehicleCache.SeedStartupRunningLapStart(
                    candidate.Vehicle,
                    candidate.Line);
                StopFact restoredStop = m_Runtime.m_StopRuntime.RestoreRegistration(
                    candidate.Vehicle,
                    candidate.Line,
                    boarding,
                    waypoint,
                    frame);
                if (restoredStop.Exists)
                    m_PublishStopFact(restoredStop);

            }

            for (int i = 0; i < m_StartupLines.Count; i++)
                m_Runtime.m_LineInitialAdopted.Add(m_StartupLines[i]);

            ClearStartupGate();
            return true;
        }

        private bool CollectStartupIndex(List<StartupCandidate> candidates, List<Entity> lines)
        {
            candidates.Clear();
            lines.Clear();
            m_StartupWaitingLines.Clear();
            m_StartupOrderedLines.Clear();
            m_StartupSeenVehicles.Clear();
            NativeArray<Entity> queriedLines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < queriedLines.Length; i++)
                    m_StartupOrderedLines.Add(queriedLines[i]);
                m_StartupOrderedLines.Sort(CompareEntity);
                for (int i = 0; i < m_StartupOrderedLines.Count; i++)
                {
                    Entity line = m_StartupOrderedLines[i];
                    StartupLineState lineState = TryStartupLine(line, out DynamicBuffer<RouteVehicle> members, out _);
                    if (lineState == StartupLineState.WaitingStable)
                    {
                        m_StartupWaitingLines.Add(line);
                        continue;
                    }
                    if (lineState != StartupLineState.Stable)
                        continue;

                    int order = lines.Count;
                    lines.Add(line);
                    m_StartupOrderedVehicles.Clear();
                    for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                    {
                        Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(members[memberIndex].m_Vehicle);
                        if (vehicle != Entity.Null)
                            m_StartupOrderedVehicles.Add(vehicle);
                    }
                    m_StartupOrderedVehicles.Sort(CompareEntity);
                    for (int vehicleIndex = 0; vehicleIndex < m_StartupOrderedVehicles.Count; vehicleIndex++)
                    {
                        Entity vehicle = m_StartupOrderedVehicles[vehicleIndex];
                        if (m_StartupSeenVehicles.Contains(vehicle)
                            || !TryStartupRoute(line, vehicle, out _))
                        {
                            continue;
                        }

                        m_StartupSeenVehicles.Add(vehicle);
                        candidates.Add(new StartupCandidate
                        {
                            Line = line,
                            Vehicle = vehicle,
                            Order = order
                        });
                    }
                }

                if (m_StartupWaitingLines.Count != 0)
                {
                    candidates.Clear();
                    lines.Clear();
                    return false;
                }

                return true;
            }
            finally
            {
                queriedLines.Dispose();
            }
        }

        private StartupLineState TryStartupLine(
            Entity line,
            out DynamicBuffer<RouteVehicle> members,
            out DynamicBuffer<RouteWaypoint> waypoints)
        {
            members = default;
            waypoints = default;
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || m_Runtime.EntityManager.HasComponent<Disabled>(line)
                || !m_Runtime.EntityManager.HasComponent<TransportLine>(line)
                || !m_Runtime.EntityManager.HasBuffer<RouteVehicle>(line)
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return StartupLineState.Ineligible;
            }

            members = m_Runtime.EntityManager.GetBuffer<RouteVehicle>(line, true);
            waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length < 2
                || !m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch()))
            {
                return StartupLineState.Ineligible;
            }

            return m_Runtime.m_LineProfile.IsStable(line, waypoints)
                ? StartupLineState.Stable
                : StartupLineState.WaitingStable;
        }

        private bool TryStartupRoute(
            Entity line,
            Entity vehicle,
            out DynamicBuffer<RouteWaypoint> waypoints)
        {
            waypoints = default;
            if (TryStartupLine(line, out DynamicBuffer<RouteVehicle> members, out waypoints) != StartupLineState.Stable
                || vehicle == Entity.Null
                || !m_Runtime.EntityManager.Exists(vehicle)
                || m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle)
                || !m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle)
                || !m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                return false;
            }

            PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0
                || m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route != line)
            {
                return false;
            }

            for (int i = 0; i < members.Length; i++)
            {
                if (m_Runtime.m_Resolve.RuntimeVehicle(members[i].m_Vehicle) == vehicle)
                    return true;
            }
            return false;
        }

        private bool StartupIndexMatches(
            List<StartupCandidate> currentCandidates,
            List<Entity> currentLines)
        {
            if (m_StartupLines.Count != currentLines.Count
                || m_StartupCandidates.Count != currentCandidates.Count)
            {
                return false;
            }

            for (int i = 0; i < m_StartupLines.Count; i++)
            {
                if (m_StartupLines[i] != currentLines[i])
                    return false;
            }
            for (int i = 0; i < m_StartupCandidates.Count; i++)
            {
                StartupCandidate existing = m_StartupCandidates[i];
                StartupCandidate current = currentCandidates[i];
                if (existing.Line != current.Line
                    || existing.Vehicle != current.Vehicle
                    || existing.Order != current.Order)
                {
                    return false;
                }
            }
            return true;
        }

        private void ReplaceStartupIndex(
            List<StartupCandidate> candidates,
            List<Entity> lines)
        {
            m_StartupCandidates.Clear();
            m_StartupCandidates.AddRange(candidates);
            m_StartupLines.Clear();
            m_StartupLines.AddRange(lines);
        }

        private void PruneStartupVehicles(List<StartupCandidate> candidates)
        {
            var currentLines = new Dictionary<Entity, Entity>();
            for (int i = 0; i < candidates.Count; i++)
                currentLines[candidates[i].Vehicle] = candidates[i].Line;

            NativeArray<Entity> tracked = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                var ordered = new List<Entity>(tracked.Length);
                for (int i = 0; i < tracked.Length; i++)
                    ordered.Add(tracked[i]);
                ordered.Sort(CompareEntity);
                for (int i = 0; i < ordered.Count; i++)
                {
                    Entity vehicle = ordered[i];
                    if (!currentLines.TryGetValue(vehicle, out Entity currentLine)
                        || !m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity registeredLine)
                        || registeredLine != currentLine)
                    {
                        RemoveStartupVehicle(vehicle);
                    }
                }
            }
            finally
            {
                tracked.Dispose();
            }
        }

        private void EnsureStartupVehicle(
            Entity line,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity registeredLine))
            {
                if (registeredLine == line)
                    return;
                RemoveStartupVehicle(vehicle);
            }

            AdoptCandidate(
                line,
                vehicle,
                waypoints,
                adoptExistingVehicles: true,
                completeRestore: false,
                restoreVehicleCache: true,
                startupSilent: true);
        }

        private void RemoveStartupVehicle(Entity vehicle)
        {
            Entity line = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity registeredLine)
                ? registeredLine
                : Entity.Null;
            m_Runtime.m_VehicleRegistry.BeginSilentRestore();
            try
            {
                m_Runtime.m_VehicleRegistry.Remove(vehicle);
            }
            finally
            {
                m_Runtime.m_VehicleRegistry.EndSilentRestore();
            }
            m_Runtime.m_StopRuntime.RemoveVehicle(vehicle);
            if (RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle))
            {
                if (lifecycle == LifecycleKind.Rail)
                    m_Runtime.m_RailEventSource.RemoveVehicle(vehicle);
                else if (lifecycle == LifecycleKind.Road)
                    m_Runtime.m_RoadEventSource.RemoveVehicle(vehicle);
            }
        }

        internal void ObserveRailRoute(Entity vehicle)
        {
            ObserveRoute(vehicle);
        }

        internal void ObserveRoadRoute(Entity vehicle)
        {
            ObserveRoute(vehicle);
        }

        private void ObserveRoute(Entity vehicle)
        {
            if (vehicle != Entity.Null && m_Runtime.m_VehicleView.Contains(vehicle))
                m_PendingRebindCandidates.Add(vehicle);
        }

        public void Register(bool fullSweep, bool scanSpawnLines)
        {
            NativeArray<Entity> lines = default;
            NativeArray<Entity> spawnLines = default;
            NativeArray<Entity> spawnRequestLines = default;
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            BufferLookup<RouteModifier> modBuffers = m_Runtime.GetBufferLookup<RouteModifier>(false);
            ClearDisabledLineLateSpawnRetireQueue();
            try
            {
                if (fullSweep)
                {
                    lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.TempJob);
                    RegisterFullSweep(lines, rvBuffers, wpBuffers, modBuffers);
                }
                else if (scanSpawnLines)
                {
                    spawnLines = m_Runtime.m_SpawningLines.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < spawnLines.Length; i++)
                        RegisterLine(spawnLines[i], fullSweep, rvBuffers, wpBuffers, modBuffers);

                    spawnRequestLines = m_Runtime.m_LineSpawnRequestFrame.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < spawnRequestLines.Length; i++)
                    {
                        Entity line = spawnRequestLines[i];
                        bool alreadyRegistered = false;
                        for (int j = 0; j < spawnLines.Length; j++)
                        {
                            if (spawnLines[j] != line) continue;
                            alreadyRegistered = true;
                            break;
                        }
                        if (!alreadyRegistered)
                            RegisterLine(line, fullSweep, rvBuffers, wpBuffers, modBuffers);
                    }
                }
            }
            finally
            {
                if (lines.IsCreated) lines.Dispose();
                if (spawnLines.IsCreated) spawnLines.Dispose();
                if (spawnRequestLines.IsCreated) spawnRequestLines.Dispose();
            }

            if (fullSweep || m_Runtime.m_RailEventSource.CollectedThisFrame(m_Runtime.m_SimulationSystem.frameIndex))
                DrainRebindCandidates(rvBuffers, wpBuffers);
        }

        private void RegisterFullSweep(
            NativeArray<Entity> lines,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteWaypoint> wpBuffers,
            BufferLookup<RouteModifier> modBuffers)
        {
            using var eligible = new NativeList<Entity>(lines.Length, Allocator.TempJob);
            int candidateCapacity = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line)) continue;
                bool hasPendingSpawn = m_Runtime.m_SpawningLines.ContainsKey(line)
                    || m_Runtime.m_LineSpawnRequestFrame.ContainsKey(line);
                if (hasPendingSpawn && m_Runtime.EntityManager.HasComponent<Disabled>(line))
                {
                    m_DisabledLineLateSpawnHandledLines.Add(line);
                    HandleDisabledLinePendingSpawn(line, rvBuffers, modBuffers);
                    continue;
                }
                if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles)) continue;
                if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> waypoints) || waypoints.Length < 2) continue;
                if (!m_Runtime.m_LineProfile.IsStable(line, waypoints)) continue;
                if (!m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch())) continue;
                eligible.Add(line);
                candidateCapacity += vehicles.Length;
                DiagnoseLine(line, waypoints);
            }

            if (eligible.Length != 0 && candidateCapacity != 0)
            {
                using var candidates = new NativeList<VehicleCandidate>(candidateCapacity, Allocator.TempJob);
                using var jobSeen = new NativeParallelHashSet<Entity>(candidateCapacity, Allocator.TempJob);
                var job = new FilterVehiclesJob
                {
                    Lines = eligible.AsArray(),
                    RouteVehicles = rvBuffers,
                    Controllers = m_Runtime.GetComponentLookup<Controller>(true),
                    Owners = m_Runtime.GetComponentLookup<Owner>(true),
                    PublicTransports = m_Runtime.GetComponentLookup<PublicTransport>(true),
                    Layouts = m_Runtime.GetBufferLookup<LayoutElement>(true),
                    Seen = jobSeen.AsParallelWriter(),
                    Candidates = candidates.AsParallelWriter()
                };
                job.Schedule(eligible.Length, 1).Complete();

                var ordered = new List<VehicleCandidate>(candidates.Length);
                for (int i = 0; i < candidates.Length; i++) ordered.Add(candidates[i]);
                ordered.Sort((left, right) => left.LineIndex != right.LineIndex
                    ? left.LineIndex.CompareTo(right.LineIndex)
                    : left.VehicleIndex.CompareTo(right.VehicleIndex));
                var seen = new HashSet<Entity>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    VehicleCandidate candidate = ordered[i];
                    if (!seen.Add(candidate.Vehicle)) continue;
                    if (!m_Runtime.EntityManager.Exists(candidate.Vehicle)) continue;
                    if (m_Runtime.m_VehicleView.Contains(candidate.Vehicle))
                    {
                        ObserveRebind(candidate.Line, candidate.Vehicle);
                        continue;
                    }
                    if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(candidate.Vehicle)) continue;
                    if (!wpBuffers.TryGetBuffer(candidate.Line, out DynamicBuffer<RouteWaypoint> waypoints)) continue;
                    bool adoptExisting = !m_Runtime.m_LineInitialAdopted.Contains(candidate.Line);
                    AdoptCandidate(candidate.Line, candidate.Vehicle, waypoints, adoptExisting);
                }
            }

            for (int i = 0; i < eligible.Length; i++)
            {
                Entity line = eligible[i];
                if (rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> knownVehicles))
                {
                    for (int vehicleIndex = 0; vehicleIndex < knownVehicles.Length; vehicleIndex++)
                        ObserveRebind(line, knownVehicles[vehicleIndex].m_Vehicle);
                }
                if (!m_Runtime.m_LineInitialAdopted.Contains(line))
                    m_Runtime.m_LineInitialAdopted.Add(line);
            }
        }

        private void DiagnoseLine(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!m_Runtime.m_LineInitialAdopted.Contains(line) || m_Runtime.m_LineProfile.IsDiagnosed(line)) return;
            m_Runtime.m_LineProfile.MarkDiagnosed(line);
            bool isRail = RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle)
                && lifecycle == LifecycleKind.Rail;
            if (isRail)
                m_Runtime.m_TrackModel.LogLineTrackChainDiagnostics(line);
            if (RtLog.VerboseEnabled)
                m_Runtime.log.Info("[诊断] 线路" + line.Index + " (" + m_Runtime.EntityName(line) + ") waypoint数=" + waypoints.Length);
        }

        private void RegisterLine(
            Entity line,
            bool fullSweep,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteWaypoint> wpBuffers,
            BufferLookup<RouteModifier> modBuffers)
        {
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line)) return;
            if (m_DisabledLineLateSpawnHandledLines.Contains(line)) return;

            bool hasPendingSpawn = m_Runtime.m_SpawningLines.ContainsKey(line)
                || m_Runtime.m_LineSpawnRequestFrame.ContainsKey(line);
            if (hasPendingSpawn && m_Runtime.EntityManager.HasComponent<Disabled>(line))
            {
                m_DisabledLineLateSpawnHandledLines.Add(line);
                HandleDisabledLinePendingSpawn(line, rvBuffers, modBuffers);
                return;
            }

            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs)) return;
            if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> wps) || wps.Length < 2) return;
            if (!m_Runtime.m_LineProfile.IsStable(line, wps)) return;
            if (!m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch())) return;
            bool adoptExistingVehicles = !m_Runtime.m_LineInitialAdopted.Contains(line);
            bool isHotLine = adoptExistingVehicles || m_Runtime.m_SpawningLines.ContainsKey(line);
            if (!fullSweep && !isHotLine) return;

            if (!adoptExistingVehicles && !m_Runtime.m_LineProfile.IsDiagnosed(line))
            {
                m_Runtime.m_LineProfile.MarkDiagnosed(line);
                bool isRail = RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle)
                    && lifecycle == LifecycleKind.Rail;
                if (isRail)
                    m_Runtime.m_TrackModel.LogLineTrackChainDiagnostics(line);
                if (RtLog.VerboseEnabled)
                {
                    string lineTag = "线路" + line.Index;
                    string lineName = m_Runtime.EntityName(line);
                    m_Runtime.log.Info("[诊断] " + lineTag + " (" + lineName + ") waypoint数=" + wps.Length);
                }
            }

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = rvs[i].m_Vehicle;
                if (!m_Runtime.EntityManager.Exists(v)) continue;
                if (m_Runtime.m_VehicleView.Contains(v))
                    continue;
                if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(v))
                {
                    continue;
                }

                AdoptCandidate(line, v, wps, adoptExistingVehicles);
            }

            if (adoptExistingVehicles)
                m_Runtime.m_LineInitialAdopted.Add(line);
        }

        private void ObserveRebind(Entity line, Entity vehicle)
        {
            Entity resolved = m_Runtime.m_Resolve.RuntimeVehicle(vehicle);
            if (resolved == Entity.Null || !m_Runtime.EntityManager.Exists(resolved)) return;
            if (!m_Runtime.m_VehicleView.TryGetLine(resolved, out Entity registeredLine) || registeredLine == line) return;
            m_PendingRebindCandidates.Add(resolved);
        }

        private void DrainRebindCandidates(
            BufferLookup<RouteVehicle> routeVehicles,
            BufferLookup<RouteWaypoint> waypointBuffers)
        {
            if (m_PendingRebindCandidates.Count == 0)
                return;

            var candidates = new List<Entity>(m_PendingRebindCandidates);
            candidates.Sort(CompareEntity);
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity vehicle = candidates[i];
                if (vehicle == Entity.Null
                    || !m_Runtime.EntityManager.Exists(vehicle)
                    || !m_Runtime.m_VehicleView.Contains(vehicle)
                    || !m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity oldLine))
                {
                    m_PendingRebindCandidates.Remove(vehicle);
                    continue;
                }

                if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                    continue;

                if (!TryGetRebindRoute(vehicle, routeVehicles, waypointBuffers, out Entity newLine, out DynamicBuffer<RouteWaypoint> waypoints))
                    continue;

                if (newLine == oldLine)
                {
                    m_PendingRebindCandidates.Remove(vehicle);
                    continue;
                }

                RebindVehicle(oldLine, newLine, vehicle, waypoints);
                m_PendingRebindCandidates.Remove(vehicle);
            }
        }

        private bool TryGetRebindRoute(
            Entity vehicle,
            BufferLookup<RouteVehicle> routeVehicles,
            BufferLookup<RouteWaypoint> waypointBuffers,
            out Entity line,
            out DynamicBuffer<RouteWaypoint> waypoints)
        {
            line = Entity.Null;
            waypoints = default;
            if (!m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                || !m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle))
            {
                return false;
            }

            if ((m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Returning) != 0)
                return false;

            line = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || m_Runtime.EntityManager.HasComponent<Disabled>(line)
                || !m_Runtime.EntityManager.HasComponent<TransportLine>(line)
                || !routeVehicles.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> members)
                || !waypointBuffers.TryGetBuffer(line, out waypoints)
                || waypoints.Length < 2
                || !m_Runtime.m_LineProfile.IsStable(line, waypoints)
                || !m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch()))
            {
                return false;
            }

            for (int i = 0; i < members.Length; i++)
            {
                Entity member = m_Runtime.m_Resolve.RuntimeVehicle(members[i].m_Vehicle);
                if (member == vehicle)
                    return true;
            }

            return false;
        }

        private void RebindVehicle(
            Entity oldLine,
            Entity newLine,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!ConfirmRebindFacts(vehicle, newLine))
                return;
            if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, oldLine, out LifecycleKind lifecycle))
                return;

            StopCancelResult cancelledStop = m_Runtime.m_StopRuntime.CancelRebind(
                vehicle,
                m_Runtime.m_SimulationSystem.frameIndex);
            if (cancelledStop.Exists)
            {
                m_PublishStopFact(cancelledStop.Fact);
                m_ApplyStopControl(vehicle, cancelledStop.Control.WaypointIndex, cancelledStop.Control);
            }

            if (lifecycle == LifecycleKind.Rail)
                m_Runtime.m_Bypass.ClearVehicle(vehicle, "换线");
            if (lifecycle == LifecycleKind.Rail)
                m_Runtime.m_RailEventSource.RebindSource(vehicle);
            else if (lifecycle == LifecycleKind.Road)
                m_Runtime.m_RoadEventSource.RebindSource(vehicle);
            ClearRebindRuntime(vehicle, lifecycle);
            m_Runtime.m_VehicleRegistry.BeginRebind(vehicle, oldLine);
            try
            {
                m_Runtime.m_VehicleRegistry.Remove(vehicle);
                m_Runtime.m_SchedulerApply.MarkDirty(oldLine);
                AdoptCandidate(
                    newLine,
                    vehicle,
                    waypoints,
                    adoptExistingVehicles: true,
                    completeRestore: false,
                    restoreVehicleCache: false);
                m_Runtime.m_SchedulerApply.MarkDirty(newLine);
                m_Runtime.m_VehicleRegistry.EndRestore(newLine);
                if (lifecycle == LifecycleKind.Rail)
                {
                    m_Runtime.m_RailEventSource.RefreshRebindComponents(vehicle);
                    m_Runtime.m_RailEventSource.RefreshOwners(vehicle);
                }
                if (m_DeferredRestoredStops.TryGetValue(vehicle, out StopFact restoredStop))
                {
                    m_DeferredRestoredStops.Remove(vehicle);
                    m_PublishStopFact(restoredStop);
                }
            }
            catch
            {
                m_DeferredRestoredStops.Remove(vehicle);
                m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }

        }

        private bool ConfirmRebindFacts(Entity vehicle, Entity newLine)
        {
            if (!m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                || m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route != newLine
                || !m_Runtime.EntityManager.HasBuffer<RouteVehicle>(newLine))
            {
                return false;
            }

            DynamicBuffer<RouteVehicle> members = m_Runtime.EntityManager.GetBuffer<RouteVehicle>(newLine, true);
            for (int i = 0; i < members.Length; i++)
            {
                if (m_Runtime.m_Resolve.RuntimeVehicle(members[i].m_Vehicle) == vehicle)
                    return true;
            }
            return false;
        }

        private void ClearRebindRuntime(Entity vehicle, LifecycleKind lifecycle)
        {
            if (lifecycle == LifecycleKind.Rail)
            {
                m_Runtime.m_TrackProjection.ClearVehicle(vehicle);
                m_Runtime.TrackProjection.ClearVehicleProgressSuspect(vehicle, "route-rebind");
                m_Runtime.m_RailEventSource.CommitWaypoint(vehicle, -1);
                m_Runtime.m_WaypointIndex.Remove(vehicle);
            }
            m_Runtime.m_RouteProgress.Remove(vehicle);
            m_Runtime.m_ObsPersist.ClearLap(vehicle);
            m_Runtime.m_Observation.ClearDwellDeadlineCache(vehicle);
            m_Runtime.m_Observation.CancelBusSeg(vehicle);
            m_Runtime.m_ObsPersist.ClearDwell(vehicle);
            if (lifecycle == LifecycleKind.Rail)
                m_Runtime.m_Observation.ClearVehicleSlices(vehicle);
            m_Runtime.m_Observation.ClearDebug(vehicle);
            if (lifecycle == LifecycleKind.Rail)
            {
                m_Runtime.m_RailEtaService?.CancelTargetRequests(vehicle, "Rebind");
                m_Runtime.m_Observation.ClearDispatchEta(vehicle);
            }
            m_Runtime.m_Announcements.RemoveVehicle(vehicle);
            m_Runtime.m_StationContextQuery.RemoveVehicle(vehicle);
            m_Runtime.m_UICache.Remove(vehicle);
            if (lifecycle == LifecycleKind.Rail)
                m_Runtime.m_BoardingFirstFrameGuardState.Remove(vehicle);
            m_Runtime.m_PreparingFixCooldownUntil.Remove(vehicle);
            m_Runtime.m_RuntimeFramePlan.ClearDeadline(vehicle, DeadlineKind.PreparingCooldown);
            m_Runtime.m_RuntimeEngine.ClearAssistLaunchPending(vehicle);
            m_Runtime.m_SpawnIntentTrace.Remove(vehicle);
            m_Runtime.m_RuntimeLog.ClearVehicle(vehicle);
        }

        private static int CompareEntity(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Version.CompareTo(right.Version);
        }

        private PublicTransport ReadRailPublicTransport(Entity vehicle)
        {
            return m_Runtime.m_RailEventSource.TryReadPublicTransportForWrite(vehicle, out PublicTransport value)
                ? value
                : m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
        }

        private PublicTransport ReadRoadPublicTransport(Entity vehicle)
        {
            return m_Runtime.m_RoadEventSource.TryReadPublicTransportForWrite(vehicle, out PublicTransport value)
                ? value
                : m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
        }

        private void CommitRailPublicTransport(Entity vehicle, PublicTransport value)
        {
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            m_Runtime.m_RailEventSource.AppendPublicTransportWrite(vehicle, value, frame);
            m_Runtime.EntityManager.SetComponentData(vehicle, value);
        }

        private void CommitRoadPublicTransport(Entity vehicle, PublicTransport value)
        {
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            m_Runtime.m_RoadEventSource.AppendPublicTransportWrite(vehicle, value, frame);
            m_Runtime.EntityManager.SetComponentData(vehicle, value);
        }

        private void AdoptRoadCandidate(
            Entity line,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool adoptExistingVehicles,
            bool completeRestore,
            bool restoreVehicleCache,
            bool startupSilent)
        {
            if (!m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle))
                return;

            PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0)
                return;

            bool waypointKnown = m_Runtime.m_RoadEventSource.TryReadCurrentWaypoint(
                vehicle,
                line,
                out bool boarding,
                out int waypointIndex);
            VehicleState initialState = ResolveRoadInitialState(
                waypointKnown,
                waypointIndex,
                boarding,
                adoptExistingVehicles);
            uint? dispatchFrame = null;
            if (!adoptExistingVehicles
                && m_Runtime.m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
            {
                dispatchFrame = spawnRequestFrame;
                m_Runtime.m_LineSpawnRequestFrame.Remove(line);
            }

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (startupSilent)
            {
                m_Runtime.m_VehicleRegistry.BeginSilentRestore();
            }
            else if (completeRestore)
            {
                m_Runtime.m_VehicleRegistry.BeginRestore(vehicle);
            }

            try
            {
                m_Runtime.m_RuntimeEngine.Adopt(vehicle, line, initialState, nowFrame, dispatchFrame);
                m_Runtime.m_RoadEventSource.RegisterSource(vehicle, line, waypointKnown ? waypointIndex : -1);
                if (!startupSilent)
                {
                    m_Runtime.m_ObsPersist.SetLapDistance(vehicle, -1f);
                    StopFact restoredStop = m_Runtime.m_StopRuntime.RestoreRegistration(
                        vehicle,
                        line,
                        boarding,
                        waypointKnown ? waypointIndex : -1,
                        nowFrame);
                    if (restoredStop.Exists)
                    {
                        if (completeRestore)
                            m_PublishStopFact(restoredStop);
                        else
                            m_DeferredRestoredStops[vehicle] = restoredStop;
                    }

                    m_Runtime.m_RoadEventSource.CommitWaypoint(
                        vehicle,
                        waypointKnown ? waypointIndex : -1);
                    m_Runtime.m_UICache.Remove(vehicle);
                }

                if (restoreVehicleCache)
                {
                    bool restored = m_Runtime.m_VehicleCache.Restore(
                        vehicle,
                        line,
                        initialState != VehicleState.Holding,
                        readPublicTransport: startupSilent ? null : ReadRoadPublicTransport,
                        commitPublicTransport: startupSilent ? null : CommitRoadPublicTransport,
                        registryOnly: startupSilent);
                    if (!startupSilent && !restored && initialState == VehicleState.Running)
                        m_Runtime.m_VehicleCache.RestoreRun(vehicle, line, waypoints, "road-register");
                }

                if (startupSilent)
                    m_Runtime.m_VehicleRegistry.EndSilentRestore();
                else if (completeRestore)
                    m_Runtime.m_VehicleRegistry.EndRestore(line);
            }
            catch
            {
                if (startupSilent)
                    m_Runtime.m_VehicleRegistry.EndSilentRestore();
                else if (completeRestore)
                    m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }

            if (!startupSilent
                && m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState finalState))
            {
                if (finalState == VehicleState.Running)
                    m_Runtime.m_RuntimeEngine.CommitRunning(vehicle, line);
                else if (finalState == VehicleState.Holding)
                    m_Runtime.m_Observation.Seed(vehicle, line, nowFrame);
            }

            if (!adoptExistingVehicles)
                m_Runtime.m_SelectPanel.RecordLineVehicleRegisterSummary(
                    line,
                    m_Runtime.m_RuntimeLifecycleHost.Minute(),
                    vehicle,
                    initialState);
        }

        private static VehicleState ResolveRoadInitialState(
            bool waypointKnown,
            int waypointIndex,
            bool boarding,
            bool adoptExistingVehicles)
        {
            if (boarding && waypointKnown && waypointIndex == 0)
                return VehicleState.Holding;
            if (!adoptExistingVehicles)
                return VehicleState.Preparing;
            if (waypointKnown && waypointIndex > 0)
                return VehicleState.Running;
            return VehicleState.Preparing;
        }

        private void AdoptCandidate(
            Entity line,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool adoptExistingVehicles,
            bool completeRestore = true,
            bool restoreVehicleCache = true,
            bool startupSilent = false)
        {
            if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle))
                return;
            if (lifecycle == LifecycleKind.Road)
            {
                AdoptRoadCandidate(
                    line,
                    vehicle,
                    waypoints,
                    adoptExistingVehicles,
                    completeRestore,
                    restoreVehicleCache,
                    startupSilent);
                return;
            }

            PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0) return;

            int waypointIndex = !startupSilent && boarding
                ? m_Runtime.m_WaypointIndex.Compute(vehicle, waypoints)
                : -1;
            bool atOrigin = waypointIndex == 0;
            VehicleState initialState = InferInitialState(
                vehicle,
                waypoints,
                publicTransport,
                boarding,
                waypointIndex,
                adoptExistingVehicles,
                out string initialReason);
            if (initialState == VehicleState.Holding
                && (initialReason == "boarding-origin-fallback"
                    || initialReason.StartsWith("route-progress-origin-fallback")))
            {
                waypointIndex = 0;
                atOrigin = true;
            }
            uint? dispatchFrame = null;
            if (!adoptExistingVehicles
                && m_Runtime.m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
            {
                dispatchFrame = spawnRequestFrame;
                m_Runtime.m_LineSpawnRequestFrame.Remove(line);
            }

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            bool registerStopInput = !startupSilent && !completeRestore;
            if (startupSilent)
            {
                m_Runtime.m_VehicleRegistry.BeginSilentRestore();
            }
            else if (completeRestore)
            {
                m_Runtime.m_RailEventSource.RegisterSource(
                    vehicle,
                    line,
                    publicTransport,
                    waypointIndex,
                    waypoints.Length);
                m_Runtime.m_VehicleRegistry.BeginRestore(vehicle);
            }
            bool restored = false;
            VehicleState finalState = default;
            int finalTarget = -1;
            string spawnIntent = string.Empty;
            try
            {
            m_Runtime.m_RuntimeEngine.Adopt(vehicle, line, initialState, nowFrame, dispatchFrame);
            if (registerStopInput)
            {
                m_Runtime.m_RailEventSource.RegisterStopInput(
                    vehicle,
                    line,
                    publicTransport,
                    waypointIndex,
                    waypoints.Length);
            }
            spawnIntent = dispatchFrame.HasValue
                ? m_Runtime.m_SpawnIntentTrace.Bind(line, vehicle, dispatchFrame.Value, nowFrame)
                : string.Empty;
            if (!startupSilent)
                m_Runtime.m_ObsPersist.SetLapDistance(vehicle, -1f);
            StopFact restoredStop = startupSilent
                ? default
                : m_Runtime.m_StopRuntime.RestoreRegistration(
                    vehicle,
                    line,
                    boarding,
                    waypointIndex,
                    nowFrame);
            if (restoredStop.Exists)
            {
                if (completeRestore)
                    m_PublishStopFact(restoredStop);
                else
                    m_DeferredRestoredStops[vehicle] = restoredStop;
            }
            if (!startupSilent)
            {
                m_Runtime.m_RailEventSource.CommitWaypoint(vehicle, waypointIndex);
                m_Runtime.m_UICache.Remove(vehicle);
                m_Runtime.TrackProjection.ClearVehicleProgressSuspect(vehicle, "register-reset");
                if (initialReason == "boarding-midway")
                    m_Runtime.TrackProjection.MarkVehicleProgressSuspect(vehicle, initialReason);
            }

            bool preferOriginHolding = initialState == VehicleState.Holding
                && (initialReason == "at-origin"
                    || initialReason == "boarding-origin-fallback"
                    || initialReason.StartsWith("route-progress-origin-fallback"));
            if (restoreVehicleCache)
            {
                restored = m_Runtime.m_VehicleCache.Restore(
                    vehicle,
                    line,
                    !preferOriginHolding,
                    readPublicTransport: startupSilent ? null : ReadRailPublicTransport,
                    commitPublicTransport: startupSilent ? null : CommitRailPublicTransport,
                    registryOnly: startupSilent);
                if (!startupSilent && !restored && initialState == VehicleState.Running)
                    restored = m_Runtime.m_VehicleCache.RestoreRun(vehicle, line, waypoints, initialReason);
            }
            finalState = m_Runtime.m_VehicleView.GetState(vehicle);
            finalTarget = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int target) ? target : -1;
            if (startupSilent)
                m_Runtime.m_VehicleRegistry.EndSilentRestore();
            else if (completeRestore)
            {
                m_Runtime.m_VehicleRegistry.EndRestore(line);
                m_Runtime.m_RailEventSource.RefreshOwners(vehicle);
            }
            }
            catch
            {
                if (startupSilent)
                    m_Runtime.m_VehicleRegistry.EndSilentRestore();
                else if (completeRestore)
                    m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }
            if (!startupSilent && finalState == VehicleState.Running)
                m_Runtime.m_RuntimeEngine.CommitRunning(vehicle, line);
            if (!startupSilent && finalState == VehicleState.Holding)
                m_Runtime.m_Observation.Seed(vehicle, line, nowFrame);

            if (!startupSilent && RtLog.VerboseEnabled)
            {
                string lineTag = "线路" + line.Index;
                m_Runtime.log.Info("[注册] " + lineTag + " 车辆" + vehicle.Index
                    + " 初始:" + initialState + " 最终:" + finalState
                    + (restored ? "(缓存恢复)" : "")
                    + " targetMin=" + finalTarget
                    + " initReason=" + initialReason
                    + " depot=" + m_Runtime.m_SelectPanel.DescribeVehicleOwnerDepot(vehicle));
                m_Runtime.m_RuntimeLog.Once(
                    m_Runtime.m_RuntimeLog.m_RouteVehicleOwnerMismatchLogCache,
                    vehicle,
                    "register-detail|line=" + line.Index
                        + "|state=" + finalState
                        + "|target=" + (m_Runtime.EntityManager.HasComponent<Target>(vehicle) ? m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target.Index : -1)
                        + "|route=" + (m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle) ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route.Index : -1),
                    "[RegisterDetail] " + lineTag + " 车辆" + vehicle.Index
                        + " " + m_Runtime.m_RuntimeLog.VehicleOwnership(line, vehicle, finalState, finalTarget, "register")
                        + " initReason=" + initialReason
                        + " restored=" + (restored ? "1" : "0")
                        + " atA0=" + (atOrigin ? "1" : "0")
                        + " initWp=" + waypointIndex);
                if (!adoptExistingVehicles)
                {
                    m_Runtime.log.Info("[OfficialSpawnResult] line=" + line.Index
                        + " vehicle=" + vehicle.Index
                        + " state=" + finalState
                        + " targetMin=" + finalTarget
                        + " initReason=" + initialReason
                        + " depot=" + m_Runtime.m_SelectPanel.DescribeVehicleOwnerDepot(vehicle)
                        + spawnIntent);
                }
            }
            if (!adoptExistingVehicles)
                m_Runtime.m_SelectPanel.RecordLineVehicleRegisterSummary(line, m_Runtime.m_RuntimeLifecycleHost.Minute(), vehicle, finalState);
        }

        private void HandleDisabledLinePendingSpawn(
            Entity line,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteModifier> modBuffers)
        {
            m_Runtime.m_SpawningLines.Remove(line);
            m_Runtime.m_LineSpawnRequestFrame.Remove(line);
            RestoreVehicleIntervalModifier(line, modBuffers);

            int queuedRetires = 0;
            if (rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
            {
                HashSet<Entity> seenVehicles = new HashSet<Entity>();
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(rvs[i].m_Vehicle);
                    if (!m_Runtime.EntityManager.Exists(vehicle)) continue;
                    if (!seenVehicles.Add(vehicle)) continue;
                    if (m_Runtime.m_VehicleView.Contains(vehicle)) continue;
                    if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                    {
                        continue;
                    }
                    if (m_Runtime.EntityManager.HasComponent<Deleted>(vehicle)
                        || m_Runtime.EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        continue;
                    }
                    if (!m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle)
                        || !m_Runtime.EntityManager.HasComponent<Target>(vehicle)
                        || !m_Runtime.EntityManager.HasComponent<Owner>(vehicle))
                    {
                        continue;
                    }
                    if (!m_DisabledLineLateSpawnRetireQueueSeen.Add(vehicle)) continue;

                    m_DisabledLineLateSpawnRetireQueue.Add(vehicle);
                    queuedRetires++;
                }
            }

            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[DisabledLineLateSpawnCleanup] 线路" + line.Index
                    + " 清理关闭线路残留产车状态 queuedRetires=" + queuedRetires);
            }
        }

        private static void RestoreVehicleIntervalModifier(
            Entity line,
            BufferLookup<RouteModifier> modBuffers)
        {
            if (!modBuffers.TryGetBuffer(line, out DynamicBuffer<RouteModifier> mods))
                return;

            int modifierIndex = (int)RouteModifierType.VehicleInterval;
            if (mods.Length <= modifierIndex)
                return;

            RouteModifier modifier = mods[modifierIndex];
            modifier.m_Delta = float2.zero;
            mods[modifierIndex] = modifier;
        }

        internal VehicleState InferInitialState(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            Game.Vehicles.PublicTransport publicTransport,
            bool boarding,
            int initialWaypointIndex,
            bool adoptExistingVehicles,
            out string reason)
        {
            bool arriving = (publicTransport.m_State & PublicTransportFlags.Arriving) != 0;

            if (initialWaypointIndex == 0)
            {
                reason = "at-origin";
                return VehicleState.Holding;
            }
            if (boarding)
            {
                if (m_Runtime.m_LineProfile.IsWithinOriginDistance(vehicle, waypoints, ModRuntimeHostSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS))
                {
                    if (!m_Runtime.m_RouteProgress.Try(vehicle, out int nearOriginWaypointIndex, out float nearOriginSegmentPosition)
                        || (nearOriginWaypointIndex == 1 && nearOriginSegmentPosition <= 0.10f)
                        || nearOriginWaypointIndex == 0)
                    {
                        reason = "boarding-origin-fallback";
                        return VehicleState.Holding;
                    }
                }
            }
            if (boarding && initialWaypointIndex > 0)
            {
                reason = "boarding-midway";
                return VehicleState.Running;
            }
            if (!adoptExistingVehicles)
            {
                reason = "new-vehicle-default";
                return VehicleState.Preparing;
            }

            if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0)
            {
                reason = "returning";
                return VehicleState.Retiring;
            }

            if (m_Runtime.m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition))
            {
                bool nearOriginProgress = nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.05f);
                if (nearOriginProgress
                    && m_Runtime.m_LineProfile.IsWithinOriginDistance(vehicle, waypoints, ModRuntimeHostSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS)
                    && (boarding || arriving))
                {
                    reason = "route-progress-origin-fallback wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                    return VehicleState.Holding;
                }
                reason = "route-progress wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                return (boarding && nextWaypointIndex == 0) ? VehicleState.Holding : VehicleState.Running;
            }

            float originDistance = m_Runtime.m_LineProfile.DistanceToOrigin(vehicle, waypoints);
            if (originDistance > ModRuntimeHostSystem.ORIGIN_CONGESTION_RADIUS_METERS)
            {
                reason = "far-from-origin " + originDistance.ToString("F0") + "m";
                return VehicleState.Running;
            }

            if (!m_Runtime.EntityManager.HasComponent<Target>(vehicle))
            {
                reason = "no-target";
                return VehicleState.Preparing;
            }

            Entity target = m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (target == Entity.Null || target == waypoints[0].m_Waypoint)
            {
                reason = "target-origin";
                return VehicleState.Preparing;
            }
            reason = "non-origin-target";
            return VehicleState.Running;
        }
    }
}
