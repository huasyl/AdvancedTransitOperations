using System;
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

    internal readonly struct PassengerMemberKey : IEquatable<PassengerMemberKey>
    {
        private readonly int m_RequestIndex;
        private readonly Entity m_Passenger;

        internal PassengerMemberKey(int requestIndex, Entity passenger)
        {
            m_RequestIndex = requestIndex;
            m_Passenger = passenger;
        }

        public bool Equals(PassengerMemberKey other)
            => m_RequestIndex == other.m_RequestIndex && m_Passenger == other.m_Passenger;

        public override bool Equals(object obj)
            => obj is PassengerMemberKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((m_RequestIndex * 397) ^ m_Passenger.Index) * 397 ^ m_Passenger.Version;
            }
        }
    }

    [BurstCompile]
    internal struct DiffJob : IJob
    {
        [ReadOnly] public NativeArray<VehicleSampleRequest> Requests;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> PreviousPassengers;
        [ReadOnly] public NativeParallelMultiHashMap<int, Entity> CurrentPassengers;
        [ReadOnly] public NativeParallelHashSet<PassengerMemberKey> PreviousMembers;
        public NativeParallelHashSet<PassengerMemberKey> CurrentMembers;
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
                        CurrentMembers.Add(new PassengerMemberKey(i, passenger));
                        currentCount++;
                        NextBaseline.Add(i, passenger);
                        if (!PreviousMembers.Contains(new PassengerMemberKey(i, passenger)))
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
                        if (!CurrentMembers.Contains(new PassengerMemberKey(i, passenger)))
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

    }
}
