using Game.Creatures;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace RapidTransitMod.PassengerFlow.Jobs
{
    [BurstCompile]
    internal struct RepresentativeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Entity> Passengers;
        [ReadOnly] public NativeArray<PassengerFlowTransferSample> Samples;
        [ReadOnly] public ComponentLookup<CurrentVehicle> CurrentVehicles;
        [ReadOnly] public ComponentLookup<Controller> Controllers;
        [ReadOnly] public ComponentLookup<GroupMember> GroupMembers;
        [ReadOnly] public ComponentLookup<PathOwner> PathOwners;
        [ReadOnly] public BufferLookup<PathElement> Paths;
        [ReadOnly] public ComponentLookup<HumanCurrentLane> Lanes;
        [ReadOnly] public ComponentLookup<Connected> Connections;
        public NativeArray<RepresentativeRead> Results;
        public uint Frame;

        public void Execute(int index)
        {
            Entity passenger = Passengers[index];
            Results[index] = TripPath.ReadRepresentative(passenger, Samples[index], Frame,
                CurrentVehicles, Controllers, GroupMembers, PathOwners, Paths, Lanes, Connections);
        }
    }
}
