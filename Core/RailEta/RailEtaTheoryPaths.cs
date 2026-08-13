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

    internal sealed class RailEtaTheorySegmentPathResult
    {
        public RailEtaTheorySegmentRequest Request;
        public RailTravel.Path Path;
        public bool FormalPath;
    }

    internal sealed class RailEtaTheoryPaths
    {
        private readonly EntityManager m_Entities;
        private readonly RailTravel.QuerySystem m_Query;
        private readonly DepotSourceLockSystem m_Depots;
        private readonly Action<string> m_Log;
        private readonly Dictionary<string, Entity> m_Pending = new Dictionary<string, Entity>(StringComparer.Ordinal);
        private readonly Dictionary<string, RailEtaTheorySegmentRequest> m_SegmentPending =
            new Dictionary<string, RailEtaTheorySegmentRequest>(StringComparer.Ordinal);
        private readonly Dictionary<int, RailEtaTheorySegmentPathResult> m_SegmentResults =
            new Dictionary<int, RailEtaTheorySegmentPathResult>();
        private Entity m_Line;
        private Entity m_Target;
        private Entity m_Model;
        private string m_SegmentFailure = string.Empty;
        private RailEtaTheoryFailure m_SegmentFailureInfo;
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

        internal bool StartSegments(Entity line, Entity model, RailEtaTheorySegmentRequest[] segments, out string failure)
        {
            Cancel();
            failure = string.Empty;
            if (line == Entity.Null || model == Entity.Null || segments == null || segments.Length == 0 || segments.Length > 256)
            {
                failure = "TheorySegmentRequestInvalid";
                return false;
            }
            if (!TrySetup(line, model, out PathfindParameters parameters, out RouteConnectionData routeData)
                || !m_Entities.HasBuffer<RouteSegment>(line)
                || !m_Entities.HasBuffer<RouteWaypoint>(line))
            {
                failure = "TheorySegmentPathSetupMissing";
                return false;
            }

            DynamicBuffer<RouteSegment> routeSegments = m_Entities.GetBuffer<RouteSegment>(line, true);
            DynamicBuffer<RouteWaypoint> waypoints = m_Entities.GetBuffer<RouteWaypoint>(line, true);
            var seen = new HashSet<int>();
            m_Line = line;
            m_Model = model;
            for (int i = 0; i < segments.Length; i++)
            {
                RailEtaTheorySegmentRequest request = segments[i];
                if (!seen.Add(request.PathSlotIndex)
                    || request.PathSlotIndex < 0
                    || request.PathSlotIndex >= routeSegments.Length
                    || request.FromWaypointIndex < 0
                    || request.FromWaypointIndex >= waypoints.Length
                    || request.ToWaypointIndex < 0
                    || request.ToWaypointIndex >= waypoints.Length)
                {
                    failure = "TheorySegmentSequenceInvalid";
                    Cancel();
                    return false;
                }

                Entity from = waypoints[request.FromWaypointIndex].m_Waypoint;
                Entity to = waypoints[request.ToWaypointIndex].m_Waypoint;
                if (from == Entity.Null || to == Entity.Null
                    || from.Version != request.FromWaypointVersion
                    || to.Version != request.ToWaypointVersion)
                {
                    failure = "TheorySegmentSignatureMismatch";
                    Cancel();
                    return false;
                }

                Entity routeSegment = routeSegments[request.PathSlotIndex].m_Segment;
                if (routeSegment != Entity.Null
                    && new RailTravel.PathQuery(m_Entities).TryBuild(routeSegment, out RailTravel.Path existing)
                    && existing != null && existing.Segments.Length != 0)
                {
                    m_SegmentResults[request.PathSlotIndex] = new RailEtaTheorySegmentPathResult
                    {
                        Request = request,
                        Path = existing,
                        FormalPath = true
                    };
                    continue;
                }

                SetupQueueTarget origin = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = parameters.m_Methods,
                    m_TrackTypes = routeData.m_RouteTrackType,
                    m_RoadTypes = routeData.m_RouteRoadType,
                    m_Entity = from
                };
                SetupQueueTarget destination = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = parameters.m_Methods,
                    m_TrackTypes = routeData.m_RouteTrackType,
                    m_RoadTypes = routeData.m_RouteRoadType,
                    m_Entity = to
                };
                string id = m_Query.Start(parameters, origin, destination, 512u, 64, 0);
                if (String.IsNullOrEmpty(id))
                {
                    failure = "TheorySegmentPathSubmitFailed";
                    Cancel();
                    return false;
                }
                m_SegmentPending.Add(id, request);
            }

            return true;
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

        internal bool PollSegments(
            out List<RailEtaTheorySegmentPathResult> results,
            out string failure,
            out RailEtaTheoryFailure failureInfo)
        {
            results = null;
            failure = m_SegmentFailure;
            failureInfo = m_SegmentFailureInfo;
            if (m_SegmentPending.Count != 0)
            {
                List<string> completed = null;
                foreach (KeyValuePair<string, RailEtaTheorySegmentRequest> pending in m_SegmentPending)
                {
                    if (!m_Query.TryGetResult(pending.Key, out RailTravel.QueryResult query)
                        || String.Equals(query.State, "pending", StringComparison.Ordinal))
                        continue;
                    (completed ?? (completed = new List<string>())).Add(pending.Key);
                    if (!query.Success || !query.ProjectionSuccess || query.Path == null
                        || query.Path.Segments.Length == 0)
                    {
                        string code = !query.Success
                            ? "segment-path-failed"
                            : !query.ProjectionSuccess
                                ? "segment-path-projection-failed"
                                : "segment-path-empty";
                        if (m_SegmentFailureInfo == null)
                        {
                            RailEtaTheorySegmentRequest failedRequest = pending.Value;
                            m_SegmentFailure = code;
                            m_SegmentFailureInfo = new RailEtaTheoryFailure
                            {
                                SegmentIndex = failedRequest.SegmentIndex,
                                FromWaypointIndex = failedRequest.FromWaypointIndex,
                                ToWaypointIndex = failedRequest.ToWaypointIndex,
                                Failure = code,
                                Detail = SegmentFailureDetail(failedRequest, code, query.Error)
                            };
                        }
                        continue;
                    }
                    RailEtaTheorySegmentRequest completedRequest = pending.Value;
                    m_SegmentResults[completedRequest.PathSlotIndex] = new RailEtaTheorySegmentPathResult
                    {
                        Request = completedRequest,
                        Path = query.Path
                    };
                }
                if (completed != null)
                    for (int i = 0; i < completed.Count; i++) m_SegmentPending.Remove(completed[i]);
                if (m_SegmentPending.Count != 0) return false;
            }

            if (!String.IsNullOrEmpty(m_SegmentFailure))
            {
                failure = m_SegmentFailure;
                failureInfo = m_SegmentFailureInfo;
                return true;
            }
            results = new List<RailEtaTheorySegmentPathResult>(m_SegmentResults.Values);
            results.Sort((left, right) => left.Request.PathSlotIndex.CompareTo(right.Request.PathSlotIndex));
            return true;
        }

        private static string SegmentFailureDetail(
            RailEtaTheorySegmentRequest request, string code, string reason)
        {
            string detail = "code=" + code
                + ";seg=" + request.SegmentIndex
                + ";from=" + request.FromWaypointIndex
                + ";to=" + request.ToWaypointIndex
                + ";slot=" + request.PathSlotIndex;
            if (String.IsNullOrEmpty(reason)) return detail;
            string token = reason.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
            if (token.Length > 128) token = token.Substring(0, 128);
            return detail + ";reason=" + token;
        }

        internal void Cancel()
        {
            foreach (string id in m_Pending.Keys) m_Query.Cancel(id);
            foreach (string id in m_SegmentPending.Keys) m_Query.Cancel(id);
            m_Pending.Clear();
            m_SegmentPending.Clear();
            m_SegmentResults.Clear();
            m_Line = Entity.Null;
            m_Target = Entity.Null;
            m_Model = Entity.Null;
            m_SegmentFailure = string.Empty;
            m_SegmentFailureInfo = null;
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
