using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal sealed class RailEtaTheoryPathResult
    {
        public Entity Source;
        public RailTravel.Path Path;
        public float Cost;
        public int CandidateCount;
        public int ReachableCount;
        public int ProjectedCount;
    }

    internal sealed class RailEtaTheoryPaths
    {
        private readonly EntityManager m_Entities;
        private readonly RailTravel.QuerySystem m_Query;
        private readonly DepotSourceLockSystem m_Depots;
        private readonly Action<string> m_Log;
        private readonly Dictionary<string, Entity> m_Pending = new Dictionary<string, Entity>(StringComparer.Ordinal);
        private Entity m_Line;
        private Entity m_Target;
        private bool m_Configured;
        private int m_CandidateCount;
        private int m_ReachableCount;
        private int m_ProjectedCount;
        private Entity m_BestSource;
        private RailTravel.Path m_BestPath;
        private float m_BestCost;

        internal RailEtaTheoryPaths(World world, RailTravel.QuerySystem query, Action<string> log)
        {
            m_Entities = world.EntityManager;
            m_Query = query;
            m_Depots = world.GetOrCreateSystemManaged<DepotSourceLockSystem>();
            m_Log = log ?? (_ => { });
        }

        internal bool Start(RailEtaRequestDescriptor descriptor, out string failure)
        {
            Cancel();
            failure = string.Empty;
            m_Line = RailEtaEntityId.ToEntity(descriptor);
            m_Target = RailEtaEntityId.ToEntity(descriptor.TargetCheckpointId);
            Entity model = new Entity { Index = descriptor.ModelIndex, Version = descriptor.ModelVersion };
            Entity configuredDepot = new Entity { Index = descriptor.DepotIndex, Version = descriptor.DepotVersion };
            if (!TrySetup(m_Line, model, out PathfindParameters parameters, out RouteConnectionData routeData))
            {
                failure = "TheoryPathSetupMissing";
                return false;
            }

            var sources = new List<Entity>();
            m_Configured = configuredDepot != Entity.Null;
            if (m_Configured)
            {
                if (!m_Depots.TryTheorySource(m_Line, configuredDepot, out Entity source))
                {
                    failure = "TheoryConfiguredDepotUnavailable";
                    return false;
                }
                sources.Add(source);
            }
            else if (CollectSources(sources) == 0)
            {
                failure = "TheoryDepotSourcesMissing";
                return false;
            }

            SetupQueueTarget destination = new SetupQueueTarget
            {
                m_Type = SetupTargetType.CurrentLocation,
                m_Methods = parameters.m_Methods,
                m_TrackTypes = routeData.m_RouteTrackType,
                m_RoadTypes = routeData.m_RouteRoadType,
                m_Entity = m_Target
            };
            for (int i = 0; i < sources.Count; i++)
            {
                Entity source = sources[i];
                SetupQueueTarget origin = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = parameters.m_Methods,
                    m_TrackTypes = routeData.m_RouteTrackType,
                    m_RoadTypes = routeData.m_RouteRoadType,
                    m_Entity = source
                };
                string id = m_Query.Start(parameters, origin, destination);
                if (!String.IsNullOrEmpty(id)) m_Pending.Add(id, source);
            }
            m_CandidateCount = sources.Count;
            m_BestCost = float.PositiveInfinity;
            if (m_Pending.Count != 0) return true;
            failure = "TheoryPathSubmitFailed";
            return false;
        }

        internal bool Poll(out RailEtaTheoryPathResult result, out string failure)
        {
            result = null;
            failure = string.Empty;
            if (m_Pending.Count == 0)
            {
                failure = "TheoryPathRequestsMissing";
                return true;
            }

            List<string> completed = null;
            foreach (KeyValuePair<string, Entity> pending in m_Pending)
            {
                if (!m_Query.TryGetTheoryDepotResult(pending.Key, out RailTravel.QueryResult query))
                    continue;
                if (String.Equals(query.State, "pending", StringComparison.Ordinal)) continue;
                (completed ?? (completed = new List<string>())).Add(pending.Key);
                if (!query.Success || query.Information.m_Origin != pending.Value
                    || query.Information.m_Destination != m_Target) continue;
                float cost = query.Information.m_TotalCost;
                if (float.IsNaN(cost) || float.IsInfinity(cost) || cost < 0f) continue;
                m_ReachableCount++;
                bool projected = query.ProjectionSuccess && query.Path != null && !query.Path.IsEmpty;
                if (!projected) continue;
                m_ProjectedCount++;
                if (m_BestSource == Entity.Null || cost < m_BestCost
                    || (cost == m_BestCost && Before(pending.Value, m_BestSource)))
                {
                    m_BestSource = pending.Value;
                    m_BestPath = query.Path;
                    m_BestCost = cost;
                }
            }
            if (completed != null)
                for (int i = 0; i < completed.Count; i++) m_Pending.Remove(completed[i]);
            if (m_Pending.Count != 0) return false;

            if (m_Log != null)
                m_Log("[RailEtaTheory] line=" + m_Line.Index + " depotCandidates=" + m_CandidateCount
                    + " vanillaReachableDepots=" + m_ReachableCount
                    + " projectedDepots=" + m_ProjectedCount + " selectedCost="
                    + (m_BestSource == Entity.Null ? "none" : m_BestCost.ToString("F2"))
                    + " selectedSource=" + m_BestSource.Index + ":" + m_BestSource.Version);
            if (m_BestSource == Entity.Null)
            {
                failure = m_ReachableCount != 0
                    ? (m_Configured ? "TheoryConfiguredDepotProjectionFailed" : "TheoryDepotProjectionFailed")
                    : (m_Configured ? "TheoryConfiguredDepotUnreachable" : "TheoryNoReachableDepot");
                return true;
            }
            result = new RailEtaTheoryPathResult
            {
                Source = m_BestSource,
                Path = m_BestPath,
                Cost = m_BestCost,
                CandidateCount = m_CandidateCount,
                ReachableCount = m_ReachableCount,
                ProjectedCount = m_ProjectedCount
            };
            return true;
        }

        internal void Cancel()
        {
            foreach (string id in m_Pending.Keys) m_Query.Cancel(id);
            m_Pending.Clear();
            m_Line = Entity.Null;
            m_Target = Entity.Null;
            m_Configured = false;
            m_CandidateCount = 0;
            m_ReachableCount = 0;
            m_ProjectedCount = 0;
            m_BestSource = Entity.Null;
            m_BestPath = null;
            m_BestCost = float.PositiveInfinity;
        }

        private bool TrySetup(Entity line, Entity model, out PathfindParameters parameters, out RouteConnectionData routeData)
        {
            parameters = default;
            routeData = default;
            if (!m_Entities.Exists(line) || !m_Entities.Exists(model)
                || !m_Entities.HasComponent<PrefabRef>(line) || !m_Entities.HasComponent<TrainData>(model)) return false;
            Entity prefab = m_Entities.GetComponentData<PrefabRef>(line).m_Prefab;
            if (prefab == Entity.Null || !m_Entities.HasComponent<RouteConnectionData>(prefab)) return false;
            routeData = m_Entities.GetComponentData<RouteConnectionData>(prefab);
            TrainData train = m_Entities.GetComponentData<TrainData>(model);
            PathMethod methods = RouteUtils.GetPathMethods(routeData.m_RouteConnectionType, RouteType.TransportLine,
                routeData.m_RouteTrackType, routeData.m_RouteRoadType, routeData.m_RouteSizeClass);
            parameters = new PathfindParameters
            {
                m_MaxSpeed = train.m_MaxSpeed,
                m_WalkSpeed = 5.555556f,
                m_Weights = new PathfindWeights(1f, 1f, 1f, 1f),
                m_Methods = methods,
                m_IgnoredRules = RuleFlags.ForbidCombustionEngines | RuleFlags.ForbidHeavyTraffic
                    | RuleFlags.ForbidPrivateTraffic | RuleFlags.ForbidSlowTraffic | RuleFlags.AvoidBicycles,
                m_PathfindFlags = PathfindFlags.Stable | PathfindFlags.IgnoreFlow | PathfindFlags.IgnoreExtraEndAccessRequirements
            };
            return true;
        }

        private int CollectSources(List<Entity> sources)
        {
            EntityQuery depots = m_Entities.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.TransportDepot>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Buildings.ServiceUpgrade>(), ComponentType.Exclude<Deleted>());
            using (NativeArray<Entity> values = depots.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < values.Length; i++)
                {
                    Entity depot = values[i];
                    if (!DepotCompatibilityService.Match(m_Entities, m_Line, depot)) continue;
                    Game.Buildings.TransportDepot state = m_Entities.GetComponentData<Game.Buildings.TransportDepot>(depot);
                    if ((state.m_Flags & TransportDepotFlags.HasAvailableVehicles) == 0) continue;
                    if (!sources.Contains(depot)) sources.Add(depot);
                }
            }
            sources.Sort(BeforeCompare);
            return sources.Count;
        }

        private static int BeforeCompare(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Version.CompareTo(right.Version);
        }

        private static bool Before(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index < 0 || index == 0 && left.Version < right.Version;
        }
    }
}
