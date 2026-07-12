using Game;
using Game.Common;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public sealed partial class RetireDispatchPreTrainAiQuarantineSystem : GameSystemBase
    {
        private EntityQuery m_ActiveQuery;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 16;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ActiveQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<RtRetireDispatchLock>(),
                    ComponentType.ReadOnly<Train>(),
                    ComponentType.ReadWrite<PublicTransport>(),
                    ComponentType.ReadWrite<ServiceDispatch>(),
                    ComponentType.ReadOnly<CurrentRoute>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<ParkedTrain>(),
                    ComponentType.ReadOnly<Deleted>()
                }
            });
            RequireForUpdate(m_ActiveQuery);
        }

        protected override void OnUpdate()
        {
            Dependency = new QuarantineJob
            {
                PublicTransportType = GetComponentTypeHandle<PublicTransport>(false),
                ServiceDispatchType = GetBufferTypeHandle<ServiceDispatch>(false)
            }.ScheduleParallel(m_ActiveQuery, Dependency);
        }

        [BurstCompile]
        private struct QuarantineJob : IJobChunk
        {
            public ComponentTypeHandle<PublicTransport> PublicTransportType;
            public BufferTypeHandle<ServiceDispatch> ServiceDispatchType;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                NativeArray<PublicTransport> publicTransports = chunk.GetNativeArray(ref PublicTransportType);
                BufferAccessor<ServiceDispatch> serviceDispatches = chunk.GetBufferAccessor(ref ServiceDispatchType);
                for (int i = 0; i < chunk.Count; i++)
                {
                    DynamicBuffer<ServiceDispatch> serviceDispatch = serviceDispatches[i];
                    if (serviceDispatch.Length != 0)
                        serviceDispatch.Clear();

                    PublicTransport publicTransport = publicTransports[i];
                    PublicTransportFlags oldState = publicTransport.m_State;
                    int oldRequestCount = publicTransport.m_RequestCount;
                    publicTransport.m_RequestCount = 1;
                    if ((oldState & PublicTransportFlags.Returning) == 0)
                        publicTransport.m_State |= PublicTransportFlags.AbandonRoute;
                    if (oldRequestCount != 1 || publicTransport.m_State != oldState)
                        publicTransports[i] = publicTransport;
                }
            }
        }
    }
}
