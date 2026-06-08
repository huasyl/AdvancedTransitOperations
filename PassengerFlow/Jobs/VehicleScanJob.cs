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
        internal int StatusCode;
    }

    [BurstCompile]
    internal struct VehicleScanJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<VehicleSampleRequest> Requests;
        [ReadOnly] public BufferLookup<Passenger> PassengerBuffers;
        [ReadOnly] public BufferLookup<LayoutElement> LayoutBuffers;
        public NativeParallelMultiHashMap<int, Entity>.ParallelWriter CurrentPassengers;
        public NativeArray<VehicleSampleResult> Results;

        public void Execute(int index)
        {
            VehicleSampleRequest request = Requests[index];
            Entity vehicle = request.RuntimeVehicle != Entity.Null ? request.RuntimeVehicle : request.Vehicle;
            int count = 0;
            VehicleScanStatus status = VehicleScanStatus.Ok;

            if (vehicle == Entity.Null)
            {
                status = VehicleScanStatus.PassengerBufferMissing;
            }
            else if (LayoutBuffers.HasBuffer(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = LayoutBuffers[vehicle];
                if (layout.Length == 0)
                {
                    status = VehicleScanStatus.LayoutMissing;
                }
                else
                {
                    bool anyBuffer = false;
                    for (int i = 0; i < layout.Length; i++)
                    {
                        Entity layoutVehicle = layout[i].m_Vehicle;
                        if (layoutVehicle == Entity.Null || !PassengerBuffers.HasBuffer(layoutVehicle))
                            continue;

                        anyBuffer = true;
                        DynamicBuffer<Passenger> passengers = PassengerBuffers[layoutVehicle];
                        for (int p = 0; p < passengers.Length; p++)
                        {
                            Entity passenger = passengers[p].m_Passenger;
                            if (passenger == Entity.Null)
                                continue;

                            CurrentPassengers.Add(index, passenger);
                            count++;
                        }
                    }

                    if (!anyBuffer)
                        status = VehicleScanStatus.PassengerBufferMissing;
                }
            }
            else if (PassengerBuffers.HasBuffer(vehicle))
            {
                DynamicBuffer<Passenger> passengers = PassengerBuffers[vehicle];
                for (int p = 0; p < passengers.Length; p++)
                {
                    Entity passenger = passengers[p].m_Passenger;
                    if (passenger == Entity.Null)
                        continue;

                    CurrentPassengers.Add(index, passenger);
                    count++;
                }
            }
            else
            {
                status = VehicleScanStatus.PassengerBufferMissing;
            }

            Results[index] = new VehicleSampleResult
            {
                RequestIndex = index,
                PassengerCount = count,
                StatusCode = (int)status
            };
        }
    }
}
