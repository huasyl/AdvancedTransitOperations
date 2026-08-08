#if RT_DEBUG_TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RapidTransitMod.TramTrain
{
    internal static class ObjMeshLoader
    {
        internal sealed class Result
        {
            public Mesh Mesh;
            public string[] Materials;
        }

        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            public readonly int Position;
            public readonly int TexCoord;
            public readonly int Normal;

            public VertexKey(int position, int texCoord, int normal)
            {
                Position = position;
                TexCoord = texCoord;
                Normal = normal;
            }

            public bool Equals(VertexKey other)
            {
                return Position == other.Position
                    && TexCoord == other.TexCoord
                    && Normal == other.Normal;
            }

            public override bool Equals(object obj)
            {
                return obj is VertexKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Position;
                    hash = (hash * 397) ^ TexCoord;
                    return (hash * 397) ^ Normal;
                }
            }
        }

        public static Result Load(string path, float heightOffset)
        {
            List<Vector3> sourcePositions = new List<Vector3>();
            List<Vector2> sourceTexCoords = new List<Vector2>();
            List<Vector3> sourceNormals = new List<Vector3>();
            List<Vector3> positions = new List<Vector3>();
            List<Vector2> texCoords = new List<Vector2>();
            List<Vector3> normals = new List<Vector3>();
            List<string> materials = new List<string>();
            List<List<int>> triangles = new List<List<int>>();
            Dictionary<string, int> materialIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<VertexKey, int> vertices = new Dictionary<VertexKey, int>();
            int currentMaterial = -1;

            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                switch (parts[0])
                {
                    case "v" when parts.Length >= 4:
                        sourcePositions.Add(new Vector3(
                            ParseFloat(parts[1]),
                            ParseFloat(parts[2]) + heightOffset,
                            ParseFloat(parts[3])));
                        break;
                    case "vt" when parts.Length >= 3:
                        sourceTexCoords.Add(new Vector2(ParseFloat(parts[1]), ParseFloat(parts[2])));
                        break;
                    case "vn" when parts.Length >= 4:
                        sourceNormals.Add(new Vector3(
                            ParseFloat(parts[1]),
                            ParseFloat(parts[2]),
                            ParseFloat(parts[3])));
                        break;
                    case "usemtl" when parts.Length >= 2:
                        currentMaterial = AddMaterial(parts[1], materials, triangles, materialIndices);
                        break;
                    case "f" when parts.Length >= 4:
                        if (currentMaterial < 0)
                            currentMaterial = AddMaterial("Default", materials, triangles, materialIndices);
                        int first = GetVertex(parts[1], sourcePositions, sourceTexCoords, sourceNormals,
                            positions, texCoords, normals, vertices);
                        int previous = GetVertex(parts[2], sourcePositions, sourceTexCoords, sourceNormals,
                            positions, texCoords, normals, vertices);
                        for (int i = 3; i < parts.Length; i++)
                        {
                            int current = GetVertex(parts[i], sourcePositions, sourceTexCoords, sourceNormals,
                                positions, texCoords, normals, vertices);
                            triangles[currentMaterial].Add(first);
                            triangles[currentMaterial].Add(previous);
                            triangles[currentMaterial].Add(current);
                            previous = current;
                        }
                        break;
                }
            }

            if (positions.Count == 0)
                throw new InvalidDataException("过渡段网格没有顶点：" + path);

            Mesh mesh = new Mesh
            {
                name = Path.GetFileNameWithoutExtension(path)
            };
            mesh.SetVertices(positions);
            if (normals.Count == positions.Count)
                mesh.SetNormals(normals);
            if (texCoords.Count == positions.Count)
                mesh.SetUVs(0, texCoords);
            mesh.subMeshCount = triangles.Count;
            for (int i = 0; i < triangles.Count; i++)
                mesh.SetTriangles(triangles[i], i, false);
            if (normals.Count != positions.Count)
                mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            return new Result
            {
                Mesh = mesh,
                Materials = materials.ToArray()
            };
        }

        private static int AddMaterial(
            string name,
            List<string> materials,
            List<List<int>> triangles,
            Dictionary<string, int> indices)
        {
            if (indices.TryGetValue(name, out int index))
                return index;
            index = materials.Count;
            materials.Add(name);
            triangles.Add(new List<int>());
            indices.Add(name, index);
            return index;
        }

        private static int GetVertex(
            string token,
            List<Vector3> sourcePositions,
            List<Vector2> sourceTexCoords,
            List<Vector3> sourceNormals,
            List<Vector3> positions,
            List<Vector2> texCoords,
            List<Vector3> normals,
            Dictionary<VertexKey, int> vertices)
        {
            string[] values = token.Split('/');
            int position = ParseIndex(values, 0, sourcePositions.Count);
            int texCoord = ParseIndex(values, 1, sourceTexCoords.Count);
            int normal = ParseIndex(values, 2, sourceNormals.Count);
            VertexKey key = new VertexKey(position, texCoord, normal);
            if (vertices.TryGetValue(key, out int index))
                return index;

            index = positions.Count;
            positions.Add(sourcePositions[position]);
            texCoords.Add(texCoord >= 0 ? sourceTexCoords[texCoord] : Vector2.zero);
            normals.Add(normal >= 0 ? sourceNormals[normal] : Vector3.up);
            vertices.Add(key, index);
            return index;
        }

        private static int ParseIndex(string[] values, int part, int count)
        {
            if (part >= values.Length || values[part].Length == 0)
                return -1;
            int value = int.Parse(values[part], NumberStyles.Integer, CultureInfo.InvariantCulture);
            return value > 0 ? value - 1 : count + value;
        }

        private static float ParseFloat(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
#endif
