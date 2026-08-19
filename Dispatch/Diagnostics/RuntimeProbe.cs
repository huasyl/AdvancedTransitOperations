#if RT_DEBUG_TOOLS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RapidTransitMod.Dispatch.Observation;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace RapidTransitMod.Dispatch.Diagnostics
{
    // 仅调试构建启用：文件请求只读取当前运行时与 ECS，不参与调度阶段。
    internal sealed class RuntimeProbe
    {
        private const uint PollIntervalFrames = 15;
        private const int DefaultLimit = 50;
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly string m_RequestDir;
        private readonly string m_ResponseDir;
        private uint m_NextPollFrame;
        private bool m_FileErrorLogged;

        internal RuntimeProbe(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
            string root = Path.Combine(Application.persistentDataPath, "RapidTransitMod", "Probe");
            m_RequestDir = Path.Combine(root, "requests");
            m_ResponseDir = Path.Combine(root, "responses");
            Directory.CreateDirectory(m_RequestDir);
            Directory.CreateDirectory(m_ResponseDir);
        }

        internal void Tick(uint frame)
        {
            if (frame < m_NextPollFrame)
                return;

            m_NextPollFrame = frame + PollIntervalFrames;
            try
            {
                string[] requests = Directory.GetFiles(m_RequestDir, "*.json");
                if (requests.Length == 0)
                    return;

                Array.Sort(requests, StringComparer.Ordinal);
                Process(requests[0]);
            }
            catch (Exception ex)
            {
                if (m_FileErrorLogged)
                    return;

                m_FileErrorLogged = true;
                m_Runtime.log.Info("[RuntimeProbe] 文件通信失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Process(string requestPath)
        {
            string requestId = Path.GetFileNameWithoutExtension(requestPath);
            JObject request = null;
            JObject response;
            try
            {
                request = JObject.Parse(File.ReadAllText(requestPath, Encoding.UTF8));
                JToken result = Execute(request);
                response = Success(request.Value<string>("id") ?? requestId, result);
            }
            catch (Exception ex)
            {
                response = Failure(request?.Value<string>("id") ?? requestId, Describe(ex));
            }

            string responsePath = Path.Combine(m_ResponseDir, requestId + ".json");
            Write(responsePath, response);
            File.Delete(requestPath);
        }

        private JToken Execute(JObject request)
        {
            string command = request.Value<string>("command");
            if (string.IsNullOrWhiteSpace(command))
                throw new ProbeError("缺少 command。");

            switch (command)
            {
                case "ping":
                    return Ping();
                case "mod.slices":
                    return ReadSlices(request);
                case "mod.read":
                    return ReadObject(request);
                case "mod.systems":
                    return ListSystems();
                case "entity.components":
                    return ListComponents(ReadEntity(request));
                case "entity.component":
                    return ReadComponent(request, false);
                case "entity.buffer":
                    return ReadComponent(request, true);
                default:
                    throw new ProbeError("未知 command: " + command);
            }
        }

        private JObject Ping()
        {
            return new JObject
            {
                ["probe"] = "RuntimeProbe",
                ["debugTools"] = BuildFlavor.DebugTools,
                ["frame"] = m_Runtime.m_SimulationSystem.frameIndex,
                ["runtimeReady"] = m_Runtime.m_SystemReady
            };
        }

        private JObject ReadSlices(JObject request)
        {
            ReadPage(request, out int offset, out int limit);
            SliceStore slices = m_Runtime.m_Slices;
            return new JObject
            {
                ["observations"] = ObservationPage(slices.Observations, offset, limit),
                ["sessions"] = SessionPage(slices.Sessions, offset, limit),
                ["lastSampleFrames"] = EntityFramePage(slices.LastSampleFrames, offset, limit),
                ["lastPositionSampleFrames"] = EntityFramePage(slices.LastPositionSampleFrames, offset, limit),
                ["nextSampleFrames"] = EntityFramePage(slices.NextSampleFrames, offset, limit),
                ["plans"] = PlanPage(slices.Plans, offset, limit),
                ["lineEligibility"] = EligibilityPage(slices.LineEligibility, offset, limit),
                ["nextEntryProbeFrames"] = EntityFramePage(slices.NextEntryProbeFrames, offset, limit),
                ["lapDebug"] = LapDebugPage(slices.LapDebug, offset, limit),
                ["actualSamples"] = ListPage(slices.RecentActualSamples, offset, limit, ActualNode),
                ["positionSamples"] = ListPage(slices.RecentPositionSamples, offset, limit, PositionNode)
            };
        }

        private JObject ListComponents(Entity entity)
        {
            NativeArray<ComponentType> types = m_Runtime.EntityManager.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                JArray items = new JArray();
                for (int i = 0; i < types.Length; i++)
                    items.Add(ComponentNode(types[i]));

                return new JObject
                {
                    ["entity"] = EntityNode(entity),
                    ["items"] = items
                };
            }
            finally
            {
                types.Dispose();
            }
        }

        private JObject ReadComponent(JObject request, bool buffer)
        {
            Entity entity = ReadEntity(request);
            string name = request.Value<string>("type");
            if (string.IsNullOrWhiteSpace(name))
                throw new ProbeError("缺少 type。");

            Type type = FindComponentType(entity, name, out ComponentType componentType);
            if (buffer)
            {
                if (!componentType.IsBuffer || !typeof(IBufferElementData).IsAssignableFrom(type))
                    throw new ProbeError("指定类型不是动态缓冲区元素: " + name);

                ReadPage(request, out int offset, out int limit);
                return BufferNode(entity, type, offset, limit);
            }

            if (componentType.IsBuffer)
                throw new ProbeError("指定类型是动态缓冲区，请使用 entity.buffer。");
            if (componentType.IsSharedComponent || componentType.IsManagedComponent || type.IsClass)
                throw new ProbeError("托管或共享组件暂不支持读取: " + name);
            if (!typeof(IComponentData).IsAssignableFrom(type))
                throw new ProbeError("指定类型不是普通 IComponentData: " + name);

            object value = GetComponent(entity, type);
            return new JObject
            {
                ["entity"] = EntityNode(entity),
                ["type"] = type.FullName,
                ["value"] = ValueNode(value, type, 0)
            };
        }

        private Entity ReadEntity(JObject request)
        {
            JObject node = request["entity"] as JObject;
            if (node == null)
                throw new ProbeError("缺少 entity。");

            int? index = node.Value<int?>("index");
            int version = node.Value<int?>("version") ?? 1;
            if (!index.HasValue || index.Value < 0 || version < 0)
                throw new ProbeError("entity.index 或 entity.version 无效。");

            Entity entity = new Entity { Index = index.Value, Version = version };
            if (!m_Runtime.EntityManager.Exists(entity))
                throw new ProbeError("实体不存在: " + entity.Index + ":" + entity.Version);

            return entity;
        }

        private Type FindComponentType(Entity entity, string name, out ComponentType found)
        {
            NativeArray<ComponentType> types = m_Runtime.EntityManager.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i].GetManagedType();
                    if (type != null && string.Equals(type.FullName, name, StringComparison.Ordinal))
                    {
                        found = types[i];
                        return type;
                    }
                }
            }
            finally
            {
                types.Dispose();
            }

            throw new ProbeError("实体不含指定类型: " + name);
        }

        private object GetComponent(Entity entity, Type type)
        {
            MethodInfo method = typeof(EntityManager).GetMethod(
                "GetComponentData",
                new[] { typeof(Entity) });
            if (method == null)
                throw new ProbeError("未找到 ECS 普通组件读取接口。");

            try
            {
                return method.MakeGenericMethod(type).Invoke(m_Runtime.EntityManager, new object[] { entity });
            }
            catch (TargetInvocationException ex)
            {
                throw new ProbeError("读取组件失败: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private JObject BufferNode(Entity entity, Type type, int offset, int limit)
        {
            MethodInfo method = typeof(EntityManager).GetMethod(
                "GetBuffer",
                new[] { typeof(Entity), typeof(bool) });
            if (method == null)
                throw new ProbeError("未找到 ECS 动态缓冲区读取接口。");

            object buffer;
            try
            {
                buffer = method.MakeGenericMethod(type).Invoke(
                    m_Runtime.EntityManager,
                    new object[] { entity, true });
            }
            catch (TargetInvocationException ex)
            {
                throw new ProbeError("读取动态缓冲区失败: " + (ex.InnerException?.Message ?? ex.Message));
            }

            Type bufferType = buffer.GetType();
            int total = (int)bufferType.GetProperty("Length").GetValue(buffer, null);
            int start = Math.Min(offset, total);
            int end = Math.Min(start + limit, total);
            PropertyInfo item = bufferType.GetProperty("Item");
            JArray items = new JArray();
            for (int i = start; i < end; i++)
            {
                object value = item.GetValue(buffer, new object[] { i });
                items.Add(ValueNode(value, type, 0));
            }

            return new JObject
            {
                ["entity"] = EntityNode(entity),
                ["type"] = type.FullName,
                ["total"] = total,
                ["offset"] = start,
                ["limit"] = limit,
                ["items"] = items
            };
        }

        private static JObject ObservationPage(
            Dictionary<ulong, TraversalSliceObservation> values,
            int offset,
            int limit)
        {
            JArray items = new JArray();
            int index = 0;
            foreach (KeyValuePair<ulong, TraversalSliceObservation> pair in values)
            {
                if (index >= offset && items.Count < limit)
                {
                    TraversalSliceObservation value = pair.Value;
                    items.Add(new JObject
                    {
                        ["key"] = pair.Key.ToString(),
                        ["lineIndex"] = unchecked((int)(uint)(pair.Key >> 32)),
                        ["sliceIndex"] = unchecked((int)(uint)pair.Key),
                        ["averageFrames"] = value.AverageFrames,
                        ["fastBaselineFrames"] = value.FastBaselineFrames,
                        ["sampleCount"] = value.SampleCount,
                        ["lastObservedFrame"] = value.LastObservedFrame
                    });
                }
                index++;
            }

            return Page(values.Count, offset, limit, items);
        }

        private static JObject EntityFramePage(
            Dictionary<Entity, uint> values,
            int offset,
            int limit)
        {
            JArray items = new JArray();
            int index = 0;
            foreach (KeyValuePair<Entity, uint> pair in values)
            {
                if (index >= offset && items.Count < limit)
                {
                    items.Add(new JObject
                    {
                        ["entity"] = EntityNode(pair.Key),
                        ["frame"] = pair.Value
                    });
                }
                index++;
            }

            return Page(values.Count, offset, limit, items);
        }

        private static JObject PlanPage(
            Dictionary<Entity, TraversalSliceSamplingPlanCache> values,
            int offset,
            int limit)
        {
            JArray items = new JArray();
            int index = 0;
            foreach (KeyValuePair<Entity, TraversalSliceSamplingPlanCache> pair in values)
            {
                if (index >= offset && items.Count < limit)
                {
                    TraversalSliceSamplingPlanCache value = pair.Value;
                    TraversalSliceSamplingPlan plan = value.Plan;
                    items.Add(new JObject
                    {
                        ["vehicle"] = EntityNode(pair.Key),
                        ["line"] = EntityNode(value.Line),
                        ["chainSignature"] = value.ChainSignature.ToString(),
                        ["sliceIndex"] = value.SliceIndex,
                        ["nextRefreshFrame"] = value.NextRefreshFrame,
                        ["available"] = plan.Available,
                        ["segmentIndex"] = plan.SegmentIndex,
                        ["segmentPosition"] = plan.SegmentPosition,
                        ["sampleIntervalFrames"] = plan.SampleIntervalFrames,
                        ["isHighSampling"] = plan.IsHighSampling,
                        ["isMediumSampling"] = plan.IsMediumSampling,
                        ["hasUpcomingCutPoint"] = plan.HasUpcomingCutPoint,
                        ["upcomingCutPointProgress"] = plan.UpcomingCutPointProgress,
                        ["upcomingCutPointDistance"] = plan.UpcomingCutPointDistance
                    });
                }
                index++;
            }

            return Page(values.Count, offset, limit, items);
        }

        private static JObject EligibilityPage(
            Dictionary<Entity, TraversalSliceLineEligibilityCache> values,
            int offset,
            int limit)
        {
            JArray items = new JArray();
            int index = 0;
            foreach (KeyValuePair<Entity, TraversalSliceLineEligibilityCache> pair in values)
            {
                if (index >= offset && items.Count < limit)
                {
                    TraversalSliceLineEligibilityCache value = pair.Value;
                    items.Add(new JObject
                    {
                        ["line"] = EntityNode(pair.Key),
                        ["cachedLine"] = EntityNode(value.Line),
                        ["chainSignature"] = value.ChainSignature.ToString(),
                        ["eligible"] = value.Eligible,
                        ["nextRefreshFrame"] = value.NextRefreshFrame
                    });
                }
                index++;
            }

            return Page(values.Count, offset, limit, items);
        }

        private static JObject LapDebugPage(
            Dictionary<ulong, TraversalSliceLapDebugAggregate> values,
            int offset,
            int limit)
        {
            JArray items = new JArray();
            int index = 0;
            foreach (KeyValuePair<ulong, TraversalSliceLapDebugAggregate> pair in values)
            {
                if (index >= offset && items.Count < limit)
                {
                    TraversalSliceLapDebugAggregate value = pair.Value;
                    items.Add(new JObject
                    {
                        ["key"] = pair.Key.ToString(),
                        ["vehicleIndex"] = unchecked((int)(uint)(pair.Key >> 32)),
                        ["sliceIndex"] = unchecked((int)(uint)pair.Key),
                        ["startCount"] = value.StartCount,
                        ["finalizeCount"] = value.FinalizeCount,
                        ["midSliceStartCount"] = value.MidSliceStartCount,
                        ["droppedWithoutFinalizeCount"] = value.DroppedWithoutFinalizeCount,
                        ["enterOffsetSumAtoms"] = value.EnterOffsetSumAtoms,
                        ["maxEnterOffsetAtoms"] = value.MaxEnterOffsetAtoms,
                        ["observedFramesSum"] = value.ObservedFramesSum,
                        ["minObservedFrames"] = value.MinObservedFrames,
                        ["maxObservedFrames"] = value.MaxObservedFrames
                    });
                }
                index++;
            }

            return Page(values.Count, offset, limit, items);
        }

        private static JObject SessionPage(
            Dictionary<Entity, VehicleTraversalSliceSession> values,
            int offset,
            int limit)
        {
            JArray items = new JArray();
            int index = 0;
            foreach (KeyValuePair<Entity, VehicleTraversalSliceSession> pair in values)
            {
                if (index >= offset && items.Count < limit)
                {
                    VehicleTraversalSliceSession value = pair.Value;
                    items.Add(new JObject
                    {
                        ["vehicle"] = EntityNode(pair.Key),
                        ["line"] = EntityNode(value.Line),
                        ["sliceIndex"] = value.SliceIndex,
                        ["enterFrame"] = value.EnterFrame,
                        ["enterAtomIndex"] = value.EnterAtomIndex,
                        ["enterAtomPosition01"] = value.EnterAtomPosition01
                    });
                }
                index++;
            }

            return Page(values.Count, offset, limit, items);
        }

        private static JObject ListPage<T>(
            IReadOnlyList<T> values,
            int offset,
            int limit,
            Func<T, JObject> node)
        {
            int start = Math.Min(offset, values.Count);
            int end = Math.Min(start + limit, values.Count);
            JArray items = new JArray();
            for (int i = start; i < end; i++)
                items.Add(node(values[i]));

            return Page(values.Count, start, limit, items);
        }

        private static JObject ActualNode(TraversalSliceActualSample value)
        {
            return new JObject
            {
                ["line"] = EntityNode(value.Line),
                ["vehicle"] = EntityNode(value.Vehicle),
                ["sliceIndex"] = value.SliceIndex,
                ["enterFrame"] = value.EnterFrame,
                ["exitFrame"] = value.ExitFrame,
                ["enterAtomIndex"] = value.EnterAtomIndex,
                ["enterAtomPosition01"] = value.EnterAtomPosition01,
                ["exitAtomIndex"] = value.ExitAtomIndex,
                ["exitAtomPosition01"] = value.ExitAtomPosition01
            };
        }

        private static JObject PositionNode(TraversalPositionSample value)
        {
            return new JObject
            {
                ["line"] = EntityNode(value.Line),
                ["vehicle"] = EntityNode(value.Vehicle),
                ["frame"] = value.Frame,
                ["sliceIndex"] = value.SliceIndex,
                ["segmentIndex"] = value.SegmentIndex,
                ["segmentPosition"] = value.SegmentPosition,
                ["atomIndex"] = value.AtomIndex,
                ["atomPosition01"] = value.AtomPosition01,
                ["physicalLane"] = EntityNode(value.PhysicalLane),
                ["speedMetersPerSecond"] = value.SpeedMetersPerSecond,
                ["odometerMeters"] = value.OdometerMeters
            };
        }

        private static JObject ComponentNode(ComponentType value)
        {
            Type type = value.GetManagedType();
            return new JObject
            {
                ["type"] = type?.FullName ?? value.ToString(),
                ["kind"] = value.IsBuffer
                    ? "buffer"
                    : value.IsSharedComponent
                        ? "shared"
                        : value.IsManagedComponent
                            ? "managed"
                            : "component",
                ["zeroSized"] = value.IsZeroSized
            };
        }

        private JToken ReadObject(JObject request)
        {
            ReadPage(request, out int offset, out int limit);
            int maxDepth = request.Value<int?>("depth") ?? 4;
            if (maxDepth < 1)
                throw new ProbeError("depth 必须大于 0。");

            string root = request.Value<string>("root");
            string path = request.Value<string>("path") ?? string.Empty;
            ReadView view = new ReadView(
                offset,
                limit,
                maxDepth,
                request.Value<string>("key"),
                request.Value<string>("prefix"));
            object value;
            Type type;
            switch (root)
            {
                case "runtime":
                    value = ResolvePath(m_Runtime, m_Runtime.GetType(), path, false, out type);
                    break;
                case "system":
                    value = FindSystem(request.Value<string>("type"));
                    value = ResolvePath(value, value.GetType(), path, false, out type);
                    break;
                case "static":
                    Type staticType = FindModType(request.Value<string>("type"));
                    value = ResolvePath(null, staticType, path, true, out type);
                    break;
                default:
                    throw new ProbeError("root 必须是 runtime、system 或 static。");
            }

            return new JObject
            {
                ["root"] = root,
                ["type"] = type?.FullName ?? string.Empty,
                ["path"] = path,
                ["value"] = ValueNode(value, type, 0, view)
            };
        }

        private JObject ListSystems()
        {
            JArray items = new JArray();
            Assembly modAssembly = typeof(ModRuntimeHostSystem).Assembly;
            foreach (ComponentSystemBase system in m_Runtime.World.Systems)
            {
                if (system == null || system.GetType().Assembly != modAssembly)
                    continue;

                items.Add(new JObject
                {
                    ["type"] = system.GetType().FullName,
                    ["enabled"] = system.Enabled
                });
            }

            return new JObject
            {
                ["total"] = items.Count,
                ["items"] = items
            };
        }

        private object FindSystem(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ProbeError("读取 system 时缺少 type。");

            ComponentSystemBase match = null;
            foreach (ComponentSystemBase system in m_Runtime.World.Systems)
            {
                if (system == null || system.GetType().Assembly != typeof(ModRuntimeHostSystem).Assembly)
                    continue;

                Type type = system.GetType();
                if (string.Equals(type.FullName, name, StringComparison.Ordinal))
                    return system;
                if (string.Equals(type.Name, name, StringComparison.Ordinal))
                {
                    if (match != null)
                        throw new ProbeError("系统短名称不唯一，请使用完整类型名: " + name);
                    match = system;
                }
            }

            return match ?? throw new ProbeError("当前 World 中没有该模组系统: " + name);
        }

        private static Type FindModType(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ProbeError("读取 static 时缺少 type。");

            Assembly assembly = typeof(ModRuntimeHostSystem).Assembly;
            Type exact = assembly.GetType(name, false, false);
            if (exact != null)
                return exact;

            Type match = null;
            Type[] types = assembly.GetTypes();
            for (int i = 0; i < types.Length; i++)
            {
                if (!string.Equals(types[i].Name, name, StringComparison.Ordinal))
                    continue;
                if (match != null)
                    throw new ProbeError("类型短名称不唯一，请使用完整类型名: " + name);
                match = types[i];
            }

            return match ?? throw new ProbeError("模组中没有该类型: " + name);
        }

        private static object ResolvePath(
            object value,
            Type type,
            string path,
            bool firstStatic,
            out Type resultType)
        {
            resultType = type;
            if (string.IsNullOrWhiteSpace(path))
            {
                if (firstStatic)
                    throw new ProbeError("读取 static 时缺少 path。");
                return value;
            }

            string[] parts = path.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(parts[i]))
                    throw new ProbeError("path 包含空字段。");

                bool useStatic = firstStatic && i == 0;
                FieldInfo field = FindField(resultType, parts[i], useStatic);
                if (field != null)
                {
                    value = field.GetValue(useStatic ? null : value);
                    resultType = field.FieldType;
                }
                else
                {
                    PropertyInfo property = FindProperty(resultType, parts[i], useStatic);
                    if (property == null || property.GetIndexParameters().Length != 0)
                        throw new ProbeError("找不到字段或属性: " + parts[i]);
                    value = property.GetValue(useStatic ? null : value, null);
                    resultType = property.PropertyType;
                }

                if (value == null && i + 1 < parts.Length)
                    throw new ProbeError("字段为空，无法继续读取: " + parts[i]);
            }

            return value;
        }

        private static FieldInfo FindField(Type type, string name, bool isStatic)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                | BindingFlags.DeclaredOnly;
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, flags);
                if (field != null)
                    return field;
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name, bool isStatic)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                | BindingFlags.DeclaredOnly;
            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty(name, flags);
                if (property != null)
                    return property;
            }
            return null;
        }

        private static JObject EntityNode(Entity value)
        {
            return new JObject
            {
                ["index"] = value.Index,
                ["version"] = value.Version
            };
        }

        private static JToken ValueNode(object value, Type type, int depth)
        {
            return ValueNode(value, type, depth, new ReadView(0, DefaultLimit, 4, null, null));
        }

        private static JToken ValueNode(object value, Type type, int depth, ReadView view)
        {
            if (value == null)
                return JValue.CreateNull();
            type = value.GetType();
            if (type == typeof(Entity))
                return EntityNode((Entity)value);
            if (type.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(type);
                bool unsigned = underlying == typeof(byte)
                    || underlying == typeof(ushort)
                    || underlying == typeof(uint)
                    || underlying == typeof(ulong);
                return new JObject
                {
                    ["name"] = value.ToString(),
                    ["value"] = unsigned
                        ? new JValue(Convert.ToUInt64(value))
                        : new JValue(Convert.ToInt64(value))
                };
            }
            if (type == typeof(string) || type == typeof(char))
                return new JValue(value.ToString());
            if (type.IsPrimitive || type == typeof(decimal))
                return JToken.FromObject(value);
            if (type == typeof(DateTime)
                || type == typeof(TimeSpan)
                || type == typeof(Guid)
                || type == typeof(Type)
                || type == typeof(IntPtr)
                || type == typeof(UIntPtr))
            {
                return new JValue(value.ToString());
            }
            if (type.Namespace == "Unity.Collections"
                && type.Name.StartsWith("FixedString", StringComparison.Ordinal))
            {
                return new JValue(value.ToString());
            }
            if (value is IDictionary dictionary)
                return DictionaryNode(dictionary, depth, view);
            if (value is IEnumerable enumerable && !(value is string))
                return EnumerableNode(enumerable, depth, view);
            if (depth >= view.MaxDepth)
                return new JValue(value.ToString());
            if (type.Namespace == "Unity.Collections" || typeof(Delegate).IsAssignableFrom(type))
                return new JValue(value.ToString());

            List<FieldInfo> fields = Fields(type);
            if (fields.Count == 0)
                return new JValue(value.ToString());

            JObject node = new JObject();
            for (int i = 0; i < fields.Count; i++)
            {
                FieldInfo field = fields[i];
                node[field.Name] = ValueNode(field.GetValue(value), field.FieldType, depth + 1, view);
            }
            return node;
        }

        private static JObject DictionaryNode(IDictionary values, int depth, ReadView view)
        {
            JArray items = new JArray();
            int matched = 0;
            foreach (DictionaryEntry entry in values)
            {
                string keyText = entry.Key?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(view.Key)
                    && !string.Equals(keyText, view.Key, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(view.Prefix)
                    && !keyText.StartsWith(view.Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (matched >= view.Offset && items.Count < view.Limit)
                {
                    items.Add(new JObject
                    {
                        ["key"] = ValueNode(entry.Key, entry.Key?.GetType(), depth + 1, view),
                        ["value"] = ValueNode(entry.Value, entry.Value?.GetType(), depth + 1, view)
                    });
                }
                matched++;
            }

            return Page(matched, view.Offset, view.Limit, items);
        }

        private static JObject EnumerableNode(IEnumerable values, int depth, ReadView view)
        {
            JArray items = new JArray();
            int count = 0;
            foreach (object item in values)
            {
                if (count >= view.Offset && items.Count < view.Limit)
                    items.Add(ValueNode(item, item?.GetType(), depth + 1, view));
                count++;
            }

            return Page(count, view.Offset, view.Limit, items);
        }

        private static List<FieldInfo> Fields(Type type)
        {
            List<FieldInfo> fields = new List<FieldInfo>();
            BindingFlags flags = BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly;
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo[] declared = current.GetFields(flags);
                for (int i = 0; i < declared.Length; i++)
                {
                    if (!declared[i].IsStatic)
                        fields.Add(declared[i]);
                }
            }
            return fields;
        }

        private static JObject Page(int total, int offset, int limit, JArray items)
        {
            return new JObject
            {
                ["total"] = total,
                ["offset"] = Math.Min(offset, total),
                ["limit"] = limit,
                ["items"] = items
            };
        }

        private static void ReadPage(JObject request, out int offset, out int limit)
        {
            offset = request.Value<int?>("offset") ?? 0;
            limit = request.Value<int?>("limit") ?? DefaultLimit;
            if (offset < 0 || limit < 1)
                throw new ProbeError("offset 必须不小于 0，limit 必须大于 0。");
        }

        private static JObject Success(string id, JToken result)
        {
            return new JObject
            {
                ["id"] = id,
                ["ok"] = true,
                ["result"] = result
            };
        }

        private static JObject Failure(string id, string message)
        {
            return new JObject
            {
                ["id"] = id,
                ["ok"] = false,
                ["error"] = new JObject { ["message"] = message }
            };
        }

        private static string Describe(Exception ex)
        {
            return ex is ProbeError ? ex.Message : ex.GetType().Name + ": " + ex.Message;
        }

        private static void Write(string path, JObject response)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, response.ToString(Formatting.None), new UTF8Encoding(false));
            File.Move(temporary, path);
        }

        private sealed class ProbeError : Exception
        {
            internal ProbeError(string message) : base(message)
            {
            }
        }

        private readonly struct ReadView
        {
            internal readonly int Offset;
            internal readonly int Limit;
            internal readonly int MaxDepth;
            internal readonly string Key;
            internal readonly string Prefix;

            internal ReadView(int offset, int limit, int maxDepth, string key, string prefix)
            {
                Offset = offset;
                Limit = limit;
                MaxDepth = maxDepth;
                Key = key;
                Prefix = prefix;
            }
        }
    }
}
#endif
