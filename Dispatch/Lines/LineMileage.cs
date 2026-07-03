using System;
using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Routes;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Dispatch.Workbench;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineMileage
    {
        private readonly LineMileagePort m_Port;
        private readonly Dictionary<Entity, LineMileageModel> m_Models = new Dictionary<Entity, LineMileageModel>();
        private readonly Dictionary<Entity, LineMileageFrameValidation> m_FrameValidations = new Dictionary<Entity, LineMileageFrameValidation>();
        private SharedLocalCorridorGraph m_Shared;
        private uint m_SharedSignatureFrame;
        private ulong m_SharedSignatureValue;
        private bool m_HasSharedSignatureFrame;
        private bool m_Faulted;

        private readonly struct LineMileageFrameValidation
        {
            public readonly uint Frame;
            public readonly int WaypointCount;
            public readonly LineMileageModel Model;

            public LineMileageFrameValidation(uint frame, int waypointCount, LineMileageModel model)
            {
                Frame = frame;
                WaypointCount = waypointCount;
                Model = model;
            }
        }

        public LineMileage(LineMileagePort port)
        {
            m_Port = port;
        }

        public void Clear()
        {
            m_Models.Clear();
            m_FrameValidations.Clear();
            m_Shared = null;
            m_SharedSignatureFrame = 0;
            m_SharedSignatureValue = 0;
            m_HasSharedSignatureFrame = false;
            m_Faulted = false;
        }

        public ulong Signature(Entity line, DynamicBuffer<RouteWaypoint> waypoints, DynamicBuffer<RouteSegment> segments)
        {
            ulong hash = m_Port.WaypointSignature(waypoints);
            hash = m_Port.MixSignature(hash, line.Index);
            hash = m_Port.MixSignature(hash, segments.Length);
            for (int i = 0; i < segments.Length; i++)
            {
                hash = m_Port.MixSignature(hash, segments[i].m_Segment.Index);
            }
            uint endpointSignature = RouteWaypointEndpointResolver.ComputeRouteEndpointSignature(m_Port.EntityManager, line, m_Port.ResolveStop);
            hash = m_Port.MixSignature(hash, (int)(endpointSignature & 0x7FFFFFFF));
            hash = m_Port.MixSignature(hash, (int)((endpointSignature >> 16) & 0x7FFFFFFF));
            return hash;
        }

        public bool Build(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineMileageModel model)
        {
            return Get(line, waypoints, out model);
        }

        public bool Get(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineMileageModel model)
        {
            model = null;
            if (m_Faulted)
                return false;

            try
            {
                if (line == Entity.Null
                    || waypoints.Length == 0
                    || !m_Port.EntityManager.HasBuffer<RouteSegment>(line))
                {
                    return false;
                }

                DynamicBuffer<RouteSegment> segments = m_Port.EntityManager.GetBuffer<RouteSegment>(line, true);
                if (segments.Length != waypoints.Length)
                    return false;

                uint frame = m_Port.Frame != null ? m_Port.Frame() : 0u;
                if (m_FrameValidations.TryGetValue(line, out LineMileageFrameValidation validation)
                    && validation.Frame == frame
                    && validation.WaypointCount == waypoints.Length
                    && validation.Model != null
                    && validation.Model.TotalDistanceMeters > 0f)
                {
                    model = validation.Model;
                    return true;
                }

                ulong signature = Signature(line, waypoints, segments);
                if (Shared(out SharedLocalCorridorGraph sharedGraph) && sharedGraph != null)
                {
                    signature = m_Port.MixSignature(signature, (int)(sharedGraph.Signature & 0x7FFFFFFF));
                    signature = m_Port.MixSignature(signature, (int)((sharedGraph.Signature >> 32) & 0x7FFFFFFF));
                }
                if (m_Models.TryGetValue(line, out model)
                    && model != null
                    && model.Signature == signature
                    && model.WaypointDistances.Length == waypoints.Length
                    && model.TotalDistanceMeters > 0f)
                {
                    m_FrameValidations[line] = new LineMileageFrameValidation(frame, waypoints.Length, model);
                    return true;
                }

                if (ReadBuf(line, signature, waypoints.Length, out model))
                {
                    m_Models[line] = model;
                    m_FrameValidations[line] = new LineMileageFrameValidation(frame, waypoints.Length, model);
                    return true;
                }

                model = Make(line, waypoints, segments, signature, sharedGraph);
                if (model == null || model.TotalDistanceMeters <= 0f)
                    return false;

                m_Models[line] = model;
                m_FrameValidations[line] = new LineMileageFrameValidation(frame, waypoints.Length, model);
                Log(line, model);
                WriteBuf(line, model);
                return true;
            }
            catch (Exception ex)
            {
                m_Faulted = true;
                m_Models.Clear();
                m_Shared = null;
                m_Port.Log("[走廊模型异常] 已停用走廊图建模: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        public bool Project(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineDistanceProjection projection)
        {
            projection = default;
            if (!Get(line, waypoints, out LineMileageModel model)
                || model.TotalDistanceMeters <= 0f
                || model.WaypointDistances.Length != waypoints.Length)
            {
                return false;
            }

            if (!m_Port.TryRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
            {
                int cachedWaypointIndex = m_Port.CachedWaypointIndex(vehicle);
                if (cachedWaypointIndex < 0
                    || cachedWaypointIndex >= waypoints.Length)
                {
                    return false;
                }

                nextWaypointIndex = cachedWaypointIndex;
                segmentPosition = 0f;
            }

            nextWaypointIndex = math.clamp(nextWaypointIndex, 0, waypoints.Length - 1);
            int previousWaypointIndex = nextWaypointIndex == 0 ? waypoints.Length - 1 : nextWaypointIndex - 1;
            float previousMeters = model.WaypointDistances[previousWaypointIndex];
            float nextMeters = model.WaypointDistances[nextWaypointIndex];
            float segmentMeters = nextWaypointIndex == 0
                ? math.max(1f, model.TotalDistanceMeters - previousMeters)
                : math.max(1f, nextMeters - previousMeters);

            float distanceMeters = previousMeters + segmentMeters * math.saturate(segmentPosition);

            projection = new LineDistanceProjection
            {
                TotalDistanceMeters = model.TotalDistanceMeters,
                DistanceMeters = math.clamp(distanceMeters, 0f, math.max(0f, model.TotalDistanceMeters - 0.01f)),
                Progress01 = math.saturate(distanceMeters / model.TotalDistanceMeters),
                NextWaypointIndex = nextWaypointIndex,
                SegmentPosition = math.saturate(segmentPosition)
            };
            return true;
        }

        public static float Forward(float totalDistanceMeters, float fromMeters, float toMeters)
        {
            if (totalDistanceMeters <= 0f)
                return float.MaxValue;

            float forward = toMeters - fromMeters;
            if (forward < 0f)
                forward += totalDistanceMeters;
            return forward;
        }

        public bool ReadBuf(Entity line, ulong signature, int waypointCount, out LineMileageModel model)
        {
            model = null;
            return false;
        }

        public void WriteBuf(Entity line, LineMileageModel model)
        {
        }

        public LineMileageModel Make(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            ulong signature,
            SharedLocalCorridorGraph sharedGraph)
        {
            int count = waypoints.Length;
            if (count == 0 || segments.Length != count)
                return null;

            if (!Waypoints(waypoints, segments, out Entity[] waypointBuildings, out _, out _))
                return null;

            float[] anchors = new float[count];
            List<CorridorNode> corridorNodes = new List<CorridorNode>(count * 2);
            Dictionary<Entity, float> buildingDistances = new Dictionary<Entity, float>();
            float cumulative = 0f;
            anchors[0] = 0f;
            if (waypointBuildings[0] != Entity.Null)
            {
                CorridorNode startNode = new CorridorNode
                {
                    Building = waypointBuildings[0],
                    DistanceMeters = 0f,
                    IsStopNode = true
                };
                corridorNodes.Add(startNode);
                buildingDistances[startNode.Building] = 0f;
            }

            for (int waypointIndex = 0; waypointIndex < count; waypointIndex++)
            {
                int nextWaypointIndex = (waypointIndex + 1) % count;
                Entity startBuilding = waypointBuildings[waypointIndex];
                Entity endBuilding = waypointBuildings[nextWaypointIndex];
                float fallbackDistance = ReadSegment(segments[waypointIndex].m_Segment, waypoints, waypointIndex);

                bool expanded = false;
                if (startBuilding != Entity.Null
                    && endBuilding != Entity.Null
                    && startBuilding != endBuilding
                    && sharedGraph != null
                    && Path(sharedGraph, startBuilding, endBuilding, out List<Entity> pathBuildings, out List<float> pathEdges)
                    && pathBuildings.Count >= 2)
                {
                    for (int pathIndex = 1; pathIndex < pathBuildings.Count; pathIndex++)
                    {
                        cumulative += pathEdges[pathIndex - 1];
                        bool isStopNode = pathIndex == pathBuildings.Count - 1;
                        CorridorNode node = new CorridorNode
                        {
                            Building = pathBuildings[pathIndex],
                            DistanceMeters = cumulative,
                            IsStopNode = isStopNode
                        };

                        if (nextWaypointIndex != 0 || pathIndex != pathBuildings.Count - 1)
                        {
                            if (corridorNodes.Count == 0 || corridorNodes[corridorNodes.Count - 1].Building != node.Building)
                                corridorNodes.Add(node);
                            else if (node.IsStopNode)
                            {
                                CorridorNode lastNode = corridorNodes[corridorNodes.Count - 1];
                                lastNode.IsStopNode = true;
                                lastNode.DistanceMeters = node.DistanceMeters;
                                corridorNodes[corridorNodes.Count - 1] = lastNode;
                            }

                            if (node.Building != Entity.Null && !buildingDistances.ContainsKey(node.Building))
                                buildingDistances[node.Building] = node.DistanceMeters;
                        }

                        if (isStopNode && nextWaypointIndex != 0)
                            anchors[nextWaypointIndex] = cumulative;
                    }

                    expanded = true;
                }

                if (!expanded)
                {
                    cumulative += fallbackDistance;
                    if (nextWaypointIndex != 0)
                        anchors[nextWaypointIndex] = cumulative;

                    if (endBuilding != Entity.Null && nextWaypointIndex != 0)
                    {
                        CorridorNode node = new CorridorNode
                        {
                            Building = endBuilding,
                            DistanceMeters = cumulative,
                            IsStopNode = true
                        };
                        if (corridorNodes.Count == 0 || corridorNodes[corridorNodes.Count - 1].Building != node.Building)
                            corridorNodes.Add(node);
                        if (!buildingDistances.ContainsKey(node.Building))
                            buildingDistances[node.Building] = node.DistanceMeters;
                    }
                }
            }

            cumulative = math.max(1f, cumulative);
            float[] bypassWaypointDistances = BypassMeters(waypoints, anchors);
            float[] bypassStopNodeDistances = BypassStops(corridorNodes);
            Approach(
                waypoints,
                buildingDistances,
                out int[] previousDistinctStationWaypointIndices,
                out float[] previousDistinctStationMeters,
                out float[] currentStationMeters);

            return new LineMileageModel
            {
                Signature = signature,
                TotalDistanceMeters = cumulative,
                WaypointDistances = anchors,
                BypassWaypointDistances = bypassWaypointDistances,
                BypassStopNodeDistances = bypassStopNodeDistances,
                PreviousDistinctStationWaypointIndices = previousDistinctStationWaypointIndices,
                PreviousDistinctStationMeters = previousDistinctStationMeters,
                CurrentStationMeters = currentStationMeters,
                CorridorNodes = corridorNodes,
                BuildingDistances = buildingDistances
            };
        }

        public float[] BypassMeters(DynamicBuffer<RouteWaypoint> waypoints, float[] waypointDistances)
        {
            if (waypoints.Length == 0 || waypointDistances == null || waypointDistances.Length != waypoints.Length)
                return Array.Empty<float>();

            List<float> distances = new List<float>(waypoints.Length);
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (m_Port.BypassBuildingForWaypoint(waypoints, i) == Entity.Null)
                    continue;

                distances.Add(waypointDistances[i]);
            }

            return distances.Count > 0 ? distances.ToArray() : Array.Empty<float>();
        }

        public float[] BypassStops(List<CorridorNode> corridorNodes)
        {
            if (corridorNodes == null || corridorNodes.Count == 0)
                return Array.Empty<float>();

            List<float> distances = new List<float>(corridorNodes.Count);
            for (int i = 0; i < corridorNodes.Count; i++)
            {
                CorridorNode node = corridorNodes[i];
                if (!node.IsStopNode || node.Building == Entity.Null || !m_Port.IsBypassStation(node.Building))
                    continue;

                distances.Add(node.DistanceMeters);
            }

            return distances.Count > 0 ? distances.ToArray() : Array.Empty<float>();
        }

        public void Approach(
            DynamicBuffer<RouteWaypoint> waypoints,
            Dictionary<Entity, float> buildingDistances,
            out int[] previousDistinctStationWaypointIndices,
            out float[] previousDistinctStationMeters,
            out float[] currentStationMeters)
        {
            int count = waypoints.Length;
            previousDistinctStationWaypointIndices = new int[count];
            previousDistinctStationMeters = new float[count];
            currentStationMeters = new float[count];

            for (int i = 0; i < count; i++)
            {
                previousDistinctStationWaypointIndices[i] = -1;
            }

            if (count == 0 || buildingDistances == null || buildingDistances.Count == 0)
                return;

            for (int waypointIndex = 0; waypointIndex < count; waypointIndex++)
            {
                Entity currentStationBuilding = m_Port.StationBuildingForWaypoint(waypoints, waypointIndex);
                if (currentStationBuilding == Entity.Null
                    || !buildingDistances.TryGetValue(currentStationBuilding, out float currentMeters))
                {
                    continue;
                }

                currentStationMeters[waypointIndex] = currentMeters;
                for (int offset = 1; offset < count; offset++)
                {
                    int candidateIndex = waypointIndex - offset;
                    if (candidateIndex < 0)
                        candidateIndex += count;

                    Entity candidateBuilding = m_Port.StationBuildingForWaypoint(waypoints, candidateIndex);
                    if (candidateBuilding == Entity.Null || candidateBuilding == currentStationBuilding)
                        continue;
                    if (!buildingDistances.TryGetValue(candidateBuilding, out float previousMeters))
                        break;

                    previousDistinctStationWaypointIndices[waypointIndex] = candidateIndex;
                    previousDistinctStationMeters[waypointIndex] = previousMeters;
                    break;
                }
            }
        }

        public bool Shared(out SharedLocalCorridorGraph graph)
        {
            ulong signature = SharedSignature();
            if (m_Shared != null
                && m_Shared.Signature == signature)
            {
                graph = m_Shared;
                return graph.Adjacency.Count > 0;
            }

            graph = SharedBuild(signature);
            m_Shared = graph;
            return graph != null && graph.Adjacency.Count > 0;
        }

        public SharedLocalCorridorGraph SharedBuild(ulong signature)
        {
            SharedLocalCorridorGraph graph = new SharedLocalCorridorGraph
            {
                Signature = signature
            };

            foreach (KeyValuePair<string, AppliedLine> entry in m_Port.AppliedLines())
            {
                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !m_Port.EntityManager.Exists(line)
                    || !m_Port.EntityManager.HasBuffer<RouteWaypoint>(line)
                    || !m_Port.EntityManager.HasBuffer<RouteSegment>(line)
                    || !m_Port.IsLocalLine(line))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = m_Port.EntityManager.GetBuffer<RouteWaypoint>(line, true);
                DynamicBuffer<RouteSegment> segments = m_Port.EntityManager.GetBuffer<RouteSegment>(line, true);
                if (!Waypoints(waypoints, segments, out Entity[] waypointBuildings, out float[] waypointDistances, out _))
                    continue;

                for (int i = 0; i < waypointBuildings.Length; i++)
                {
                    int nextIndex = (i + 1) % waypointBuildings.Length;
                    Entity startBuilding = waypointBuildings[i];
                    Entity endBuilding = waypointBuildings[nextIndex];
                    if (startBuilding == Entity.Null || endBuilding == Entity.Null || startBuilding == endBuilding)
                        continue;

                    float startDistance = waypointDistances[i];
                    float endDistance = nextIndex == 0 ? waypointDistances[i] + ReadSegment(segments[i].m_Segment, waypoints, i) : waypointDistances[nextIndex];
                    float segmentDistance = math.max(1f, endDistance - startDistance);
                    AddEdge(graph, startBuilding, endBuilding, segmentDistance);
                }
            }

            return graph;
        }

        public bool Path(
            SharedLocalCorridorGraph graph,
            Entity startBuilding,
            Entity endBuilding,
            out List<Entity> pathBuildings,
            out List<float> pathEdgeDistances)
        {
            pathBuildings = null;
            pathEdgeDistances = null;
            if (graph == null
                || startBuilding == Entity.Null
                || endBuilding == Entity.Null
                || startBuilding == endBuilding
                || !graph.Adjacency.ContainsKey(startBuilding))
            {
                return false;
            }

            Dictionary<Entity, float> distances = new Dictionary<Entity, float>();
            Dictionary<Entity, Entity> previous = new Dictionary<Entity, Entity>();
            Dictionary<Entity, float> previousEdgeDistance = new Dictionary<Entity, float>();
            List<Entity> open = new List<Entity> { startBuilding };
            distances[startBuilding] = 0f;

            while (open.Count > 0)
            {
                int bestIndex = 0;
                float bestDistance = distances[open[0]];
                for (int i = 1; i < open.Count; i++)
                {
                    float candidateDistance = distances[open[i]];
                    if (candidateDistance < bestDistance)
                    {
                        bestDistance = candidateDistance;
                        bestIndex = i;
                    }
                }

                Entity current = open[bestIndex];
                open.RemoveAt(bestIndex);
                if (current == endBuilding)
                    break;

                if (!graph.Adjacency.TryGetValue(current, out List<SharedLocalCorridorEdge> edges))
                    continue;

                for (int i = 0; i < edges.Count; i++)
                {
                    SharedLocalCorridorEdge edge = edges[i];
                    float nextDistance = bestDistance + edge.DistanceMeters;
                    if (distances.TryGetValue(edge.ToBuilding, out float knownDistance) && knownDistance <= nextDistance)
                        continue;

                    distances[edge.ToBuilding] = nextDistance;
                    previous[edge.ToBuilding] = current;
                    previousEdgeDistance[edge.ToBuilding] = edge.DistanceMeters;
                    if (!open.Contains(edge.ToBuilding))
                        open.Add(edge.ToBuilding);
                }
            }

            if (!distances.ContainsKey(endBuilding))
                return false;

            pathBuildings = new List<Entity>();
            pathEdgeDistances = new List<float>();
            Entity cursor = endBuilding;
            while (cursor != startBuilding)
            {
                pathBuildings.Add(cursor);
                if (!previous.TryGetValue(cursor, out Entity prev))
                    return false;
                pathEdgeDistances.Add(previousEdgeDistance[cursor]);
                cursor = prev;
            }
            pathBuildings.Add(startBuilding);
            pathBuildings.Reverse();
            pathEdgeDistances.Reverse();
            return true;
        }

        public bool Waypoints(
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            out Entity[] waypointBuildings,
            out float[] waypointDistances,
            out float totalDistanceMeters)
        {
            int count = waypoints.Length;
            waypointBuildings = Array.Empty<Entity>();
            waypointDistances = Array.Empty<float>();
            totalDistanceMeters = 0f;
            if (count == 0 || segments.Length != count)
                return false;

            waypointBuildings = new Entity[count];
            waypointDistances = new float[count];
            float cumulative = 0f;
            waypointDistances[0] = 0f;
            waypointBuildings[0] = m_Port.StationBuildingForWaypoint(waypoints, 0);
            for (int waypointIndex = 1; waypointIndex < count; waypointIndex++)
            {
                cumulative += ReadSegment(segments[waypointIndex - 1].m_Segment, waypoints, waypointIndex - 1);
                waypointDistances[waypointIndex] = cumulative;
                waypointBuildings[waypointIndex] = m_Port.StationBuildingForWaypoint(waypoints, waypointIndex);
            }

            cumulative += ReadSegment(segments[count - 1].m_Segment, waypoints, count - 1);
            totalDistanceMeters = math.max(1f, cumulative);
            return true;
        }

        public float ReadSegment(Entity segmentEntity, DynamicBuffer<RouteWaypoint> waypoints, int segmentIndex)
        {
            if (segmentEntity != Entity.Null
                && m_Port.EntityManager.HasComponent<PathInformation>(segmentEntity))
            {
                float distance = m_Port.EntityManager.GetComponentData<PathInformation>(segmentEntity).m_Distance;
                if (distance > 1f)
                    return distance;
            }

            if (waypoints.Length == 0)
                return 1f;

            int count = waypoints.Length;
            int startWaypointIndex = segmentIndex;
            int endWaypointIndex = (segmentIndex + 1) % count;
            Entity startWaypoint = waypoints[startWaypointIndex].m_Waypoint;
            Entity endWaypoint = waypoints[endWaypointIndex].m_Waypoint;

            if (m_Port.EntityManager.HasComponent<Position>(startWaypoint) && m_Port.EntityManager.HasComponent<Position>(endWaypoint))
            {
                float distance = math.distance(
                    m_Port.EntityManager.GetComponentData<Position>(startWaypoint).m_Position,
                    m_Port.EntityManager.GetComponentData<Position>(endWaypoint).m_Position);
                if (distance > 1f)
                    return distance;
            }

            return 1f;
        }

        private ulong SharedSignature()
        {
            uint frame = m_Port.Frame != null ? m_Port.Frame() : 0u;
            if (m_HasSharedSignatureFrame && m_SharedSignatureFrame == frame)
                return m_SharedSignatureValue;

            ulong hash = 1469598103934665603UL;
            List<Entity> localLines = new List<Entity>();
            foreach (KeyValuePair<string, AppliedLine> entry in m_Port.AppliedLines())
            {
                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !m_Port.EntityManager.Exists(line)
                    || !m_Port.EntityManager.HasBuffer<RouteWaypoint>(line)
                    || !m_Port.EntityManager.HasBuffer<RouteSegment>(line)
                    || !m_Port.IsLocalLine(line))
                {
                    continue;
                }

                if (!localLines.Contains(line))
                    localLines.Add(line);
            }

            localLines.Sort((a, b) => a.Index.CompareTo(b.Index));
            hash = m_Port.MixSignature(hash, localLines.Count);
            for (int i = 0; i < localLines.Count; i++)
            {
                Entity line = localLines[i];
                hash = m_Port.MixSignature(hash, line.Index);
                DynamicBuffer<RouteWaypoint> waypoints = m_Port.EntityManager.GetBuffer<RouteWaypoint>(line, true);
                for (int j = 0; j < waypoints.Length; j++)
                {
                    Entity building = m_Port.StationBuildingForWaypoint(waypoints, j);
                    hash = m_Port.MixSignature(hash, building.Index);
                }
            }

            m_SharedSignatureFrame = frame;
            m_SharedSignatureValue = hash;
            m_HasSharedSignatureFrame = true;
            return hash;
        }

        private static void AddEdge(SharedLocalCorridorGraph graph, Entity fromBuilding, Entity toBuilding, float distanceMeters)
        {
            if (fromBuilding == Entity.Null || toBuilding == Entity.Null || distanceMeters <= 0f)
                return;

            if (!graph.Adjacency.TryGetValue(fromBuilding, out List<SharedLocalCorridorEdge> edges))
            {
                edges = new List<SharedLocalCorridorEdge>();
                graph.Adjacency[fromBuilding] = edges;
            }

            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].ToBuilding != toBuilding)
                    continue;

                if (distanceMeters < edges[i].DistanceMeters)
                    edges[i] = new SharedLocalCorridorEdge { ToBuilding = toBuilding, DistanceMeters = distanceMeters };
                return;
            }

            edges.Add(new SharedLocalCorridorEdge
            {
                ToBuilding = toBuilding,
                DistanceMeters = distanceMeters
            });
        }

        private void Log(Entity line, LineMileageModel model)
        {
            if (line == Entity.Null || model == null || model.CorridorNodes.Count == 0)
                return;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[走廊模型] 线路").Append(line.Index)
              .Append(" 总长=").Append((model.TotalDistanceMeters / 1000f).ToString("F2")).Append("km")
              .Append(" 节点=");

            for (int i = 0; i < model.CorridorNodes.Count; i++)
            {
                CorridorNode node = model.CorridorNodes[i];
                if (i > 0)
                    sb.Append(" -> ");

                string label = "建筑" + node.Building.Index;
                if (node.Building != Entity.Null)
                {
                    try
                    {
                        string name = m_Port.Name(node.Building);
                        if (!string.IsNullOrWhiteSpace(name))
                            label = name;
                    }
                    catch
                    {
                    }
                }

                sb.Append(label)
                  .Append("@")
                  .Append((node.DistanceMeters / 1000f).ToString("F2"))
                  .Append("km");

                if (node.IsStopNode)
                    sb.Append("[停]");
            }

            m_Port.Log(sb.ToString());
        }
    }

    internal sealed class LineMileageModel
    {
        public ulong Signature;
        public float TotalDistanceMeters;
        public float[] WaypointDistances = Array.Empty<float>();
        public float[] BypassWaypointDistances = Array.Empty<float>();
        public float[] BypassStopNodeDistances = Array.Empty<float>();
        public int[] PreviousDistinctStationWaypointIndices = Array.Empty<int>();
        public float[] PreviousDistinctStationMeters = Array.Empty<float>();
        public float[] CurrentStationMeters = Array.Empty<float>();
        public List<CorridorNode> CorridorNodes = new List<CorridorNode>();
        public Dictionary<Entity, float> BuildingDistances = new Dictionary<Entity, float>();
    }

    internal struct CorridorNode
    {
        public Entity Building;
        public float DistanceMeters;
        public bool IsStopNode;
    }

    internal sealed class SharedLocalCorridorGraph
    {
        public ulong Signature;
        public Dictionary<Entity, List<SharedLocalCorridorEdge>> Adjacency = new Dictionary<Entity, List<SharedLocalCorridorEdge>>();
    }

    internal struct SharedLocalCorridorEdge
    {
        public Entity ToBuilding;
        public float DistanceMeters;
    }

    internal struct LineDistanceProjection
    {
        public float TotalDistanceMeters;
        public float DistanceMeters;
        public float Progress01;
        public int NextWaypointIndex;
        public float SegmentPosition;
    }
}
