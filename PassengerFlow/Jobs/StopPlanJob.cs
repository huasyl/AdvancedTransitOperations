using Game.Common;
using Game.Creatures;
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
    internal struct StopPlanJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<StopPlanRequest> Requests;
        [ReadOnly] public ComponentLookup<GroupMember> Members;
        [ReadOnly] public ComponentLookup<CurrentVehicle> CurrentVehicles;
        [ReadOnly] public ComponentLookup<Controller> Controllers;
        [ReadOnly] public ComponentLookup<Game.Creatures.Resident> Residents;
        [ReadOnly] public ComponentLookup<PathOwner> Owners;
        [ReadOnly] public BufferLookup<PathElement> Paths;
        [ReadOnly] public ComponentLookup<Connected> Connections;
        [ReadOnly] public ComponentLookup<BoardingVehicle> Boarding;
        [ReadOnly] public ComponentLookup<Owner> LineOwners;
        public NativeArray<StopPlanRaw> Results;

        public void Execute(int index)
        {
            Results[index] = TripPath.ReadStopPlanRaw(Requests[index], Members,
                CurrentVehicles, Controllers, Residents,
                Owners, Paths, Connections, Boarding, LineOwners);
        }
    }
}
