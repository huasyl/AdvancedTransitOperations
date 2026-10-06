using Game.Pathfind;
using Game.Routes;
using Game.Creatures;
using Game.Common;
using Game.Prefabs;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;

namespace RapidTransitMod.PassengerFlow
{
    internal enum NextLegKind
    {
        NoNextLeg,
        NextLeg,
        Unresolved,
        AlreadyAboard,
        UnsupportedLeg
    }

    internal readonly struct NextLegRead
    {
        internal readonly NextLegKind Kind;
        internal readonly int BoardStationSakIndex;

        internal NextLegRead(NextLegKind kind, int boardStationSakIndex = -1)
        {
            Kind = kind;
            BoardStationSakIndex = boardStationSakIndex;
        }
    }

    internal readonly struct StopPlanRead
    {
        internal readonly bool AlightsHere;
        internal readonly NextLegRead NextLeg;
        internal readonly Entity BoardWaypoint;
        internal readonly Entity BoardStop;
        internal readonly Entity BoardLine;

        internal StopPlanRead(bool alightsHere, NextLegRead nextLeg,
            Entity boardWaypoint, Entity boardStop, Entity boardLine)
        {
            AlightsHere = alightsHere;
            NextLeg = nextLeg;
            BoardWaypoint = boardWaypoint;
            BoardStop = boardStop;
            BoardLine = boardLine;
        }
    }

    internal readonly struct StopPlanRequest
    {
        internal readonly Entity Passenger;
        internal readonly Entity CurrentWaypoint;
        internal readonly Entity Vehicle;
        internal readonly Entity RuntimeVehicle;

        internal StopPlanRequest(Entity passenger, Entity currentWaypoint, Entity vehicle, Entity runtimeVehicle)
        {
            Passenger = passenger; CurrentWaypoint = currentWaypoint;
            Vehicle = vehicle; RuntimeVehicle = runtimeVehicle;
        }
    }

    internal readonly struct StopPlanRaw
    {
        internal readonly bool AlightsHere;
        internal readonly NextLegKind Kind;
        internal readonly Entity BoardWaypoint;
        internal readonly Entity BoardStop;
        internal readonly Entity BoardLine;
        internal readonly int WalkStartIndex;
        internal readonly int WalkEndIndex;

        internal StopPlanRaw(bool alightsHere, NextLegKind kind, Entity boardWaypoint,
            Entity boardStop, Entity boardLine,
            int walkStartIndex = -1, int walkEndIndex = -1)
        {
            AlightsHere = alightsHere; Kind = kind; BoardWaypoint = boardWaypoint;
            BoardStop = boardStop; BoardLine = boardLine;
            WalkStartIndex = walkStartIndex; WalkEndIndex = walkEndIndex;
        }
    }

    internal enum WalkFailure : byte
    {
        None, InvalidPathOwner, NoPath, InvalidRange, BehaviorPath,
        MissingEdge, NonPedestrianEdge, InvalidDelta, InvalidEdgeLength
    }

    internal struct WalkMeasure
    {
        internal double Meters;
        internal Entity PathEntity;
        internal int Index;
        internal Entity Target;
        internal float2 Delta;
        internal WalkFailure Failure;

        internal bool Fail(WalkFailure failure) { Failure = failure; Meters = 0d; return false; }
    }

    [BurstCompile]
    internal struct WalkMeasureJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Entity> Passengers;
        [ReadOnly] public NativeArray<StopPlanRaw> Plans;
        [ReadOnly] public ComponentLookup<GroupMember> Members;
        [ReadOnly] public ComponentLookup<PathOwner> Owners;
        [ReadOnly] public BufferLookup<PathElement> Paths;
        [ReadOnly] public NativePathfindData Graph;
        public NativeArray<WalkMeasure> Results;

        public void Execute(int index)
        {
            TripPath.MeasureWalk(Passengers[index], Plans[index], Members, Owners, Paths, Graph,
                out WalkMeasure result);
            Results[index] = result;
        }
    }

    internal enum RepresentativeKind : byte { Waiting, Started, Reached, Invalid }

    internal readonly struct RepresentativeRead
    {
        internal readonly RepresentativeKind Kind;
        internal readonly uint AlightFrame;

        internal RepresentativeRead(RepresentativeKind kind, uint alightFrame)
        {
            Kind = kind;
            AlightFrame = alightFrame;
        }
    }

    // 只读取下车后的首个公共交通腿；不保存路径，也不启动寻路。
    internal static class TripPath
    {
        internal static StopPlanRaw ReadStopPlanRaw(StopPlanRequest request,
            ComponentLookup<GroupMember> members,
            ComponentLookup<CurrentVehicle> currentVehicles,
            ComponentLookup<Game.Vehicles.Controller> controllers,
            ComponentLookup<Game.Creatures.Resident> residents,
            ComponentLookup<PathOwner> owners,
            BufferLookup<PathElement> paths,
            ComponentLookup<Connected> connections,
            ComponentLookup<BoardingVehicle> boarding,
            ComponentLookup<Owner> lineOwners)
        {
            Entity pathEntity = members.HasComponent(request.Passenger)
                ? members[request.Passenger].m_Leader : request.Passenger;
            Entity rider = currentVehicles.HasComponent(request.Passenger)
                ? request.Passenger : pathEntity;
            if (currentVehicles.HasComponent(rider))
            {
                Entity riding = currentVehicles[rider].m_Vehicle;
                Entity controller = controllers.HasComponent(riding)
                    ? controllers[riding].m_Controller : riding;
                if (controller != request.Vehicle && controller != request.RuntimeVehicle)
                    return new StopPlanRaw(false, NextLegKind.AlreadyAboard,
                        Entity.Null, Entity.Null, Entity.Null);
            }
            if (pathEntity == Entity.Null || !owners.HasComponent(pathEntity) || !paths.HasBuffer(pathEntity))
                return new StopPlanRaw(false, NextLegKind.Unresolved, Entity.Null, Entity.Null, Entity.Null);
            PathOwner owner = owners[pathEntity];
            if ((owner.m_State & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Failed)) != 0)
                return new StopPlanRaw(false, NextLegKind.Unresolved, Entity.Null, Entity.Null, Entity.Null);
            DynamicBuffer<PathElement> path = paths[pathEntity];
            bool disembarking = residents.HasComponent(pathEntity)
                && (residents[pathEntity].m_Flags & ResidentFlags.Disembarking) != 0;
            int alightIndex = disembarking ? owner.m_ElementIndex - 1 : owner.m_ElementIndex + 1;
            if (alightIndex < 0 || alightIndex >= path.Length
                || path[alightIndex].m_Target != request.CurrentWaypoint)
                return new StopPlanRaw(false, NextLegKind.NoNextLeg, Entity.Null, Entity.Null, Entity.Null);
            for (int index = alightIndex + 1; index < path.Length; index++)
            {
                Entity target = path[index].m_Target;
                if (target == Entity.Null)
                    continue;
                if (boarding.HasComponent(target))
                    return new StopPlanRaw(true, NextLegKind.UnsupportedLeg, Entity.Null, Entity.Null, Entity.Null);
                if (!connections.HasComponent(target))
                    continue;
                Entity stop = connections[target].m_Connected;
                if (stop == Entity.Null || !boarding.HasComponent(stop) || !lineOwners.HasComponent(target)
                    || index + 1 >= path.Length || !lineOwners.HasComponent(path[index + 1].m_Target))
                    return new StopPlanRaw(true, NextLegKind.Unresolved, Entity.Null, Entity.Null, Entity.Null);
                Entity line = lineOwners[target].m_Owner;
                if (line == Entity.Null || line != lineOwners[path[index + 1].m_Target].m_Owner)
                    return new StopPlanRaw(true, NextLegKind.Unresolved, Entity.Null, Entity.Null, Entity.Null);
                return new StopPlanRaw(true, NextLegKind.NextLeg, target, stop, line,
                    alightIndex + 1, index);
            }
            return new StopPlanRaw(true, NextLegKind.NoNextLeg, Entity.Null, Entity.Null, Entity.Null);
        }

        internal static StopPlanRead FinishStopPlan(StopPlanRaw raw, Port port, Anchors anchors,
            out TransitMode mode, out string lineId)
        {
            mode = TransitMode.Unknown;
            lineId = string.Empty;
            if (raw.Kind != NextLegKind.NextLeg)
                return new StopPlanRead(raw.AlightsHere, new NextLegRead(raw.Kind),
                    Entity.Null, Entity.Null, Entity.Null);
            if (!port.TryLineMetadata(raw.BoardLine, out mode, out lineId))
                return new StopPlanRead(true, new NextLegRead(NextLegKind.Unresolved),
                    Entity.Null, Entity.Null, Entity.Null);
            if (!SamplingSystem.SupportsMode(mode))
                return new StopPlanRead(true, new NextLegRead(NextLegKind.UnsupportedLeg),
                    Entity.Null, Entity.Null, Entity.Null);
            if (!anchors.TryForStop(port, raw.BoardStop, out StationKey station))
                return new StopPlanRead(true, new NextLegRead(NextLegKind.Unresolved),
                    Entity.Null, Entity.Null, Entity.Null);
            return new StopPlanRead(true,
                new NextLegRead(NextLegKind.NextLeg, station.Index),
                raw.BoardWaypoint, raw.BoardStop, raw.BoardLine);
        }

        internal static bool MeasureWalk(Entity passenger, StopPlanRaw plan,
            ComponentLookup<GroupMember> members, ComponentLookup<PathOwner> owners,
            BufferLookup<PathElement> paths, NativePathfindData graph,
            out WalkMeasure result)
        {
            Entity pathEntity = members.HasComponent(passenger) ? members[passenger].m_Leader : passenger;
            result = new WalkMeasure { PathEntity = pathEntity, Index = -1 };
            if (pathEntity == Entity.Null || !owners.HasComponent(pathEntity)
                || (owners[pathEntity].m_State & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Failed)) != 0)
                return result.Fail(WalkFailure.InvalidPathOwner);
            if (!paths.HasBuffer(pathEntity))
                return result.Fail(WalkFailure.NoPath);
            DynamicBuffer<PathElement> path = paths[pathEntity];
            if (plan.WalkStartIndex < 0 || plan.WalkEndIndex < plan.WalkStartIndex
                || plan.WalkEndIndex > path.Length)
                return result.Fail(WalkFailure.InvalidRange);
            UnsafePathfindData data = graph.GetReadOnlyData();
            for (int index = plan.WalkStartIndex; index < plan.WalkEndIndex; index++)
            {
                PathElement element = path[index];
                result.Index = index; result.Target = element.m_Target; result.Delta = element.m_TargetDelta;
                if ((element.m_Flags & (PathElementFlags.Action | PathElementFlags.Leader
                    | PathElementFlags.Hangaround | PathElementFlags.WaitPosition | PathElementFlags.Return)) != 0)
                    return result.Fail(WalkFailure.BehaviorPath);
                EdgeID edgeId;
                bool found = (element.m_Flags & PathElementFlags.Secondary) != 0
                    ? graph.GetSecondaryEdge(element.m_Target, out edgeId)
                    : graph.GetEdge(element.m_Target, out edgeId);
                if (!found)
                    return result.Fail(WalkFailure.MissingEdge);
                Edge edge = data.GetEdge(edgeId);
                if ((edge.m_Specification.m_Methods & PathMethod.Pedestrian) == 0)
                    return result.Fail(WalkFailure.NonPedestrianEdge);
                float2 delta = element.m_TargetDelta;
                if (!math.isfinite(delta.x) || !math.isfinite(delta.y)
                    || delta.x < 0f || delta.x > 1f || delta.y < 0f || delta.y > 1f)
                    return result.Fail(WalkFailure.InvalidDelta);
                if (!math.isfinite(edge.m_Specification.m_Length) || edge.m_Specification.m_Length < 0f)
                    return result.Fail(WalkFailure.InvalidEdgeLength);
                float length = PathUtils.CalculateLength(in edge.m_Specification, delta);
                result.Meters += length;
            }
            return true;
        }

        internal static void LogWalkFailure(Entity passenger, StopPlanRaw plan, WalkMeasure result)
        {
            if (!Diagnostics.Enabled)
                return;
            Diagnostics.Log("PassengerFlow", "walk measure failed: passenger=" + passenger.Index
                + ":" + passenger.Version + " path=" + result.PathEntity.Index + ":" + result.PathEntity.Version
                + " walkStart=" + plan.WalkStartIndex + " walkEnd=" + plan.WalkEndIndex
                + " index=" + result.Index + " target=" + result.Target.Index + ":" + result.Target.Version
                + " delta=" + result.Delta.x + ":" + result.Delta.y + " reason=" + result.Failure);
        }

        internal static RepresentativeRead ReadRepresentative(Entity passenger,
            PassengerFlowTransferSample sample, uint frame,
            ComponentLookup<CurrentVehicle> currentVehicles,
            ComponentLookup<Game.Vehicles.Controller> controllers,
            ComponentLookup<GroupMember> groupMembers,
            ComponentLookup<PathOwner> pathOwners,
            BufferLookup<PathElement> paths,
            ComponentLookup<HumanCurrentLane> lanes,
            ComponentLookup<Connected> connections)
        {
            Entity pathEntity = passenger;
            if (groupMembers.TryGetComponent(passenger, out GroupMember groupMember))
                pathEntity = groupMember.m_Leader;
            if (pathEntity == Entity.Null)
                return new RepresentativeRead(RepresentativeKind.Invalid, sample.m_AlightFrame);
            bool aboard = currentVehicles.TryGetComponent(passenger, out CurrentVehicle currentVehicle)
                || (pathEntity != passenger && currentVehicles.TryGetComponent(pathEntity, out currentVehicle));
            uint alightFrame = aboard ? sample.m_AlightFrame
                : sample.m_AlightFrame == 0 ? frame : sample.m_AlightFrame;
            if (frame > sample.m_ExpiresFrame)
                return new RepresentativeRead(RepresentativeKind.Invalid, alightFrame);
            if (aboard)
            {
                Entity vehicle = currentVehicle.m_Vehicle;
                if (controllers.TryGetComponent(vehicle, out Game.Vehicles.Controller controller))
                    vehicle = controller.m_Controller;
                if (vehicle != sample.m_OldVehicle)
                    return new RepresentativeRead(RepresentativeKind.Waiting, alightFrame);
            }
            if (!pathOwners.TryGetComponent(pathEntity, out PathOwner owner)
                || !paths.HasBuffer(pathEntity))
                return new RepresentativeRead(RepresentativeKind.Invalid, alightFrame);
            if ((owner.m_State & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Failed)) != 0)
                return new RepresentativeRead(RepresentativeKind.Invalid, alightFrame);
            DynamicBuffer<PathElement> path = paths[pathEntity];
            if (owner.m_ElementIndex < 0 || owner.m_ElementIndex >= path.Length)
                return new RepresentativeRead(RepresentativeKind.Invalid, alightFrame);
            RepresentativeKind kind = RepresentativeKind.Waiting;
            if (!aboard)
            {
                if (!lanes.TryGetComponent(pathEntity, out HumanCurrentLane lane))
                    return new RepresentativeRead(RepresentativeKind.Invalid, alightFrame);
                if (CreatureUtils.TransportStopReached(lane))
                {
                    Entity target = path[owner.m_ElementIndex].m_Target;
                    bool reached = target == sample.m_TargetWaypoint
                        && connections.TryGetComponent(target, out Connected connection)
                        && connection.m_Connected == sample.m_TargetStop;
                    return new RepresentativeRead(reached ? RepresentativeKind.Reached : RepresentativeKind.Invalid,
                        alightFrame);
                }
                kind = sample.m_AlightFrame == 0 ? RepresentativeKind.Started : RepresentativeKind.Waiting;
            }
            for (int index = owner.m_ElementIndex; index < path.Length; index++)
                if (path[index].m_Target == sample.m_TargetWaypoint)
                    return new RepresentativeRead(kind, alightFrame);
            return new RepresentativeRead(RepresentativeKind.Invalid, alightFrame);
        }

        internal static NextLegRead ReadNextLeg(EntityManager entityManager, Port port, Anchors anchors, Entity passenger)
        {
            if (entityManager.HasComponent<CurrentVehicle>(passenger))
                return new NextLegRead(NextLegKind.AlreadyAboard);

            if (!ResolvePassenger(entityManager, passenger, out Entity pathOwnerEntity))
                return new NextLegRead(NextLegKind.Unresolved);

            if (pathOwnerEntity != passenger && entityManager.HasComponent<CurrentVehicle>(pathOwnerEntity))
                return new NextLegRead(NextLegKind.AlreadyAboard);

            if (!entityManager.HasComponent<PathOwner>(pathOwnerEntity)
                || !entityManager.HasBuffer<PathElement>(pathOwnerEntity))
            {
                return new NextLegRead(NextLegKind.Unresolved);
            }

            PathOwner owner = entityManager.GetComponentData<PathOwner>(pathOwnerEntity);
            if ((owner.m_State & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Failed)) != 0)
                return new NextLegRead(NextLegKind.Unresolved);

            DynamicBuffer<PathElement> path = entityManager.GetBuffer<PathElement>(pathOwnerEntity, true);
            if (owner.m_ElementIndex < 0 || owner.m_ElementIndex > path.Length)
                return new NextLegRead(NextLegKind.Unresolved);

            for (int index = owner.m_ElementIndex; index < path.Length; index++)
            {
                Entity target = path[index].m_Target;
                if (target == Entity.Null)
                    continue;

                if (entityManager.HasComponent<BoardingVehicle>(target))
                    return new NextLegRead(NextLegKind.UnsupportedLeg);

                if (!entityManager.HasComponent<Connected>(target))
                    continue;
                Entity stop = entityManager.GetComponentData<Connected>(target).m_Connected;

                if (stop == Entity.Null || !entityManager.HasComponent<BoardingVehicle>(stop))
                    return new NextLegRead(NextLegKind.Unresolved);

                if (index + 1 >= path.Length || !entityManager.HasComponent<Connected>(path[index + 1].m_Target)
                    || !entityManager.HasComponent<Owner>(target)
                    || !entityManager.HasComponent<Owner>(path[index + 1].m_Target))
                    return new NextLegRead(NextLegKind.Unresolved);

                Entity line = entityManager.GetComponentData<Owner>(target).m_Owner;
                Entity alightLine = entityManager.GetComponentData<Owner>(path[index + 1].m_Target).m_Owner;
                if (line == Entity.Null || line != alightLine
                    || !DispatchLineEligibility.TryGetTransportLineData(entityManager, line, out TransportLineData lineData))
                    return new NextLegRead(NextLegKind.Unresolved);
                TransitMode mode = TransportModeResolver.Resolve(lineData);
                if (!SamplingSystem.SupportsMode(mode))
                    return new NextLegRead(NextLegKind.UnsupportedLeg);

                return anchors.TryForStop(port, stop, out StationKey station)
                    ? new NextLegRead(NextLegKind.NextLeg, station.Index)
                    : new NextLegRead(NextLegKind.Unresolved);
            }

            return new NextLegRead(NextLegKind.NoNextLeg);
        }

        internal static bool ResolvePassenger(EntityManager entityManager, Entity passenger, out Entity pathOwnerEntity)
        {
            pathOwnerEntity = passenger;
            if (!entityManager.HasComponent<GroupMember>(passenger))
                return true;
            Entity leader = entityManager.GetComponentData<GroupMember>(passenger).m_Leader;
            if (leader == Entity.Null || !entityManager.HasComponent<PathOwner>(leader))
                return false;
            pathOwnerEntity = leader;
            return true;
        }

    }
}
