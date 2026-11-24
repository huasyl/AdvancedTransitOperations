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
    internal sealed partial class RuntimeProbe
    {
        private const float PollIntervalSeconds = 0.1f;
        private const int DefaultLimit = 50;
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly string m_RequestDir;
        private readonly string m_ResponseDir;
        private readonly string m_TraceDir;
        private readonly Dictionary<Type, MethodInfo> m_ComponentReadMethods = new Dictionary<Type, MethodInfo>();
        private readonly Dictionary<Type, BufferReader> m_BufferReaders = new Dictionary<Type, BufferReader>();
        private static readonly Dictionary<Type, List<FieldInfo>> s_Fields = new Dictionary<Type, List<FieldInfo>>();
        private float m_NextPollTime;
        private bool m_FileErrorLogged;

        internal RuntimeProbe(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
            string root = Path.Combine(Application.persistentDataPath, "RapidTransitMod", "Probe");
            m_RequestDir = Path.Combine(root, "requests");
            m_ResponseDir = Path.Combine(root, "responses");
            m_TraceDir = Path.Combine(root, "traces");
            Directory.CreateDirectory(m_RequestDir);
            Directory.CreateDirectory(m_ResponseDir);
        }

        internal void PollRequests()
        {
            float now = Time.realtimeSinceStartup;
            if (now < m_NextPollTime)
                return;

            m_NextPollTime = now + PollIntervalSeconds;
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

        internal void CaptureFrame(uint frame)
        {
            CaptureTrace(frame);
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
                case "trace.start":
                    return StartTrace(request);
                case "trace.status":
                    return TraceStatus();
                case "trace.stop":
                    StopTrace("requested");
                    return TraceStatus();
                case "trace.read":
                    return ReadTrace(request);
                case "trace.snapshot":
                    return ReadTraceSnapshot(request);
                case "trace.export":
                    return ExportTrace();
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
            if (!m_ComponentReadMethods.TryGetValue(type, out MethodInfo method))
            {
                MethodInfo definition = typeof(EntityManager).GetMethod(
                    "GetComponentData",
                    new[] { typeof(Entity) });
                if (definition == null)
                    throw new ProbeError("未找到 ECS 普通组件读取接口。");
                method = definition.MakeGenericMethod(type);
                m_ComponentReadMethods.Add(type, method);
            }

            try
            {
                return method.Invoke(m_Runtime.EntityManager, new object[] { entity });
            }
            catch (TargetInvocationException ex)
            {
                throw new ProbeError("读取组件失败: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        private JObject BufferNode(Entity entity, Type type, int offset, int limit)
        {
            object buffer = ReadBuffer(entity, type, out int total, out PropertyInfo item);
            int start = Math.Min(offset, total);
            int end = Math.Min(start + limit, total);
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

        private object ReadBuffer(Entity entity, Type type, out int total, out PropertyInfo item)
        {
            if (!m_BufferReaders.TryGetValue(type, out BufferReader reader))
            {
                MethodInfo definition = typeof(EntityManager).GetMethod(
                    "GetBuffer",
                    new[] { typeof(Entity), typeof(bool) });
                if (definition == null)
                    throw new ProbeError("未找到 ECS 动态缓冲区读取接口。");

                Type bufferType = typeof(DynamicBuffer<>).MakeGenericType(type);
                reader = new BufferReader(
                    definition.MakeGenericMethod(type),
                    bufferType.GetProperty("Length"),
                    bufferType.GetProperty("Item"));
                m_BufferReaders.Add(type, reader);
            }

            object buffer;
            try
            {
                buffer = reader.Method.Invoke(
                    m_Runtime.EntityManager,
                    new object[] { entity, true });
            }
            catch (TargetInvocationException ex)
            {
                throw new ProbeError("读取动态缓冲区失败: " + (ex.InnerException?.Message ?? ex.Message));
            }

            total = (int)reader.Length.GetValue(buffer, null);
            item = reader.Item;
            return buffer;
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

            string[] parts = SplitPath(path);
            for (int i = 0; i < parts.Length; i++)
            {
                string name = parts[i];
                string index = null;
                int open = name.IndexOf('[');
                if (open >= 0)
                {
                    if (name[name.Length - 1] != ']')
                        throw new ProbeError("下标缺少右方括号: " + parts[i]);
                    index = name.Substring(open + 1, name.Length - open - 2);
                    name = name.Substring(0, open);
                }

                if (string.IsNullOrWhiteSpace(name))
                    throw new ProbeError("path 包含空字段。");

                bool useStatic = firstStatic && i == 0;
                FieldInfo field = FindField(resultType, name, useStatic);
                if (field != null)
                {
                    value = field.GetValue(useStatic ? null : value);
                    resultType = field.FieldType;
                }
                else
                {
                    PropertyInfo property = FindProperty(resultType, name, useStatic);
                    if (property == null || property.GetIndexParameters().Length != 0)
                        throw new ProbeError("找不到字段或属性: " + name);
                    value = property.GetValue(useStatic ? null : value, null);
                    resultType = property.PropertyType;
                }

                if (index != null)
                {
                    if (value == null)
                        throw new ProbeError("字段为空，无法按下标读取: " + parts[i]);
                    value = ReadElement(value, index, parts[i]);
                    resultType = value?.GetType() ?? typeof(object);
                    if (value == null && i + 1 < parts.Length)
                        throw new ProbeError("下标处的值为空，无法继续读取: " + parts[i]);
                    continue;
                }

                if (value == null && i + 1 < parts.Length)
                    throw new ProbeError("字段为空，无法继续读取: " + parts[i]);
            }

            return value;
        }

        private static string[] SplitPath(string path)
        {
            List<string> parts = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth < 0)
                        throw new ProbeError("path 的方括号不匹配。");
                }
                else if (c == '.' && depth == 0)
                {
                    parts.Add(path.Substring(start, i - start));
                    start = i + 1;
                }
            }

            if (depth != 0)
                throw new ProbeError("path 的方括号不匹配。");
            parts.Add(path.Substring(start));
            return parts.ToArray();
        }

        private static object ReadElement(object collection, string index, string part)
        {
            if (collection is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (string.Equals(entry.Key?.ToString(), index, StringComparison.Ordinal))
                        return entry.Value;
                }
                throw new ProbeError("字典中找不到键: " + index);
            }

            if (collection is IList list)
            {
                if (!int.TryParse(index, out int position) || position < 0)
                    throw new ProbeError("列表下标必须是非负整数: " + index);
                if (position >= list.Count)
                    throw new ProbeError("下标越界: " + index + "，元素数 " + list.Count);
                return list[position];
            }

            throw new ProbeError("字段不是字典或列表，不能使用下标: " + part);
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
            return ValueNode(value, type, depth, view, false);
        }

        private static bool TryFullValueNode(object value, out JToken node, out string reason)
        {
            try
            {
                node = ValueNode(value, value?.GetType(), 0, new ReadView(0, DefaultLimit, 4, null, null), true);
                reason = null;
                return true;
            }
            catch (ProbeError ex)
            {
                node = null;
                reason = ex.Message;
                return false;
            }
        }

        private static JToken ValueNode(object value, Type type, int depth, ReadView view, bool full)
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
            if (full && (type.IsPointer || type.IsByRef || type == typeof(IntPtr) || type == typeof(UIntPtr)))
                throw new ProbeError("字段类型 " + type.FullName + " 不能安全展开。");
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
            if (full && (type.IsClass || type.IsArray || typeof(Delegate).IsAssignableFrom(type)))
                throw new ProbeError("托管字段类型 " + type.FullName + " 不在追踪范围内。");
            if (value is IDictionary dictionary)
            {
                if (full)
                    throw new ProbeError("字典字段不能完整展开。");
                return DictionaryNode(dictionary, depth, view);
            }
            if (value is IEnumerable enumerable && !(value is string))
            {
                if (full)
                    throw new ProbeError("集合字段不能完整展开。");
                return EnumerableNode(enumerable, depth, view);
            }
            if (!full && depth >= view.MaxDepth)
                return new JValue(value.ToString());
            if (type.Namespace == "Unity.Collections" || typeof(Delegate).IsAssignableFrom(type))
            {
                if (full)
                    throw new ProbeError("字段类型 " + type.FullName + " 不能完整展开。");
                return new JValue(value.ToString());
            }

            List<FieldInfo> fields = Fields(type);
            if (fields.Count == 0)
            {
                if (full)
                    return new JObject();
                return new JValue(value.ToString());
            }

            JObject node = new JObject();
            ReadView inner = view.Inner();
            for (int i = 0; i < fields.Count; i++)
            {
                FieldInfo field = fields[i];
                node[field.Name] = ValueNode(field.GetValue(value), field.FieldType, depth + 1, inner, full);
            }
            return node;
        }

        private static JObject DictionaryNode(IDictionary values, int depth, ReadView view)
        {
            JArray items = new JArray();
            ReadView inner = view.Inner();
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
                        ["key"] = ValueNode(entry.Key, entry.Key?.GetType(), depth + 1, inner),
                        ["value"] = ValueNode(entry.Value, entry.Value?.GetType(), depth + 1, inner)
                    });
                }
                matched++;
            }

            return Page(matched, view.Offset, view.Limit, items);
        }

        private static JObject EnumerableNode(IEnumerable values, int depth, ReadView view)
        {
            JArray items = new JArray();
            ReadView inner = view.Inner();
            int count = 0;
            foreach (object item in values)
            {
                if (count >= view.Offset && items.Count < view.Limit)
                    items.Add(ValueNode(item, item?.GetType(), depth + 1, inner));
                count++;
            }

            return Page(count, view.Offset, view.Limit, items);
        }

        private static List<FieldInfo> Fields(Type type)
        {
            if (s_Fields.TryGetValue(type, out List<FieldInfo> cached))
                return cached;

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
            s_Fields.Add(type, fields);
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

        private sealed class BufferReader
        {
            internal readonly MethodInfo Method;
            internal readonly PropertyInfo Length;
            internal readonly PropertyInfo Item;

            internal BufferReader(MethodInfo method, PropertyInfo length, PropertyInfo item)
            {
                Method = method;
                Length = length;
                Item = item;
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

            internal ReadView Inner()
            {
                return new ReadView(0, Limit, MaxDepth, null, null);
            }
        }
    }
}
#endif
