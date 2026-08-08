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
                string path = Path.Combine(Mod.RootPath, MeshPath.Replace('/', Path.DirectorySeparatorChar));
                ObjMeshLoader.Result loaded = ObjMeshLoader.Load(path, 0.025f);
                int vertexCount = loaded.Mesh.vertexCount;
                int subMeshCount = loaded.Mesh.subMeshCount;
                GeometryAsset geometry = RegisterGeometry(loaded.Mesh, out bool unloadGeometry);
                NetPiecePrefab overlay = CreatePiece(loaded, geometry, tramSurface, trainSurface);
                if (unloadGeometry)
                    geometry.Unload(force: true);
                TrackPrefab track = CreateTrack(template, overlay);
                if (!m_PrefabSystem.AddPrefab(track, Mod.Id))
                    throw new InvalidOperationException("轨道预制体注册失败");

                Mod.log.Info("[TramTrainAsset] registered track=" + track.name
                    + " vertices=" + vertexCount
                    + " subMeshes=" + subMeshCount
                    + " materials=" + string.Join(",", loaded.Materials));
                Enabled = false;
            }
            catch (Exception ex)
            {
                Mod.log.Info("[TramTrainAsset] registration failed " + ex);
                Enabled = false;
            }
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

        private static GeometryAsset RegisterGeometry(Mesh mesh, out bool unloadGeometry)
        {
            Colossal.Hash128 guid = Colossal.Hash128.Parse("41544f5452414d545241494e30303031");
            if (AssetDatabase.global.TryGetAsset(guid, out GeometryAsset existing))
            {
                if (existing.isPersistent)
                {
                    unloadGeometry = false;
                    return existing;
                }
                existing.SetData(new[] { mesh });
                existing.Save(force: true);
                unloadGeometry = true;
                return existing;
            }

            GeometryAsset geometry = AssetDatabase.user.AddAsset<GeometryAsset, Mesh[]>(
                AssetDataPath.Create("RapidTransitMod/TramTrain", "Transition.Geometry", true),
                new[] { mesh },
                guid);
            geometry.Save(force: true);
            unloadGeometry = true;
            return geometry;
        }

        private static NetPiecePrefab CreatePiece(
            ObjMeshLoader.Result loaded,
            GeometryAsset geometry,
            SurfaceAsset tramSurface,
            SurfaceAsset trainSurface)
        {
            Mesh mesh = loaded.Mesh;
            NetPiecePrefab piece = PrefabBase.Create<NetPiecePrefab>(PieceName);
            piece.m_Layer = NetPieceLayer.Surface;
            piece.m_Width = 12f;
            piece.m_Length = 16f;
            piece.m_HeightRange = new Bounds1(-0.36f, 0.07f);
            piece.m_SurfaceHeights = new float4(-0.175f);
            piece.geometryAsset = geometry;
            piece.surfaceAssets = ResolveSurfaces(loaded.Materials, tramSurface, trainSurface);
            Bounds bounds = mesh.bounds;
            piece.bounds = new Bounds3(
                new float3(bounds.min.x, bounds.min.y, bounds.min.z),
                new float3(bounds.max.x, bounds.max.y, bounds.max.z));
            piece.indexCount = mesh.triangles.Length;
            piece.vertexCount = mesh.vertexCount;
            piece.meshCount = mesh.subMeshCount;
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
                string name = materials[i];
                result[i] = name.IndexOf("Train", StringComparison.Ordinal) >= 0
                    || name.IndexOf("Rail", StringComparison.Ordinal) >= 0
                    || name.IndexOf("Sleeper", StringComparison.Ordinal) >= 0
                    ? trainSurface
                    : tramSurface;
            }
            return result;
        }

        private static TrackPrefab CreateTrack(TrackPrefab template, NetPiecePrefab overlay)
        {
            TrackPrefab track = template.Clone(TrackName) as TrackPrefab;
            if (track == null)
                throw new InvalidOperationException("双线电车轨模板复制失败");

            Dictionary<NetSectionPrefab, NetSectionPrefab> sections = new Dictionary<NetSectionPrefab, NetSectionPrefab>();
            Dictionary<NetPiecePrefab, NetPiecePrefab> pieces = new Dictionary<NetPiecePrefab, NetPiecePrefab>();
            Dictionary<NetLanePrefab, NetLanePrefab> lanes = new Dictionary<NetLanePrefab, NetLanePrefab>();
            NetSectionInfo[] sectionInfos = new NetSectionInfo[track.m_Sections.Length];
            int replaced = 0;
            for (int i = 0; i < track.m_Sections.Length; i++)
            {
                NetSectionInfo source = track.m_Sections[i];
                NetSectionPrefab section = source.m_Section;
                if (ContainsTramPiece(section))
                {
                    section = CloneSection(section, overlay, sections, pieces, lanes);
                    replaced++;
                }
                sectionInfos[i] = CopySectionInfo(source, section);
            }
            if (replaced == 0)
                throw new InvalidOperationException("没有找到双线电车轨的地面断面");
            track.m_Sections = sectionInfos;

            if (track.TryGet<UIObject>(out UIObject ui))
            {
                ui.m_IsDebugObject = false;
                ui.m_Priority += 1;
            }
            return track;
        }

        private static bool ContainsTramPiece(NetSectionPrefab section)
        {
            if (section == null || section.m_Pieces == null)
                return false;
            for (int i = 0; i < section.m_Pieces.Length; i++)
            {
                NetPiecePrefab piece = section.m_Pieces[i].m_Piece;
                if (piece != null && piece.name.IndexOf(TramPieceName, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static NetSectionPrefab CloneSection(
            NetSectionPrefab source,
            NetPiecePrefab overlay,
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
            clone.m_Pieces = new NetPieceInfo[count + 1];
            for (int i = 0; i < count; i++)
            {
                NetPieceInfo info = source.m_Pieces[i];
                clone.m_Pieces[i] = CopyPieceInfo(info, ClonePiece(info.m_Piece, pieces, lanes));
            }
            clone.m_Pieces[count] = new NetPieceInfo
            {
                m_Piece = overlay,
                m_Offset = float3.zero
            };
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
