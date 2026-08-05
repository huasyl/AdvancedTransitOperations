using Unity.Collections;
using Unity.Entities;
using System.Collections.Generic;

namespace RapidTransitMod
{
    internal enum VehicleLabelType
    {
        Returning,
        StopTimeoutAssist,
        PathFault,
        Holding,
        WaitingDispatch,
        GoingOrigin,
        Running,
        BoardingEnd,
        StopTimeout,
        BypassExpress,
        AbnormalDeparture
    }

    internal sealed class RuntimeVehicleLabels
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, string> m_LabelCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, LocalizedLabelSpec> m_LocalizedSpecCache = new Dictionary<Entity, LocalizedLabelSpec>();
        private readonly Dictionary<string, string> m_LocalizedBaseCache = new Dictionary<string, string>();
        private object m_ActiveLocalizationDictionary;

        public RuntimeVehicleLabels(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Set(Entity vehicle, string message)
        {
            m_LocalizedSpecCache.Remove(vehicle);
            SetCore(vehicle, message);
        }

        private void SetCore(Entity vehicle, string message)
        {
            message ??= string.Empty;
            if (m_LabelCache.TryGetValue(vehicle, out string cachedLabel)
                && string.Equals(cachedLabel, message, System.StringComparison.Ordinal))
            {
                return;
            }

            var fixedMessage = new FixedString64Bytes(message);
            if (!m_Runtime.m_UICache.TryGetValue(vehicle, out var cached) || cached != fixedMessage)
            {
                m_Runtime.m_NameSystem.SetCustomName(vehicle, message);
                m_Runtime.m_UICache[vehicle] = fixedMessage;
            }
            m_LabelCache[vehicle] = message;
        }

        public void SetLocalized(Entity vehicle, string key, string fallback, string suffix = "")
        {
            SetLocalizedCore(vehicle, key, fallback, string.Empty, suffix);
        }

        public void SetPrefixedLocalized(Entity vehicle, string key, string fallback, string prefix, string suffix = "")
        {
            SetLocalizedCore(vehicle, key, fallback, prefix, suffix);
        }

        public void SetRuntime(
            Entity vehicle,
            VehicleLabelType type,
            int vehicleNumber,
            int currentSlotMinute = -1,
            int nextSlotMinute = -1,
            bool late = false,
            bool abnormal = false,
            bool includeHoldingInWaiting = true)
        {
            EnsureLocalizationCacheFresh();
            LocalizedLabelSpec spec = new LocalizedLabelSpec(
                type,
                vehicleNumber,
                currentSlotMinute,
                nextSlotMinute,
                late,
                abnormal,
                includeHoldingInWaiting);
            if (m_LocalizedSpecCache.TryGetValue(vehicle, out LocalizedLabelSpec cachedSpec)
                && cachedSpec.Equals(spec))
            {
                return;
            }

            ResolveRuntimeLabel(spec, out string key, out string fallback, out string prefix, out string suffix);
            string message = prefix + Label(key, fallback) + suffix;
            SetCore(vehicle, message);
            m_LocalizedSpecCache[vehicle] = spec;
        }

        private static void ResolveRuntimeLabel(
            LocalizedLabelSpec spec,
            out string key,
            out string fallback,
            out string prefix,
            out string suffix)
        {
            prefix = string.Empty;
            suffix = " #" + spec.VehicleNumber;
            switch (spec.Type)
            {
                case VehicleLabelType.Returning:
                    key = "Returning";
                    fallback = "回库中";
                    return;
                case VehicleLabelType.StopTimeoutAssist:
                    key = "StopTimeoutAssist";
                    fallback = "停站超时协助中";
                    return;
                case VehicleLabelType.PathFault:
                    key = "PathFault";
                    fallback = "寻路异常";
                    return;
                case VehicleLabelType.Holding:
                    if (spec.CurrentSlotMinute >= 0)
                    {
                        key = spec.Late ? "HoldingLate" : "Holding";
                        fallback = spec.Late ? "候车 补发" : "候车";
                        suffix = " " + ModRuntimeHostSystem.SlotStr(spec.CurrentSlotMinute) + suffix;
                    }
                    else
                    {
                        key = spec.IncludeHoldingInWaiting ? "HoldingWaitingDispatch" : "WaitingDispatch";
                        fallback = spec.IncludeHoldingInWaiting ? "候车 等待调度" : "等待调度";
                    }
                    return;
                case VehicleLabelType.WaitingDispatch:
                    key = "WaitingDispatch";
                    fallback = "等待调度";
                    return;
                case VehicleLabelType.GoingOrigin:
                    key = "GoingOrigin";
                    fallback = "前往始发站";
                    if (spec.NextSlotMinute >= 0)
                        suffix = " " + ModRuntimeHostSystem.SlotStr(spec.NextSlotMinute) + suffix;
                    return;
                case VehicleLabelType.Running:
                    key = spec.Abnormal ? "RunningAbnormal" : (spec.Late ? "RunningLate" : "Running");
                    fallback = spec.Abnormal ? "运行中(异常)" : (spec.Late ? "运行中 补发" : "运行中");
                    if (!spec.Abnormal)
                    {
                        if (spec.CurrentSlotMinute >= 0 || spec.CurrentSlotMinute == int.MinValue)
                            suffix = (spec.CurrentSlotMinute == int.MinValue ? "?" : ModRuntimeHostSystem.SlotStr(spec.CurrentSlotMinute))
                                + (spec.NextSlotMinute >= 0 ? "->" + ModRuntimeHostSystem.SlotStr(spec.NextSlotMinute) : string.Empty)
                                + suffix;
                        else if (spec.NextSlotMinute >= 0)
                            suffix = " " + ModRuntimeHostSystem.SlotStr(spec.NextSlotMinute) + suffix;
                    }
                    return;
                case VehicleLabelType.BoardingEnd:
                    key = "BoardingEnd";
                    fallback = "结束上客";
                    if (spec.CurrentSlotMinute >= 0)
                        suffix = " " + ModRuntimeHostSystem.SlotStr(spec.CurrentSlotMinute) + suffix;
                    return;
                case VehicleLabelType.StopTimeout:
                    key = "StopTimeout";
                    fallback = "停站超时";
                    return;
                case VehicleLabelType.BypassExpress:
                    key = "BypassExpress";
                    fallback = "待避快车";
                    prefix = "#" + spec.VehicleNumber + " ";
                    suffix = string.Empty;
                    return;
                default:
                    key = "AbnormalDeparture";
                    fallback = "运行中(异常离站)";
                    return;
            }
        }

        private void SetLocalizedCore(Entity vehicle, string key, string fallback, string prefix, string suffix)
        {
            EnsureLocalizationCacheFresh();
            key ??= string.Empty;
            fallback ??= string.Empty;
            prefix ??= string.Empty;
            suffix ??= string.Empty;

            LocalizedLabelSpec spec = new LocalizedLabelSpec(key, fallback, prefix, suffix);
            if (m_LocalizedSpecCache.TryGetValue(vehicle, out LocalizedLabelSpec cachedSpec)
                && cachedSpec.Equals(spec))
            {
                return;
            }

            string message = prefix + Label(key, fallback) + suffix;
            SetCore(vehicle, message);
            m_LocalizedSpecCache[vehicle] = spec;
        }

        private string Label(string key, string fallback)
        {
            string cacheKey = key + "\u001f" + fallback;
            if (m_LocalizedBaseCache.TryGetValue(cacheKey, out string cachedLabel))
            {
                return cachedLabel;
            }

            string localizationKey = "RapidTransit.VehicleLabel." + key;
            string translated = Names.Key(localizationKey);
            string label = string.Equals(translated, localizationKey, System.StringComparison.Ordinal)
                ? fallback ?? string.Empty
                : translated;
            m_LocalizedBaseCache[cacheKey] = label;
            return label;
        }

        private void EnsureLocalizationCacheFresh()
        {
            object activeDictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            if (ReferenceEquals(m_ActiveLocalizationDictionary, activeDictionary))
            {
                return;
            }

            m_ActiveLocalizationDictionary = activeDictionary;
            m_LocalizedBaseCache.Clear();
            m_LocalizedSpecCache.Clear();
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle != Entity.Null)
            {
                m_LabelCache.Remove(vehicle);
                m_LocalizedSpecCache.Remove(vehicle);
            }
        }

        public void Clear()
        {
            m_LabelCache.Clear();
            m_LocalizedSpecCache.Clear();
            m_LocalizedBaseCache.Clear();
            m_ActiveLocalizationDictionary = null;
        }

        private readonly struct LocalizedLabelSpec
        {
            public readonly VehicleLabelType Type;
            public readonly int VehicleNumber;
            public readonly int CurrentSlotMinute;
            public readonly int NextSlotMinute;
            public readonly bool Late;
            public readonly bool Abnormal;
            public readonly bool IncludeHoldingInWaiting;
            private readonly string m_Key;
            private readonly string m_Fallback;
            private readonly string m_Prefix;
            private readonly string m_Suffix;

            public LocalizedLabelSpec(string key, string fallback, string prefix, string suffix)
            {
                Type = default;
                VehicleNumber = 0;
                CurrentSlotMinute = 0;
                NextSlotMinute = 0;
                Late = false;
                Abnormal = false;
                IncludeHoldingInWaiting = false;
                m_Key = key;
                m_Fallback = fallback;
                m_Prefix = prefix;
                m_Suffix = suffix;
            }

            public LocalizedLabelSpec(
                VehicleLabelType type,
                int vehicleNumber,
                int currentSlotMinute,
                int nextSlotMinute,
                bool late,
                bool abnormal,
                bool includeHoldingInWaiting)
            {
                Type = type;
                VehicleNumber = vehicleNumber;
                CurrentSlotMinute = currentSlotMinute;
                NextSlotMinute = nextSlotMinute;
                Late = late;
                Abnormal = abnormal;
                IncludeHoldingInWaiting = includeHoldingInWaiting;
                m_Key = null;
                m_Fallback = null;
                m_Prefix = null;
                m_Suffix = null;
            }

            public bool Equals(LocalizedLabelSpec other)
            {
                return Type == other.Type
                    && VehicleNumber == other.VehicleNumber
                    && CurrentSlotMinute == other.CurrentSlotMinute
                    && NextSlotMinute == other.NextSlotMinute
                    && Late == other.Late
                    && Abnormal == other.Abnormal
                    && IncludeHoldingInWaiting == other.IncludeHoldingInWaiting
                    && string.Equals(m_Key, other.m_Key, System.StringComparison.Ordinal)
                    && string.Equals(m_Fallback, other.m_Fallback, System.StringComparison.Ordinal)
                    && string.Equals(m_Prefix, other.m_Prefix, System.StringComparison.Ordinal)
                    && string.Equals(m_Suffix, other.m_Suffix, System.StringComparison.Ordinal);
            }
        }
    }
}
