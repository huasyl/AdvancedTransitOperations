using System;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
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
    public sealed class DepotSourceLockSystem : GameSystemBase
    {
        private EntityQuery m_PendingRequestQuery;
        private EntityQuery m_DepotQuery;
        private EntityQuery m_LineQuery;
        private NameSystem m_NameSystem = null!;
        private PathfindSetupSystem m_PathfindSetupSystem = null!;

        private readonly Dictionary<Entity, Entity> m_PreferredDepotByLine = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, string> m_RequestDecisionLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, Entity> m_PendingConfiguredRequestSources = new Dictionary<Entity, Entity>();
        private readonly HashSet<Entity> m_ConfiguredRequestParkedFallbacks = new HashSet<Entity>();
        private readonly List<Entity> m_RequestCleanupScratch = new List<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_PathfindSetupSystem = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
            m_PendingRequestQuery = GetEntityQuery(
                ComponentType.ReadOnly<ServiceRequest>(),
                ComponentType.ReadOnly<TransportVehicleRequest>(),
                ComponentType.Exclude<Dispatched>(),
                ComponentType.Exclude<PathInformation>(),
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
            CleanupConfiguredRequestTracking();
            if (m_PendingRequestQuery.IsEmptyIgnoreFilter || m_DepotQuery.IsEmptyIgnoreFilter || m_LineQuery.IsEmptyIgnoreFilter)
                return;

            UpdateLineDepotAffinity();
            QueueConfiguredDepotRequests();
            ApplyDepotSourceLocks();
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

            if (m_ConfiguredRequestParkedFallbacks.Count == 0)
                return;

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
                m_ConfiguredRequestParkedFallbacks.Remove(m_RequestCleanupScratch[i]);
            }
        }

        private void QueueConfiguredDepotRequests()
        {
            NativeQueue<SetupQueueItem> pathfindQueue = default;
            bool hasPathfindQueue = false;

            using (NativeArray<Entity> requests = m_PendingRequestQuery.ToEntityArray(Allocator.Temp))
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
                        || !EntityManager.HasComponent<TransportLine>(line))
                    {
                        continue;
                    }

                    Entity configuredDepot = DepartureControlSystem.Instance?.GetConfiguredAllowedDepot(line) ?? Entity.Null;
                    if (!IsDepotCompatibleWithLine(configuredDepot, line))
                        continue;

                    PromoteConfiguredRequestFallbackIfNeeded(request, configuredDepot);

                    if (!TryGetRouteConnectionData(line, out RouteConnectionData routeConnectionData, out PathMethod pathMethods))
                        continue;

                    Entity source = ResolveConfiguredRequestSource(request, configuredDepot, line);
                    if (source == Entity.Null)
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
                        m_Methods = pathMethods,
                        m_IgnoredRules = (RuleFlags.ForbidCombustionEngines | RuleFlags.ForbidHeavyTraffic | RuleFlags.ForbidPrivateTraffic | RuleFlags.ForbidSlowTraffic | RuleFlags.AvoidBicycles),
                        m_PathfindFlags = PathfindFlags.IgnoreExtraEndAccessRequirements
                    };

                    SetupQueueTarget origin = new SetupQueueTarget
                    {
                        m_Type = SetupTargetType.CurrentLocation,
                        m_Methods = pathMethods,
                        m_Entity = source,
                        m_TrackTypes = routeConnectionData.m_RouteTrackType,
                        m_RoadTypes = routeConnectionData.m_RouteRoadType
                    };
                    SetupQueueTarget destination = new SetupQueueTarget
                    {
                        m_Type = SetupTargetType.RouteWaypoints,
                        m_Methods = pathMethods,
                        m_TrackTypes = routeConnectionData.m_RouteTrackType,
                        m_RoadTypes = routeConnectionData.m_RouteRoadType,
                        m_Entity = line
                    };

                    pathfindQueue.Enqueue(new SetupQueueItem(request, parameters, origin, destination));
                    m_PendingConfiguredRequestSources[request] = source;
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
                Entity parkedSource = FindReusableConfiguredDepotVehicle(configuredDepot, line);
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

            Entity owner = DepartureControlSystem.Instance?.CanonicalizeTransportDepotEntity(
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
            if (depot == Entity.Null
                || line == Entity.Null
                || !EntityManager.Exists(depot)
                || !EntityManager.Exists(line)
                || !EntityManager.HasComponent<Game.Buildings.TransportDepot>(depot)
                || !EntityManager.HasComponent<PrefabRef>(depot)
                || !EntityManager.HasComponent<PrefabRef>(line))
            {
                return false;
            }

            Entity depotPrefab = EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
            Entity linePrefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (depotPrefab == Entity.Null
                || linePrefab == Entity.Null
                || !EntityManager.HasComponent<TransportDepotData>(depotPrefab)
                || !EntityManager.HasComponent<TransportLineData>(linePrefab))
            {
                return false;
            }

            TransportDepotData depotData = EntityManager.GetComponentData<TransportDepotData>(depotPrefab);
            TransportLineData lineData = EntityManager.GetComponentData<TransportLineData>(linePrefab);
            return depotData.m_TransportType == lineData.m_TransportType;
        }

        private void UpdateLineDepotAffinity()
        {
            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            var ownerLookup = GetComponentLookup<Owner>(true);
            var depotLookup = GetComponentLookup<Game.Buildings.TransportDepot>(true);
            using (NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles))
                        continue;

                    Dictionary<Entity, int> counts = null;
                    Entity bestDepot = Entity.Null;
                    int bestCount = 0;

                    for (int j = 0; j < vehicles.Length; j++)
                    {
                        Entity vehicle = vehicles[j].m_Vehicle;
                        if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) || !ownerLookup.HasComponent(vehicle))
                            continue;

                        Entity depot = ownerLookup[vehicle].m_Owner;
                        if (depot == Entity.Null || !EntityManager.Exists(depot) || !depotLookup.HasComponent(depot))
                            continue;

                        counts ??= new Dictionary<Entity, int>();
                        int nextCount = counts.TryGetValue(depot, out int currentCount) ? currentCount + 1 : 1;
                        counts[depot] = nextCount;
                        if (nextCount > bestCount)
                        {
                            bestCount = nextCount;
                            bestDepot = depot;
                        }
                    }

                    if (bestDepot != Entity.Null)
                    {
                        m_PreferredDepotByLine[line] = bestDepot;
                    }
                }
            }
        }

        private void ApplyDepotSourceLocks()
        {
            var requestLookup = GetComponentLookup<TransportVehicleRequest>(true);
            var lineLookup = GetComponentLookup<TransportLine>(true);
            var depotLookup = GetComponentLookup<Game.Buildings.TransportDepot>(false);
            var prefabLookup = GetComponentLookup<PrefabRef>(true);
            var lineDataLookup = GetComponentLookup<TransportLineData>(true);
            var depotDataLookup = GetComponentLookup<TransportDepotData>(true);

            Dictionary<TransportType, (Entity depot, float priority)> lockedDepotByType =
                new Dictionary<TransportType, (Entity depot, float priority)>();

            using (var requests = m_PendingRequestQuery.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                for (int i = 0; i < requests.Length; i++)
                {
                    Entity requestEntity = requests[i];
                    if (!requestLookup.HasComponent(requestEntity))
                        continue;

                    TransportVehicleRequest request = requestLookup[requestEntity];
                    Entity line = request.m_Route;
                    if (line == Entity.Null || !EntityManager.Exists(line) || !lineLookup.HasComponent(line))
                        continue;

                    Entity configuredDepot = DepartureControlSystem.Instance?.GetConfiguredAllowedDepot(line) ?? Entity.Null;
                    Entity preferredDepot = ResolvePreferredDepot(line);
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

                    if (!lockedDepotByType.TryGetValue(lineData.m_TransportType, out var existing)
                        || request.m_Priority > existing.priority)
                    {
                        lockedDepotByType[lineData.m_TransportType] = (preferredDepot, request.m_Priority);
                    }

                    LogRequestDecision(line, request, configuredDepot, preferredDepot, preferredDepot, true, "lock-candidate");
                }
            }

            if (lockedDepotByType.Count == 0)
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
                    if (!lockedDepotByType.TryGetValue(depotData.m_TransportType, out var locked)
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

        private Entity ResolvePreferredDepot(Entity line)
        {
            DepartureControlSystem control = DepartureControlSystem.Instance;
            if (control != null)
            {
                Entity configuredDepot = control.GetConfiguredAllowedDepot(line);
                if (configuredDepot != Entity.Null)
                {
                    return configuredDepot;
                }
            }

            return m_PreferredDepotByLine.TryGetValue(line, out Entity inferredDepot) ? inferredDepot : Entity.Null;
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
