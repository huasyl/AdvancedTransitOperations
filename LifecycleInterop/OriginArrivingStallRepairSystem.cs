using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Routes;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public sealed partial class OriginArrivingStallRepairSystem : GameSystemBase
    {
        private const uint CandidateSettleFrames = 16;
        private const uint RepairAckTimeoutFrames = 32;
        private const int MaxRepairsPerUpdate = 2;

        private EntityQuery m_Query;
        private SimulationSystem m_SimulationSystem;
        private readonly Dictionary<Entity, RepairRecord> m_Records = new Dictionary<Entity, RepairRecord>();
        private readonly Dictionary<Entity, string> m_LastRejectKeys = new Dictionary<Entity, string>();

        private struct RepairRecord
        {
            public Entity Target;
            public uint FirstFrame;
            public uint RepairFrame;
            public bool Repaired;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 16;
        }

        public override int GetUpdateOffset(SystemUpdatePhase phase)
        {
            return 3;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PublicTransport>(),
                    ComponentType.ReadOnly<Target>(),
                    ComponentType.ReadOnly<PathOwner>(),
                    ComponentType.ReadOnly<CurrentRoute>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<TripSource>()
                }
            });
            RequireForUpdate(m_Query);
        }

        protected override void OnDestroy()
        {
            m_Records.Clear();
            m_LastRejectKeys.Clear();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (GameManager.instance.gameMode != GameMode.Game)
                return;

            LifecyclePort lifecycle = LifecyclePort.Current;
            OriginRepairPort originRepair = lifecycle != null ? lifecycle.OriginRepair : null;
            if (originRepair == null || !originRepair.IsReady())
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            int repairs = 0;
            NativeArray<Entity> vehicles = m_Query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!EntityManager.Exists(vehicle))
                        continue;

                    if (TryProcessRepairAck(vehicle, nowFrame))
                        continue;

                    if (!TryBuildCandidate(originRepair, vehicle, out Candidate candidate, out RejectDiagnostic reject))
                    {
                        LogRejectOnce(vehicle, reject, nowFrame);
                        m_Records.Remove(vehicle);
                        continue;
                    }

                    m_LastRejectKeys.Remove(vehicle);

                    if (!m_Records.TryGetValue(vehicle, out RepairRecord record)
                        || record.Target != candidate.Target)
                    {
                        m_Records[vehicle] = new RepairRecord
                        {
                            Target = candidate.Target,
                            FirstFrame = nowFrame
                        };
                        LogCandidate(vehicle, candidate, nowFrame);
                        continue;
                    }

                    if (record.Repaired || nowFrame - record.FirstFrame < CandidateSettleFrames)
                        continue;

                    if (repairs >= MaxRepairsPerUpdate)
                        continue;

                    if (TryRepairCandidate(vehicle, candidate, nowFrame))
                    {
                        record.Repaired = true;
                        record.RepairFrame = nowFrame;
                        m_Records[vehicle] = record;
                        repairs++;
                    }
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        private bool TryProcessRepairAck(Entity vehicle, uint nowFrame)
        {
            if (!m_Records.TryGetValue(vehicle, out RepairRecord record) || !record.Repaired)
                return false;

            if (!EntityManager.Exists(vehicle)
                || !EntityManager.HasComponent<PublicTransport>(vehicle)
                || !EntityManager.HasComponent<Target>(vehicle))
            {
                m_Records.Remove(vehicle);
                return true;
            }

            PublicTransport publicTransport = EntityManager.GetComponentData<PublicTransport>(vehicle);
            Target target = EntityManager.GetComponentData<Target>(vehicle);
            if ((publicTransport.m_State & PublicTransportFlags.Boarding) != 0)
            {
                if (RtLog.VerboseEnabled)
                {
                    Mod.log.Info("[OriginArrivingStallAck] vehicle=" + vehicle.Index
                        + " target=" + record.Target.Index
                        + " frame=" + nowFrame
                        + " ptState=" + publicTransport.m_State);
                }
                m_Records.Remove(vehicle);
                return true;
            }

            if (target.m_Target != record.Target)
            {
                m_Records.Remove(vehicle);
                return true;
            }

            if (nowFrame - record.RepairFrame >= RepairAckTimeoutFrames)
            {
                if (RtLog.VerboseEnabled)
                {
                    Mod.log.Info("[OriginArrivingStallRepairMiss] vehicle=" + vehicle.Index
                        + " target=" + record.Target.Index
                        + " frame=" + nowFrame
                        + " ptState=" + publicTransport.m_State);
                }
                m_Records.Remove(vehicle);
                return true;
            }

            return true;
        }

        private bool TryBuildCandidate(
            OriginRepairPort originRepair,
            Entity vehicle,
            out Candidate candidate,
            out RejectDiagnostic reject)
        {
            candidate = default;
            reject = new RejectDiagnostic
            {
                Vehicle = vehicle,
                NavigationEndIndex = -1,
                WaypointIndex = -1,
                RouteProgressWaypointIndex = -1,
                Speed = -1f
            };

            if (!originRepair.TryVehicleState(vehicle, out VehicleState state)
                || state != VehicleState.Preparing)
            {
                return false;
            }

            PublicTransport publicTransport = EntityManager.GetComponentData<PublicTransport>(vehicle);
            if ((publicTransport.m_State & PublicTransportFlags.Arriving) == 0
                || (publicTransport.m_State & (PublicTransportFlags.Boarding
                    | PublicTransportFlags.Returning
                    | PublicTransportFlags.Disabled)) != 0)
            {
                return false;
            }

            Target target = EntityManager.GetComponentData<Target>(vehicle);
            CurrentRoute currentRoute = EntityManager.GetComponentData<CurrentRoute>(vehicle);
            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
            reject.Line = currentRoute.m_Route;
            reject.Target = target.m_Target;
            reject.PathState = pathOwner.m_State;
            reject.PublicTransportState = publicTransport.m_State;
            if (target.m_Target == Entity.Null
                || currentRoute.m_Route == Entity.Null
                || !EntityManager.HasBuffer<RouteWaypoint>(currentRoute.m_Route))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(currentRoute.m_Route, true);
            if (waypoints.Length == 0 || waypoints[0].m_Waypoint != target.m_Target)
                return false;

            reject.KeepState = true;

            if ((pathOwner.m_State & (PathFlags.Pending
                | PathFlags.Failed
                | PathFlags.Stuck
                | PathFlags.Obsolete
                | PathFlags.Updated)) != 0)
            {
                reject.Reason = "path-state-blocked";
                return false;
            }

            if (!EntityManager.HasComponent<Connected>(target.m_Target))
            {
                reject.Reason = "target-no-connected-stop";
                return false;
            }

            Entity stop = EntityManager.GetComponentData<Connected>(target.m_Target).m_Connected;
            reject.Stop = stop;
            if (stop == Entity.Null)
            {
                reject.Reason = "target-no-connected-stop";
                return false;
            }

            if (!EntityManager.HasComponent<BoardingVehicle>(stop))
            {
                reject.Reason = "stop-no-boardingvehicle";
                return false;
            }

            BoardingVehicle boardingVehicle = EntityManager.GetComponentData<BoardingVehicle>(stop);
            reject.StopBoardingVehicle = boardingVehicle.m_Vehicle;
            reject.StopBoardingVehicleBoarding = boardingVehicle.m_Vehicle != Entity.Null
                && IsVehicleBoarding(boardingVehicle.m_Vehicle);
            if (boardingVehicle.m_Vehicle != Entity.Null
                && boardingVehicle.m_Vehicle != vehicle
                && reject.StopBoardingVehicleBoarding)
            {
                reject.Reason = "stop-occupied-by-other-boarder";
                return false;
            }

            Entity headVehicle = ResolveHeadVehicle(vehicle);
            reject.HeadVehicle = headVehicle;
            reject.HasHead = headVehicle != Entity.Null;
            reject.HasCurrentLane = headVehicle != Entity.Null && EntityManager.HasComponent<TrainCurrentLane>(headVehicle);
            reject.HasNavigation = headVehicle != Entity.Null && EntityManager.HasComponent<TrainNavigation>(headVehicle);
            reject.HasNavigationBuffer = EntityManager.HasBuffer<TrainNavigationLane>(vehicle);
            if (!reject.HasHead)
            {
                reject.Reason = "head-missing";
                return false;
            }

            if (!reject.HasCurrentLane)
            {
                reject.Reason = "head-no-currentlane";
                return false;
            }

            if (!reject.HasNavigation)
            {
                reject.Reason = "head-no-navigation";
                return false;
            }

            if (!reject.HasNavigationBuffer)
            {
                reject.Reason = "vehicle-no-nav-buffer";
                return false;
            }

            TrainNavigation navigation = EntityManager.GetComponentData<TrainNavigation>(headVehicle);
            reject.Speed = navigation.m_Speed;
            if (!(navigation.m_Speed < 0.1f))
            {
                reject.Reason = "speed-above-threshold";
                return false;
            }

            TrainCurrentLane currentLane = EntityManager.GetComponentData<TrainCurrentLane>(headVehicle);
            TrainLaneFlags frontFlags = currentLane.m_Front.m_LaneFlags;
            reject.FrontFlags = frontFlags;
            TrainLaneFlags pathEndFlags = TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached;
            if ((frontFlags & pathEndFlags) == pathEndFlags)
            {
                reject.Reason = "already-path-end-reached";
                return false;
            }

            DynamicBuffer<TrainNavigationLane> navigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(vehicle);
            reject.NavigationLaneCount = navigationLanes.Length;
            int navigationEndIndex = FindNavigationEndIndex(navigationLanes);
            reject.NavigationEndIndex = navigationEndIndex;
            if ((frontFlags & TrainLaneFlags.EndOfPath) == 0 && navigationEndIndex < 0)
            {
                reject.Reason = "no-front-endofpath-and-no-nav-end";
                return false;
            }

            int waypointIndex = originRepair.ComputeWaypointIndex(vehicle, waypoints);
            reject.WaypointIndex = waypointIndex;
            bool routeProgressAtOrigin = originRepair.TryOriginProgress(
                vehicle,
                out int routeProgressWaypointIndex,
                out float routeProgressSegmentPosition);
            reject.HasRouteProgress = routeProgressAtOrigin;
            reject.RouteProgressWaypointIndex = routeProgressWaypointIndex;
            reject.RouteProgressSegmentPosition = routeProgressSegmentPosition;
            if (waypointIndex != 0 && !routeProgressAtOrigin)
            {
                reject.Reason = "not-origin-by-compute-or-routeprogress";
                return false;
            }

            candidate = new Candidate(
                vehicle,
                headVehicle,
                currentRoute.m_Route,
                target.m_Target,
                stop,
                navigation.m_Speed,
                frontFlags,
                navigationLanes.Length,
                navigationEndIndex,
                waypointIndex,
                routeProgressWaypointIndex,
                routeProgressSegmentPosition,
                routeProgressAtOrigin,
                publicTransport.m_State,
                pathOwner.m_State);
            return true;
        }

        private void LogRejectOnce(Entity vehicle, RejectDiagnostic reject, uint nowFrame)
        {
            if (!reject.KeepState || string.IsNullOrEmpty(reject.Reason))
            {
                m_LastRejectKeys.Remove(vehicle);
                return;
            }

            string key = BuildRejectKey(reject);
            if (m_LastRejectKeys.TryGetValue(vehicle, out string lastKey) && lastKey == key)
                return;

            m_LastRejectKeys[vehicle] = key;
            if (RtLog.VerboseEnabled)
            {
                Mod.log.Info("[OriginArrivingStallReject] vehicle=" + GetEntityIndex(vehicle)
                    + " head=" + GetEntityIndex(reject.HeadVehicle)
                    + " line=" + GetEntityIndex(reject.Line)
                    + " target=" + GetEntityIndex(reject.Target)
                    + " stop=" + GetEntityIndex(reject.Stop)
                    + " frame=" + nowFrame
                    + " reason=" + reject.Reason
                    + " speed=" + reject.Speed.ToString("F2")
                    + " frontFlags=" + reject.FrontFlags
                    + " navLen=" + reject.NavigationLaneCount
                    + " navEndIndex=" + reject.NavigationEndIndex
                    + " computeWp=" + reject.WaypointIndex
                    + " routeWp=" + reject.RouteProgressWaypointIndex
                    + " routeSeg=" + reject.RouteProgressSegmentPosition.ToString("F2")
                    + " routeHit=" + (reject.HasRouteProgress ? "1" : "0")
                    + " pathState=" + reject.PathState
                    + " ptState=" + reject.PublicTransportState
                    + " stopVehicle=" + GetEntityIndex(reject.StopBoardingVehicle)
                    + " stopVehicleBoarding=" + (reject.StopBoardingVehicleBoarding ? "1" : "0")
                    + " hasHead=" + (reject.HasHead ? "1" : "0")
                    + " hasCurrentLane=" + (reject.HasCurrentLane ? "1" : "0")
                    + " hasNavigation=" + (reject.HasNavigation ? "1" : "0")
                    + " hasNavBuffer=" + (reject.HasNavigationBuffer ? "1" : "0"));
            }
        }

        private static string BuildRejectKey(RejectDiagnostic reject)
        {
            return reject.Reason
                + "|target=" + GetEntityIndex(reject.Target)
                + "|path=" + reject.PathState
                + "|front=" + reject.FrontFlags
                + "|navEnd=" + reject.NavigationEndIndex
                + "|wp=" + reject.WaypointIndex
                + "|routeWp=" + reject.RouteProgressWaypointIndex
                + "|head=" + GetEntityIndex(reject.HeadVehicle)
                + "|stopVeh=" + GetEntityIndex(reject.StopBoardingVehicle);
        }

        private static int GetEntityIndex(Entity entity)
        {
            return entity == Entity.Null ? -1 : entity.Index;
        }

        private bool TryRepairCandidate(Entity vehicle, Candidate candidate, uint nowFrame)
        {
            if (!EntityManager.Exists(candidate.HeadVehicle)
                || !EntityManager.HasComponent<TrainCurrentLane>(candidate.HeadVehicle)
                || !EntityManager.HasBuffer<TrainNavigationLane>(vehicle))
            {
                return false;
            }

            TrainCurrentLane currentLane = EntityManager.GetComponentData<TrainCurrentLane>(candidate.HeadVehicle);
            TrainLaneFlags beforeFlags = currentLane.m_Front.m_LaneFlags;
            TrainLaneFlags movedFlags = 0;
            DynamicBuffer<TrainNavigationLane> navigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(vehicle);
            int consumedNavigationLanes = 0;

            if ((currentLane.m_Front.m_LaneFlags & TrainLaneFlags.EndOfPath) == 0)
            {
                int endIndex = FindNavigationEndIndex(navigationLanes);
                if (endIndex < 0)
                    return false;

                movedFlags = navigationLanes[endIndex].m_Flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return);
                currentLane.m_Front.m_LaneFlags |= movedFlags;
                consumedNavigationLanes = endIndex + 1;
                navigationLanes.RemoveRange(0, consumedNavigationLanes);
            }

            if ((currentLane.m_Front.m_LaneFlags & TrainLaneFlags.EndOfPath) == 0)
                return false;

            currentLane.m_Front.m_LaneFlags |= TrainLaneFlags.EndReached;
            EntityManager.SetComponentData(candidate.HeadVehicle, currentLane);

            if (RtLog.VerboseEnabled)
            {
                Mod.log.Info("[OriginArrivingStallRepair] vehicle=" + vehicle.Index
                    + " head=" + candidate.HeadVehicle.Index
                    + " line=" + candidate.Line.Index
                    + " target=" + candidate.Target.Index
                    + " stop=" + candidate.Stop.Index
                    + " frame=" + nowFrame
                    + " speed=" + candidate.Speed
                    + " frontBefore=" + beforeFlags
                    + " frontAfter=" + currentLane.m_Front.m_LaneFlags
                    + " movedFlags=" + movedFlags
                    + " navLenBefore=" + candidate.NavigationLaneCount
                    + " navEndIndex=" + candidate.NavigationEndIndex
                    + " computeWp=" + candidate.WaypointIndex
                    + " routeWp=" + candidate.RouteProgressWaypointIndex
                    + " routeSeg=" + candidate.RouteProgressSegmentPosition.ToString("F2")
                    + " routeOrigin=" + candidate.RouteProgressAtOrigin
                    + " navConsumed=" + consumedNavigationLanes
                    + " ptState=" + candidate.PublicTransportState
                    + " pathState=" + candidate.PathState);
            }
            return true;
        }

        private Entity ResolveHeadVehicle(Entity vehicle)
        {
            if (EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                if (layout.Length != 0)
                    return layout[0].m_Vehicle;
            }

            return vehicle;
        }

        private bool IsVehicleBoarding(Entity vehicle)
        {
            if (!EntityManager.Exists(vehicle))
                return false;

            if (EntityManager.HasComponent<PublicTransport>(vehicle)
                && (EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0)
            {
                return true;
            }

            if (EntityManager.HasComponent<CargoTransport>(vehicle)
                && (EntityManager.GetComponentData<CargoTransport>(vehicle).m_State & CargoTransportFlags.Boarding) != 0)
            {
                return true;
            }

            return false;
        }

        private static int FindNavigationEndIndex(DynamicBuffer<TrainNavigationLane> navigationLanes)
        {
            for (int i = 0; i < navigationLanes.Length; i++)
            {
                if ((navigationLanes[i].m_Flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return)) != 0)
                    return i;
            }

            return -1;
        }

        private static void LogCandidate(Entity vehicle, Candidate candidate, uint nowFrame)
        {
            if (!RtLog.VerboseEnabled)
                return;

            Mod.log.Info("[OriginArrivingStallCandidate] vehicle=" + vehicle.Index
                + " head=" + candidate.HeadVehicle.Index
                + " line=" + candidate.Line.Index
                + " target=" + candidate.Target.Index
                + " stop=" + candidate.Stop.Index
                + " frame=" + nowFrame
                + " speed=" + candidate.Speed
                + " front=" + candidate.FrontFlags
                + " navLen=" + candidate.NavigationLaneCount
                + " navEndIndex=" + candidate.NavigationEndIndex
                + " computeWp=" + candidate.WaypointIndex
                + " routeWp=" + candidate.RouteProgressWaypointIndex
                + " routeSeg=" + candidate.RouteProgressSegmentPosition.ToString("F2")
                + " routeOrigin=" + candidate.RouteProgressAtOrigin
                + " ptState=" + candidate.PublicTransportState
                + " pathState=" + candidate.PathState);
        }

        private readonly struct Candidate
        {
            public readonly Entity Vehicle;
            public readonly Entity HeadVehicle;
            public readonly Entity Line;
            public readonly Entity Target;
            public readonly Entity Stop;
            public readonly float Speed;
            public readonly TrainLaneFlags FrontFlags;
            public readonly int NavigationLaneCount;
            public readonly int NavigationEndIndex;
            public readonly int WaypointIndex;
            public readonly int RouteProgressWaypointIndex;
            public readonly float RouteProgressSegmentPosition;
            public readonly bool RouteProgressAtOrigin;
            public readonly PublicTransportFlags PublicTransportState;
            public readonly PathFlags PathState;

            public Candidate(
                Entity vehicle,
                Entity headVehicle,
                Entity line,
                Entity target,
                Entity stop,
                float speed,
                TrainLaneFlags frontFlags,
                int navigationLaneCount,
                int navigationEndIndex,
                int waypointIndex,
                int routeProgressWaypointIndex,
                float routeProgressSegmentPosition,
                bool routeProgressAtOrigin,
                PublicTransportFlags publicTransportState,
                PathFlags pathState)
            {
                Vehicle = vehicle;
                HeadVehicle = headVehicle;
                Line = line;
                Target = target;
                Stop = stop;
                Speed = speed;
                FrontFlags = frontFlags;
                NavigationLaneCount = navigationLaneCount;
                NavigationEndIndex = navigationEndIndex;
                WaypointIndex = waypointIndex;
                RouteProgressWaypointIndex = routeProgressWaypointIndex;
                RouteProgressSegmentPosition = routeProgressSegmentPosition;
                RouteProgressAtOrigin = routeProgressAtOrigin;
                PublicTransportState = publicTransportState;
                PathState = pathState;
            }
        }

        private struct RejectDiagnostic
        {
            public Entity Vehicle;
            public Entity HeadVehicle;
            public Entity Line;
            public Entity Target;
            public Entity Stop;
            public Entity StopBoardingVehicle;
            public bool StopBoardingVehicleBoarding;
            public float Speed;
            public TrainLaneFlags FrontFlags;
            public int NavigationLaneCount;
            public int NavigationEndIndex;
            public int WaypointIndex;
            public int RouteProgressWaypointIndex;
            public float RouteProgressSegmentPosition;
            public bool HasRouteProgress;
            public PathFlags PathState;
            public PublicTransportFlags PublicTransportState;
            public bool HasHead;
            public bool HasCurrentLane;
            public bool HasNavigation;
            public bool HasNavigationBuffer;
            public bool KeepState;
            public string Reason;
        }
    }
}
