#if RT_DEBUG_TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Colossal.IO.AssetDatabase;
using Game.Net;
using Game.Prefabs;
using Newtonsoft.Json;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RapidTransitMod
{
    internal static class TrackMeshExporter
    {
        private const string ExportRoot = @"D:\tmp\tramtrain-assets\exports";
        private static readonly MethodInfo s_CreateMeshes = typeof(GeometryAsset).GetMethod(
            "CreateMeshes",
            BindingFlags.NonPublic | BindingFlags.Static);

        internal static bool TryExport(World world, Entity edge, out string exportPath, out string error)
        {
            exportPath = string.Empty;
            error = string.Empty;
            if (world == null || !world.IsCreated)
            {
                error = "world-unavailable";
                return false;
            }

            EntityManager entities = world.EntityManager;
            if (edge == Entity.Null || !entities.Exists(edge) || !entities.HasComponent<Edge>(edge))
            {
                error = "hovered-entity-is-not-edge";
                return false;
            }

            if (!entities.HasComponent<PrefabRef>(edge) || !entities.HasComponent<Composition>(edge))
            {
                error = "edge-missing-prefab-or-composition";
                return false;
            }

            Entity trackPrefabEntity = entities.GetComponentData<PrefabRef>(edge).m_Prefab;
            if (trackPrefabEntity == Entity.Null || !entities.HasComponent<TrackData>(trackPrefabEntity))
            {
                error = "edge-is-not-track";
                return false;
            }

            PrefabSystem prefabs = world.GetOrCreateSystemManaged<PrefabSystem>();
            if (!prefabs.TryGetPrefab(trackPrefabEntity, out TrackPrefab trackPrefab))
            {
                error = "track-prefab-unavailable";
                return false;
            }

            Composition composition = entities.GetComponentData<Composition>(edge);
            if (composition.m_Edge == Entity.Null || !entities.HasBuffer<NetCompositionPiece>(composition.m_Edge))
            {
                error = "edge-composition-unavailable";
                return false;
            }

            string folderName = SafeName(trackPrefab.name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            exportPath = Path.Combine(ExportRoot, folderName);
            Directory.CreateDirectory(exportPath);

            DynamicBuffer<NetCompositionPiece> pieces = entities.GetBuffer<NetCompositionPiece>(composition.m_Edge, true);
            Dictionary<Entity, List<string>> exportedPieces = new Dictionary<Entity, List<string>>();
            Dictionary<string, MaterialExport> exportedMaterials = new Dictionary<string, MaterialExport>();
            List<PieceExport> pieceRows = new List<PieceExport>();
            for (int i = 0; i < pieces.Length; i++)
            {
                NetCompositionPiece compositionPiece = pieces[i];
                PieceExport row = ExportPiece(
                    entities,
                    prefabs,
                    compositionPiece,
                    i,
                    exportPath,
                    exportedPieces,
                    exportedMaterials);
                pieceRows.Add(row);
            }

            TrackData trackData = entities.GetComponentData<TrackData>(trackPrefabEntity);
            TrackExport manifest = new TrackExport
            {
                TrackPrefab = trackPrefab.name,
                TrackType = trackData.m_TrackType.ToString(),
                Edge = edge.Index + ":" + edge.Version,
                Composition = composition.m_Edge.Index + ":" + composition.m_Edge.Version,
                Pieces = pieceRows,
                Materials = exportedMaterials.Values.ToList()
            };
            string manifestPath = Path.Combine(exportPath, "metadata.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));
            return true;
        }

        private static PieceExport ExportPiece(
            EntityManager entities,
            PrefabSystem prefabs,
            NetCompositionPiece compositionPiece,
            int index,
            string exportPath,
            Dictionary<Entity, List<string>> exportedPieces,
            Dictionary<string, MaterialExport> exportedMaterials)
        {
            PieceExport row = new PieceExport
            {
                Index = index,
                Entity = compositionPiece.m_Piece.Index + ":" + compositionPiece.m_Piece.Version,
                Offset = Values(compositionPiece.m_Offset),
                Size = Values(compositionPiece.m_Size),
                SectionFlags = compositionPiece.m_SectionFlags.ToString(),
                PieceFlags = compositionPiece.m_PieceFlags.ToString(),
                MeshFiles = new List<string>(),
                Materials = new List<string>()
            };

            if (compositionPiece.m_Piece == Entity.Null
                || !prefabs.TryGetPrefab(compositionPiece.m_Piece, out NetPiecePrefab piecePrefab))
            {
                row.Error = "piece-prefab-unavailable";
                return row;
            }

            row.Prefab = piecePrefab.name;
            if (entities.HasComponent<NetPieceData>(compositionPiece.m_Piece))
            {
                NetPieceData data = entities.GetComponentData<NetPieceData>(compositionPiece.m_Piece);
                row.Width = data.m_Width;
                row.Length = data.m_Length;
                row.HeightRange = new[] { data.m_HeightRange.min, data.m_HeightRange.max };
                row.SurfaceHeights = Values(data.m_SurfaceHeights);
            }

            foreach (SurfaceAsset surface in piecePrefab.surfaceAssets)
            {
                if (surface == null)
                    continue;
                row.Materials.Add(surface.name);
                if (!exportedMaterials.ContainsKey(surface.name))
                    exportedMaterials[surface.name] = ExportSurface(surface, exportPath);
            }
            if (exportedPieces.TryGetValue(compositionPiece.m_Piece, out List<string> existingFiles))
            {
                row.MeshFiles.AddRange(existingFiles);
                return row;
            }

            Mesh[] meshes = null;
            bool releasePrefabMeshes = false;
            bool destroyIsolatedMeshes = false;
            try
            {
                try
                {
                    meshes = piecePrefab.ObtainMeshes();
                    releasePrefabMeshes = true;
                }
                catch (Exception sharedError)
                {
                    try
                    {
                        meshes = ObtainMeshesIsolated(piecePrefab);
                        destroyIsolatedMeshes = true;
                    }
                    catch (Exception isolatedError)
                    {
                        throw new InvalidOperationException(
                            "shared=" + ErrorText(sharedError) + "; isolated=" + ErrorText(isolatedError),
                            isolatedError);
                    }
                }

                if (meshes == null || meshes.Length == 0)
                {
                    row.Error = "piece-has-no-mesh";
                    return row;
                }

                string pieceName = index.ToString("D2", CultureInfo.InvariantCulture) + "_" + SafeName(piecePrefab.name);
                for (int i = 0; i < meshes.Length; i++)
                {
                    Mesh mesh = meshes[i];
                    if (mesh == null)
                        continue;

                    string fileName = pieceName + "_" + i.ToString("D2", CultureInfo.InvariantCulture) + ".obj";
                    WriteObj(Path.Combine(exportPath, fileName), mesh);
                    row.MeshFiles.Add(fileName);
                }
                exportedPieces[compositionPiece.m_Piece] = new List<string>(row.MeshFiles);
            }
            catch (Exception ex)
            {
                row.Error = ErrorText(ex);
            }
            finally
            {
                if (releasePrefabMeshes)
                    piecePrefab.ReleaseMeshes();
                if (destroyIsolatedMeshes && meshes != null)
                {
                    for (int i = 0; i < meshes.Length; i++)
                    {
                        if (meshes[i] != null)
                            UnityEngine.Object.Destroy(meshes[i]);
                    }
                }
            }

            return row;
        }

        private static Mesh[] ObtainMeshesIsolated(NetPiecePrefab piecePrefab)
        {
            GeometryAsset asset = piecePrefab.geometryAsset;
            if (asset == null)
                return Array.Empty<Mesh>();
            if (s_CreateMeshes == null)
                throw new MissingMethodException(typeof(GeometryAsset).FullName, "CreateMeshes");

            GeometryAsset.Data data = default;
            GeometryAsset.Loading loading = default;
            try
            {
                var descriptor = asset.database.GetAsyncReadDescriptor(asset.id);
                GeometryAsset.LoadSync(descriptor, uint.MaxValue, ref data, ref loading);
                object[] arguments = { descriptor.name, data };
                Mesh[] meshes = s_CreateMeshes.Invoke(null, arguments) as Mesh[];
                if (arguments[1] is GeometryAsset.Data updatedData)
                    data = updatedData;
                return meshes ?? Array.Empty<Mesh>();
            }
            finally
            {
                loading.Dispose();
                data.Dispose();
            }
        }

        private static string ErrorText(Exception error)
        {
            Exception current = error;
            while (current is TargetInvocationException invocation && invocation.InnerException != null)
                current = invocation.InnerException;
            return current.GetType().Name + ": " + current.Message;
        }

        private static MaterialExport ExportSurface(SurfaceAsset surface, string exportPath)
        {
            string folder = Path.Combine(exportPath, "materials", SafeName(surface.name));
            Directory.CreateDirectory(folder);
            MaterialExport result = new MaterialExport
            {
                Name = surface.name,
                RawFile = RelativePath(exportPath, Path.Combine(folder, "surface.Surface"))
            };

            CopyAsset(surface, Path.Combine(folder, "surface.Surface"));
            surface.LoadProperties(useVT: false);
            try
            {
                result.Floats = surface.floats.ToDictionary(pair => pair.Key, pair => pair.Value);
                result.Ints = surface.ints.ToDictionary(pair => pair.Key, pair => pair.Value);
                result.Vectors = surface.vectors.ToDictionary(pair => pair.Key, pair => Values(pair.Value));
                result.Colors = surface.colors.ToDictionary(pair => pair.Key, pair => Values(pair.Value));
                result.Keywords = surface.keywords.ToList();

                if (surface.hasVTSurfaceAsset && surface.vtSurfaceAsset != null)
                {
                    string vtPath = Path.Combine(folder, "surface.VTSurface");
                    CopyAsset(surface.vtSurfaceAsset, vtPath);
                    result.VtRawFile = RelativePath(exportPath, vtPath);
                }

                foreach (KeyValuePair<string, TextureAsset> pair in surface.textures)
                {
                    if (pair.Value != null)
                        result.Textures.Add(ExportTexture(pair.Key, pair.Value, folder, exportPath));
                }
            }
            catch (Exception ex)
            {
                result.Error = ErrorText(ex);
            }
            finally
            {
                surface.UnloadProperties();
            }

            return result;
        }

        private static TextureExport ExportTexture(
            string property,
            TextureAsset asset,
            string folder,
            string exportPath)
        {
            string baseName = SafeName(property) + "_" + SafeName(asset.name);
            string rawPath = Path.Combine(folder, baseName + ".Texture");
            TextureExport result = new TextureExport
            {
                Property = property,
                Name = asset.name,
                Format = asset.format.ToString(),
                Dimension = asset.dimension.ToString(),
                Width = asset.width,
                Height = asset.height,
                Depth = asset.depth,
                Mips = asset.mipsCount,
                RawFile = RelativePath(exportPath, rawPath)
            };
            CopyAsset(asset, rawPath);

            Texture texture = null;
            try
            {
                texture = asset.Load(0, TextureAsset.KeepOnCPU.Object);
                if (texture is Texture2D texture2D)
                {
                    string imagePath = Path.Combine(folder, baseName + ".tga");
                    File.WriteAllBytes(imagePath, WriteTextureTga(texture2D));
                    result.ImageFiles.Add(RelativePath(exportPath, imagePath));
                }
                else if (texture != null)
                {
                    result.Error = "PNG preview unavailable for " + texture.GetType().Name + "; raw asset exported";
                }
            }
            catch (Exception ex)
            {
                result.Error = ErrorText(ex);
            }
            finally
            {
                if (texture != null)
                    asset.Unload();
            }

            return result;
        }

        private static byte[] WriteTextureTga(Texture2D source)
        {
            RenderTexture target = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readable.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                Color32[] pixels = readable.GetPixels32();
                byte[] bytes = new byte[18 + pixels.Length * 4];
                bytes[2] = 2;
                bytes[12] = (byte)(source.width & 255);
                bytes[13] = (byte)((source.width >> 8) & 255);
                bytes[14] = (byte)(source.height & 255);
                bytes[15] = (byte)((source.height >> 8) & 255);
                bytes[16] = 32;
                bytes[17] = 8;
                int offset = 18;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 pixel = pixels[i];
                    bytes[offset++] = pixel.b;
                    bytes[offset++] = pixel.g;
                    bytes[offset++] = pixel.r;
                    bytes[offset++] = pixel.a;
                }
                return bytes;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null)
                    UnityEngine.Object.Destroy(readable);
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void CopyAsset(AssetData asset, string path)
        {
            using Stream input = asset.GetReadStream();
            using FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        private static string RelativePath(string root, string path)
        {
            return path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
        }

        private static void WriteObj(string path, Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uv = mesh.uv;
            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasUv = uv != null && uv.Length == vertices.Length;

            using StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("# " + mesh.name);
            for (int i = 0; i < vertices.Length; i++)
                writer.WriteLine(FormattableString.Invariant($"v {vertices[i].x:R} {vertices[i].y:R} {vertices[i].z:R}"));
            if (hasUv)
            {
                for (int i = 0; i < uv.Length; i++)
                    writer.WriteLine(FormattableString.Invariant($"vt {uv[i].x:R} {uv[i].y:R}"));
            }
            if (hasNormals)
            {
                for (int i = 0; i < normals.Length; i++)
                    writer.WriteLine(FormattableString.Invariant($"vn {normals[i].x:R} {normals[i].y:R} {normals[i].z:R}"));
            }

            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                writer.WriteLine("g submesh_" + subMesh.ToString(CultureInfo.InvariantCulture));
                int[] triangles = mesh.GetTriangles(subMesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = triangles[i] + 1;
                    int b = triangles[i + 1] + 1;
                    int c = triangles[i + 2] + 1;
                    writer.WriteLine("f " + Face(a, hasUv, hasNormals) + " " + Face(b, hasUv, hasNormals) + " " + Face(c, hasUv, hasNormals));
                }
            }
        }

        private static string Face(int index, bool hasUv, bool hasNormals)
        {
            if (hasUv && hasNormals)
                return index + "/" + index + "/" + index;
            if (hasUv)
                return index + "/" + index;
            if (hasNormals)
                return index + "//" + index;
            return index.ToString(CultureInfo.InvariantCulture);
        }

        private static string SafeName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value) ? "track" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '_');
            return result;
        }

        private static float[] Values(float3 value)
        {
            return new[] { value.x, value.y, value.z };
        }

        private static float[] Values(float4 value)
        {
            return new[] { value.x, value.y, value.z, value.w };
        }

        private static float[] Values(Vector4 value)
        {
            return new[] { value.x, value.y, value.z, value.w };
        }

        private static float[] Values(Color value)
        {
            return new[] { value.r, value.g, value.b, value.a };
        }

        private sealed class TrackExport
        {
            public string TrackPrefab { get; set; } = string.Empty;
            public string TrackType { get; set; } = string.Empty;
            public string Edge { get; set; } = string.Empty;
            public string Composition { get; set; } = string.Empty;
            public List<PieceExport> Pieces { get; set; } = new List<PieceExport>();
            public List<MaterialExport> Materials { get; set; } = new List<MaterialExport>();
        }

        private sealed class MaterialExport
        {
            public string Name { get; set; } = string.Empty;
            public string RawFile { get; set; } = string.Empty;
            public string VtRawFile { get; set; } = string.Empty;
            public Dictionary<string, float> Floats { get; set; } = new Dictionary<string, float>();
            public Dictionary<string, int> Ints { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, float[]> Vectors { get; set; } = new Dictionary<string, float[]>();
            public Dictionary<string, float[]> Colors { get; set; } = new Dictionary<string, float[]>();
            public List<string> Keywords { get; set; } = new List<string>();
            public List<TextureExport> Textures { get; set; } = new List<TextureExport>();
            public string Error { get; set; } = string.Empty;
        }

        private sealed class TextureExport
        {
            public string Property { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Format { get; set; } = string.Empty;
            public string Dimension { get; set; } = string.Empty;
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; }
            public int Mips { get; set; }
            public string RawFile { get; set; } = string.Empty;
            public List<string> ImageFiles { get; set; } = new List<string>();
            public string Error { get; set; } = string.Empty;
        }

        private sealed class PieceExport
        {
            public int Index { get; set; }
            public string Entity { get; set; } = string.Empty;
            public string Prefab { get; set; } = string.Empty;
            public float[] Offset { get; set; } = Array.Empty<float>();
            public float[] Size { get; set; } = Array.Empty<float>();
            public string SectionFlags { get; set; } = string.Empty;
            public string PieceFlags { get; set; } = string.Empty;
            public float Width { get; set; }
            public float Length { get; set; }
            public float[] HeightRange { get; set; } = Array.Empty<float>();
            public float[] SurfaceHeights { get; set; } = Array.Empty<float>();
            public List<string> MeshFiles { get; set; } = new List<string>();
            public List<string> Materials { get; set; } = new List<string>();
            public string Error { get; set; } = string.Empty;
        }
    }
}
#endif
