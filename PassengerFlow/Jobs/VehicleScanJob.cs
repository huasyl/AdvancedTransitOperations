using Game.Creatures;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace RapidTransitMod.PassengerFlow.Jobs
{
    internal enum VehicleScanStatus : int
    {
        Ok = 0,
        PassengerBufferMissing = 1,
        LayoutMissing = 2
    }

    internal struct VehicleSampleResult
    {
        internal int RequestIndex;
        internal int PassengerCount;
        internal int PassengerCapacity;
        internal byte HasCapacity;
        internal int StatusCode;
        internal int FailureBranch;
        internal int LayoutLength;
        internal int ReadableMembers;
        internal Entity MissingMember;
        internal int MissingPosition;
        internal int MissingPassengerKind;
    }

    [BurstCompile]
    internal struct VehicleScanJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<VehicleSampleRequest> Requests;
        [ReadOnly] public BufferLookup<Passenger> PassengerBuffers;
        [ReadOnly] public BufferLookup<LayoutElement> LayoutBuffers;
        [ReadOnly] public ComponentLookup<PrefabRef> Prefabs;
        [ReadOnly] public ComponentLookup<PublicTransportVehicleData> PublicTransportVehicles;
        [ReadOnly] public ComponentLookup<Game.Creatures.Pet> Pets;
        public NativeParallelMultiHashMap<int, Entity>.ParallelWriter CurrentPassengers;
        public NativeArray<VehicleSampleResult> Results;
        public bool CountOnly;

        public void Execute(int index)
        {
            VehicleSampleRequest request = Requests[index];
            Entity vehicle = request.RuntimeVehicle != Entity.Null ? request.RuntimeVehicle : request.Vehicle;
            int count = 0;
            int capacity = 0;
            bool hasCapacity = true;
            VehicleScanStatus status = VehicleScanStatus.Ok;
            int branch = 0, length = -1, readable = 0, missingPosition = -1, missingKind = -1;
            Entity missingMember = Entity.Null;

            if (vehicle == Entity.Null)
            {
                status = VehicleScanStatus.PassengerBufferMissing;
                branch = 1;
            }
            else if (LayoutBuffers.HasBuffer(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = LayoutBuffers[vehicle];
                length = layout.Length;
                if (layout.Length == 0)
                {
                    status = VehicleScanStatus.LayoutMissing;
                    branch = 2;
                }
                else
                {
                    bool missingBuffer = false;
                    for (int i = 0; i < layout.Length; i++)
                    {
                        Entity layoutVehicle = layout[i].m_Vehicle;
                        if (layoutVehicle == Entity.Null)
                        {
                            hasCapacity = false;
                            missingBuffer = true;
                            if (branch == 0) { branch = 3; missingPosition = i; }
                            continue;
                        }

                        int kind;
                        if (TryCapacity(layoutVehicle, out int layoutCapacity, out kind))
                            capacity += layoutCapacity;
                        else
                            hasCapacity = false;

                        if (!PassengerBuffers.HasBuffer(layoutVehicle))
                        {
                            if (kind == 0)
                                continue;
                            missingBuffer = true;
                            if (branch == 0)
                            {
                                branch = 4; missingMember = layoutVehicle;
                                missingPosition = i; missingKind = kind;
                            }
                            continue;
                        }

                        readable++;
                        DynamicBuffer<Passenger> passengers = PassengerBuffers[layoutVehicle];
                        for (int p = 0; p < passengers.Length; p++)
                        {
                            Entity passenger = passengers[p].m_Passenger;
                            if (passenger == Entity.Null || Pets.HasComponent(passenger))
                                continue;

                            if (!CountOnly) CurrentPassengers.Add(index, passenger);
                            count++;
                        }
                    }

                    if (missingBuffer)
                        status = VehicleScanStatus.PassengerBufferMissing;
                }
            }
            else if (PassengerBuffers.HasBuffer(vehicle))
            {
                if (!TryCapacity(vehicle, out capacity, out _))
                    hasCapacity = false;
                readable = 1;
                DynamicBuffer<Passenger> passengers = PassengerBuffers[vehicle];
                for (int p = 0; p < passengers.Length; p++)
                {
                    Entity passenger = passengers[p].m_Passenger;
                    if (passenger == Entity.Null || Pets.HasComponent(passenger))
                        continue;

                    if (!CountOnly) CurrentPassengers.Add(index, passenger);
                    count++;
                }
            }
            else
            {
                hasCapacity = TryCapacity(vehicle, out capacity, out int kind);
                if (kind != 0)
                {
                    status = VehicleScanStatus.PassengerBufferMissing;
                    branch = 5; missingMember = vehicle; missingPosition = 0; missingKind = kind;
                }
            }

            Results[index] = new VehicleSampleResult
            {
                RequestIndex = index,
                PassengerCount = count,
                PassengerCapacity = capacity,
                HasCapacity = hasCapacity ? (byte)1 : (byte)0,
                StatusCode = (int)status,
                FailureBranch = branch, LayoutLength = length, ReadableMembers = readable,
                MissingMember = missingMember, MissingPosition = missingPosition,
                MissingPassengerKind = missingKind
            };
        }

        private bool TryCapacity(Entity vehicle, out int capacity, out int passengerKind)
        {
            capacity = 0;
            passengerKind = -1;
            if (!Prefabs.TryGetComponent(vehicle, out PrefabRef reference))
                return false;

            Entity prefab = reference.m_Prefab;
            if (prefab == Entity.Null)
                return false;

            if (!PublicTransportVehicles.TryGetComponent(prefab,
                out PublicTransportVehicleData data, out bool prefabExists))
            {
                if (!prefabExists)
                    return false;
                passengerKind = 0;
                return true;
            }

            passengerKind = 1;
            capacity = data.m_PassengerCapacity;
            return true;
        }
    }
}
