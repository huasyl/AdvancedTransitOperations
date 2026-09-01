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

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TrackQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadWrite<NetData>(),
                ComponentType.ReadWrite<NetGeometryData>());
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

            Mod.log.Info("[TramTrainAsset] scoped network patch completed track="
                + AssetSystem.TrackName);
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

    }
}
#endif
