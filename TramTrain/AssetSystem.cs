#if RT_DEBUG_TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RapidTransitMod.TramTrain
{
    public sealed partial class AssetSystem : GameSystemBase
    {
        internal const string TrackName = "ATO Tram-Train Transition Track";
        internal const string PieceName = "ATO Tram-Train Transition Piece";
        private const string TemplateName = "Double Tram Track";
        private const string TramPieceName = "Tram Track Piece";
        private const string TramSurfaceName = "Road";
        private const string TrainSurfaceName = "TrainSubwayTrack";
        private const string MeshPath = "Assets/TramTrain/tramtrain_transition_v03.obj";
        private const string LodMeshPath = "Assets/TramTrain/tramtrain_transition_lod1_v03.obj";
        private const string GeometryGuid = "41544f5452414d545241494e30303033";
        private const string LodGeometryGuid = "41544f5452414d545241494e30303034";

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_TrackQuery;
        private EntityQuery m_PieceQuery;
        private bool m_WaitLogged;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TrackQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<TrackData>());
            m_PieceQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetPieceData>());
            Mod.log.Info("[TramTrainAsset] system created");
        }

        protected override void OnUpdate()
        {
            if (string.IsNullOrEmpty(Mod.RootPath))
                return;

            TrackPrefab template = FindTrack(TemplateName);
            SurfaceAsset tramSurface = FindSurface(TramPieceName, TramSurfaceName);
            SurfaceAsset trainSurface = FindSurface("Train Track Piece", TrainSurfaceName);
            if (template == null || tramSurface == null || trainSurface == null)
            {
                if (!m_WaitLogged)
                {
                    Mod.log.Info("[TramTrainAsset] waiting template=" + (template?.name ?? "missing")
                        + " tramSurface=" + (tramSurface?.name ?? "missing")
                        + " trainSurface=" + (trainSurface?.name ?? "missing"));
                    m_WaitLogged = true;
                }
                return;
            }

            try
            {
                ObjMeshLoader.Result loaded = LoadMesh(MeshPath);
                ObjMeshLoader.Result lodLoaded = LoadMesh(LodMeshPath);
                int vertexCount = loaded.Mesh.vertexCount;
                int subMeshCount = loaded.Mesh.subMeshCount;
                int lodVertexCount = lodLoaded.Mesh.vertexCount;
                GeometryAsset geometry = RegisterGeometry(
                    loaded.Mesh,
                    GeometryGuid,
                    "TransitionNode.Geometry",
                    out bool unloadGeometry,
                    out bool destroyLoadedMesh);
                GeometryAsset lodGeometry = RegisterGeometry(
                    lodLoaded.Mesh,
                    LodGeometryGuid,
                    "TransitionNodeLod1.Geometry",
                    out bool unloadLodGeometry,
                    out bool destroyLoadedLodMesh);

                NetPiecePrefab lod = CreatePiece(
                    "ATO Tram-Train Transition LOD1 Piece",
                    lodLoaded,
                    lodGeometry,
                    tramSurface,
                    trainSurface,
                    addVertexMatch: false);
                NetPiecePrefab transition = CreatePiece(
                    PieceName,
                    loaded,
                    geometry,
                    tramSurface,
                    trainSurface,
                    addVertexMatch: true);
                transition.AddComponent<LodProperties>().m_LodMeshes = new RenderPrefab[] { lod };

                NetSectionPrefab leftSync = CreateSyncSection(
                    "ATO Tram-Train Left Sync Section",
                    "ATO Tram-Train Left Sync Piece",
                    new[] { 3.2825f, 4.7175f });
                NetSectionPrefab transitionSection = CreateSection(
                    "ATO Tram-Train Transition Section",
                    transition);
                NetSectionPrefab rightSync = CreateSyncSection(
                    "ATO Tram-Train Right Sync Section",
                    "ATO Tram-Train Right Sync Piece",
                    new[] { -4.7175f, -3.2825f });

                TrackPrefab track = CreateTrack(
                    template,
                    leftSync,
                    transitionSection,
                    rightSync,
                    out int localRailLanes);
                if (!m_PrefabSystem.AddPrefab(track, Mod.Id))
                    throw new InvalidOperationException("轨道预制体注册失败");

                if (unloadGeometry)
                    geometry.Unload(force: true);
                if (destroyLoadedMesh)
                    UnityEngine.Object.Destroy(loaded.Mesh);
                if (unloadLodGeometry)
                    lodGeometry.Unload(force: true);
                if (destroyLoadedLodMesh)
                    UnityEngine.Object.Destroy(lodLoaded.Mesh);

                Mod.log.Info("[TramTrainAsset] registered track=" + track.name
                    + " vertices=" + vertexCount
                    + " subMeshes=" + subMeshCount
                    + " lodVertices=" + lodVertexCount
                    + " localRailLanes=" + localRailLanes
                    + " materials=" + string.Join(",", loaded.Materials));
                Enabled = false;
            }
            catch (Exception ex)
            {
                Mod.log.Info("[TramTrainAsset] registration failed " + ex);
                Enabled = false;
            }
        }

        private static ObjMeshLoader.Result LoadMesh(string relativePath)
        {
            string path = Path.Combine(
                Mod.RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            return ObjMeshLoader.Load(path);
        }

        private TrackPrefab FindTrack(string name)
        {
            using NativeArray<Entity> entities = m_TrackQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (m_PrefabSystem.TryGetPrefab(entities[i], out TrackPrefab prefab)
                    && string.Equals(prefab.name, name, StringComparison.Ordinal))
                    return prefab;
            }
            return null;
        }

        private SurfaceAsset FindSurface(string pieceName, string surfaceName)
        {
            using NativeArray<Entity> entities = m_PieceQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab(entities[i], out NetPiecePrefab piece)
                    || piece.name.IndexOf(pieceName, StringComparison.Ordinal) < 0)
                    continue;
                for (int j = 0; j < piece.materialCount; j++)
                {
                    SurfaceAsset surface = piece.GetSurfaceAsset(j);
                    if (surface != null
                        && (string.Equals(surface.name, surfaceName, StringComparison.Ordinal)
                            || surface.name.StartsWith(surfaceName + "_", StringComparison.Ordinal)))
                        return surface;
                }
            }
            return null;
        }

        private static GeometryAsset RegisterGeometry(
            Mesh mesh,
            string guidText,
            string assetName,
            out bool unloadGeometry,
            out bool destroyLoadedMesh)
        {
            Colossal.Hash128 guid = Colossal.Hash128.Parse(guidText);
            if (AssetDatabase.global.TryGetAsset(guid, out GeometryAsset existing))
            {
                if (existing.isPersistent)
                {
                    unloadGeometry = false;
                    destroyLoadedMesh = true;
                    return existing;
                }
                existing.Save(force: true);
                unloadGeometry = true;
                destroyLoadedMesh = true;
                return existing;
            }

            GeometryAsset geometry = AssetDatabase.user.AddAsset<GeometryAsset, Mesh[]>(
                AssetDataPath.Create("RapidTransitMod/TramTrain", assetName, true),
                new[] { mesh },
                guid);
            geometry.Save(force: true);
            unloadGeometry = true;
            destroyLoadedMesh = false;
            return geometry;
        }

        private static NetPiecePrefab CreatePiece(
            string name,
            ObjMeshLoader.Result loaded,
            GeometryAsset geometry,
            SurfaceAsset tramSurface,
            SurfaceAsset trainSurface,
            bool addVertexMatch)
        {
            Mesh mesh = loaded.Mesh;
            Bounds bounds = mesh.bounds;
            NetPiecePrefab piece = PrefabBase.Create<NetPiecePrefab>(name);
            piece.m_Layer = NetPieceLayer.Surface;
            piece.m_Width = 12f;
            piece.m_Length = bounds.size.z;
            piece.m_HeightRange = new Bounds1(bounds.min.y, bounds.max.y);
            piece.m_SurfaceHeights = new float4(-0.2f);
            piece.geometryAsset = geometry;
            if (loaded.Materials.Length != mesh.subMeshCount)
                throw new InvalidDataException("过渡构件的材质槽数量与网格不一致：" + name);
            piece.surfaceAssets = ResolveSurfaces(loaded.Materials, tramSurface, trainSurface);
            piece.bounds = new Bounds3(
                new float3(bounds.min.x, bounds.min.y, bounds.min.z),
                new float3(bounds.max.x, bounds.max.y, bounds.max.z));
            piece.indexCount = mesh.triangles.Length;
            piece.vertexCount = mesh.vertexCount;
            piece.meshCount = mesh.subMeshCount;
            if (addVertexMatch)
                piece.AddComponent<MatchPieceVertices>().m_Offsets = new[] { 0f, 0f };
            return piece;
        }

        private static SurfaceAsset[] ResolveSurfaces(
            string[] materials,
            SurfaceAsset tramSurface,
            SurfaceAsset trainSurface)
        {
            SurfaceAsset[] result = new SurfaceAsset[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                switch (materials[i])
                {
                    case "TramSurfaceSlot":
                    case "TransitionSurfaceSlot":
                    case "RailGrooveSlot":
                    case "RailGroove":
                        result[i] = tramSurface;
                        break;
                    case "TrainSurfaceSlot":
                    case "RailSteelSlot":
                    case "RailSteel":
                    case "ConcreteSleeperSlot":
                    case "ConcreteSleeper":
                        result[i] = trainSurface;
                        break;
                    default:
                        throw new InvalidDataException("过渡构件包含未配置的材质槽：" + materials[i]);
                }
            }
            return result;
        }

        private static NetSectionPrefab CreateSection(string name, NetPiecePrefab piece)
        {
            NetSectionPrefab section = PrefabBase.Create<NetSectionPrefab>(name);
            section.m_Pieces = new[]
            {
                new NetPieceInfo
                {
                    m_Piece = piece,
                    m_Offset = float3.zero
                }
            };
            return section;
        }

        private static NetSectionPrefab CreateSyncSection(
            string sectionName,
            string pieceName,
            float[] offsets)
        {
            NetPiecePrefab marker = PrefabBase.Create<NetPiecePrefab>(pieceName);
            marker.m_Layer = NetPieceLayer.Surface;
            marker.m_Width = 0f;
            marker.m_Length = 64f;
            marker.m_HeightRange = new Bounds1(-0.2f, -0.2f);
            marker.m_SurfaceHeights = new float4(-0.2f);
            marker.AddComponent<MatchPieceVertices>().m_Offsets = offsets;
            return CreateSection(sectionName, marker);
        }

        private static TrackPrefab CreateTrack(
            TrackPrefab template,
            NetSectionPrefab leftSync,
            NetSectionPrefab transition,
            NetSectionPrefab rightSync,
            out int localRailLanes)
        {
            TrackPrefab track = template.Clone(TrackName) as TrackPrefab;
            if (track == null)
                throw new InvalidOperationException("双线电车轨模板复制失败");
            if (track.m_Sections == null
                || track.m_Sections.Length == 0
                || (track.m_Sections.Length & 1) != 0)
                throw new InvalidOperationException("双线电车轨地面断面不是可识别的左右对称结构");
            for (int i = 0; i < track.m_Sections.Length; i++)
            {
                if (track.m_Sections[i].m_Median)
                    throw new InvalidOperationException("双线电车轨地面断面包含意外的中间断面");
            }

            Dictionary<NetSectionPrefab, NetSectionPrefab> sections = new Dictionary<NetSectionPrefab, NetSectionPrefab>();
            Dictionary<NetPiecePrefab, NetPiecePrefab> pieces = new Dictionary<NetPiecePrefab, NetPiecePrefab>();
            Dictionary<NetLanePrefab, NetLanePrefab> lanes = new Dictionary<NetLanePrefab, NetLanePrefab>();
            NetSectionInfo[] source = track.m_Sections;
            NetSectionInfo[] result = new NetSectionInfo[source.Length + 3];
            int split = source.Length / 2;
            int output = 0;
            for (int i = 0; i <= source.Length; i++)
            {
                if (i == split)
                {
                    result[output++] = CreateNodeSection(leftSync, median: false);
                    result[output++] = CreateNodeSection(transition, median: true);
                    result[output++] = CreateNodeSection(rightSync, median: false);
                }
                if (i == source.Length)
                    break;

                NetSectionInfo info = source[i];
                NetSectionPrefab section = CloneSection(info.m_Section, sections, pieces, lanes);
                NetSectionInfo copy = CopySectionInfo(info, section);
                copy.m_RequireNone = AddRequirement(copy.m_RequireNone, NetPieceRequirements.StyleBreak);
                result[output++] = copy;
            }
            track.m_Sections = result;
            localRailLanes = lanes.Count;

            if (track.TryGet<UIObject>(out UIObject ui))
            {
                ui.m_IsDebugObject = false;
                ui.m_Priority += 1;
            }
            return track;
        }

        private static NetSectionInfo CreateNodeSection(NetSectionPrefab section, bool median)
        {
            return new NetSectionInfo
            {
                m_Section = section,
                m_RequireAll = new[]
                {
                    NetPieceRequirements.Node,
                    NetPieceRequirements.StyleBreak
                },
                m_Median = median,
                m_Offset = float3.zero
            };
        }

        private static NetSectionPrefab CloneSection(
            NetSectionPrefab source,
            Dictionary<NetSectionPrefab, NetSectionPrefab> sections,
            Dictionary<NetPiecePrefab, NetPiecePrefab> pieces,
            Dictionary<NetLanePrefab, NetLanePrefab> lanes)
        {
            if (sections.TryGetValue(source, out NetSectionPrefab existing))
                return existing;

            NetSectionPrefab clone = PrefabBase.Create<NetSectionPrefab>("ATO " + source.name);
            sections.Add(source, clone);
            clone.m_SubSections = source.m_SubSections;
            int count = source.m_Pieces?.Length ?? 0;
            clone.m_Pieces = new NetPieceInfo[count];
            for (int i = 0; i < count; i++)
            {
                NetPieceInfo info = source.m_Pieces[i];
                clone.m_Pieces[i] = CopyPieceInfo(info, ClonePiece(info.m_Piece, pieces, lanes));
            }
            return clone;
        }

        private static NetPiecePrefab ClonePiece(
            NetPiecePrefab source,
            Dictionary<NetPiecePrefab, NetPiecePrefab> pieces,
            Dictionary<NetLanePrefab, NetLanePrefab> lanes)
        {
            if (pieces.TryGetValue(source, out NetPiecePrefab existing))
                return existing;
            NetPiecePrefab clone = source.Clone("ATO " + source.name) as NetPiecePrefab;
            if (clone == null)
                throw new InvalidOperationException("轨道构件复制失败：" + source.name);
            pieces.Add(source, clone);

            if (clone.TryGet<NetPieceLanes>(out NetPieceLanes pieceLanes) && pieceLanes.m_Lanes != null)
            {
                NetLaneInfo[] infos = new NetLaneInfo[pieceLanes.m_Lanes.Length];
                for (int i = 0; i < infos.Length; i++)
                {
                    NetLaneInfo info = pieceLanes.m_Lanes[i];
                    infos[i] = new NetLaneInfo
                    {
                        m_Lane = CloneLane(info.m_Lane, lanes),
                        m_Position = info.m_Position,
                        m_FindAnchor = info.m_FindAnchor
                    };
                }
                pieceLanes.m_Lanes = infos;
            }
            return clone;
        }

        private static NetLanePrefab CloneLane(
            NetLanePrefab source,
            Dictionary<NetLanePrefab, NetLanePrefab> lanes)
        {
            if (lanes.TryGetValue(source, out NetLanePrefab existing))
                return existing;
            NetLanePrefab clone = source.Clone("ATO " + source.name) as NetLanePrefab;
            if (clone == null)
                throw new InvalidOperationException("轨道车道复制失败：" + source.name);
            lanes.Add(source, clone);
            if (clone.TryGet<Game.Prefabs.TrackLane>(out Game.Prefabs.TrackLane trackLane))
                trackLane.m_TrackType = TrackTypes.Train | TrackTypes.Tram;
            return clone;
        }

        private static NetPieceRequirements[] AddRequirement(
            NetPieceRequirements[] source,
            NetPieceRequirements requirement)
        {
            int count = source?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                if (source[i] == requirement)
                    return source;
            }
            NetPieceRequirements[] result = new NetPieceRequirements[count + 1];
            if (count != 0)
                Array.Copy(source, result, count);
            result[count] = requirement;
            return result;
        }

        private static NetSectionInfo CopySectionInfo(NetSectionInfo source, NetSectionPrefab section)
        {
            return new NetSectionInfo
            {
                m_Section = section,
                m_RequireAll = source.m_RequireAll,
                m_RequireAny = source.m_RequireAny,
                m_RequireNone = source.m_RequireNone,
                m_HiddenLayers = source.m_HiddenLayers,
                m_Invert = source.m_Invert,
                m_Flip = source.m_Flip,
                m_Median = source.m_Median,
                m_HalfLength = source.m_HalfLength,
                m_Offset = source.m_Offset
            };
        }

        private static NetPieceInfo CopyPieceInfo(NetPieceInfo source, NetPiecePrefab piece)
        {
            return new NetPieceInfo
            {
                m_Piece = piece,
                m_RequireAll = source.m_RequireAll,
                m_RequireAny = source.m_RequireAny,
                m_RequireNone = source.m_RequireNone,
                m_Offset = source.m_Offset
            };
        }
    }
}
#endif
