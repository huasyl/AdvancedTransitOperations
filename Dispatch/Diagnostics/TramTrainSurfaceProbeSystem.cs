#if RT_DEBUG_TOOLS
using System;
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.IO.AssetDatabase.VirtualTexturing;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace RapidTransitMod
{
    public sealed partial class TramTrainSurfaceProbeSystem : GameSystemBase
    {
        private const string TramPieceName = "Tram Track Piece";
        private const string TrainPieceName = "Train Track Piece";
        private const string TramSurfaceName = "Road";
        private const string TrainSurfaceName = "TrainSubwayTrack";

        private readonly List<Mesh> m_Meshes = new List<Mesh>();
        private readonly List<Material> m_Materials = new List<Material>();
        private RaycastSystem m_RaycastSystem = null!;
        private CameraUpdateSystem m_CameraSystem = null!;
        private PrefabSystem m_PrefabSystem = null!;
        private TextureStreamingSystem m_TextureSystem = null!;
        private EntityQuery m_PieceQuery;
        private GameObject m_Root;
        private bool m_KeyArmed = true;
        private bool m_Pending;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_RaycastSystem = World.GetOrCreateSystemManaged<RaycastSystem>();
            m_CameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TextureSystem = World.GetOrCreateSystemManaged<TextureStreamingSystem>();
            m_PieceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<NetPieceData>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>()
                }
            });
        }

        protected override void OnDestroy()
        {
            ClearProbe();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            bool modifierDown = Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftAlt);
            bool triggerDown = modifierDown && Input.GetKey(KeyCode.M);
            if (!triggerDown)
            {
                m_KeyArmed = true;
            }
            else if (m_KeyArmed)
            {
                m_KeyArmed = false;
                ToggleProbe();
            }

            if (!m_Pending)
                return;

            NativeArray<RaycastResult> results = m_RaycastSystem.GetResult(this);
            if (results.IsCreated && results.Length > 0)
            {
                RaycastResult result = SelectNearest(results);
                m_Pending = false;
                CreateProbe(result.m_Hit.m_HitPosition);
                return;
            }

            if (!m_CameraSystem.TryGetViewer(out var viewer))
                return;

            m_RaycastSystem.AddInput(this, new RaycastInput
            {
                m_Line = ToolRaycastSystem.CalculateRaycastLine(viewer.camera),
                m_TypeMask = TypeMask.Terrain | TypeMask.Net,
                m_Flags = RaycastFlags.Markers | RaycastFlags.Decals,
                m_CollisionMask = CollisionMask.OnGround | CollisionMask.Overground | CollisionMask.ExclusiveGround,
                m_NetLayerMask = Layer.TrainTrack | Layer.TramTrack
            });
        }

        private void ToggleProbe()
        {
            if (m_Root != null)
            {
                ClearProbe();
                Mod.log.Info("[TramTrainSurfaceProbe] removed");
                return;
            }

            if (m_Pending)
            {
                m_Pending = false;
                Mod.log.Info("[TramTrainSurfaceProbe] placement cancelled");
                return;
            }

            m_Pending = true;
            Mod.log.Info("[TramTrainSurfaceProbe] point at terrain or track to place probe");
        }

        private void CreateProbe(float3 hitPosition)
        {
            SurfaceAsset tramSurface = FindSurface(TramPieceName, TramSurfaceName);
            SurfaceAsset trainSurface = FindSurface(TrainPieceName, TrainSurfaceName);
            if (tramSurface == null || trainSurface == null)
            {
                Mod.log.Info("[TramTrainSurfaceProbe] skipped tramSurface="
                    + DescribeSurface(tramSurface) + " trainSurface=" + DescribeSurface(trainSurface));
                return;
            }

            try
            {
                m_Root = new GameObject("TramTrainSurfaceProbe")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                m_Root.transform.position = new Vector3(hitPosition.x, hitPosition.y + 0.08f, hitPosition.z);
                CreateStrip("TramSurface", -2.25f, tramSurface);
                CreateStrip("TrainSurface", 2.25f, trainSurface);
                Mod.log.Info("[TramTrainSurfaceProbe] created position=" + FormatPosition(hitPosition)
                    + " tram=" + DescribeSurface(tramSurface)
                    + " train=" + DescribeSurface(trainSurface));
            }
            catch (Exception ex)
            {
                ClearProbe();
                Mod.log.Info("[TramTrainSurfaceProbe] failed " + ex);
            }
        }

        private void CreateStrip(string name, float centerX, SurfaceAsset surface)
        {
            Mesh mesh = CreateMesh(name, centerX);
            Material material = CreateMaterial(name, surface);
            GameObject child = new GameObject(name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            child.transform.SetParent(m_Root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            m_Meshes.Add(mesh);
            m_Materials.Add(material);
        }

        private Material CreateMaterial(string name, SurfaceAsset surface)
        {
            Material source = surface.Load(-1, true, TextureAsset.KeepOnCPU.Dont, true);
            if (source == null)
                throw new InvalidOperationException("Surface material failed to load: " + surface.name);

            Material material = new Material(source)
            {
                name = name + "_" + surface.name,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (material.HasProperty("_LodFade"))
                material.SetFloat("_LodFade", 1f);

            VTAtlassingInfo[] infos = surface.VTAtlassingInfos ?? surface.PreReservedAtlassingInfos;
            int boundCount = 0;
            if (infos != null)
            {
                for (int i = 0; i < 2 && i < infos.Length; i++)
                {
                    if (infos[i].indexInStack < 0)
                        continue;

                    LocalKeyword keyword = material.shader.keywordSpace.FindKeyword("ENABLE_VT");
                    if (keyword.isValid)
                        material.EnableKeyword(keyword);
                    m_TextureSystem.BindMaterial(
                        material,
                        infos[i].stackGlobalIndex,
                        i,
                        m_TextureSystem.GetTextureParamBlock(infos[i]));
                    boundCount++;
                }
            }

            Mod.log.Info("[TramTrainSurfaceProbe] material surface=" + surface.name
                + " shader=" + material.shader.name
                + " vtStacks=" + (infos?.Length ?? 0)
                + " vtBound=" + boundCount);
            return material;
        }

        private SurfaceAsset FindSurface(string pieceName, string surfaceName)
        {
            using NativeArray<Entity> entities = m_PieceQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (!m_PrefabSystem.TryGetPrefab(entities[i], out NetPiecePrefab piece)
                    || !string.Equals(piece.name, pieceName, StringComparison.Ordinal))
                    continue;

                for (int materialIndex = 0; materialIndex < piece.materialCount; materialIndex++)
                {
                    SurfaceAsset surface = piece.GetSurfaceAsset(materialIndex);
                    if (surface != null && string.Equals(surface.name, surfaceName, StringComparison.Ordinal))
                        return surface;
                }
            }
            return null;
        }

        private static Mesh CreateMesh(string name, float centerX)
        {
            const float halfWidth = 2f;
            const float halfLength = 4f;
            Mesh mesh = new Mesh
            {
                name = name + "Mesh",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(centerX - halfWidth, 0f, -halfLength),
                    new Vector3(centerX - halfWidth, 0f, halfLength),
                    new Vector3(centerX + halfWidth, 0f, halfLength),
                    new Vector3(centerX + halfWidth, 0f, -halfLength)
                },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                tangents = new[]
                {
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector4(1f, 0f, 0f, 1f)
                },
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(0f, 4f),
                    new Vector2(2f, 4f),
                    new Vector2(2f, 0f)
                },
                colors = new[] { Color.white, Color.white, Color.white, Color.white },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static RaycastResult SelectNearest(NativeArray<RaycastResult> results)
        {
            RaycastResult nearest = results[0];
            for (int i = 1; i < results.Length; i++)
            {
                if (results[i].m_Hit.m_NormalizedDistance < nearest.m_Hit.m_NormalizedDistance)
                    nearest = results[i];
            }
            return nearest;
        }

        private void ClearProbe()
        {
            if (m_Root != null)
            {
                UnityEngine.Object.Destroy(m_Root);
                m_Root = null;
            }
            for (int i = 0; i < m_Materials.Count; i++)
                UnityEngine.Object.Destroy(m_Materials[i]);
            for (int i = 0; i < m_Meshes.Count; i++)
                UnityEngine.Object.Destroy(m_Meshes[i]);
            m_Materials.Clear();
            m_Meshes.Clear();
        }

        private static string DescribeSurface(SurfaceAsset surface)
        {
            return surface == null ? "missing" : surface.name + ":" + surface.id;
        }

        private static string FormatPosition(float3 position)
        {
            return position.x.ToString("0.00") + ","
                + position.y.ToString("0.00") + ","
                + position.z.ToString("0.00");
        }
    }
}
#endif
