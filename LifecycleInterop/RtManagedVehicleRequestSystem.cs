using Game;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public sealed partial class RtManagedVehicleRequestSystem : GameSystemBase
    {
        private EntityQuery m_LineQuery;
        private EntityQuery m_SpawnPermitQuery;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return 16;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_LineQuery = GetEntityQuery(
                ComponentType.ReadWrite<TransportLine>(),
                ComponentType.Exclude<Deleted>());
            m_SpawnPermitQuery = GetEntityQuery(
                ComponentType.ReadOnly<RtSpawnPermitRequest>(),
                ComponentType.ReadOnly<TransportVehicleRequest>(),
                ComponentType.Exclude<Deleted>());
        }

        protected override void OnUpdate()
        {
            LifecyclePort lifecycle = LifecyclePort.Current;
            ManagedRequestPort managedRequests = lifecycle != null ? lifecycle.ManagedRequests : null;
            if (managedRequests == null || m_LineQuery.IsEmptyIgnoreFilter)
                return;

            using (NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp))
            using (NativeHashSet<Entity> spawnPermitLines = BuildSpawnPermitLineSet())
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    if (line == Entity.Null || !EntityManager.Exists(line))
                        continue;

                    TransportLine transportLine = EntityManager.GetComponentData<TransportLine>(line);
                    bool managed = managedRequests.IsManagedLine(line);
                    if (!managed)
                    {
                        RemoveRtRequestFromUnmanagedLine(line, ref transportLine);
                        continue;
                    }

                    Entity request = transportLine.m_VehicleRequest;
                    if (IsLiveRequest(request))
                    {
                        if (EntityManager.HasComponent<RtVehicleRequestSentinel>(request))
                        {
                            if (!IsParkedSentinelNormalized(request, line))
                                NormalizeParkedSentinel(request, line);
                            if (ShouldPromoteSentinel(managedRequests, line, spawnPermitLines))
                                PromoteSentinelToSpawnPermit(request, line);
                            continue;
                        }

                        if (EntityManager.HasComponent<RtSpawnPermitRequest>(request))
                        {
                            ReconcileSpawnPermit(managedRequests, request, line, ref transportLine);
                            continue;
                        }

                        if (ShouldReplaceUnauthorizedPendingRequest(request, line))
                        {
                            EntityManager.DestroyEntity(request);
                            transportLine.m_VehicleRequest = Entity.Null;
                            EntityManager.SetComponentData(line, transportLine);
                            InstallParkedSentinel(line);
                        }

                        continue;
                    }

                    Entity sentinel = InstallParkedSentinel(line);
                    if (ShouldPromoteSentinel(managedRequests, line, spawnPermitLines))
                        PromoteSentinelToSpawnPermit(sentinel, line);
                }
            }
        }

        private NativeHashSet<Entity> BuildSpawnPermitLineSet()
        {
            NativeArray<Entity> permits = m_SpawnPermitQuery.ToEntityArray(Allocator.Temp);
            NativeHashSet<Entity> lines = new NativeHashSet<Entity>(permits.Length, Allocator.Temp);
            try
            {
                for (int i = 0; i < permits.Length; i++)
                {
                    Entity permit = permits[i];
                    if (!EntityManager.Exists(permit)
                        || !EntityManager.HasComponent<TransportVehicleRequest>(permit))
                    {
                        continue;
                    }

                    Entity line = EntityManager.GetComponentData<TransportVehicleRequest>(permit).m_Route;
                    if (line != Entity.Null)
                        lines.Add(line);
                }
            }
            finally
            {
                if (permits.IsCreated) permits.Dispose();
            }

            return lines;
        }

        private bool IsLiveRequest(Entity request)
        {
            return request != Entity.Null
                && EntityManager.Exists(request)
                && !EntityManager.HasComponent<Deleted>(request);
        }

        private void RemoveRtRequestFromUnmanagedLine(Entity line, ref TransportLine transportLine)
        {
            Entity request = transportLine.m_VehicleRequest;
            if (!IsLiveRequest(request))
                return;

            if (EntityManager.HasComponent<RtVehicleRequestSentinel>(request))
            {
                EntityManager.DestroyEntity(request);
                transportLine.m_VehicleRequest = Entity.Null;
                EntityManager.SetComponentData(line, transportLine);
                return;
            }

            if (!EntityManager.HasComponent<RtSpawnPermitRequest>(request))
                return;

            if (!EntityManager.HasComponent<Dispatched>(request)
                && !EntityManager.HasComponent<PathInformation>(request))
            {
                EntityManager.DestroyEntity(request);
                transportLine.m_VehicleRequest = Entity.Null;
                EntityManager.SetComponentData(line, transportLine);
                return;
            }

            EntityManager.RemoveComponent<RtSpawnPermitRequest>(request);
        }

        private Entity InstallParkedSentinel(Entity line)
        {
            Entity request = EntityManager.CreateEntity();
            EntityManager.AddComponentData(request, default(ServiceRequest));
            EntityManager.AddComponentData(request, new TransportVehicleRequest(line, 0f));
            EntityManager.AddComponent<RtVehicleRequestSentinel>(request);

            TransportLine transportLine = EntityManager.GetComponentData<TransportLine>(line);
            transportLine.m_VehicleRequest = request;
            EntityManager.SetComponentData(line, transportLine);
            return request;
        }

        private void NormalizeParkedSentinel(Entity request, Entity line)
        {
            if (!EntityManager.HasComponent<ServiceRequest>(request))
                EntityManager.AddComponentData(request, default(ServiceRequest));
            else
                EntityManager.SetComponentData(request, default(ServiceRequest));

            if (!EntityManager.HasComponent<TransportVehicleRequest>(request))
                EntityManager.AddComponentData(request, new TransportVehicleRequest(line, 0f));
            else
                EntityManager.SetComponentData(request, new TransportVehicleRequest(line, 0f));

            if (EntityManager.HasComponent<RtSpawnPermitRequest>(request))
                EntityManager.RemoveComponent<RtSpawnPermitRequest>(request);
            if (EntityManager.HasComponent<RequestGroup>(request))
                EntityManager.RemoveComponent<RequestGroup>(request);
            if (EntityManager.HasComponent<UpdateFrame>(request))
                EntityManager.RemoveComponent<UpdateFrame>(request);
            if (EntityManager.HasComponent<PathInformation>(request))
                EntityManager.RemoveComponent<PathInformation>(request);
            if (EntityManager.HasBuffer<PathElement>(request))
                EntityManager.RemoveComponent<PathElement>(request);
            if (EntityManager.HasComponent<Dispatched>(request))
                EntityManager.RemoveComponent<Dispatched>(request);
            if (EntityManager.HasComponent<HandleRequest>(request))
                EntityManager.RemoveComponent<HandleRequest>(request);
        }

        private bool IsParkedSentinelNormalized(Entity request, Entity line)
        {
            if (!EntityManager.HasComponent<ServiceRequest>(request)
                || !EntityManager.HasComponent<TransportVehicleRequest>(request))
            {
                return false;
            }

            TransportVehicleRequest vehicleRequest = EntityManager.GetComponentData<TransportVehicleRequest>(request);
            if (vehicleRequest.m_Route != line)
                return false;

            return !EntityManager.HasComponent<RtSpawnPermitRequest>(request)
                && !EntityManager.HasComponent<RequestGroup>(request)
                && !EntityManager.HasComponent<UpdateFrame>(request)
                && !EntityManager.HasComponent<PathInformation>(request)
                && !EntityManager.HasBuffer<PathElement>(request)
                && !EntityManager.HasComponent<Dispatched>(request)
                && !EntityManager.HasComponent<HandleRequest>(request);
        }

        private bool ShouldPromoteSentinel(
            ManagedRequestPort managedRequests,
            Entity line,
            NativeHashSet<Entity> spawnPermitLines)
        {
            if (spawnPermitLines.Contains(line))
                return false;

            if (!managedRequests.TryGetSpawnTarget(line, out int targetCount))
                return false;

            int actualCount = managedRequests.CountActiveVehicles(line);
            return targetCount > actualCount;
        }

        private void PromoteSentinelToSpawnPermit(Entity request, Entity line)
        {
            if (EntityManager.HasComponent<RtVehicleRequestSentinel>(request))
                EntityManager.RemoveComponent<RtVehicleRequestSentinel>(request);
            if (!EntityManager.HasComponent<RtSpawnPermitRequest>(request))
                EntityManager.AddComponent<RtSpawnPermitRequest>(request);

            EntityManager.SetComponentData(request, default(ServiceRequest));
            EntityManager.SetComponentData(request, new TransportVehicleRequest(line, 1f));
            TransportLine transportLine = EntityManager.GetComponentData<TransportLine>(line);
            transportLine.m_Flags |= TransportLineFlags.RequireVehicles;
            transportLine.m_VehicleRequest = request;
            EntityManager.SetComponentData(line, transportLine);

            if (!EntityManager.HasComponent<RequestGroup>(request))
                EntityManager.AddComponentData(request, new RequestGroup(8u));
        }

        private void ReconcileSpawnPermit(
            ManagedRequestPort managedRequests,
            Entity request,
            Entity line,
            ref TransportLine transportLine)
        {
            bool committed = EntityManager.HasComponent<PathInformation>(request)
                || EntityManager.HasComponent<Dispatched>(request);
            bool stillRequired = managedRequests.TryGetSpawnTarget(line, out int targetCount)
                && targetCount > managedRequests.CountActiveVehicles(line);
            if (committed || stillRequired)
            {
                if ((transportLine.m_Flags & TransportLineFlags.RequireVehicles) == 0)
                {
                    transportLine.m_Flags |= TransportLineFlags.RequireVehicles;
                    EntityManager.SetComponentData(line, transportLine);
                }
                return;
            }

            if (!EntityManager.HasComponent<RtVehicleRequestSentinel>(request))
                EntityManager.AddComponent<RtVehicleRequestSentinel>(request);
            NormalizeParkedSentinel(request, line);
            transportLine.m_Flags &= ~TransportLineFlags.RequireVehicles;
            transportLine.m_VehicleRequest = request;
            EntityManager.SetComponentData(line, transportLine);
        }

        private bool ShouldReplaceUnauthorizedPendingRequest(Entity request, Entity line)
        {
            if (!EntityManager.HasComponent<ServiceRequest>(request)
                || !EntityManager.HasComponent<TransportVehicleRequest>(request))
            {
                return false;
            }

            TransportVehicleRequest vehicleRequest = EntityManager.GetComponentData<TransportVehicleRequest>(request);
            if (vehicleRequest.m_Route != line)
                return false;

            return !EntityManager.HasComponent<Dispatched>(request)
                && !EntityManager.HasComponent<PathInformation>(request);
        }
    }
}
