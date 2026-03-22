using System;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
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

        private readonly Dictionary<Entity, Entity> m_PreferredDepotByLine = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, string> m_RequestDecisionLogCache = new Dictionary<Entity, string>();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_PendingRequestQuery = GetEntityQuery(
                ComponentType.ReadOnly<ServiceRequest>(),
                ComponentType.ReadOnly<TransportVehicleRequest>(),
                ComponentType.Exclude<Dispatched>(),
                ComponentType.Exclude<Game.Pathfind.PathInformation>(),
                ComponentType.Exclude<Deleted>());
            m_DepotQuery = GetEntityQuery(
                ComponentType.ReadWrite<Game.Buildings.TransportDepot>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>());
            m_LineQuery = GetEntityQuery(
                ComponentType.ReadOnly<TransportLine>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>());
        }

        protected override void OnUpdate()
        {
            if (m_PendingRequestQuery.IsEmptyIgnoreFilter || m_DepotQuery.IsEmptyIgnoreFilter || m_LineQuery.IsEmptyIgnoreFilter)
                return;

            UpdateLineDepotAffinity();
            ApplyDepotSourceLocks();
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
