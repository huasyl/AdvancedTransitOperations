#if RT_DEBUG_TOOLS
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.TramTrain
{
    public sealed partial class ScopedPatchSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_TrackQuery;
        private EntityQuery m_LaneQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TrackQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadWrite<NetData>(),
                ComponentType.ReadWrite<NetGeometryData>());
            m_LaneQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadWrite<TrackLaneData>());
        }

        protected override void OnUpdate()
        {
            Entity track = FindTrack();
            if (track == Entity.Null)
                return;

            const Layer railLayers = Layer.TrainTrack | Layer.TramTrack;
            NetData netData = EntityManager.GetComponentData<NetData>(track);
            netData.m_RequiredLayers |= railLayers;
            netData.m_ConnectLayers |= railLayers;
            EntityManager.SetComponentData(track, netData);

            NetGeometryData geometryData = EntityManager.GetComponentData<NetGeometryData>(track);
            geometryData.m_MergeLayers |= railLayers;
            geometryData.m_IntersectLayers |= railLayers;
            EntityManager.SetComponentData(track, geometryData);

            int laneCount = PatchRailLanes();
            Mod.log.Info("[TramTrainAsset] scoped network patch completed track="
                + AssetSystem.TrackName + " railLanes=" + laneCount);
            Enabled = false;
        }

        private Entity FindTrack()
        {
            using NativeArray<Entity> entities = m_TrackQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (m_PrefabSystem.TryGetPrefab(entities[i], out TrackPrefab prefab)
                    && prefab != null
                    && prefab.name == AssetSystem.TrackName)
                    return entities[i];
            }
            return Entity.Null;
        }

        private int PatchRailLanes()
        {
            int count = 0;
            using NativeArray<Entity> lanes = m_LaneQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < lanes.Length; i++)
            {
                TrackLaneData data = EntityManager.GetComponentData<TrackLaneData>(lanes[i]);
                if (data.m_TrackTypes != TrackTypes.Train
                    && data.m_TrackTypes != TrackTypes.Tram)
                    continue;
                data.m_TrackTypes = TrackTypes.Train | TrackTypes.Tram;
                EntityManager.SetComponentData(lanes[i], data);
                count++;
            }
            return count;
        }
    }
}
#endif
