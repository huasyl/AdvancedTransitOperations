using System;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.UI;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public sealed partial class DepotSourceLockSystem : GameSystemBase
    {
        private EntityQuery m_RtSpawnPermitRequestQuery;
        private EntityQuery m_ConfiguredDispatchRequestQuery;
        private EntityQuery m_DepotQuery;
        private EntityQuery m_LineQuery;
        private NameSystem m_NameSystem = null!;
        private SimulationSystem m_SimulationSystem = null!;
        private PathfindSetupSystem m_PathfindSetupSystem = null!;

        private readonly struct PendingRequestRouteSetupCacheEntry
        {
            public readonly bool Available;
            public readonly RouteConnectionData RouteConnectionData;
            public readonly PathMethod PathMethods;
            public readonly Entity DestinationWaypoint;

            public PendingRequestRouteSetupCacheEntry(
                bool available,
                RouteConnectionData routeConnectionData,
                PathMethod pathMethods,
                Entity destinationWaypoint)
            {
                Available = available;
                RouteConnectionData = routeConnectionData;
                PathMethods = pathMethods;
                DestinationWaypoint = destinationWaypoint;
            }
        }

        private readonly struct PendingRequestFrameEntry
        {
            public readonly Entity Request;
            public readonly ServiceRequest ServiceRequest;
            public readonly TransportVehicleRequest VehicleRequest;
            public readonly Entity Line;

            public PendingRequestFrameEntry(
                Entity request,
                ServiceRequest serviceRequest,
                TransportVehicleRequest vehicleRequest,
                Entity line)
            {
                Request = request;
                ServiceRequest = serviceRequest;
                VehicleRequest = vehicleRequest;
                Line = line;
            }
        }

        private readonly struct PendingRequestLineFrameState
        {
            public readonly Entity Line;
            public readonly Entity ConfiguredDepot;
            public readonly bool ConfiguredDepotCompatible;
            public readonly bool HasRouteSetup;
            public readonly RouteConnectionData RouteConnectionData;
            public readonly PathMethod PathMethods;
            public readonly Entity DestinationWaypoint;
            public readonly Entity PreferredDepot;

            public PendingRequestLineFrameState(
                Entity line,
                Entity configuredDepot,
                bool configuredDepotCompatible,
                bool hasRouteSetup,
                RouteConnectionData routeConnectionData,
                PathMethod pathMethods,
                Entity destinationWaypoint,
                Entity preferredDepot)
            {
                Line = line;
                ConfiguredDepot = configuredDepot;
                ConfiguredDepotCompatible = configuredDepotCompatible;
                HasRouteSetup = hasRouteSetup;
                RouteConnectionData = routeConnectionData;
                PathMethods = pathMethods;
                DestinationWaypoint = destinationWaypoint;
                PreferredDepot = preferredDepot;
            }

            public PendingRequestLineFrameState WithPreferredDepot(Entity preferredDepot)
            {
                return new PendingRequestLineFrameState(
                    Line,
                    ConfiguredDepot,
                    ConfiguredDepotCompatible,
                    HasRouteSetup,
                    RouteConnectionData,
                    PathMethods,
                    DestinationWaypoint,
                    preferredDepot);
            }
        }

        private readonly struct LineRuntimeSnapshot
        {
            public readonly Entity Line;
            public readonly ulong SettingsVersion;
            public readonly Entity LinePrefab;
            public readonly Entity ConfiguredDepot;
            public readonly bool ConfiguredDepotCompatible;
            public readonly bool HasRouteSetup;
            public readonly RouteConnectionData RouteConnectionData;
            public readonly PathMethod PathMethods;
            public readonly Entity DestinationWaypoint;
            public readonly int DestinationWaypointIndex;

            public LineRuntimeSnapshot(
                Entity line,
                ulong settingsVersion,
                Entity linePrefab,
                Entity configuredDepot,
                bool configuredDepotCompatible,
                bool hasRouteSetup,
                RouteConnectionData routeConnectionData,
                PathMethod pathMethods,
                Entity destinationWaypoint,
                int destinationWaypointIndex)
            {
                Line = line;
                SettingsVersion = settingsVersion;
                LinePrefab = linePrefab;
                ConfiguredDepot = configuredDepot;
                ConfiguredDepotCompatible = configuredDepotCompatible;
                HasRouteSetup = hasRouteSetup;
                RouteConnectionData = routeConnectionData;
                PathMethods = pathMethods;
                DestinationWaypoint = destinationWaypoint;
                DestinationWaypointIndex = destinationWaypointIndex;
            }
        }

        private readonly struct DepotLineCacheKey : IEquatable<DepotLineCacheKey>
        {
            public readonly Entity Depot;
            public readonly Entity Line;

            public DepotLineCacheKey(Entity depot, Entity line)
            {
                Depot = depot;
                Line = line;
            }

            public bool Equals(DepotLineCacheKey other)
            {
                return Depot == other.Depot && Line == other.Line;
            }

            public override bool Equals(object obj)
            {
                return obj is DepotLineCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Depot.GetHashCode() * 397) ^ Line.GetHashCode();
                }
            }
        }

        private readonly struct ConfiguredDepotBlockedRequestState
        {
            public readonly Entity Request;
            public readonly Entity Line;
            public readonly Entity ConfiguredDepot;
            public readonly Entity BlockedLane;
            public readonly ulong SettingsVersion;
            public readonly uint NextRetryFrame;

            public ConfiguredDepotBlockedRequestState(
                Entity request,
                Entity line,
                Entity configuredDepot,
                Entity blockedLane,
                ulong settingsVersion,
                uint nextRetryFrame)
            {
                Request = request;
                Line = line;
                ConfiguredDepot = configuredDepot;
                BlockedLane = blockedLane;
                SettingsVersion = settingsVersion;
                NextRetryFrame = nextRetryFrame;
            }

            public ConfiguredDepotBlockedRequestState WithRetry(uint nextRetryFrame)
            {
                return new ConfiguredDepotBlockedRequestState(
                    Request,
                    Line,
                    ConfiguredDepot,
                    BlockedLane,
                    SettingsVersion,
                    nextRetryFrame);
            }
        }

        private readonly Dictionary<Entity, string> m_RequestDecisionLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, Entity> m_PendingConfiguredRequestSources = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, ConfiguredDepotBlockedRequestState> m_ConfiguredDepotBlockedRequests = new Dictionary<Entity, ConfiguredDepotBlockedRequestState>();
        private readonly HashSet<Entity> m_ConfiguredRequestParkedFallbacks = new HashSet<Entity>();
        private readonly List<Entity> m_RequestCleanupScratch = new List<Entity>();
        private readonly List<Entity> m_LineRuntimeSnapshotCleanupScratch = new List<Entity>();
        private readonly List<PendingRequestFrameEntry> m_FramePendingRequests = new List<PendingRequestFrameEntry>();
        private readonly List<Entity> m_FramePendingLines = new List<Entity>();
        private readonly HashSet<Entity> m_FramePendingLineSet = new HashSet<Entity>();
        private readonly Dictionary<Entity, PendingRequestLineFrameState> m_FramePendingLineStates = new Dictionary<Entity, PendingRequestLineFrameState>();
        private readonly Dictionary<Entity, Entity> m_FrameConfiguredDepotByLine = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, PendingRequestRouteSetupCacheEntry> m_FramePendingRequestRouteSetupByLine = new Dictionary<Entity, PendingRequestRouteSetupCacheEntry>();
        private readonly Dictionary<DepotLineCacheKey, Entity> m_FrameReusableConfiguredSourceByDepotLine = new Dictionary<DepotLineCacheKey, Entity>();
        private readonly Dictionary<TransportType, (Entity depot, float priority)> m_FrameLockedDepotByType =
            new Dictionary<TransportType, (Entity depot, float priority)>();
        private readonly Dictionary<Entity, LineRuntimeSnapshot> m_LineRuntimeSnapshots = new Dictionary<Entity, LineRuntimeSnapshot>();
        private int m_FrameCacheLogCountdown = DEPOT_FRAME_CACHE_LOG_INTERVAL_FRAMES;
        private int m_ConfiguredDepotFrameCacheHits;
        private int m_ConfiguredDepotFrameCacheMisses;
        private int m_RouteSetupFrameCacheHits;
        private int m_RouteSetupFrameCacheMisses;
        private int m_ReusableSourceFrameCacheHits;
        private int m_ReusableSourceFrameCacheMisses;
        private int m_LineRuntimeSnapshotHits;
        private int m_LineRuntimeSnapshotMisses;
        private int m_BlockedRequestFreezeSkips;
        private int m_BlockedRequestProbeExtends;
        private int m_BlockedRequestProbeReleases;
        private const byte CONFIGURED_DEPOT_BRANCH_BLOCK_COOLDOWN = 16;
        private const int CONFIGURED_DEPOT_OUTBOUND_PATH_LOOKAHEAD = 8;
        private const int DEPOT_FRAME_CACHE_LOG_INTERVAL_FRAMES = 3600;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_PathfindSetupSystem = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
            m_RtSpawnPermitRequestQuery = GetEntityQuery(
                ComponentType.ReadOnly<ServiceRequest>(),
                ComponentType.ReadOnly<TransportVehicleRequest>(),
                ComponentType.ReadOnly<RtSpawnPermitRequest>(),
                ComponentType.Exclude<Dispatched>(),
                ComponentType.Exclude<PathInformation>(),
                ComponentType.Exclude<Deleted>());
            m_ConfiguredDispatchRequestQuery = GetEntityQuery(
                ComponentType.ReadOnly<ServiceRequest>(),
                ComponentType.ReadOnly<TransportVehicleRequest>(),
                ComponentType.ReadOnly<RtSpawnPermitRequest>(),
                ComponentType.ReadOnly<PathInformation>(),
                ComponentType.Exclude<Dispatched>(),
                ComponentType.Exclude<Deleted>());
            m_DepotQuery = GetEntityQuery(
                ComponentType.ReadWrite<Game.Buildings.TransportDepot>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Buildings.ServiceUpgrade>(),
                ComponentType.Exclude<Deleted>());
            m_LineQuery = GetEntityQuery(
                ComponentType.ReadOnly<TransportLine>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>());
        }

        protected override void OnUpdate()
        {
            ClearFrameDepotCaches();
            CleanupConfiguredRequestTracking();
            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            if (control != null && !control.m_Features.DepotLock())
            {
                TickDepotFrameCacheLogging();
                return;
            }

            if ((m_RtSpawnPermitRequestQuery.IsEmptyIgnoreFilter && m_ConfiguredDispatchRequestQuery.IsEmptyIgnoreFilter)
                || m_DepotQuery.IsEmptyIgnoreFilter
                || m_LineQuery.IsEmptyIgnoreFilter)
            {
                TickDepotFrameCacheLogging();
                return;
            }

            BuildPendingRequestFrameState();
            FinalizePendingRequestPreferredDepots();
            QueueConfiguredDepotRequests();
            GateConfiguredDepotDispatchRequests();
            ApplyDepotSourceLocks();
            TickDepotFrameCacheLogging();
        }

        private void ClearFrameDepotCaches()
        {
            m_FramePendingRequests.Clear();
            m_FramePendingLines.Clear();
            m_FramePendingLineSet.Clear();
            m_FramePendingLineStates.Clear();
            m_FrameConfiguredDepotByLine.Clear();
            m_FramePendingRequestRouteSetupByLine.Clear();
            m_FrameReusableConfiguredSourceByDepotLine.Clear();
            m_FrameLockedDepotByType.Clear();
        }

        private void TickDepotFrameCacheLogging()
        {
            if (!RtLog.VerboseEnabled)
            {
                m_FrameCacheLogCountdown = DEPOT_FRAME_CACHE_LOG_INTERVAL_FRAMES;
                m_ConfiguredDepotFrameCacheHits = 0;
                m_ConfiguredDepotFrameCacheMisses = 0;
                m_RouteSetupFrameCacheHits = 0;
                m_RouteSetupFrameCacheMisses = 0;
                m_ReusableSourceFrameCacheHits = 0;
                m_ReusableSourceFrameCacheMisses = 0;
                m_LineRuntimeSnapshotHits = 0;
                m_LineRuntimeSnapshotMisses = 0;
                m_BlockedRequestFreezeSkips = 0;
                m_BlockedRequestProbeExtends = 0;
                m_BlockedRequestProbeReleases = 0;
                return;
            }

            m_FrameCacheLogCountdown--;
            if (m_FrameCacheLogCountdown > 0)
                return;

            CleanupLineRuntimeSnapshots();

            int configuredDepotLookups = m_ConfiguredDepotFrameCacheHits + m_ConfiguredDepotFrameCacheMisses;
            int routeSetupLookups = m_RouteSetupFrameCacheHits + m_RouteSetupFrameCacheMisses;
            int reusableSourceLookups = m_ReusableSourceFrameCacheHits + m_ReusableSourceFrameCacheMisses;
            int lineRuntimeLookups = m_LineRuntimeSnapshotHits + m_LineRuntimeSnapshotMisses;
            int blockedRequestEvents = m_BlockedRequestFreezeSkips + m_BlockedRequestProbeExtends + m_BlockedRequestProbeReleases;
            if (configuredDepotLookups > 0 || routeSetupLookups > 0 || reusableSourceLookups > 0 || lineRuntimeLookups > 0 || blockedRequestEvents > 0)
            {
                Mod.log.Info(
                    "[DepotSourceLockCache] intervalFrames=" + DEPOT_FRAME_CACHE_LOG_INTERVAL_FRAMES
                    + " lineRuntimeHit=" + m_LineRuntimeSnapshotHits
                    + " lineRuntimeMiss=" + m_LineRuntimeSnapshotMisses
                    + " configuredDepotHit=" + m_ConfiguredDepotFrameCacheHits
                    + " configuredDepotMiss=" + m_ConfiguredDepotFrameCacheMisses
                    + " routeSetupHit=" + m_RouteSetupFrameCacheHits
                    + " routeSetupMiss=" + m_RouteSetupFrameCacheMisses
                    + " reusableSourceHit=" + m_ReusableSourceFrameCacheHits
                    + " reusableSourceMiss=" + m_ReusableSourceFrameCacheMisses
                    + " blockedFreezeSkip=" + m_BlockedRequestFreezeSkips
                    + " blockedProbeExtend=" + m_BlockedRequestProbeExtends
                    + " blockedProbeRelease=" + m_BlockedRequestProbeReleases);
            }

            m_FrameCacheLogCountdown = DEPOT_FRAME_CACHE_LOG_INTERVAL_FRAMES;
            m_ConfiguredDepotFrameCacheHits = 0;
            m_ConfiguredDepotFrameCacheMisses = 0;
            m_RouteSetupFrameCacheHits = 0;
            m_RouteSetupFrameCacheMisses = 0;
            m_ReusableSourceFrameCacheHits = 0;
            m_ReusableSourceFrameCacheMisses = 0;
            m_LineRuntimeSnapshotHits = 0;
            m_LineRuntimeSnapshotMisses = 0;
            m_BlockedRequestFreezeSkips = 0;
            m_BlockedRequestProbeExtends = 0;
            m_BlockedRequestProbeReleases = 0;
        }

        private void CleanupLineRuntimeSnapshots()
        {
            if (m_LineRuntimeSnapshots.Count == 0)
                return;

            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            ulong settingsVersion = control != null
                ? control.m_LineView.CfgVersion()
                : 0ul;

            m_LineRuntimeSnapshotCleanupScratch.Clear();
            foreach (KeyValuePair<Entity, LineRuntimeSnapshot> entry in m_LineRuntimeSnapshots)
            {
                Entity line = entry.Key;
                LineRuntimeSnapshot snapshot = entry.Value;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || snapshot.Line != line
                    || snapshot.SettingsVersion != settingsVersion
                    || !EntityManager.HasComponent<PrefabRef>(line))
                {
                    m_LineRuntimeSnapshotCleanupScratch.Add(line);
                    continue;
                }

                Entity linePrefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                if (snapshot.LinePrefab != linePrefab)
                {
                    m_LineRuntimeSnapshotCleanupScratch.Add(line);
                    continue;
                }

                if (snapshot.ConfiguredDepot != Entity.Null
                    && (!EntityManager.Exists(snapshot.ConfiguredDepot)
                        || !EntityManager.HasComponent<Game.Buildings.TransportDepot>(snapshot.ConfiguredDepot)
                        || EntityManager.HasComponent<Deleted>(snapshot.ConfiguredDepot)))
                {
                    m_LineRuntimeSnapshotCleanupScratch.Add(line);
                    continue;
                }

                if (snapshot.DestinationWaypoint != Entity.Null
                    && !EntityManager.Exists(snapshot.DestinationWaypoint))
                {
                    m_LineRuntimeSnapshotCleanupScratch.Add(line);
                }
            }

            for (int i = 0; i < m_LineRuntimeSnapshotCleanupScratch.Count; i++)
            {
                m_LineRuntimeSnapshots.Remove(m_LineRuntimeSnapshotCleanupScratch[i]);
            }
        }

        private uint GetCurrentFrame()
        {
            return m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
        }

        private void RememberConfiguredDepotBlockedRequest(
            Entity request,
            Entity line,
            Entity configuredDepot,
            Entity blockedLane)
        {
            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            if (request == Entity.Null
                || line == Entity.Null
                || configuredDepot == Entity.Null
                || blockedLane == Entity.Null
                || control == null)
            {
                return;
            }

            uint nextRetryFrame = GetCurrentFrame() + CONFIGURED_DEPOT_BRANCH_BLOCK_COOLDOWN;
            m_ConfiguredDepotBlockedRequests[request] = new ConfiguredDepotBlockedRequestState(
                request,
                line,
                configuredDepot,
                blockedLane,
                control.m_LineView.CfgVersion(),
                nextRetryFrame);
        }

        private bool ShouldSkipFrozenConfiguredDepotRequest(Entity request, Entity line)
        {
            if (!m_ConfiguredDepotBlockedRequests.TryGetValue(request, out ConfiguredDepotBlockedRequestState state))
                return false;

            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            if (control == null
                || state.Request != request
                || state.Line != line
                || !EntityManager.Exists(request)
                || !EntityManager.HasComponent<ServiceRequest>(request)
                || !EntityManager.HasComponent<TransportVehicleRequest>(request)
                || state.SettingsVersion != control.m_LineView.CfgVersion()
                || state.ConfiguredDepot == Entity.Null
                || !EntityManager.Exists(state.ConfiguredDepot)
                || !EntityManager.HasComponent<Game.Buildings.TransportDepot>(state.ConfiguredDepot)
                || EntityManager.HasComponent<Deleted>(state.ConfiguredDepot))
            {
                m_ConfiguredDepotBlockedRequests.Remove(request);
                return false;
            }

            uint nowFrame = GetCurrentFrame();
            if (nowFrame < state.NextRetryFrame)
            {
                m_BlockedRequestFreezeSkips++;
                return true;
            }

            if (state.BlockedLane != Entity.Null
                && TryGetInboundDepotReservationBlocker(state.BlockedLane, state.ConfiguredDepot, out _))
            {
                m_ConfiguredDepotBlockedRequests[request] = state.WithRetry(nowFrame + CONFIGURED_DEPOT_BRANCH_BLOCK_COOLDOWN);
                m_BlockedRequestProbeExtends++;
                return true;
            }

            m_ConfiguredDepotBlockedRequests.Remove(request);
            m_BlockedRequestProbeReleases++;
            return false;
        }

        private void BuildPendingRequestFrameState()
        {
            var lineLookup = GetComponentLookup<TransportLine>(true);
            using (NativeArray<Entity> requests = m_RtSpawnPermitRequestQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < requests.Length; i++)
                {
                    Entity request = requests[i];
                    if (!EntityManager.Exists(request)
                        || !EntityManager.HasComponent<ServiceRequest>(request)
                        || !EntityManager.HasComponent<TransportVehicleRequest>(request))
                    {
                        continue;
                    }

                    ServiceRequest serviceRequest = EntityManager.GetComponentData<ServiceRequest>(request);
                    if ((serviceRequest.m_Flags & ServiceRequestFlags.Reversed) != 0)
                        continue;

                    TransportVehicleRequest vehicleRequest = EntityManager.GetComponentData<TransportVehicleRequest>(request);
                    Entity line = vehicleRequest.m_Route;
                    if (line == Entity.Null
                        || !EntityManager.Exists(line)
                        || !lineLookup.HasComponent(line))
                    {
                        continue;
                    }

                    ManagedRequestPort managedRequests = LifecyclePort.Current?.ManagedRequests;
                    if (managedRequests == null || !managedRequests.IsManagedLine(line))
                        continue;

                    TransportLine transportLine = lineLookup[line];
                    if (transportLine.m_VehicleRequest != request)
                        continue;

                    if (ShouldSkipFrozenConfiguredDepotRequest(request, line))
                        continue;

                    m_FramePendingRequests.Add(new PendingRequestFrameEntry(request, serviceRequest, vehicleRequest, line));
                    if (!m_FramePendingLineSet.Add(line))
                        continue;

                    m_FramePendingLines.Add(line);
                    if (TryBuildPendingRequestLineFrameState(line, out PendingRequestLineFrameState lineState))
                        m_FramePendingLineStates[line] = lineState;
                }
            }
        }

        private void FinalizePendingRequestPreferredDepots()
        {
            for (int i = 0; i < m_FramePendingLines.Count; i++)
            {
                Entity line = m_FramePendingLines[i];
                if (!m_FramePendingLineStates.TryGetValue(line, out PendingRequestLineFrameState state))
                    continue;

                m_FramePendingLineStates[line] = state.WithPreferredDepot(state.ConfiguredDepot);
            }
        }

        private bool TryGetPendingRequestLineFrameState(Entity line, out PendingRequestLineFrameState state)
        {
            return m_FramePendingLineStates.TryGetValue(line, out state);
        }

        private bool TryBuildPendingRequestLineFrameState(Entity line, out PendingRequestLineFrameState state)
        {
            state = default;
            if (line == Entity.Null || !EntityManager.Exists(line))
                return false;

            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            if (control == null)
                return false;

            ulong settingsVersion = control.m_LineView.CfgVersion();
            if (TryGetLineRuntimeSnapshot(line, settingsVersion, out LineRuntimeSnapshot snapshot))
            {
                m_LineRuntimeSnapshotHits++;
                state = new PendingRequestLineFrameState(
                    line,
                    snapshot.ConfiguredDepot,
                    snapshot.ConfiguredDepotCompatible,
                    snapshot.HasRouteSetup,
                    snapshot.RouteConnectionData,
                    snapshot.PathMethods,
                    snapshot.DestinationWaypoint,
                    Entity.Null);
                return true;
            }

            m_LineRuntimeSnapshotMisses++;
            if (!TryRebuildLineRuntimeSnapshot(line, settingsVersion, out snapshot))
                return false;

            state = new PendingRequestLineFrameState(
                line,
                snapshot.ConfiguredDepot,
                snapshot.ConfiguredDepotCompatible,
                snapshot.HasRouteSetup,
                snapshot.RouteConnectionData,
                snapshot.PathMethods,
                snapshot.DestinationWaypoint,
                Entity.Null);
            return true;
        }

        private bool TryGetLineRuntimeSnapshot(Entity line, ulong settingsVersion, out LineRuntimeSnapshot snapshot)
        {
            snapshot = default;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !m_LineRuntimeSnapshots.TryGetValue(line, out snapshot)
                || snapshot.Line != line
                || snapshot.SettingsVersion != settingsVersion)
            {
                return false;
            }

            if (!EntityManager.HasComponent<PrefabRef>(line))
                return false;

            Entity linePrefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (snapshot.LinePrefab != linePrefab)
                return false;

            if (snapshot.ConfiguredDepot != Entity.Null
                && (!EntityManager.Exists(snapshot.ConfiguredDepot)
                    || !EntityManager.HasComponent<Game.Buildings.TransportDepot>(snapshot.ConfiguredDepot)
                    || EntityManager.HasComponent<Deleted>(snapshot.ConfiguredDepot)))
            {
                return false;
            }

            if (!snapshot.HasRouteSetup)
                return true;

            if (snapshot.DestinationWaypoint == Entity.Null
                || !EntityManager.Exists(snapshot.DestinationWaypoint)
                || !EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            int waypointIndex = snapshot.DestinationWaypointIndex;
            if (waypointIndex < 0
                || waypointIndex >= waypoints.Length
                || waypoints[waypointIndex].m_Waypoint != snapshot.DestinationWaypoint)
            {
                return false;
            }

            return true;
        }

        private bool TryRebuildLineRuntimeSnapshot(Entity line, ulong settingsVersion, out LineRuntimeSnapshot snapshot)
        {
            snapshot = default;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !EntityManager.HasComponent<PrefabRef>(line))
            {
                m_LineRuntimeSnapshots.Remove(line);
                return false;
            }

            Entity linePrefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            Entity configuredDepot = GetConfiguredDepotForLine(line);
            bool configuredDepotCompatible = IsDepotCompatibleWithLine(configuredDepot, line);
            RouteConnectionData routeConnectionData = default;
            PathMethod pathMethods = default;
            Entity destinationWaypoint = Entity.Null;
            int destinationWaypointIndex = -1;
            bool hasRouteSetup = configuredDepotCompatible
                && TryGetPendingRequestRouteSetup(
                    line,
                    out routeConnectionData,
                    out pathMethods,
                    out destinationWaypoint,
                    out destinationWaypointIndex);

            snapshot = new LineRuntimeSnapshot(
                line,
                settingsVersion,
                linePrefab,
                configuredDepot,
                configuredDepotCompatible,
                hasRouteSetup,
                routeConnectionData,
                pathMethods,
                destinationWaypoint,
                destinationWaypointIndex);
            if (snapshot.ConfiguredDepot != Entity.Null
                && snapshot.ConfiguredDepotCompatible
                && snapshot.HasRouteSetup)
            {
                m_LineRuntimeSnapshots[line] = snapshot;
            }
            else
            {
                m_LineRuntimeSnapshots.Remove(line);
            }

            return true;
        }

        private void CleanupConfiguredRequestTracking()
        {
            if (m_PendingConfiguredRequestSources.Count > 0)
            {
                m_RequestCleanupScratch.Clear();
                foreach (KeyValuePair<Entity, Entity> entry in m_PendingConfiguredRequestSources)
                {
                    Entity request = entry.Key;
                    if (request == Entity.Null
                        || !EntityManager.Exists(request)
                        || EntityManager.HasComponent<Dispatched>(request)
                        || !EntityManager.HasComponent<TransportVehicleRequest>(request)
                        || !EntityManager.HasComponent<ServiceRequest>(request))
                    {
                        m_RequestCleanupScratch.Add(request);
                    }
                }

                for (int i = 0; i < m_RequestCleanupScratch.Count; i++)
                {
                    Entity request = m_RequestCleanupScratch[i];
                    m_PendingConfiguredRequestSources.Remove(request);
                    m_ConfiguredRequestParkedFallbacks.Remove(request);
                }
            }

            if (m_ConfiguredRequestParkedFallbacks.Count > 0)
            {
                m_RequestCleanupScratch.Clear();
                foreach (Entity request in m_ConfiguredRequestParkedFallbacks)
                {
                    if (request == Entity.Null
                        || !EntityManager.Exists(request)
                        || EntityManager.HasComponent<Dispatched>(request)
                        || !EntityManager.HasComponent<TransportVehicleRequest>(request)
                        || !EntityManager.HasComponent<ServiceRequest>(request))
                    {
                        m_RequestCleanupScratch.Add(request);
                    }
                }

                for (int i = 0; i < m_RequestCleanupScratch.Count; i++)
                {
                    Entity request = m_RequestCleanupScratch[i];
                    m_ConfiguredRequestParkedFallbacks.Remove(request);
                }
            }

            if (m_ConfiguredDepotBlockedRequests.Count == 0)
                return;

            m_RequestCleanupScratch.Clear();
            foreach (KeyValuePair<Entity, ConfiguredDepotBlockedRequestState> entry in m_ConfiguredDepotBlockedRequests)
            {
                Entity request = entry.Key;
                if (request == Entity.Null
                    || !EntityManager.Exists(request)
                    || EntityManager.HasComponent<Dispatched>(request)
                    || !EntityManager.HasComponent<TransportVehicleRequest>(request)
                    || !EntityManager.HasComponent<ServiceRequest>(request))
                {
                    m_RequestCleanupScratch.Add(request);
                }
            }

            for (int i = 0; i < m_RequestCleanupScratch.Count; i++)
            {
                Entity request = m_RequestCleanupScratch[i];
                m_ConfiguredDepotBlockedRequests.Remove(request);
            }
        }

        private void QueueConfiguredDepotRequests()
        {
            NativeQueue<SetupQueueItem> pathfindQueue = default;
            bool hasPathfindQueue = false;
            for (int i = 0; i < m_FramePendingRequests.Count; i++)
            {
                PendingRequestFrameEntry entry = m_FramePendingRequests[i];
                Entity request = entry.Request;
                ServiceRequest serviceRequest = entry.ServiceRequest;
                if ((serviceRequest.m_Flags & ServiceRequestFlags.Reversed) != 0)
                    continue;
                if (serviceRequest.m_Cooldown > 0)
                    continue;

                if (!TryGetPendingRequestLineFrameState(entry.Line, out PendingRequestLineFrameState lineState)
                    || !lineState.ConfiguredDepotCompatible)
                {
                    continue;
                }

                Entity configuredDepot = lineState.ConfiguredDepot;
                PromoteConfiguredRequestFallbackIfNeeded(request, configuredDepot);

                Entity source = ResolveConfiguredRequestSource(request, configuredDepot, entry.Line);
                if (source == Entity.Null || !lineState.HasRouteSetup)
                    continue;

                if (!hasPathfindQueue)
                {
                    pathfindQueue = m_PathfindSetupSystem.GetQueue(this, 64);
                    hasPathfindQueue = true;
                }

                EnsurePendingRequestPathContainer(request);

                PathfindParameters parameters = new PathfindParameters
                {
                    m_MaxSpeed = 277.77777f,
                    m_WalkSpeed = 5.555556f,
                    m_Weights = new PathfindWeights(1f, 1f, 1f, 1f),
                    m_Methods = lineState.PathMethods,
                    m_IgnoredRules = (RuleFlags.ForbidCombustionEngines | RuleFlags.ForbidHeavyTraffic | RuleFlags.ForbidPrivateTraffic | RuleFlags.ForbidSlowTraffic | RuleFlags.AvoidBicycles),
                    m_PathfindFlags = PathfindFlags.IgnoreExtraEndAccessRequirements
                };

                SetupQueueTarget origin = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = lineState.PathMethods,
                    m_Entity = source,
                    m_TrackTypes = lineState.RouteConnectionData.m_RouteTrackType,
                    m_RoadTypes = lineState.RouteConnectionData.m_RouteRoadType
                };
                SetupQueueTarget destination = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = lineState.PathMethods,
                    m_TrackTypes = lineState.RouteConnectionData.m_RouteTrackType,
                    m_RoadTypes = lineState.RouteConnectionData.m_RouteRoadType,
                    m_Entity = lineState.DestinationWaypoint
                };

                pathfindQueue.Enqueue(new SetupQueueItem(request, parameters, origin, destination));
                m_PendingConfiguredRequestSources[request] = source;
                m_ConfiguredDepotBlockedRequests.Remove(request);
            }
        }

        private void GateConfiguredDepotDispatchRequests()
        {
            if (m_ConfiguredDispatchRequestQuery.IsEmptyIgnoreFilter)
                return;

            var lineLookup = GetComponentLookup<TransportLine>(true);
            using (NativeArray<Entity> requests = m_ConfiguredDispatchRequestQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < requests.Length; i++)
                {
                    Entity requestEntity = requests[i];
                    if (!EntityManager.Exists(requestEntity)
                        || !EntityManager.HasComponent<ServiceRequest>(requestEntity)
                        || !EntityManager.HasComponent<TransportVehicleRequest>(requestEntity)
                        || !EntityManager.HasComponent<PathInformation>(requestEntity)
                        || !EntityManager.HasBuffer<PathElement>(requestEntity))
                    {
                        continue;
                    }

                    ServiceRequest serviceRequest = EntityManager.GetComponentData<ServiceRequest>(requestEntity);
                    if ((serviceRequest.m_Flags & ServiceRequestFlags.Reversed) != 0)
                        continue;

                    TransportVehicleRequest request = EntityManager.GetComponentData<TransportVehicleRequest>(requestEntity);
                    Entity line = request.m_Route;
                    if (line == Entity.Null
                        || !EntityManager.Exists(line)
                        || !lineLookup.HasComponent(line))
                        continue;

                    ManagedRequestPort managedRequests = LifecyclePort.Current?.ManagedRequests;
                    if (managedRequests == null || !managedRequests.IsManagedLine(line))
                        continue;

                    TransportLine transportLine = lineLookup[line];
                    if (transportLine.m_VehicleRequest != requestEntity)
                        continue;

                    Entity configuredDepot = GetConfiguredDepotForLine(line);
                    if (!IsDepotCompatibleWithLine(configuredDepot, line))
                        continue;

                    if (!IsConfiguredRequestPathOrigin(requestEntity, configuredDepot))
                        continue;

                    if (!IsConfiguredDepotOutboundPathBlocked(requestEntity, configuredDepot, out Entity blocker, out Entity blockedLane))
                        continue;

                    RememberConfiguredDepotBlockedRequest(requestEntity, line, configuredDepot, blockedLane);
                    BlockConfiguredDispatchRequest(requestEntity);
                    LogConfiguredDepotGate(line, requestEntity, configuredDepot, blocker, blockedLane);
                    LogRequestDecision(line, request, configuredDepot, configuredDepot, blocker, false, "configured-depot-lane-blocked");
                }
            }
        }

        private void PromoteConfiguredRequestFallbackIfNeeded(Entity request, Entity configuredDepot)
        {
            if (!m_PendingConfiguredRequestSources.TryGetValue(request, out Entity previousSource))
                return;

            if (previousSource != Entity.Null && previousSource != configuredDepot)
            {
                m_ConfiguredRequestParkedFallbacks.Add(request);
            }

            m_PendingConfiguredRequestSources.Remove(request);
        }

        private Entity ResolveConfiguredRequestSource(Entity request, Entity configuredDepot, Entity line)
        {
            if (!m_ConfiguredRequestParkedFallbacks.Contains(request))
            {
                DepotLineCacheKey cacheKey = new DepotLineCacheKey(configuredDepot, line);
                if (!m_FrameReusableConfiguredSourceByDepotLine.TryGetValue(cacheKey, out Entity parkedSource))
                {
                    m_ReusableSourceFrameCacheMisses++;
                    parkedSource = FindReusableConfiguredDepotVehicle(configuredDepot, line);
                    m_FrameReusableConfiguredSourceByDepotLine[cacheKey] = parkedSource;
                }
                else
                {
                    m_ReusableSourceFrameCacheHits++;
                }

                if (parkedSource != Entity.Null)
                    return parkedSource;
            }

            return configuredDepot;
        }

        private Entity FindReusableConfiguredDepotVehicle(Entity configuredDepot, Entity line)
        {
            if (configuredDepot == Entity.Null
                || !EntityManager.Exists(configuredDepot)
                || !EntityManager.HasBuffer<OwnedVehicle>(configuredDepot))
            {
                return Entity.Null;
            }

            DynamicBuffer<OwnedVehicle> ownedVehicles = EntityManager.GetBuffer<OwnedVehicle>(configuredDepot, true);
            DynamicBuffer<VehicleModel> lineVehicleModels = default;
            bool hasLineVehicleModels = EntityManager.HasBuffer<VehicleModel>(line);
            if (hasLineVehicleModels)
            {
                lineVehicleModels = EntityManager.GetBuffer<VehicleModel>(line, true);
            }

            ComponentLookup<PrefabRef> prefabLookup = GetComponentLookup<PrefabRef>(true);
            ComponentLookup<MultipleUnitTrainData> multipleUnitTrainLookup = GetComponentLookup<MultipleUnitTrainData>(true);

            for (int i = 0; i < ownedVehicles.Length; i++)
            {
                Entity source = ResolveTransportVehicleController(ownedVehicles[i].m_Vehicle);
                if (CanReuseConfiguredDepotVehicle(source, configuredDepot, lineVehicleModels, hasLineVehicleModels, ref prefabLookup, ref multipleUnitTrainLookup))
                {
                    return source;
                }
            }

            return Entity.Null;
        }

        internal bool TryTheorySource(Entity line, Entity configuredDepot, out Entity source)
        {
            if (!IsDepotCompatibleWithLine(configuredDepot, line))
            {
                source = Entity.Null;
                return false;
            }
            source = configuredDepot;
            return source != Entity.Null && EntityManager.Exists(source);
        }

        private Entity ResolveTransportVehicleController(Entity vehicle)
        {
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return Entity.Null;

            if (EntityManager.HasComponent<Controller>(vehicle))
            {
                Entity controller = EntityManager.GetComponentData<Controller>(vehicle).m_Controller;
                if (controller != Entity.Null
                    && controller != vehicle
                    && EntityManager.Exists(controller))
                {
                    return controller;
                }
            }

            return vehicle;
        }

        private bool CanReuseConfiguredDepotVehicle(
            Entity vehicle,
            Entity configuredDepot,
            DynamicBuffer<VehicleModel> lineVehicleModels,
            bool hasLineVehicleModels,
            ref ComponentLookup<PrefabRef> prefabLookup,
            ref ComponentLookup<MultipleUnitTrainData> multipleUnitTrainLookup)
        {
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasComponent<Owner>(vehicle)
                || !EntityManager.HasComponent<PrefabRef>(vehicle))
            {
                return false;
            }

            Entity owner = DispatchRuntimeSystem.Instance?.CanonDepot(
                EntityManager.GetComponentData<Owner>(vehicle).m_Owner) ?? Entity.Null;
            if (owner != configuredDepot)
                return false;

            bool parked = EntityManager.HasComponent<ParkedCar>(vehicle) || EntityManager.HasComponent<ParkedTrain>(vehicle);
            if (!parked)
                return false;

            if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
                if (publicTransport.m_RequestCount > 0
                    || (publicTransport.m_State & (PublicTransportFlags.EnRoute
                        | PublicTransportFlags.Evacuating
                        | PublicTransportFlags.PrisonerTransport
                        | PublicTransportFlags.RequiresMaintenance
                        | PublicTransportFlags.DummyTraffic
                        | PublicTransportFlags.Disabled)) != 0)
                {
                    return false;
                }
            }
            else if (EntityManager.HasComponent<Game.Vehicles.CargoTransport>(vehicle))
            {
                Game.Vehicles.CargoTransport cargoTransport = EntityManager.GetComponentData<Game.Vehicles.CargoTransport>(vehicle);
                if (cargoTransport.m_RequestCount > 0
                    || (cargoTransport.m_State & (CargoTransportFlags.EnRoute
                        | CargoTransportFlags.RequiresMaintenance
                        | CargoTransportFlags.DummyTraffic
                        | CargoTransportFlags.Disabled)) != 0)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            if (!hasLineVehicleModels || lineVehicleModels.Length == 0)
                return true;

            DynamicBuffer<LayoutElement> layout = default;
            if (EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            }

            return RouteUtils.CheckVehicleModel(
                lineVehicleModels,
                prefabLookup[vehicle],
                layout,
                ref prefabLookup,
                ref multipleUnitTrainLookup);
        }

        private void EnsurePendingRequestPathContainer(Entity request)
        {
            if (EntityManager.HasComponent<PathInformation>(request))
            {
                EntityManager.SetComponentData(request, default(PathInformation));
            }
            else
            {
                EntityManager.AddComponentData(request, default(PathInformation));
            }

            if (!EntityManager.HasBuffer<PathElement>(request))
            {
                EntityManager.AddBuffer<PathElement>(request);
                return;
            }

            EntityManager.GetBuffer<PathElement>(request).Clear();
        }

        private bool TryGetConfiguredRequestDestinationWaypoint(Entity line, out Entity destinationWaypoint)
        {
            return TryGetConfiguredRequestDestinationWaypoint(line, out destinationWaypoint, out _);
        }

        private bool TryGetConfiguredRequestDestinationWaypoint(Entity line, out Entity destinationWaypoint, out int destinationWaypointIndex)
        {
            destinationWaypoint = Entity.Null;
            destinationWaypointIndex = -1;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint == Entity.Null || !EntityManager.Exists(waypoint))
                    continue;

                destinationWaypoint = waypoint;
                destinationWaypointIndex = i;
                return true;
            }

            return false;
        }

        private bool TryGetRouteConnectionData(Entity line, out RouteConnectionData routeConnectionData, out PathMethod pathMethods)
        {
            routeConnectionData = default;
            pathMethods = default;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !EntityManager.HasComponent<PrefabRef>(line))
            {
                return false;
            }

            Entity linePrefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (linePrefab == Entity.Null || !EntityManager.HasComponent<RouteConnectionData>(linePrefab))
                return false;

            routeConnectionData = EntityManager.GetComponentData<RouteConnectionData>(linePrefab);
            pathMethods = RouteUtils.GetPathMethods(
                routeConnectionData.m_RouteConnectionType,
                RouteType.TransportLine,
                routeConnectionData.m_RouteTrackType,
                routeConnectionData.m_RouteRoadType,
                routeConnectionData.m_RouteSizeClass);
            return true;
        }

        private bool IsDepotCompatibleWithLine(Entity depot, Entity line)
        {
            return depot != Entity.Null
                && EntityManager.Exists(depot)
                && EntityManager.HasComponent<Game.Buildings.TransportDepot>(depot)
                && DepotCompatibilityService.Match(EntityManager, line, depot);
        }

        private void ApplyDepotSourceLocks()
        {
            var depotLookup = GetComponentLookup<Game.Buildings.TransportDepot>(false);
            var prefabLookup = GetComponentLookup<PrefabRef>(true);
            var lineDataLookup = GetComponentLookup<TransportLineData>(true);
            var depotDataLookup = GetComponentLookup<TransportDepotData>(true);

            for (int i = 0; i < m_FramePendingRequests.Count; i++)
            {
                PendingRequestFrameEntry entry = m_FramePendingRequests[i];
                TransportVehicleRequest request = entry.VehicleRequest;
                Entity line = entry.Line;
                if (!TryGetPendingRequestLineFrameState(line, out PendingRequestLineFrameState lineState))
                    continue;

                Entity configuredDepot = lineState.ConfiguredDepot;
                Entity preferredDepot = lineState.PreferredDepot;
                if (preferredDepot == Entity.Null
                    || !EntityManager.Exists(preferredDepot)
                    || !depotLookup.HasComponent(preferredDepot))
                {
                    LogRequestDecision(line, request, configuredDepot, preferredDepot, Entity.Null, false, "no-eligible-preferred-depot");
                    continue;
                }

                if (!prefabLookup.HasComponent(line) || !prefabLookup.HasComponent(preferredDepot))
                    continue;

                Entity linePrefab = prefabLookup[line].m_Prefab;
                Entity depotPrefab = prefabLookup[preferredDepot].m_Prefab;
                if (linePrefab == Entity.Null
                    || depotPrefab == Entity.Null
                    || !lineDataLookup.HasComponent(linePrefab)
                    || !depotDataLookup.HasComponent(depotPrefab))
                {
                    continue;
                }

                TransportLineData lineData = lineDataLookup[linePrefab];
                TransportDepotData depotData = depotDataLookup[depotPrefab];
                if (lineData.m_TransportType != depotData.m_TransportType)
                {
                    LogRequestDecision(line, request, configuredDepot, preferredDepot, preferredDepot, false, "transport-type-mismatch");
                    continue;
                }

                Game.Buildings.TransportDepot depotState = depotLookup[preferredDepot];
                bool preferredDepotAvailable = (depotState.m_Flags & TransportDepotFlags.HasAvailableVehicles) != 0
                    && depotState.m_AvailableVehicles > 0;
                if (!preferredDepotAvailable)
                {
                    LogRequestDecision(line, request, configuredDepot, preferredDepot, preferredDepot, false, "preferred-depot-unavailable");
                    continue;
                }

                if (!m_FrameLockedDepotByType.TryGetValue(lineData.m_TransportType, out var existing)
                    || request.m_Priority > existing.priority)
                {
                    m_FrameLockedDepotByType[lineData.m_TransportType] = (preferredDepot, request.m_Priority);
                }

                LogRequestDecision(line, request, configuredDepot, preferredDepot, preferredDepot, true, "lock-candidate");
            }

            if (m_FrameLockedDepotByType.Count == 0)
                return;

            using (var depots = m_DepotQuery.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                for (int i = 0; i < depots.Length; i++)
                {
                    Entity depot = depots[i];
                    if (!depotLookup.HasComponent(depot) || !prefabLookup.HasComponent(depot))
                        continue;

                    Entity depotPrefab = prefabLookup[depot].m_Prefab;
                    if (depotPrefab == Entity.Null || !depotDataLookup.HasComponent(depotPrefab))
                        continue;

                    TransportDepotData depotData = depotDataLookup[depotPrefab];
                    if (!m_FrameLockedDepotByType.TryGetValue(depotData.m_TransportType, out var locked)
                        || locked.depot == depot)
                    {
                        continue;
                    }

                    Game.Buildings.TransportDepot depotState = depotLookup[depot];
                    TransportDepotFlags nextFlags = depotState.m_Flags & ~TransportDepotFlags.HasAvailableVehicles;
                    if (nextFlags == depotState.m_Flags && depotState.m_AvailableVehicles == 0)
                        continue;

                    depotState.m_Flags = nextFlags;
                    depotState.m_AvailableVehicles = 0;
                    depotLookup[depot] = depotState;
                }
            }
        }

        private Entity GetConfiguredDepotForLine(Entity line)
        {
            if (line == Entity.Null || !EntityManager.Exists(line))
                return Entity.Null;

            if (m_FrameConfiguredDepotByLine.TryGetValue(line, out Entity configuredDepot))
            {
                m_ConfiguredDepotFrameCacheHits++;
                return configuredDepot;
            }

            m_ConfiguredDepotFrameCacheMisses++;
            configuredDepot = DispatchRuntimeSystem.Instance?.GetDepot(line) ?? Entity.Null;
            m_FrameConfiguredDepotByLine[line] = configuredDepot;
            return configuredDepot;
        }

        private bool TryGetPendingRequestRouteSetup(
            Entity line,
            out RouteConnectionData routeConnectionData,
            out PathMethod pathMethods,
            out Entity destinationWaypoint)
        {
            return TryGetPendingRequestRouteSetup(line, out routeConnectionData, out pathMethods, out destinationWaypoint, out _);
        }

        private bool TryGetPendingRequestRouteSetup(
            Entity line,
            out RouteConnectionData routeConnectionData,
            out PathMethod pathMethods,
            out Entity destinationWaypoint,
            out int destinationWaypointIndex)
        {
            routeConnectionData = default;
            pathMethods = default;
            destinationWaypoint = Entity.Null;
            destinationWaypointIndex = -1;
            if (line == Entity.Null || !EntityManager.Exists(line))
                return false;

            if (!m_FramePendingRequestRouteSetupByLine.TryGetValue(line, out PendingRequestRouteSetupCacheEntry cached))
            {
                m_RouteSetupFrameCacheMisses++;
                bool available = TryGetRouteConnectionData(line, out routeConnectionData, out pathMethods)
                    && TryGetConfiguredRequestDestinationWaypoint(line, out destinationWaypoint, out destinationWaypointIndex);
                cached = new PendingRequestRouteSetupCacheEntry(
                    available,
                    routeConnectionData,
                    pathMethods,
                    destinationWaypoint);
                m_FramePendingRequestRouteSetupByLine[line] = cached;
            }
            else
            {
                m_RouteSetupFrameCacheHits++;
            }

            if (!cached.Available)
                return false;

            routeConnectionData = cached.RouteConnectionData;
            pathMethods = cached.PathMethods;
            destinationWaypoint = cached.DestinationWaypoint;
            if (destinationWaypoint != Entity.Null && EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                for (int i = 0; i < waypoints.Length; i++)
                {
                    if (waypoints[i].m_Waypoint != destinationWaypoint)
                        continue;

                    destinationWaypointIndex = i;
                    break;
                }
            }
            return true;
        }

        private void DelayBlockedConfiguredRequest(Entity request)
        {
            if (request == Entity.Null
                || !EntityManager.Exists(request)
                || !EntityManager.HasComponent<ServiceRequest>(request))
            {
                return;
            }

            ServiceRequest serviceRequest = EntityManager.GetComponentData<ServiceRequest>(request);
            if (serviceRequest.m_Cooldown < CONFIGURED_DEPOT_BRANCH_BLOCK_COOLDOWN)
            {
                serviceRequest.m_Cooldown = CONFIGURED_DEPOT_BRANCH_BLOCK_COOLDOWN;
                EntityManager.SetComponentData(request, serviceRequest);
            }
        }

        private void BlockConfiguredDispatchRequest(Entity request)
        {
            if (request == Entity.Null || !EntityManager.Exists(request))
                return;

            if (EntityManager.HasBuffer<PathElement>(request))
            {
                EntityManager.GetBuffer<PathElement>(request).Clear();
            }

            if (EntityManager.HasComponent<PathInformation>(request))
            {
                EntityManager.RemoveComponent<PathInformation>(request);
            }

            DelayBlockedConfiguredRequest(request);
        }

        private bool IsConfiguredRequestPathOrigin(Entity request, Entity configuredDepot)
        {
            if (request == Entity.Null
                || configuredDepot == Entity.Null
                || !EntityManager.Exists(request)
                || !EntityManager.HasComponent<PathInformation>(request))
            {
                return false;
            }

            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            if (control == null)
                return false;

            PathInformation pathInformation = EntityManager.GetComponentData<PathInformation>(request);
            Entity origin = pathInformation.m_Origin;
            if (origin == Entity.Null || !EntityManager.Exists(origin))
                return false;

            Entity canonicalOrigin = control.CanonDepot(origin);
            if (canonicalOrigin == configuredDepot)
                return true;

            if (EntityManager.HasComponent<Owner>(origin))
            {
                Entity originOwner = control.CanonDepot(EntityManager.GetComponentData<Owner>(origin).m_Owner);
                if (originOwner == configuredDepot)
                    return true;
            }

            return false;
        }

        private bool IsConfiguredDepotOutboundPathBlocked(Entity request, Entity configuredDepot, out Entity blocker, out Entity blockedLane)
        {
            blocker = Entity.Null;
            blockedLane = Entity.Null;
            if (request == Entity.Null
                || configuredDepot == Entity.Null
                || !EntityManager.Exists(request)
                || !EntityManager.HasBuffer<PathElement>(request))
            {
                return false;
            }

            DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(request, true);
            int laneChecks = 0;
            for (int i = 0; i < path.Length && laneChecks < CONFIGURED_DEPOT_OUTBOUND_PATH_LOOKAHEAD; i++)
            {
                Entity lane = path[i].m_Target;
                if (lane == Entity.Null || !EntityManager.Exists(lane))
                    continue;

                if (!EntityManager.HasComponent<LaneReservation>(lane))
                    continue;

                laneChecks++;

                if (TryGetInboundDepotReservationBlocker(lane, configuredDepot, out blocker))
                {
                    blockedLane = lane;
                    return true;
                }

                if (!EntityManager.HasBuffer<LaneOverlap>(lane))
                    continue;

                DynamicBuffer<LaneOverlap> overlaps = EntityManager.GetBuffer<LaneOverlap>(lane, true);
                for (int overlapIndex = 0; overlapIndex < overlaps.Length; overlapIndex++)
                {
                    Entity otherLane = overlaps[overlapIndex].m_Other;
                    if (TryGetInboundDepotReservationBlocker(otherLane, configuredDepot, out blocker))
                    {
                        blockedLane = otherLane;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryGetInboundDepotReservationBlocker(Entity lane, Entity configuredDepot, out Entity blocker)
        {
            blocker = Entity.Null;
            if (lane == Entity.Null
                || configuredDepot == Entity.Null
                || !EntityManager.Exists(lane)
                || !EntityManager.HasComponent<LaneReservation>(lane))
            {
                return false;
            }

            LaneReservation reservation = EntityManager.GetComponentData<LaneReservation>(lane);
            if (reservation.GetPriority() == 0 || reservation.m_Blocker == Entity.Null)
                return false;

            if (!IsInboundDepotBlocker(reservation.m_Blocker, configuredDepot, out blocker))
                return false;

            return true;
        }

        private bool IsInboundDepotBlocker(Entity blockerCandidate, Entity configuredDepot, out Entity blocker)
        {
            blocker = Entity.Null;
            if (blockerCandidate == Entity.Null || configuredDepot == Entity.Null)
                return false;

            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            if (control == null)
                return false;

            Entity vehicle = ResolveTransportVehicleController(blockerCandidate);
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || EntityManager.HasComponent<ParkedCar>(vehicle)
                || EntityManager.HasComponent<ParkedTrain>(vehicle)
                || !EntityManager.HasComponent<Owner>(vehicle)
                || !EntityManager.HasComponent<Target>(vehicle))
            {
                return false;
            }

            Entity ownerDepot = control.CanonDepot(EntityManager.GetComponentData<Owner>(vehicle).m_Owner);
            if (ownerDepot != configuredDepot)
                return false;

            Entity targetDepot = control.CanonDepot(EntityManager.GetComponentData<Target>(vehicle).m_Target);
            if (targetDepot != configuredDepot)
                return false;

            blocker = vehicle;
            return true;
        }

        private void LogRequestDecision(
            Entity line,
            TransportVehicleRequest request,
            Entity configuredDepot,
            Entity inferredOrPreferredDepot,
            Entity effectiveDepot,
            bool willLock,
            string reason)
        {
            if (!RtLog.VerboseEnabled)
                return;

            string key = request.m_Priority.ToString("F2")
                + "|cfg=" + configuredDepot.Index
                + "|pref=" + inferredOrPreferredDepot.Index
                + "|eff=" + effectiveDepot.Index
                + "|lock=" + (willLock ? "1" : "0")
                + "|reason=" + reason;
            if (m_RequestDecisionLogCache.TryGetValue(line, out string existing) && string.Equals(existing, key, StringComparison.Ordinal))
                return;

            m_RequestDecisionLogCache[line] = key;
            Mod.log.Info("[OfficialSpawnCandidate] line=" + line.Index
                + " priority=" + request.m_Priority.ToString("F2")
                + " configured=" + FormatDepotLabel(configuredDepot)
                + " preferred=" + FormatDepotLabel(inferredOrPreferredDepot)
                + " effective=" + FormatDepotLabel(effectiveDepot)
                + " lock=" + (willLock ? "yes" : "no")
                + " reason=" + reason);
        }

        private void LogConfiguredDepotGate(
            Entity line,
            Entity request,
            Entity configuredDepot,
            Entity blocker,
            Entity blockedLane)
        {
            if (!RtLog.VerboseEnabled)
                return;

            string key = "gate|req=" + request.Index
                + "|depot=" + configuredDepot.Index
                + "|blocker=" + blocker.Index
                + "|lane=" + blockedLane.Index;
            if (m_RequestDecisionLogCache.TryGetValue(line, out string existing)
                && string.Equals(existing, key, StringComparison.Ordinal))
            {
                return;
            }

            m_RequestDecisionLogCache[line] = key;
            Mod.log.Info("[ConfiguredDepotGate] line=" + line.Index
                + " request=" + request.Index
                + " depot=" + FormatDepotLabel(configuredDepot)
                + " blocker=" + FormatDepotLabel(blocker)
                + " lane=" + (blockedLane == Entity.Null ? "-" : ("#" + blockedLane.Index))
                + " cooldown=" + CONFIGURED_DEPOT_BRANCH_BLOCK_COOLDOWN);
        }

        private string FormatDepotLabel(Entity depot)
        {
            if (depot == Entity.Null || !EntityManager.Exists(depot))
                return "-";

            string name = m_NameSystem?.GetRenderedLabelName(depot);
            if (string.IsNullOrEmpty(name))
                return "#" + depot.Index;

            return "#" + depot.Index + "[" + name + "]";
        }
    }
}
