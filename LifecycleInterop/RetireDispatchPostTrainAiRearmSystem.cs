using Game;
using Game.Common;
using Game.Simulation;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public sealed partial class RetireDispatchPostTrainAiRearmSystem : GameSystemBase
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
                    ComponentType.ReadWrite<ServiceDispatch>()
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
            Dependency = new RearmJob
            {
                PublicTransportType = GetComponentTypeHandle<PublicTransport>(false),
                ServiceDispatchType = GetBufferTypeHandle<ServiceDispatch>(false)
            }.ScheduleParallel(m_ActiveQuery, Dependency);
        }

        [BurstCompile]
        private struct RearmJob : IJobChunk
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
                    if (publicTransport.m_RequestCount == 1)
                        continue;

                    publicTransport.m_RequestCount = 1;
                    publicTransports[i] = publicTransport;
                }
            }
        }
    }
}
