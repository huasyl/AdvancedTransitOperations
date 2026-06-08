using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace RapidTransitMod.PassengerFlow.Jobs
{
    internal struct BoardEvent
    {
        internal int RequestIndex;
        internal Entity Passenger;
    }

    internal struct AlightEvent
    {
        internal int RequestIndex;
        internal Entity Passenger;
    }

    internal struct DepartureLoadEvent
    {
        internal int RequestIndex;
        internal int PassengerCount;
    }

    [BurstCompile]
    internal struct DiffJob : IJob
    {
        [ReadOnly] public NativeArray<VehicleSampleRequest> Requests;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> PreviousPassengers;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> CurrentPassengers;
        public NativeList<BoardEvent> BoardEvents;
        public NativeList<AlightEvent> AlightEvents;
        public NativeList<DepartureLoadEvent> DepartureLoadEvents;
        public NativeParallelMultiHashMap<int, Entity> NextBaseline;

        public void Execute()
        {
            for (int i = 0; i < Requests.Length; i++)
            {
                int currentCount = 0;
                NativeParallelMultiHashMapIterator<int> iterator;
                Entity passenger;
                if (CurrentPassengers.TryGetFirstValue(i, out passenger, out iterator))
                {
                    do
                    {
                        currentCount++;
                        NextBaseline.Add(i, passenger);
                        if (!Contains(PreviousPassengers, i, passenger))
                        {
                            BoardEvents.Add(new BoardEvent
                            {
                                RequestIndex = i,
                                Passenger = passenger
                            });
                        }
                    }
                    while (CurrentPassengers.TryGetNextValue(out passenger, ref iterator));
                }

                if (PreviousPassengers.TryGetFirstValue(i, out passenger, out iterator))
                {
                    do
                    {
                        if (!Contains(CurrentPassengers, i, passenger))
                        {
                            AlightEvents.Add(new AlightEvent
                            {
                                RequestIndex = i,
                                Passenger = passenger
                            });
                        }
                    }
                    while (PreviousPassengers.TryGetNextValue(out passenger, ref iterator));
                }

                DepartureLoadEvents.Add(new DepartureLoadEvent
                {
                    RequestIndex = i,
                    PassengerCount = currentCount
                });
            }
        }

        private static bool Contains(NativeParallelMultiHashMap<int, Entity> map, int key, Entity value)
        {
            NativeParallelMultiHashMapIterator<int> iterator;
            Entity candidate;
            if (!map.TryGetFirstValue(key, out candidate, out iterator))
                return false;

            do
            {
                if (candidate == value)
                    return true;
            }
            while (map.TryGetNextValue(out candidate, ref iterator));

            return false;
        }
    }
}
