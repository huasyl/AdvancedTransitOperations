#if RT_DEBUG_TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Game.Vehicles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Diagnostics
{
    internal sealed partial class RuntimeProbe
    {
        private const int DefaultTraceMaxMegabytes = 32;
        private const int DefaultTraceIntervalFrames = 16;
        private const string TraceSamplePoint = "宿主 OnUpdate 末尾，EndFrameBarrier 回放之前";
        private static readonly Dictionary<Type, TraceValueInfo> s_TraceValueInfos
            = new Dictionary<Type, TraceValueInfo>();
        private TraceSession m_Trace;

        private JObject StartTrace(JObject request)
        {
            if (m_Trace != null && m_Trace.Active)
                throw new ProbeError("已有活动追踪会话，请先执行 trace stop。");

            ArchiveTrace();
            Entity root = ReadTraceEntity(request, "entity");
            if (!m_Runtime.EntityManager.Exists(root))
                throw new ProbeError("实体不存在: " + root.Index + ":" + root.Version);

            int maxMegabytes = request.Value<int?>("maxMb") ?? DefaultTraceMaxMegabytes;
            if (maxMegabytes < 1)
                throw new ProbeError("maxMb 必须是大于 0 的整数。");

            int intervalFrames = request.Value<int?>("intervalFrames") ?? DefaultTraceIntervalFrames;
            if (intervalFrames < 1)
                throw new ProbeError("intervalFrames 必须是大于 0 的整数。");

            bool followConsist = request.Value<bool?>("consist") ?? false;
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            TraceSession session = new TraceSession(
                root,
                followConsist,
                frame,
                (uint)intervalFrames,
                (long)maxMegabytes * 1024L * 1024L);

            HashSet<Entity> targets = BuildTraceTargets(session, out HashSet<Entity> members);
            JObject baseline = BuildBaseline(targets, members, frame, out Dictionary<Entity, TraceEntitySnapshot> snapshots);
            long baselineBytes = SerializedBytes(baseline) + SampleFrameBytes(frame);
            if (baselineBytes > session.LimitBytes)
                throw new ProbeError("完整基线超过记录上限，请提高 --max-mb。");

            session.Baseline = baseline;
            session.RecordBytes = baselineBytes;
            session.SampleFrames.Add(frame);
            session.LastSampleFrame = frame;
            session.LastCaptureFrame = frame;
            session.CurrentMembers = members;
            session.LastSnapshots = snapshots;

            m_Trace = session;
            return TraceStatus();
        }

        private JObject TraceStatus()
        {
            if (m_Trace == null)
            {
                return new JObject
                {
                    ["available"] = false,
                    ["active"] = false
                };
            }

            TraceSession session = m_Trace;
            JObject node = new JObject
            {
                ["available"] = true,
                ["active"] = session.Active,
                ["root"] = EntityNode(session.Root),
                ["consist"] = session.FollowConsist,
                ["intervalFrames"] = session.IntervalFrames,
                ["baselineFrame"] = session.StartFrame,
                ["samplePoint"] = TraceSamplePoint,
                ["sampleCount"] = session.SampleFrames.Count,
                ["recordCount"] = session.Records.Count,
                ["memberCount"] = session.CurrentMembers.Count,
                ["serializedRecordBytes"] = session.RecordBytes,
                ["maxRecordBytes"] = session.LimitBytes
            };
            if (session.LastSampleFrame.HasValue)
                node["lastSampleFrame"] = session.LastSampleFrame.Value;
            if (session.LastCaptureFrame.HasValue)
                node["lastCaptureFrame"] = session.LastCaptureFrame.Value;
            if (!string.IsNullOrEmpty(session.StopReason))
                node["stopReason"] = session.StopReason;
            if (session.CutoffSampleFrame.HasValue)
                node["cutoffSampleFrame"] = session.CutoffSampleFrame.Value;
            if (!string.IsNullOrEmpty(session.LastExportPath))
                node["exportPath"] = session.LastExportPath;
            if (!string.IsNullOrEmpty(session.ExportError))
                node["exportError"] = session.ExportError;
            return node;
        }

        internal void StopTrace(string reason)
        {
            if (m_Trace == null)
                return;
            if (m_Trace.Active)
            {
                m_Trace.Active = false;
                m_Trace.StopReason = reason;
                m_Trace.CutoffSampleFrame = m_Trace.LastSampleFrame ?? m_Trace.StartFrame;
                m_Trace.LastExportPath = null;
            }
            if (string.IsNullOrEmpty(m_Trace.LastExportPath))
                TryExportTrace(m_Trace);
        }

        private JObject ReadTrace(JObject request)
        {
            TraceSession session = RequireTrace();
            ReadPage(request, out int offset, out int limit);
            return new JObject
            {
                ["status"] = TraceStatus(),
                ["records"] = TokenPage(session.Records, offset, limit),
                ["sampleFrames"] = SampleFramePage(session.SampleFrames, offset, limit)
            };
        }

        private JObject ReadTraceSnapshot(JObject request)
        {
            TraceSession session = RequireTrace();
            long requested = request.Value<long?>("frame") ?? -1;
            if (requested < 0 || requested > uint.MaxValue)
                throw new ProbeError("trace snapshot 需要有效的 --frame。");

            uint requestedFrame = (uint)requested;
            uint? sampleFrame = FindSampleFrame(session, requestedFrame);
            if (!sampleFrame.HasValue)
                throw new ProbeError("请求帧早于追踪基线，无法还原。");

            Entity? entityFilter = request["entity"] is JObject
                ? ReadTraceEntity(request, "entity")
                : (Entity?)null;
            string typeFilter = request.Value<string>("type");
            Dictionary<string, JObject> state = RestoreTraceSnapshot(session, sampleFrame.Value);
            JArray entities = SnapshotEntities(state, entityFilter, typeFilter);
            return new JObject
            {
                ["requestedFrame"] = requestedFrame,
                ["sampleFrame"] = sampleFrame.Value,
                ["exact"] = sampleFrame.Value == requestedFrame,
                ["entities"] = entities
            };
        }

        private JObject ExportTrace()
        {
            TraceSession session = RequireTrace();
            if (!TryExportTrace(session))
                throw new ProbeError("追踪导出失败: " + session.ExportError);
            return TraceStatus();
        }

        private void CaptureTrace(uint frame)
        {
            TraceSession session = m_Trace;
            if (session == null || !session.Active || session.LastCaptureFrame == frame)
                return;

            try
            {
                HashSet<Entity> targets = BuildTraceTargets(session, out HashSet<Entity> members);
                JArray events = CaptureTraceEvents(targets);
                bool sample = !m_Runtime.EntityManager.Exists(session.Root)
                    || (uint)(frame - session.LastSampleFrame.Value) >= session.IntervalFrames;
                if (!sample)
                {
                    if (events.Count == 0)
                    {
                        session.LastCaptureFrame = frame;
                        return;
                    }

                    JObject eventRecord = new JObject
                    {
                        ["frame"] = frame,
                        ["events"] = events
                    };
                    long eventBytes = SerializedBytes(eventRecord) + 1L;
                    if (session.RecordBytes + eventBytes > session.LimitBytes)
                    {
                        StopTrace("capacity");
                        return;
                    }

                    session.RecordBytes += eventBytes;
                    session.Records.Add(eventRecord);
                    session.LastCaptureFrame = frame;
                    return;
                }

                JObject membership = BuildMembershipChange(session.CurrentMembers, members);
                JArray entities = new JArray();
                Dictionary<Entity, TraceEntitySnapshot> snapshots = new Dictionary<Entity, TraceEntitySnapshot>();
                foreach (Entity target in SortEntities(targets))
                {
                    TraceEntitySnapshot current = CaptureEntity(target);
                    snapshots[target] = current;
                    session.LastSnapshots.TryGetValue(target, out TraceEntitySnapshot previous);
                    JObject change = BuildEntityChange(target, previous, current);
                    if (change != null)
                        entities.Add(change);
                }

                bool hasRecord = membership != null || entities.Count > 0 || events.Count > 0;
                JObject record = null;
                if (hasRecord)
                {
                    record = new JObject { ["frame"] = frame };
                    if (membership != null)
                        record["membership"] = membership;
                    if (entities.Count > 0)
                        record["entities"] = entities;
                    if (events.Count > 0)
                        record["events"] = events;
                }

                long bytes = SampleFrameBytes(frame) + (hasRecord ? SerializedBytes(record) + 1L : 0L);
                if (session.RecordBytes + bytes > session.LimitBytes)
                {
                    StopTrace("capacity");
                    return;
                }

                session.RecordBytes += bytes;
                session.SampleFrames.Add(frame);
                session.LastSampleFrame = frame;
                if (hasRecord)
                    session.Records.Add(record);
                session.LastCaptureFrame = frame;
                session.CurrentMembers = members;
                session.LastSnapshots = snapshots;

                if (!snapshots[session.Root].Exists)
                    StopTrace("rootMissing");
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[RuntimeProbe] 追踪已停止: " + ex.GetType().Name + ": " + ex.Message);
                StopTrace("captureError");
            }
        }

        private HashSet<Entity> BuildTraceTargets(
            TraceSession session,
            out HashSet<Entity> members)
        {
            HashSet<Entity> targets = new HashSet<Entity> { session.Root };
            members = new HashSet<Entity>();
            if (!session.FollowConsist
                || !m_Runtime.EntityManager.Exists(session.Root)
                || !m_Runtime.EntityManager.HasBuffer<LayoutElement>(session.Root))
            {
                return targets;
            }

            DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(session.Root, true);
            for (int i = 0; i < layout.Length; i++)
            {
                Entity vehicle = layout[i].m_Vehicle;
                if (vehicle == Entity.Null || vehicle == session.Root)
                    continue;
                members.Add(vehicle);
                targets.Add(vehicle);
            }
            return targets;
        }

        private JObject BuildBaseline(
            HashSet<Entity> targets,
            HashSet<Entity> members,
            uint frame,
            out Dictionary<Entity, TraceEntitySnapshot> snapshots)
        {
            JArray entities = new JArray();
            snapshots = new Dictionary<Entity, TraceEntitySnapshot>();
            foreach (Entity target in SortEntities(targets))
            {
                TraceEntitySnapshot snapshot = CaptureEntity(target);
                snapshots[target] = snapshot;
                entities.Add(TraceEntityNode(snapshot));
            }

            JArray memberNodes = new JArray();
            foreach (Entity member in SortEntities(members))
                memberNodes.Add(EntityNode(member));
            return new JObject
            {
                ["frame"] = frame,
                ["entities"] = entities,
                ["members"] = memberNodes
            };
        }

        private TraceEntitySnapshot CaptureEntity(Entity entity)
        {
            TraceEntitySnapshot snapshot = new TraceEntitySnapshot(entity);
            if (!m_Runtime.EntityManager.Exists(entity))
            {
                snapshot.Exists = false;
                return snapshot;
            }

            snapshot.Exists = true;
            NativeArray<ComponentType> types = m_Runtime.EntityManager.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    TraceComponentSnapshot component = CaptureComponent(entity, types[i]);
                    snapshot.Components[component.Type] = component;
                }
            }
            finally
            {
                types.Dispose();
            }
            return snapshot;
        }

        private TraceComponentSnapshot CaptureComponent(Entity entity, ComponentType componentType)
        {
            Type type = componentType.GetManagedType();
            string name = type?.FullName ?? componentType.ToString();
            TraceComponentSnapshot snapshot = new TraceComponentSnapshot(name, TraceComponentKind(componentType));
            if (type == null)
                return UnsupportedComponent(snapshot, "ECS 未提供该组件的托管类型。");
            if (componentType.IsSharedComponent || componentType.IsManagedComponent || type.IsClass)
                return UnsupportedComponent(snapshot, "托管或共享组件不在追踪范围内。");

            if (componentType.IsZeroSized)
            {
                snapshot.Supported = true;
                snapshot.Present = true;
                AddEnableState(snapshot, entity, componentType);
                return snapshot;
            }

            if (componentType.IsBuffer)
            {
                if (!typeof(IBufferElementData).IsAssignableFrom(type))
                    return UnsupportedComponent(snapshot, "ECS 缓冲区类型不实现 IBufferElementData。");
                try
                {
                    snapshot.Items = TraceBufferItems(entity, type);
                    snapshot.Supported = true;
                    AddEnableState(snapshot, entity, componentType);
                    return snapshot;
                }
                catch (Exception ex)
                {
                    return UnsupportedComponent(snapshot, "无法完整展开动态缓冲区: " + Describe(ex));
                }
            }

            if (!typeof(IComponentData).IsAssignableFrom(type))
                return UnsupportedComponent(snapshot, "该 ECS 类型不是普通 IComponentData。");

            try
            {
                object value = GetComponent(entity, type);
                if (!TryFullTraceValue(value, out string reason))
                    return UnsupportedComponent(snapshot, reason);
                snapshot.Value = value;
                snapshot.Supported = true;
                AddEnableState(snapshot, entity, componentType);
                return snapshot;
            }
            catch (Exception ex)
            {
                return UnsupportedComponent(snapshot, "无法读取普通组件: " + Describe(ex));
            }
        }

        private object[] TraceBufferItems(Entity entity, Type type)
        {
            object buffer = ReadBuffer(entity, type, out int length, out PropertyInfo item);
            object[] items = new object[length];
            for (int i = 0; i < length; i++)
            {
                object value = item.GetValue(buffer, new object[] { i });
                if (!TryFullTraceValue(value, out string reason))
                    throw new ProbeError("元素 " + i + " 无法完整展开: " + reason);
                items[i] = value;
            }
            return items;
        }

        private void AddEnableState(TraceComponentSnapshot snapshot, Entity entity, ComponentType componentType)
        {
            if (componentType.IsEnableable)
                snapshot.Enabled = m_Runtime.EntityManager.IsComponentEnabled(entity, componentType);
        }

        private static TraceComponentSnapshot UnsupportedComponent(TraceComponentSnapshot snapshot, string reason)
        {
            snapshot.Supported = false;
            snapshot.Reason = reason;
            return snapshot;
        }

        private static string TraceComponentKind(ComponentType componentType)
        {
            if (componentType.IsBuffer)
                return "buffer";
            if (componentType.IsSharedComponent)
                return "shared";
            if (componentType.IsManagedComponent)
                return "managed";
            return "component";
        }

        private static JObject TraceEntityNode(TraceEntitySnapshot snapshot)
        {
            JObject node = new JObject
            {
                ["entity"] = EntityNode(snapshot.Entity),
                ["exists"] = snapshot.Exists
            };
            JArray components = new JArray();
            if (snapshot.Exists)
            {
                List<string> names = new List<string>(snapshot.Components.Keys);
                names.Sort(StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++)
                    components.Add(TraceComponentNode(snapshot.Components[names[i]]));
            }
            node["components"] = components;
            return node;
        }

        private static JObject TraceComponentNode(TraceComponentSnapshot snapshot)
        {
            JObject node = new JObject
            {
                ["type"] = snapshot.Type,
                ["kind"] = snapshot.Kind
            };
            if (!snapshot.Supported)
                return UnsupportedComponentNode(node, snapshot.Reason);

            if (snapshot.Present)
            {
                node["supported"] = true;
                node["present"] = true;
            }
            else if (snapshot.Kind == "buffer")
            {
                JArray items = new JArray();
                for (int i = 0; i < snapshot.Items.Length; i++)
                {
                    object value = snapshot.Items[i];
                    items.Add(ValueNode(value, value?.GetType(), 0, new ReadView(0, DefaultLimit, 4, null, null), true));
                }
                node["items"] = items;
                node["supported"] = true;
            }
            else
            {
                node["value"] = ValueNode(
                    snapshot.Value,
                    snapshot.Value?.GetType(),
                    0,
                    new ReadView(0, DefaultLimit, 4, null, null),
                    true);
                node["supported"] = true;
            }

            if (snapshot.Enabled.HasValue)
                node["enabled"] = snapshot.Enabled.Value;
            return node;
        }

        private static JObject UnsupportedComponentNode(JObject node, string reason)
        {
            node["supported"] = false;
            node["reason"] = reason;
            return node;
        }

        private static bool TraceComponentEquals(
            TraceComponentSnapshot left,
            TraceComponentSnapshot right)
        {
            if (!string.Equals(left.Type, right.Type, StringComparison.Ordinal)
                || !string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
                || left.Supported != right.Supported
                || left.Present != right.Present
                || left.Enabled != right.Enabled)
            {
                return false;
            }
            if (!left.Supported)
                return string.Equals(left.Reason, right.Reason, StringComparison.Ordinal);
            if (left.Present)
                return true;
            if (left.Kind != "buffer")
                return TraceValueEquals(left.Value, right.Value);
            if (left.Items.Length != right.Items.Length)
                return false;
            for (int i = 0; i < left.Items.Length; i++)
            {
                if (!TraceValueEquals(left.Items[i], right.Items[i]))
                    return false;
            }
            return true;
        }

        private static bool TraceValueEquals(object left, object right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            Type type = left.GetType();
            if (type != right.GetType())
                return false;
            if (type == typeof(Entity))
            {
                Entity first = (Entity)left;
                Entity second = (Entity)right;
                return first.Index == second.Index && first.Version == second.Version;
            }
            if (type.IsEnum)
                return left.Equals(right);
            if (type == typeof(string))
                return string.Equals((string)left, (string)right, StringComparison.Ordinal);
            if (type == typeof(char)
                || type.IsPrimitive
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(TimeSpan)
                || type == typeof(Guid))
            {
                return left.Equals(right);
            }
            if (type == typeof(Type))
                return ReferenceEquals(left, right);
            if (type.Namespace == "Unity.Collections"
                && type.Name.StartsWith("FixedString", StringComparison.Ordinal))
            {
                return left.Equals(right);
            }

            List<FieldInfo> fields = Fields(type);
            for (int i = 0; i < fields.Count; i++)
            {
                FieldInfo field = fields[i];
                if (!TraceValueEquals(field.GetValue(left), field.GetValue(right)))
                    return false;
            }
            return true;
        }

        private static bool TryFullTraceValue(object value, out string reason)
        {
            try
            {
                EnsureFullTraceValue(value);
                reason = null;
                return true;
            }
            catch (ProbeError ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static void EnsureFullTraceValue(object value)
        {
            if (value == null)
                return;

            Type type = value.GetType();
            TraceValueInfo info = GetTraceValueInfo(type);
            if (!string.IsNullOrEmpty(info.Reason))
                throw new ProbeError(info.Reason);
            if (!info.RequiresValueCheck)
                return;

            for (int i = 0; i < info.Fields.Count; i++)
            {
                FieldInfo field = info.Fields[i];
                TraceValueInfo fieldInfo = GetTraceValueInfo(field.FieldType);
                if (!string.IsNullOrEmpty(fieldInfo.Reason) || fieldInfo.RequiresValueCheck)
                    EnsureFullTraceValue(field.GetValue(value));
            }
        }

        private static TraceValueInfo GetTraceValueInfo(Type type)
        {
            if (s_TraceValueInfos.TryGetValue(type, out TraceValueInfo cached))
                return cached;

            TraceValueInfo info = new TraceValueInfo(type, Fields(type));
            s_TraceValueInfos.Add(type, info);
            return info;
        }

        private static JObject BuildMembershipChange(HashSet<Entity> previous, HashSet<Entity> current)
        {
            JArray entered = new JArray();
            JArray left = new JArray();
            foreach (Entity entity in SortEntities(current))
            {
                if (!previous.Contains(entity))
                    entered.Add(EntityNode(entity));
            }
            foreach (Entity entity in SortEntities(previous))
            {
                if (!current.Contains(entity))
                    left.Add(EntityNode(entity));
            }
            if (entered.Count == 0 && left.Count == 0)
                return null;
            return new JObject
            {
                ["entered"] = entered,
                ["left"] = left
            };
        }

        private static JObject BuildEntityChange(
            Entity entity,
            TraceEntitySnapshot previous,
            TraceEntitySnapshot current)
        {
            bool currentExists = current.Exists;
            bool previousExists = previous != null && previous.Exists;
            bool entityChanged = previous == null || currentExists != previousExists;
            JArray changes = new JArray();
            List<string> names = previous == null
                ? new List<string>()
                : new List<string>(previous.Components.Keys);
            foreach (string name in current.Components.Keys)
            {
                if (previous == null || !previous.Components.ContainsKey(name))
                    names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                TraceComponentSnapshot before = null;
                bool hadBefore = previous != null
                    && previous.Components.TryGetValue(name, out before);
                bool hasAfter = current.Components.TryGetValue(name, out TraceComponentSnapshot after);
                if (hadBefore && hasAfter && TraceComponentEquals(before, after))
                    continue;

                JObject change = new JObject
                {
                    ["type"] = name,
                    ["change"] = !hadBefore ? "added" : !hasAfter ? "removed" : "modified"
                };
                if (hadBefore)
                    change["before"] = TraceComponentNode(before);
                if (hasAfter)
                    change["after"] = TraceComponentNode(after);
                changes.Add(change);
            }

            if (!entityChanged && changes.Count == 0)
                return null;

            JObject result = new JObject
            {
                ["entity"] = EntityNode(entity),
                ["exists"] = currentExists
            };
            if (previous == null)
                result["change"] = "entered";
            else if (!previousExists && currentExists)
                result["change"] = "restored";
            else if (previousExists && !currentExists)
                result["change"] = "missing";
            if (changes.Count > 0)
                result["components"] = changes;
            return result;
        }

        private JArray CaptureTraceEvents(HashSet<Entity> targets)
        {
            JArray events = new JArray();
            IReadOnlyList<FrameEventRef> ordered = m_Runtime.m_FrameEvents.MergeBySequence();
            for (int i = 0; i < ordered.Count; i++)
            {
                FrameEventRef reference = ordered[i];
                switch (reference.Kind)
                {
                    case FrameEventKind.Lifecycle:
                    {
                        LifecycleEvent value = m_Runtime.m_FrameEvents.LifecycleEvents[reference.Index];
                        if (targets.Contains(value.Vehicle))
                            events.Add(FrameEventNode(
                                FrameEventKind.Lifecycle,
                                value.Vehicle,
                                value.Frame,
                                value.Sequence,
                                value.Kind.ToString(),
                                value));
                        break;
                    }
                    case FrameEventKind.Stop:
                    {
                        StopEvent value = m_Runtime.m_FrameEvents.StopEvents[reference.Index];
                        if (targets.Contains(value.Vehicle))
                            events.Add(FrameEventNode(
                                FrameEventKind.Stop,
                                value.Vehicle,
                                value.Frame,
                                value.Sequence,
                                value.Fact.Kind.ToString(),
                                value.Fact));
                        break;
                    }
                    case FrameEventKind.Bypass:
                    {
                        BypassEvent value = m_Runtime.m_FrameEvents.BypassEvents[reference.Index];
                        if (targets.Contains(value.Vehicle))
                            events.Add(FrameEventNode(
                                FrameEventKind.Bypass,
                                value.Vehicle,
                                value.Frame,
                                value.Sequence,
                                value.Fact.Kind.ToString(),
                                value.Fact));
                        break;
                    }
                    case FrameEventKind.Dispatch:
                    {
                        DispatchEvent value = m_Runtime.m_FrameEvents.DispatchEvents[reference.Index];
                        if (targets.Contains(value.Vehicle))
                            events.Add(FrameEventNode(
                                FrameEventKind.Dispatch,
                                value.Vehicle,
                                value.Frame,
                                value.Sequence,
                                value.Kind.ToString(),
                                value));
                        break;
                    }
                    case FrameEventKind.DeparturePending:
                    {
                        DeparturePendingEvent value = m_Runtime.m_FrameEvents.DeparturePendingEvents[reference.Index];
                        if (targets.Contains(value.Vehicle))
                            events.Add(FrameEventNode(
                                FrameEventKind.DeparturePending,
                                value.Vehicle,
                                value.Frame,
                                value.Sequence,
                                null,
                                value));
                        break;
                    }
                }
            }
            return events;
        }

        private static JObject FrameEventNode(
            FrameEventKind kind,
            Entity vehicle,
            uint frame,
            ulong sequence,
            string factKind,
            object fact)
        {
            if (!TryFullValueNode(fact, out JToken value, out string reason))
                throw new ProbeError("无法完整记录调度事件: " + reason);

            JObject node = new JObject
            {
                ["frame"] = frame,
                ["sequence"] = sequence,
                ["kind"] = kind.ToString(),
                ["vehicle"] = EntityNode(vehicle),
                ["fact"] = value
            };
            if (!string.IsNullOrEmpty(factKind))
                node["factKind"] = factKind;
            return node;
        }

        private static Dictionary<string, JObject> ComponentMap(JObject snapshot)
        {
            Dictionary<string, JObject> result = new Dictionary<string, JObject>(StringComparer.Ordinal);
            if (snapshot == null || !snapshot.Value<bool>("exists"))
                return result;
            JArray components = snapshot["components"] as JArray;
            if (components == null)
                return result;
            for (int i = 0; i < components.Count; i++)
            {
                JObject component = components[i] as JObject;
                string type = component?.Value<string>("type");
                if (!string.IsNullOrEmpty(type))
                    result[type] = component;
            }
            return result;
        }

        private Dictionary<string, JObject> RestoreTraceSnapshot(TraceSession session, uint sampleFrame)
        {
            Dictionary<string, JObject> state = new Dictionary<string, JObject>(StringComparer.Ordinal);
            JArray baselineEntities = session.Baseline["entities"] as JArray;
            for (int i = 0; i < baselineEntities.Count; i++)
            {
                JObject snapshot = (JObject)baselineEntities[i];
                state[EntityKey(snapshot["entity"] as JObject)] = (JObject)snapshot.DeepClone();
            }

            for (int i = 0; i < session.Records.Count; i++)
            {
                JObject record = (JObject)session.Records[i];
                if (record.Value<uint>("frame") > sampleFrame)
                    break;

                JObject membership = record["membership"] as JObject;
                if (membership != null)
                {
                    JArray left = membership["left"] as JArray;
                    if (left != null)
                    {
                        for (int j = 0; j < left.Count; j++)
                            state.Remove(EntityKey((JObject)left[j]));
                    }
                }

                JArray entities = record["entities"] as JArray;
                if (entities == null)
                    continue;
                for (int j = 0; j < entities.Count; j++)
                    ApplyEntityChange(state, (JObject)entities[j]);
            }
            return state;
        }

        private static void ApplyEntityChange(Dictionary<string, JObject> state, JObject change)
        {
            JObject entity = (JObject)change["entity"];
            string key = EntityKey(entity);
            if (!state.TryGetValue(key, out JObject snapshot))
            {
                snapshot = new JObject
                {
                    ["entity"] = entity.DeepClone(),
                    ["exists"] = false,
                    ["components"] = new JArray()
                };
                state[key] = snapshot;
            }

            bool exists = change.Value<bool>("exists");
            snapshot["exists"] = exists;
            if (!exists)
            {
                snapshot["components"] = new JArray();
                return;
            }

            Dictionary<string, JObject> components = ComponentMap(snapshot);
            JArray changes = change["components"] as JArray;
            if (changes != null)
            {
                for (int i = 0; i < changes.Count; i++)
                {
                    JObject component = (JObject)changes[i];
                    string type = component.Value<string>("type");
                    if (component.Value<string>("change") == "removed")
                        components.Remove(type);
                    else
                        components[type] = (JObject)component["after"].DeepClone();
                }
            }
            snapshot["components"] = ComponentArray(components);
        }

        private static JArray SnapshotEntities(
            Dictionary<string, JObject> state,
            Entity? entityFilter,
            string typeFilter)
        {
            List<JObject> values = new List<JObject>();
            foreach (JObject snapshot in state.Values)
            {
                JObject entity = snapshot["entity"] as JObject;
                if (entityFilter.HasValue && !EntityMatches(entity, entityFilter.Value))
                    continue;
                JObject result = (JObject)snapshot.DeepClone();
                if (!string.IsNullOrWhiteSpace(typeFilter))
                {
                    JArray filtered = new JArray();
                    JArray components = result["components"] as JArray;
                    if (components != null)
                    {
                        for (int i = 0; i < components.Count; i++)
                        {
                            JObject component = (JObject)components[i];
                            if (string.Equals(component.Value<string>("type"), typeFilter, StringComparison.Ordinal))
                                filtered.Add(component.DeepClone());
                        }
                    }
                    result["components"] = filtered;
                }
                values.Add(result);
            }
            values.Sort(CompareTraceEntities);
            JArray snapshots = new JArray();
            for (int i = 0; i < values.Count; i++)
                snapshots.Add(values[i]);
            return snapshots;
        }

        private static JArray ComponentArray(Dictionary<string, JObject> components)
        {
            List<string> types = new List<string>(components.Keys);
            types.Sort(StringComparer.Ordinal);
            JArray values = new JArray();
            for (int i = 0; i < types.Count; i++)
                values.Add(components[types[i]].DeepClone());
            return values;
        }

        private static int CompareTraceEntities(JObject left, JObject right)
        {
            Entity first = EntityFromNode(left["entity"] as JObject);
            Entity second = EntityFromNode(right["entity"] as JObject);
            return CompareEntities(first, second);
        }

        private static bool EntityMatches(JObject node, Entity entity)
        {
            return node != null
                && node.Value<int>("index") == entity.Index
                && node.Value<int>("version") == entity.Version;
        }

        private static string EntityKey(JObject entity)
        {
            return entity.Value<int>("index") + ":" + entity.Value<int>("version");
        }

        private static Entity EntityFromNode(JObject node)
        {
            return new Entity
            {
                Index = node.Value<int>("index"),
                Version = node.Value<int>("version")
            };
        }

        private static List<Entity> SortEntities(IEnumerable<Entity> values)
        {
            List<Entity> result = new List<Entity>(values);
            result.Sort(CompareEntities);
            return result;
        }

        private static int CompareEntities(Entity left, Entity right)
        {
            return left.Index != right.Index
                ? left.Index.CompareTo(right.Index)
                : left.Version.CompareTo(right.Version);
        }

        private static JObject TokenPage(JArray values, int offset, int limit)
        {
            int start = Math.Min(offset, values.Count);
            int end = Math.Min(start + limit, values.Count);
            JArray items = new JArray();
            for (int i = start; i < end; i++)
                items.Add(values[i].DeepClone());
            return Page(values.Count, start, limit, items);
        }

        private static JObject SampleFramePage(List<uint> values, int offset, int limit)
        {
            int start = Math.Min(offset, values.Count);
            int end = Math.Min(start + limit, values.Count);
            JArray items = new JArray();
            for (int i = start; i < end; i++)
                items.Add(values[i]);
            return Page(values.Count, start, limit, items);
        }

        private static uint? FindSampleFrame(TraceSession session, uint requested)
        {
            uint? result = null;
            for (int i = 0; i < session.SampleFrames.Count; i++)
            {
                uint frame = session.SampleFrames[i];
                if (frame > requested)
                    break;
                result = frame;
            }
            return result;
        }

        private static Entity ReadTraceEntity(JObject request, string name)
        {
            JObject node = request[name] as JObject;
            if (node == null || node["version"] == null)
                throw new ProbeError(name + " 必须是完整的 index:version 实体。");
            int? index = node.Value<int?>("index");
            int? version = node.Value<int?>("version");
            if (!index.HasValue || !version.HasValue || index.Value < 0 || version.Value < 0)
                throw new ProbeError(name + " 无效。");
            return new Entity { Index = index.Value, Version = version.Value };
        }

        private TraceSession RequireTrace()
        {
            return m_Trace ?? throw new ProbeError("当前没有可读取的追踪会话。");
        }

        private void ArchiveTrace()
        {
            if (m_Trace == null)
                return;
            if (string.IsNullOrEmpty(m_Trace.LastExportPath) && !TryExportTrace(m_Trace))
                throw new ProbeError("旧追踪会话归档失败: " + m_Trace.ExportError);
            m_Trace = null;
        }

        private bool TryExportTrace(TraceSession session)
        {
            try
            {
                Directory.CreateDirectory(m_TraceDir);
                string fileName = "trace-"
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                    + "-" + session.Root.Index + "-" + session.Root.Version
                    + "-" + Guid.NewGuid().ToString("N") + ".json";
                string path = Path.Combine(m_TraceDir, fileName);
                JObject export = new JObject
                {
                    ["schema"] = "RuntimeProbeTrace.v1",
                    ["metadata"] = new JObject
                    {
                        ["root"] = EntityNode(session.Root),
                        ["consist"] = session.FollowConsist,
                        ["intervalFrames"] = session.IntervalFrames,
                        ["samplePoint"] = TraceSamplePoint,
                        ["baselineFrame"] = session.StartFrame,
                        ["lastCaptureFrame"] = session.LastCaptureFrame.HasValue
                            ? new JValue(session.LastCaptureFrame.Value)
                            : JValue.CreateNull(),
                        ["stopReason"] = session.StopReason == null ? JValue.CreateNull() : new JValue(session.StopReason),
                        ["cutoffSampleFrame"] = session.CutoffSampleFrame.HasValue
                            ? new JValue(session.CutoffSampleFrame.Value)
                            : JValue.CreateNull(),
                        ["serializedRecordBytes"] = session.RecordBytes,
                        ["maxRecordBytes"] = session.LimitBytes
                    },
                    ["baseline"] = session.Baseline.DeepClone(),
                    ["sampleFrames"] = SampleFrameArray(session.SampleFrames),
                    ["records"] = session.Records.DeepClone()
                };
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, export.ToString(Formatting.None), new UTF8Encoding(false));
                File.Move(temporary, path);
                session.LastExportPath = path;
                session.ExportError = null;
                return true;
            }
            catch (Exception ex)
            {
                session.ExportError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static long SerializedBytes(JToken value)
        {
            return Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
        }

        private static long SampleFrameBytes(uint frame)
        {
            return Encoding.UTF8.GetByteCount(frame.ToString()) + 1L;
        }

        private static JArray SampleFrameArray(List<uint> frames)
        {
            JArray values = new JArray();
            for (int i = 0; i < frames.Count; i++)
                values.Add(frames[i]);
            return values;
        }

        private sealed class TraceValueInfo
        {
            internal readonly List<FieldInfo> Fields;
            internal readonly string Reason;
            internal readonly bool RequiresValueCheck;

            internal TraceValueInfo(Type type, List<FieldInfo> fields)
            {
                Fields = fields;
                if (type == typeof(Entity)
                    || type.IsEnum
                    || type == typeof(string)
                    || type == typeof(char)
                    || type.IsPrimitive
                    || type == typeof(decimal)
                    || type == typeof(DateTime)
                    || type == typeof(TimeSpan)
                    || type == typeof(Guid)
                    || type == typeof(Type)
                    || (type.Namespace == "Unity.Collections"
                        && type.Name.StartsWith("FixedString", StringComparison.Ordinal)))
                {
                    return;
                }
                if (type.IsPointer || type.IsByRef || type == typeof(IntPtr) || type == typeof(UIntPtr))
                {
                    Reason = "字段类型 " + type.FullName + " 不能安全展开。";
                    return;
                }
                if (type.IsClass || type.IsArray || typeof(Delegate).IsAssignableFrom(type))
                {
                    Reason = "托管字段类型 " + type.FullName + " 不在追踪范围内。";
                    return;
                }
                if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
                {
                    Reason = "字典字段不能完整展开。";
                    return;
                }
                if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                {
                    Reason = "集合字段不能完整展开。";
                    return;
                }
                if (type.Namespace == "Unity.Collections" || typeof(Delegate).IsAssignableFrom(type))
                {
                    Reason = "字段类型 " + type.FullName + " 不能完整展开。";
                    return;
                }

                for (int i = 0; i < fields.Count; i++)
                {
                    TraceValueInfo fieldInfo = GetTraceValueInfo(fields[i].FieldType);
                    if (!string.IsNullOrEmpty(fieldInfo.Reason) || fieldInfo.RequiresValueCheck)
                    {
                        RequiresValueCheck = true;
                        return;
                    }
                }
            }
        }

        private sealed class TraceEntitySnapshot
        {
            internal readonly Entity Entity;
            internal readonly Dictionary<string, TraceComponentSnapshot> Components
                = new Dictionary<string, TraceComponentSnapshot>(StringComparer.Ordinal);
            internal bool Exists;

            internal TraceEntitySnapshot(Entity entity)
            {
                Entity = entity;
            }
        }

        private sealed class TraceComponentSnapshot
        {
            internal readonly string Type;
            internal readonly string Kind;
            internal bool Supported;
            internal bool Present;
            internal bool? Enabled;
            internal string Reason;
            internal object Value;
            internal object[] Items;

            internal TraceComponentSnapshot(string type, string kind)
            {
                Type = type;
                Kind = kind;
            }
        }

        private sealed class TraceSession
        {
            internal readonly Entity Root;
            internal readonly bool FollowConsist;
            internal readonly uint StartFrame;
            internal readonly uint IntervalFrames;
            internal readonly long LimitBytes;
            internal readonly List<uint> SampleFrames = new List<uint>();
            internal readonly JArray Records = new JArray();
            internal Dictionary<Entity, TraceEntitySnapshot> LastSnapshots
                = new Dictionary<Entity, TraceEntitySnapshot>();
            internal HashSet<Entity> CurrentMembers = new HashSet<Entity>();
            internal JObject Baseline;
            internal bool Active = true;
            internal long RecordBytes;
            internal uint? LastSampleFrame;
            internal uint? LastCaptureFrame;
            internal uint? CutoffSampleFrame;
            internal string StopReason;
            internal string LastExportPath;
            internal string ExportError;

            internal TraceSession(
                Entity root,
                bool followConsist,
                uint startFrame,
                uint intervalFrames,
                long limitBytes)
            {
                Root = root;
                FollowConsist = followConsist;
                StartFrame = startFrame;
                IntervalFrames = intervalFrames;
                LimitBytes = limitBytes;
            }
        }

    }
}
#endif
