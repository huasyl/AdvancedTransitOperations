using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Serialization;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public sealed partial class RtManagedVehicleRequestSystem : GameSystemBase, IPreSerialize
    {
        private EntityQuery m_LineQuery;
        private EntityQuery m_SentinelQuery;
        private EntityQuery m_SpawnPermitQuery;
        private readonly List<SaveRestoreRecord> m_SaveRestoreRecords = new List<SaveRestoreRecord>();

        private enum SaveRestoreKind
        {
            Sentinel,
            SpawnPermit
        }

        private struct SaveRestoreRecord
        {
            public Entity Line;
            public SaveRestoreKind Kind;
            public Entity OriginalRequest;
            public bool LineReferenceCleared;
            public bool RequestDestroyed;
            public Entity RestoredRequest;
        }

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
            m_SentinelQuery = GetEntityQuery(
                ComponentType.ReadOnly<RtVehicleRequestSentinel>(),
                ComponentType.ReadOnly<TransportVehicleRequest>(),
                ComponentType.Exclude<Deleted>());
            m_SpawnPermitQuery = GetEntityQuery(
                ComponentType.ReadOnly<RtSpawnPermitRequest>(),
                ComponentType.ReadOnly<TransportVehicleRequest>(),
                ComponentType.Exclude<Deleted>());
        }

        public void PreSerialize(Context context)
        {
            PrepareForSave();
        }

        internal void RestoreAfterSave()
        {
            int recordCount = m_SaveRestoreRecords.Count;
            if (recordCount == 0)
            {
                Mod.log.Info("[RtRequestSave] 写入后恢复完成 记录=0 已恢复=0 已重新许可=0 跳过=0 失败=0");
                return;
            }

            int restoredCount = 0;
            int promotedCount = 0;
            int skippedCount = 0;
            int failedCount = 0;

            try
            {
                LifecyclePort lifecycle = LifecyclePort.Current;
                ManagedRequestPort managedRequests = lifecycle != null ? lifecycle.ManagedRequests : null;
                if (managedRequests == null)
                    Mod.log.Info("[RtRequestSave] 生命周期端口不可用，按保存事务所有权只恢复哨兵，不重新提升许可");

                for (int i = 0; i < m_SaveRestoreRecords.Count; i++)
                {
                    SaveRestoreRecord record = m_SaveRestoreRecords[i];
                    try
                    {
                        record.RestoredRequest = RestoreRequestRecord(managedRequests, record);
                        if (record.RestoredRequest != Entity.Null)
                            restoredCount++;
                        else
                            skippedCount++;
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Mod.log.Info("[RtRequestSave] 哨兵恢复失败 line=" + record.Line.Index
                            + " kind=" + record.Kind + " -> " + ex.GetType().Name + ": " + ex.Message);
                    }

                    m_SaveRestoreRecords[i] = record;
                }

                if (managedRequests == null)
                    return;

                NativeHashSet<Entity> spawnPermitLines = default;
                try
                {
                    spawnPermitLines = BuildSpawnPermitLineSet();
                    for (int i = 0; i < m_SaveRestoreRecords.Count; i++)
                    {
                        SaveRestoreRecord record = m_SaveRestoreRecords[i];
                        if (record.Kind != SaveRestoreKind.SpawnPermit
                            || record.RestoredRequest == Entity.Null
                            || !IsLiveRequest(record.RestoredRequest)
                            || !EntityManager.HasComponent<RtVehicleRequestSentinel>(record.RestoredRequest))
                        {
                            continue;
                        }

                        try
                        {
                            TransportLine transportLine = EntityManager.GetComponentData<TransportLine>(record.Line);
                            if (ShouldPromoteSentinel(managedRequests, record.Line, transportLine, spawnPermitLines))
                            {
                                PromoteSentinelToSpawnPermit(record.RestoredRequest, record.Line);
                                promotedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            Mod.log.Info("[RtRequestSave] 许可恢复失败 line=" + record.Line.Index
                                + " -> " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    Mod.log.Info("[RtRequestSave] 写入后重建许可集合失败 -> "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    if (spawnPermitLines.IsCreated)
                        spawnPermitLines.Dispose();
                }
            }
            finally
            {
                Mod.log.Info("[RtRequestSave] 写入后恢复完成 记录=" + recordCount
                    + " 已恢复=" + restoredCount
                    + " 已重新许可=" + promotedCount
                    + " 跳过=" + skippedCount
                    + " 失败=" + failedCount);
                m_SaveRestoreRecords.Clear();
            }
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
                    ManagedRequestLineState lineState = managedRequests.GetLineState(line);
                    if (lineState == ManagedRequestLineState.Paused)
                    {
                        ClosePausedLineRequest(line, ref transportLine);
                        continue;
                    }
                    if (lineState == ManagedRequestLineState.WaitingStable)
                    {
                        ParkUnstableLineRequest(line, ref transportLine);
                        continue;
                    }
                    if (lineState == ManagedRequestLineState.Unmanaged)
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
                            if (ShouldPromoteSentinel(managedRequests, line, transportLine, spawnPermitLines))
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
                    if (ShouldPromoteSentinel(managedRequests, line, transportLine, spawnPermitLines))
                        PromoteSentinelToSpawnPermit(sentinel, line);
                }
            }
        }

        private void PrepareForSave()
        {
            m_SaveRestoreRecords.Clear();
            try
            {
                m_LineQuery.CompleteDependency();
                m_SentinelQuery.CompleteDependency();
                m_SpawnPermitQuery.CompleteDependency();

                using (NativeArray<Entity> sentinels = m_SentinelQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < sentinels.Length; i++)
                        TryCleanRequestBeforeSave(sentinels[i], SaveRestoreKind.Sentinel);
                }

                using (NativeArray<Entity> permits = m_SpawnPermitQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < permits.Length; i++)
                        TryCleanRequestBeforeSave(permits[i], SaveRestoreKind.SpawnPermit);
                }
            }
            finally
            {
                int sentinelCount = 0;
                int permitCount = 0;
                int destroyedCount = 0;
                for (int i = 0; i < m_SaveRestoreRecords.Count; i++)
                {
                    SaveRestoreRecord record = m_SaveRestoreRecords[i];
                    if (record.Kind == SaveRestoreKind.Sentinel)
                        sentinelCount++;
                    else
                        permitCount++;
                    if (record.RequestDestroyed)
                        destroyedCount++;
                }

                Mod.log.Info("[RtRequestSave] 保存前清理完成 记录=" + m_SaveRestoreRecords.Count
                    + " 哨兵=" + sentinelCount
                    + " 早期许可=" + permitCount
                    + " 已销毁=" + destroyedCount
                    + " 未完成=" + (m_SaveRestoreRecords.Count - destroyedCount));
            }
        }

        private void TryCleanRequestBeforeSave(Entity request, SaveRestoreKind kind)
        {
            try
            {
                if (request == Entity.Null
                    || !EntityManager.Exists(request)
                    || EntityManager.HasComponent<Deleted>(request))
                {
                    LogSaveCandidateRejected(request, Entity.Null, kind, "请求实体不存在或已删除");
                    return;
                }

                if (!EntityManager.HasComponent<TransportVehicleRequest>(request))
                {
                    LogSaveCandidateRejected(request, Entity.Null, kind, "缺少 TransportVehicleRequest");
                    return;
                }

                if (EntityManager.HasComponent<PathInformation>(request)
                    || EntityManager.HasComponent<Dispatched>(request))
                {
                    return;
                }

                if (kind == SaveRestoreKind.Sentinel
                    && !EntityManager.HasComponent<RtVehicleRequestSentinel>(request))
                {
                    return;
                }

                if (kind == SaveRestoreKind.SpawnPermit
                    && !EntityManager.HasComponent<RtSpawnPermitRequest>(request))
                {
                    return;
                }

                Entity line = EntityManager.GetComponentData<TransportVehicleRequest>(request).m_Route;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || EntityManager.HasComponent<Deleted>(line)
                    || !EntityManager.HasComponent<TransportLine>(line))
                {
                    LogSaveCandidateRejected(request, line, kind, "线路实体不存在、已删除或缺少 TransportLine");
                    return;
                }

                TransportLine transportLine = EntityManager.GetComponentData<TransportLine>(line);
                if (transportLine.m_VehicleRequest != request)
                {
                    LogSaveCandidateRejected(request, line, kind, "线路请求引用不精确指向候选实体");
                    return;
                }

                int recordIndex = m_SaveRestoreRecords.Count;
                m_SaveRestoreRecords.Add(new SaveRestoreRecord
                {
                    Line = line,
                    Kind = kind,
                    OriginalRequest = request,
                    LineReferenceCleared = false,
                    RequestDestroyed = false,
                    RestoredRequest = Entity.Null
                });

                transportLine.m_VehicleRequest = Entity.Null;
                EntityManager.SetComponentData(line, transportLine);
                SaveRestoreRecord clearedRecord = m_SaveRestoreRecords[recordIndex];
                clearedRecord.LineReferenceCleared = true;
                m_SaveRestoreRecords[recordIndex] = clearedRecord;
                EntityManager.DestroyEntity(request);
                SaveRestoreRecord destroyedRecord = m_SaveRestoreRecords[recordIndex];
                destroyedRecord.RequestDestroyed = true;
                m_SaveRestoreRecords[recordIndex] = destroyedRecord;
            }
            catch (Exception ex)
            {
                string state = string.Empty;
                int lastRecordIndex = m_SaveRestoreRecords.Count - 1;
                if (lastRecordIndex >= 0
                    && m_SaveRestoreRecords[lastRecordIndex].OriginalRequest == request)
                {
                    SaveRestoreRecord record = m_SaveRestoreRecords[lastRecordIndex];
                    state = " lineReferenceCleared=" + record.LineReferenceCleared
                        + " requestDestroyed=" + record.RequestDestroyed;
                }

                Mod.log.Info("[RtRequestSave] 保存前清理失败 request=" + request.Index
                    + " kind=" + kind + state + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private Entity RestoreRequestRecord(
            ManagedRequestPort managedRequests,
            SaveRestoreRecord record)
        {
            Entity line = record.Line;
            SaveRestoreKind kind = record.Kind;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || EntityManager.HasComponent<Deleted>(line)
                || !EntityManager.HasComponent<TransportLine>(line))
            {
                Mod.log.Info("[RtRequestSave] 哨兵恢复跳过 line=" + line.Index
                    + " kind=" + kind + "：线路不存在或已删除");
                return Entity.Null;
            }

            TransportLine transportLine = EntityManager.GetComponentData<TransportLine>(line);
            Entity originalRequest = record.OriginalRequest;
            if (IsLiveRequest(originalRequest))
            {
                if (!EntityManager.HasComponent<TransportVehicleRequest>(originalRequest)
                    || EntityManager.GetComponentData<TransportVehicleRequest>(originalRequest).m_Route != line)
                {
                    Mod.log.Info("[RtRequestSave] 原请求仍存活但线路不匹配，拒绝新建请求 line=" + line.Index
                        + " kind=" + kind + " request=" + originalRequest.Index);
                    return Entity.Null;
                }

                if (transportLine.m_VehicleRequest == originalRequest)
                    return originalRequest;

                if (IsLiveRequest(transportLine.m_VehicleRequest))
                {
                    Mod.log.Info("[RtRequestSave] 原请求仍存活且线路已有其他请求，拒绝新建请求 line="
                        + line.Index + " kind=" + kind + " request=" + originalRequest.Index);
                    return Entity.Null;
                }

                transportLine.m_VehicleRequest = originalRequest;
                EntityManager.SetComponentData(line, transportLine);
                Mod.log.Info("[RtRequestSave] 已回接保存前仍存活的原请求 line=" + line.Index
                    + " kind=" + kind + " request=" + originalRequest.Index);
                return originalRequest;
            }

            if (IsLiveRequest(transportLine.m_VehicleRequest))
            {
                Mod.log.Info("[RtRequestSave] 哨兵恢复跳过 line=" + line.Index
                    + " kind=" + kind + "：线路已有有效请求");
                return Entity.Null;
            }

            if (managedRequests != null && !managedRequests.IsManagedLine(line))
            {
                Mod.log.Info("[RtRequestSave] 哨兵恢复跳过 line=" + line.Index
                    + " kind=" + kind + "：线路不再受模组管理");
                return Entity.Null;
            }

            return InstallParkedSentinel(line);
        }

        private void LogSaveCandidateRejected(
            Entity request,
            Entity line,
            SaveRestoreKind kind,
            string reason)
        {
            Mod.log.Info("[RtRequestSave] 保存前保留候选 request=" + request.Index
                + " line=" + line.Index + " kind=" + kind + "：" + reason);
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

        private void ClosePausedLineRequest(Entity line, ref TransportLine transportLine)
        {
            bool transportLineChanged = false;
            if ((transportLine.m_Flags & TransportLineFlags.RequireVehicles) != 0)
            {
                transportLine.m_Flags &= ~TransportLineFlags.RequireVehicles;
                transportLineChanged = true;
            }
            Entity request = transportLine.m_VehicleRequest;
            if (IsLiveRequest(request))
            {
                if (EntityManager.HasComponent<RtVehicleRequestSentinel>(request))
                {
                    EntityManager.DestroyEntity(request);
                    transportLine.m_VehicleRequest = Entity.Null;
                    transportLineChanged = true;
                }
                else if (EntityManager.HasComponent<RtSpawnPermitRequest>(request))
                {
                    bool committed = EntityManager.HasComponent<PathInformation>(request)
                        || EntityManager.HasComponent<Dispatched>(request);
                    if (!committed)
                    {
                        EntityManager.DestroyEntity(request);
                        transportLine.m_VehicleRequest = Entity.Null;
                        transportLineChanged = true;
                    }
                    else
                    {
                        EntityManager.RemoveComponent<RtSpawnPermitRequest>(request);
                    }
                }
            }

            if (EntityManager.HasBuffer<DispatchedRequest>(line))
            {
                DynamicBuffer<DispatchedRequest> requests = EntityManager.GetBuffer<DispatchedRequest>(line, true);
                for (int i = 0; i < requests.Length; i++)
                {
                    Entity dispatched = requests[i].m_VehicleRequest;
                    if (IsLiveRequest(dispatched)
                        && EntityManager.HasComponent<RtSpawnPermitRequest>(dispatched))
                    {
                        EntityManager.RemoveComponent<RtSpawnPermitRequest>(dispatched);
                    }
                }
            }
            if (transportLineChanged)
                EntityManager.SetComponentData(line, transportLine);
        }

        private void ParkUnstableLineRequest(Entity line, ref TransportLine transportLine)
        {
            Entity request = transportLine.m_VehicleRequest;
            if (IsLiveRequest(request) && EntityManager.HasComponent<RtVehicleRequestSentinel>(request))
            {
                if (!IsParkedSentinelNormalized(request, line))
                    NormalizeParkedSentinel(request, line);
            }
            else if (IsLiveRequest(request) && EntityManager.HasComponent<RtSpawnPermitRequest>(request))
            {
                bool committed = EntityManager.HasComponent<PathInformation>(request)
                    || EntityManager.HasComponent<Dispatched>(request);
                if (committed)
                {
                    EntityManager.RemoveComponent<RtSpawnPermitRequest>(request);
                }
                else
                {
                    if (!EntityManager.HasComponent<RtVehicleRequestSentinel>(request))
                        EntityManager.AddComponent<RtVehicleRequestSentinel>(request);
                    NormalizeParkedSentinel(request, line);
                }
            }
            else
            {
                // 已应用线路等待稳定时也占住产车入口，已推进的原版请求继续完成。
                if (IsLiveRequest(request))
                {
                    if (!ShouldReplaceUnauthorizedPendingRequest(request, line))
                        return;

                    EntityManager.DestroyEntity(request);
                    transportLine.m_VehicleRequest = Entity.Null;
                    EntityManager.SetComponentData(line, transportLine);
                }

                transportLine.m_VehicleRequest = InstallParkedSentinel(line);
            }
            if ((transportLine.m_Flags & TransportLineFlags.RequireVehicles) != 0)
            {
                transportLine.m_Flags &= ~TransportLineFlags.RequireVehicles;
                EntityManager.SetComponentData(line, transportLine);
            }
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
            TransportLine transportLine,
            NativeHashSet<Entity> spawnPermitLines)
        {
            if (spawnPermitLines.Contains(line))
                return false;

            if (HasLiveVanillaRequest(line, transportLine))
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

        private bool HasLiveVanillaRequest(Entity line, in TransportLine transportLine)
        {
            Entity request = transportLine.m_VehicleRequest;
            if (IsLiveRequest(request)
                && EntityManager.HasComponent<TransportVehicleRequest>(request)
                && EntityManager.GetComponentData<TransportVehicleRequest>(request).m_Route == line
                && (EntityManager.HasComponent<PathInformation>(request)
                    || EntityManager.HasComponent<Dispatched>(request)))
            {
                return true;
            }
            if (!EntityManager.HasBuffer<DispatchedRequest>(line))
                return false;
            DynamicBuffer<DispatchedRequest> requests = EntityManager.GetBuffer<DispatchedRequest>(line, true);
            for (int i = 0; i < requests.Length; i++)
            {
                Entity dispatched = requests[i].m_VehicleRequest;
                if (IsLiveRequest(dispatched)
                    && EntityManager.HasComponent<TransportVehicleRequest>(dispatched)
                    && EntityManager.GetComponentData<TransportVehicleRequest>(dispatched).m_Route == line)
                {
                    return true;
                }
            }
            return false;
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
