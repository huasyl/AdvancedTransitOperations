using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Colossal.Core;
using Game;
using Game.Audio;
using Game.Common;
using Game.Routes;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

namespace RapidTransitMod.Broadcasting
{
    internal sealed class Playback
    {
        private readonly BroadcastAccess m_Access;
        private readonly Config m_Config;
        private readonly Sequences m_Sequences;

        internal Playback(BroadcastAccess access, Config config)
        {
            m_Access = access ?? throw new ArgumentNullException(nameof(access));
            m_Config = config ?? throw new ArgumentNullException(nameof(config));
            Audio audio = new Audio(m_Access, m_Config);
            Clips clips = new Clips(m_Config, m_SequencesAccessor, m_PlatformSequencesAccessor);
            m_Sequences = new Sequences(m_Access, m_Config, audio, clips);
            clips.Attach(m_Sequences);
        }

        private IEnumerable<Sequence> m_SequencesAccessor() => m_Sequences.VehicleSequences;
        private IEnumerable<Sequence> m_PlatformSequencesAccessor() => m_Sequences.PlatformSequences;

        internal bool HasActive => m_Sequences.HasActive;

        internal void Start(Entity vehicle, TriggerContext context, string triggerId)
            => m_Sequences.Start(vehicle, context, triggerId);

        internal bool StartPlatform(
            string sequenceKey,
            Entity audioPositionEntity,
            TriggerContext context,
            BroadcastWorkbenchPlatformAnnouncementDto announcement,
            string fallbackTriggerId,
            Func<string, string> triggerLabel)
        {
            return m_Sequences.StartPlatform(sequenceKey, audioPositionEntity, context, announcement, fallbackTriggerId, triggerLabel);
        }

        internal bool ActiveForTrigger(Entity vehicle, string triggerId)
            => m_Sequences.ActiveForTrigger(vehicle, triggerId);

        internal void Tick(uint nowFrame) => m_Sequences.Tick(nowFrame);
        internal void RemoveVehicle(Entity vehicle) => m_Sequences.RemoveVehicle(vehicle);
        internal void Clear() => m_Sequences.Clear();
        internal void RemoveAsset(string assetName) => m_Sequences.RemoveAsset(assetName);
        internal void RemoveAsset(ModeScope scope, string assetName) => m_Sequences.RemoveAsset(scope, assetName);
        internal void RemoveAllAssets() => m_Sequences.RemoveAllAssets();
        internal void RemoveAllAssets(ModeScope scope) => m_Sequences.RemoveAllAssets(scope);
        internal void ApplyVolume() => m_Sequences.ApplyVolume();
        internal string Text(Entity vehicle) => m_Sequences.Text(vehicle);

        internal string AssetName(BroadcastWorkbenchRuleNodeDto node, TriggerContext context)
            => Sequences.AssetName(node, context);
    }

    internal sealed class Sequence
    {
        public Entity Vehicle;
        public Entity AudioPositionEntity;
        public string LineId = string.Empty;
        public string TriggerId = string.Empty;
        public TriggerContext Context;
        public List<BroadcastWorkbenchRuleDto> Rules = new List<BroadcastWorkbenchRuleDto>();
        public int RuleIndex;
        public int NodeIndex;
        public uint ResumeFrame;
        public float ResumeRealtime;
        public string PendingAssetName = string.Empty;
        public Task<AudioClip> PendingClipLoadTask;
        public AudioSource ActiveAudioSource;
        public string ActiveAudioAssetName = string.Empty;
    }

    internal sealed class Sequences
    {
        private readonly BroadcastAccess m_Access;
        private readonly Config m_Config;
        private readonly Audio m_Audio;
        private readonly Clips m_Clips;
        private readonly Dictionary<Entity, Sequence> m_ByVehicle = new Dictionary<Entity, Sequence>();
        private readonly Dictionary<string, Sequence> m_ByPlatformKey = new Dictionary<string, Sequence>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, string> m_LastEventTextByVehicle = new Dictionary<Entity, string>();

        internal Sequences(BroadcastAccess access, Config config, Audio audio, Clips clips)
        {
            m_Access = access;
            m_Config = config;
            m_Audio = audio;
            m_Clips = clips;
        }

        internal IEnumerable<Sequence> VehicleSequences => m_ByVehicle.Values;
        internal IEnumerable<Sequence> PlatformSequences => m_ByPlatformKey.Values;
        internal bool HasActive => m_ByVehicle.Count > 0 || m_ByPlatformKey.Count > 0;

        internal void Start(Entity vehicle, TriggerContext context, string triggerId)
        {
            if (vehicle == Entity.Null || string.IsNullOrEmpty(context.LineId) || string.IsNullOrEmpty(triggerId))
            {
                return;
            }

            if (!m_Config.RulesByLine.TryGetValue(context.LineId, out List<BroadcastWorkbenchRuleDto> rules)
                || rules == null
                || rules.Count == 0)
            {
                return;
            }

            List<BroadcastWorkbenchRuleDto> matchedRules = rules
                .Where(rule => rule != null
                    && string.Equals(rule.triggerId, triggerId, StringComparison.Ordinal)
                    && rule.nodes != null
                    && rule.nodes.Length > 0)
                .Select(m_Config.CloneRule)
                .Where(rule => rule != null)
                .ToList();
            if (matchedRules.Count == 0)
            {
                return;
            }

            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            StopVehicle(vehicle);
            Sequence state = new Sequence
            {
                Vehicle = vehicle,
                AudioPositionEntity = vehicle,
                LineId = context.LineId,
                TriggerId = triggerId,
                Context = context,
                Rules = matchedRules,
                ResumeFrame = nowFrame,
                ResumeRealtime = 0f
            };
            m_ByVehicle[vehicle] = state;
            m_LastEventTextByVehicle[vehicle] = BuildEventText(triggerId, context);
            m_Access.InvalidatePanel();
            Advance(state, nowFrame);
        }

        internal bool StartPlatform(
            string sequenceKey,
            Entity audioPositionEntity,
            TriggerContext context,
            BroadcastWorkbenchPlatformAnnouncementDto announcement,
            string fallbackTriggerId,
            Func<string, string> triggerLabel)
        {
            if (announcement == null
                || string.IsNullOrWhiteSpace(sequenceKey)
                || audioPositionEntity == Entity.Null
                || string.IsNullOrEmpty(context.LineId)
                || announcement.nodes == null
                || announcement.nodes.Length == 0)
            {
                return false;
            }

            string triggerId = string.IsNullOrWhiteSpace(announcement.triggerId)
                ? fallbackTriggerId
                : announcement.triggerId;
            BroadcastWorkbenchRuleDto rule = new BroadcastWorkbenchRuleDto
            {
                id = sequenceKey,
                title = string.IsNullOrWhiteSpace(announcement.title) ? (announcement.stationName ?? string.Empty) : announcement.title,
                triggerId = triggerId,
                trigger = triggerLabel?.Invoke(triggerId) ?? string.Empty,
                nodes = announcement.nodes
                    .Select(m_Config.CloneNode)
                    .Where(node => node != null)
                    .ToArray()
            };
            if (rule.nodes.Length == 0)
            {
                return false;
            }

            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            StopPlatform(sequenceKey);
            Sequence state = new Sequence
            {
                Vehicle = audioPositionEntity,
                AudioPositionEntity = audioPositionEntity,
                LineId = context.LineId,
                TriggerId = triggerId,
                Context = context,
                Rules = new List<BroadcastWorkbenchRuleDto> { rule },
                ResumeFrame = nowFrame,
                ResumeRealtime = 0f
            };
            m_ByPlatformKey[sequenceKey] = state;
            return Advance(state, nowFrame);
        }

        internal bool ActiveForTrigger(Entity vehicle, string triggerId)
        {
            return vehicle != Entity.Null
                && !string.IsNullOrEmpty(triggerId)
                && m_ByVehicle.TryGetValue(vehicle, out Sequence state)
                && state != null
                && string.Equals(state.TriggerId, triggerId, StringComparison.Ordinal);
        }

        internal void Tick(uint nowFrame)
        {
            if (!HasActive)
            {
                m_Clips.Prune(nowFrame);
                return;
            }

            List<Entity> completedVehicles = null;
            foreach (KeyValuePair<Entity, Sequence> entry in m_ByVehicle)
            {
                Sequence state = entry.Value;
                if (state == null || state.Vehicle == Entity.Null || !m_Access.EntityManager.Exists(state.Vehicle))
                {
                    completedVehicles ??= new List<Entity>();
                    completedVehicles.Add(entry.Key);
                    continue;
                }

                m_Audio.Move(state);
                if (Advance(state, nowFrame))
                {
                    continue;
                }

                completedVehicles ??= new List<Entity>();
                completedVehicles.Add(entry.Key);
            }

            List<string> completedPlatformKeys = null;
            foreach (KeyValuePair<string, Sequence> entry in m_ByPlatformKey)
            {
                Sequence state = entry.Value;
                if (state == null || state.Vehicle == Entity.Null || !m_Access.EntityManager.Exists(state.Vehicle))
                {
                    completedPlatformKeys ??= new List<string>();
                    completedPlatformKeys.Add(entry.Key);
                    continue;
                }

                m_Audio.Move(state);
                if (Advance(state, nowFrame))
                {
                    continue;
                }

                completedPlatformKeys ??= new List<string>();
                completedPlatformKeys.Add(entry.Key);
            }

            if (completedVehicles != null)
            {
                for (int i = 0; i < completedVehicles.Count; i++)
                {
                    Entity completedVehicle = completedVehicles[i];
                    StopVehicle(completedVehicle);
                    m_LastEventTextByVehicle.Remove(completedVehicle);
                }

                m_Access.InvalidatePanel();
            }

            if (completedPlatformKeys != null)
            {
                for (int i = 0; i < completedPlatformKeys.Count; i++)
                {
                    StopPlatform(completedPlatformKeys[i]);
                }
            }

            m_Clips.CompleteDetached(nowFrame);
            m_Clips.Prune(nowFrame);
        }

        private bool Advance(Sequence state, uint nowFrame)
        {
            if (state == null)
            {
                return false;
            }

            if (state.ActiveAudioSource != null)
            {
                if (state.ActiveAudioSource.isPlaying)
                {
                    return true;
                }

                m_Audio.Release(state);
            }

            if (state.PendingClipLoadTask != null)
            {
                if (!state.PendingClipLoadTask.IsCompleted)
                {
                    return true;
                }

                Task<AudioClip> completedTask = state.PendingClipLoadTask;
                string pendingAssetName = state.PendingAssetName ?? string.Empty;
                state.PendingClipLoadTask = null;
                state.PendingAssetName = string.Empty;
                string pendingCacheKey = m_Config.AssetCacheKey(state.LineId, pendingAssetName);
                if (!string.IsNullOrWhiteSpace(pendingCacheKey)
                    && m_Clips.TryLiveTask(pendingCacheKey, out Task<AudioClip> liveTask)
                    && ReferenceEquals(liveTask, completedTask))
                {
                    m_Clips.RemoveTask(pendingCacheKey);
                }

                AudioClip loadedClip = null;
                if (completedTask.Status == TaskStatus.RanToCompletion)
                {
                    loadedClip = completedTask.Result;
                }

                if (loadedClip == null)
                {
                    state.NodeIndex++;
                    return Advance(state, nowFrame);
                }

                m_Clips.Cache(pendingCacheKey, loadedClip, nowFrame);
                if (m_Audio.Play(state, pendingAssetName, loadedClip))
                {
                    state.NodeIndex++;
                    return true;
                }

                state.NodeIndex++;
                return Advance(state, nowFrame);
            }

            if (nowFrame < state.ResumeFrame
                || (state.ResumeRealtime > 0f && UnityEngine.Time.realtimeSinceStartup < state.ResumeRealtime))
            {
                return true;
            }

            while (state.RuleIndex < state.Rules.Count)
            {
                BroadcastWorkbenchRuleDto rule = state.Rules[state.RuleIndex];
                BroadcastWorkbenchRuleNodeDto[] nodes = rule?.nodes ?? Array.Empty<BroadcastWorkbenchRuleNodeDto>();
                if (state.NodeIndex >= nodes.Length)
                {
                    state.RuleIndex++;
                    state.NodeIndex = 0;
                    continue;
                }

                BroadcastWorkbenchRuleNodeDto node = nodes[state.NodeIndex];
                if (node == null)
                {
                    state.NodeIndex++;
                    continue;
                }

                if (string.Equals(node.type, "delay", StringComparison.Ordinal))
                {
                    float delaySeconds = node.delaySeconds > 0f ? node.delaySeconds : 0f;
                    state.NodeIndex++;
                    if (delaySeconds > 0)
                    {
                        state.ResumeFrame = nowFrame;
                        state.ResumeRealtime = UnityEngine.Time.realtimeSinceStartup + delaySeconds;
                        return true;
                    }

                    continue;
                }

                string assetName = AssetName(node, state.Context);
                if (string.IsNullOrEmpty(assetName))
                {
                    state.NodeIndex++;
                    continue;
                }

                string assetCacheKey = m_Config.AssetCacheKey(state.LineId, assetName);
                if (m_Clips.Get(assetCacheKey, nowFrame, out AudioClip cachedClip))
                {
                    if (m_Audio.Play(state, assetName, cachedClip))
                    {
                        state.NodeIndex++;
                        return true;
                    }

                    state.NodeIndex++;
                    continue;
                }

                Task<AudioClip> loadTask = m_Clips.BeginLoad(state.LineId, assetName);
                if (loadTask == null)
                {
                    state.NodeIndex++;
                    continue;
                }

                state.PendingAssetName = assetName;
                state.PendingClipLoadTask = loadTask;
                return true;
            }

            return false;
        }

        private void StopVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
            {
                return;
            }

            if (!m_ByVehicle.TryGetValue(vehicle, out Sequence state))
            {
                return;
            }

            m_Audio.Release(state);
            state.PendingClipLoadTask = null;
            state.PendingAssetName = string.Empty;
            m_ByVehicle.Remove(vehicle);
        }

        private void StopPlatform(string sequenceKey)
        {
            if (string.IsNullOrWhiteSpace(sequenceKey)
                || !m_ByPlatformKey.TryGetValue(sequenceKey, out Sequence state))
            {
                return;
            }

            m_Audio.Release(state);
            state.PendingClipLoadTask = null;
            state.PendingAssetName = string.Empty;
            m_ByPlatformKey.Remove(sequenceKey);
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            StopVehicle(vehicle);
            m_LastEventTextByVehicle.Remove(vehicle);
        }

        internal void Clear()
        {
            foreach (KeyValuePair<Entity, Sequence> entry in m_ByVehicle)
            {
                m_Audio.Release(entry.Value);
            }

            foreach (KeyValuePair<string, Sequence> entry in m_ByPlatformKey)
            {
                m_Audio.Release(entry.Value);
            }

            m_ByVehicle.Clear();
            m_ByPlatformKey.Clear();
            m_LastEventTextByVehicle.Clear();
            m_Clips.Clear();
        }

        internal void RemoveAsset(string assetName)
        {
            RemoveAsset(ModeScope.DefaultWorkbench, assetName);
        }

        internal void RemoveAsset(ModeScope scope, string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return;
            }

            List<Entity> affectedVehicles = null;
            foreach (KeyValuePair<Entity, Sequence> entry in m_ByVehicle)
            {
                Sequence state = entry.Value;
                if (state == null || !MatchesRuntimeScope(scope, state.LineId))
                {
                    continue;
                }

                if (string.Equals(state.ActiveAudioAssetName, assetName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state.PendingAssetName, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    affectedVehicles ??= new List<Entity>();
                    affectedVehicles.Add(entry.Key);
                }
            }

            List<string> affectedPlatformKeys = null;
            foreach (KeyValuePair<string, Sequence> entry in m_ByPlatformKey)
            {
                Sequence state = entry.Value;
                if (state == null || !MatchesRuntimeScope(scope, state.LineId))
                {
                    continue;
                }

                if (string.Equals(state.ActiveAudioAssetName, assetName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state.PendingAssetName, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    affectedPlatformKeys ??= new List<string>();
                    affectedPlatformKeys.Add(entry.Key);
                }
            }

            if (affectedVehicles != null)
            {
                for (int i = 0; i < affectedVehicles.Count; i++)
                {
                    StopVehicle(affectedVehicles[i]);
                }
            }

            if (affectedPlatformKeys != null)
            {
                for (int i = 0; i < affectedPlatformKeys.Count; i++)
                {
                    StopPlatform(affectedPlatformKeys[i]);
                }
            }

            m_Clips.RemoveAsset(scope.Token + ":" + assetName);
        }

        internal void RemoveAllAssets()
        {
            RemoveAllAssets(ModeScope.DefaultWorkbench);
        }

        internal void RemoveAllAssets(ModeScope scope)
        {
            foreach (Entity vehicle in m_ByVehicle
                .Where(entry => entry.Value != null && MatchesRuntimeScope(scope, entry.Value.LineId))
                .Select(entry => entry.Key)
                .ToArray())
            {
                StopVehicle(vehicle);
            }

            foreach (string platformKey in m_ByPlatformKey
                .Where(entry => entry.Value != null && MatchesRuntimeScope(scope, entry.Value.LineId))
                .Select(entry => entry.Key)
                .ToArray())
            {
                StopPlatform(platformKey);
            }

            m_Clips.RemoveMode(scope);
        }

        internal void ApplyVolume() => m_Audio.ApplyVolume(m_ByVehicle.Values, m_ByPlatformKey.Values);

        private static bool MatchesRuntimeScope(ModeScope scope, string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
            {
                return false;
            }

            if (LineIdentityService.TryGetMode(lineId, out TransitMode mode) && mode != TransitMode.Unknown)
            {
                return mode == scope.Mode;
            }

            return lineId.IndexOf(':') < 0 && scope.Mode == ModeScope.DefaultWorkbench.Mode;
        }

        internal string Text(Entity vehicle)
        {
            return m_LastEventTextByVehicle.TryGetValue(vehicle, out string text)
                ? text ?? string.Empty
                : string.Empty;
        }

        internal static string AssetName(BroadcastWorkbenchRuleNodeDto node, TriggerContext context)
        {
            if (node == null)
            {
                return string.Empty;
            }

            if (string.Equals(node.type, "asset", StringComparison.Ordinal))
            {
                return node.name ?? string.Empty;
            }

            if (!string.Equals(node.type, "variable", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            int langIndex = node.langIndex > 0 ? node.langIndex : 1;

            switch (node.nameKey ?? string.Empty)
            {
                case "broadcast.variable.current":
                    return Stations.AssetName(context.CurrentStationBindings, langIndex);
                case "broadcast.variable.next":
                    return Stations.AssetName(context.NextStationBindings, langIndex);
                case "broadcast.variable.terminal":
                    return Stations.AssetName(context.TerminalStationBindings, langIndex);
                case "broadcast.variable.turnback":
                    return Stations.AssetName(context.TurnbackStationBindings, langIndex);
                default:
                    return string.Empty;
            }
        }

        private string BuildEventText(string triggerId, TriggerContext context)
        {
            string triggerLabel;
            switch (triggerId)
            {
                case "stop_and_open":
                    triggerLabel = SelectPanel.IsChineseLocale() ? "停站上客" : "stop_and_open";
                    break;
                case "leave_station":
                    triggerLabel = SelectPanel.IsChineseLocale() ? "列车离站" : "leave_station";
                    break;
                case "approach_station":
                    triggerLabel = SelectPanel.IsChineseLocale() ? "即将进站" : "approach_station";
                    break;
                case "platform_approach_station":
                    triggerLabel = SelectPanel.IsChineseLocale() ? "站台即将进站" : "platform_approach_station";
                    break;
                case "platform_idle_clear":
                    triggerLabel = SelectPanel.IsChineseLocale() ? "站台空闲时" : "platform_idle_clear";
                    break;
                case "mid_route":
                    triggerLabel = SelectPanel.IsChineseLocale() ? "区间运行中" : "mid_route";
                    break;
                case "bypass_waiting":
                    triggerLabel = SelectPanel.IsChineseLocale() ? "待避中" : "bypass_waiting";
                    break;
                default:
                    triggerLabel = triggerId ?? string.Empty;
                    break;
            }

            if (string.IsNullOrEmpty(context.NextStationName))
            {
                return triggerLabel + " | " + (context.CurrentStationName ?? "-");
            }

            return triggerLabel + " | " + (context.CurrentStationName ?? "-") + " -> " + context.NextStationName;
        }
    }

    internal sealed class Audio
    {
        private const float VolumeScalarMin = 5f;
        private const float VolumeScalarMax = 60f;
        private static readonly FieldInfo s_WorldGroupField =
            typeof(AudioManager).GetField("m_WorldGroup", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly BroadcastAccess m_Access;
        private readonly Config m_Config;

        internal Audio(BroadcastAccess access, Config config)
        {
            m_Access = access;
            m_Config = config;
        }

        internal bool Play(Sequence state, string assetName, AudioClip clip)
        {
            Entity audioPositionEntity = state?.AudioPositionEntity ?? Entity.Null;
            if (state == null
                || clip == null
                || audioPositionEntity == Entity.Null
                || !TryPosition(audioPositionEntity, out Vector3 position))
            {
                return false;
            }

            AudioSource audioSource = AudioManager.AudioSourcePool.Get();
            audioSource.clip = clip;
            audioSource.outputAudioMixerGroup = m_Access.WorldMixerGroup(s_WorldGroupField);
            audioSource.transform.position = position;
            audioSource.pitch = 1f;
            audioSource.volume = Volume(state.LineId);
            audioSource.loop = false;
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 1f;
            audioSource.spread = 140f;
            audioSource.dopplerLevel = 0f;
            audioSource.minDistance = 90f;
            audioSource.maxDistance = 400f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.ignoreListenerPause = false;
            AudioManager.AudioSourcePool.Play(audioSource);
            state.ActiveAudioSource = audioSource;
            state.ActiveAudioAssetName = assetName ?? string.Empty;
            return true;
        }

        internal void Release(Sequence state)
        {
            if (state?.ActiveAudioSource == null)
            {
                return;
            }

            AudioSource audioSource = state.ActiveAudioSource;
            state.ActiveAudioSource = null;
            state.ActiveAudioAssetName = string.Empty;
            AudioManager.AudioSourcePool.Release(audioSource);
        }

        internal void Move(Sequence state)
        {
            Entity audioPositionEntity = state?.AudioPositionEntity ?? Entity.Null;
            if (state?.ActiveAudioSource == null || audioPositionEntity == Entity.Null)
            {
                return;
            }

            if (!TryPosition(audioPositionEntity, out Vector3 position))
            {
                return;
            }

            state.ActiveAudioSource.transform.position = position;
        }

        internal void ApplyVolume(IEnumerable<Sequence> vehicleSequences, IEnumerable<Sequence> platformSequences)
        {
            foreach (Sequence state in vehicleSequences)
            {
                if (state?.ActiveAudioSource != null)
                {
                    state.ActiveAudioSource.volume = Volume(state.LineId);
                }
            }

            foreach (Sequence state in platformSequences)
            {
                if (state?.ActiveAudioSource != null)
                {
                    state.ActiveAudioSource.volume = Volume(state.LineId);
                }
            }
        }

        private float Volume(string lineId)
        {
            return Mathf.Lerp(
                VolumeScalarMin,
                VolumeScalarMax,
                m_Config.ClampVolume(m_Config.VolumeForLine(lineId)) / 100f);
        }

        private bool TryPosition(Entity vehicle, out Vector3 position)
        {
            position = default;
            if (vehicle == Entity.Null || !m_Access.EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                return false;
            }

            Game.Objects.Transform transform = m_Access.EntityManager.GetComponentData<Game.Objects.Transform>(vehicle);
            position = new Vector3(transform.m_Position.x, transform.m_Position.y, transform.m_Position.z);
            return true;
        }
    }

    internal sealed class ClipEntry
    {
        public AudioClip Clip;
        public uint LastAccessFrame;
    }

    internal sealed class Clips
    {
        private const int Limit = 24;
        private readonly Config m_Config;
        private readonly Func<IEnumerable<Sequence>> m_VehicleSequences;
        private readonly Func<IEnumerable<Sequence>> m_PlatformSequences;
        private readonly Dictionary<string, ClipEntry> m_Cache = new Dictionary<string, ClipEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<AudioClip>> m_LoadTasks = new Dictionary<string, Task<AudioClip>>(StringComparer.OrdinalIgnoreCase);
        private Sequences m_Sequences;

        internal Clips(
            Config config,
            Func<IEnumerable<Sequence>> vehicleSequences,
            Func<IEnumerable<Sequence>> platformSequences)
        {
            m_Config = config;
            m_VehicleSequences = vehicleSequences;
            m_PlatformSequences = platformSequences;
        }

        internal void Attach(Sequences sequences) => m_Sequences = sequences;

        internal bool Get(string assetName, uint nowFrame, out AudioClip clip)
        {
            clip = null;
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return false;
            }

            if (!m_Cache.TryGetValue(assetName, out ClipEntry entry)
                || entry?.Clip == null)
            {
                return false;
            }

            entry.LastAccessFrame = nowFrame;
            clip = entry.Clip;
            return true;
        }

        internal void Cache(string assetName, AudioClip clip, uint nowFrame)
        {
            if (string.IsNullOrWhiteSpace(assetName) || clip == null)
            {
                return;
            }

            m_Cache[assetName] = new ClipEntry
            {
                Clip = clip,
                LastAccessFrame = nowFrame
            };
        }

        internal Task<AudioClip> BeginLoad(string lineId, string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return null;
            }

            string cacheKey = m_Config.AssetCacheKey(lineId, assetName);
            if (m_LoadTasks.TryGetValue(cacheKey, out Task<AudioClip> existingTask))
            {
                return existingTask;
            }

            BroadcastWorkbenchAssetDto asset = m_Config.AssetsForLine(lineId).FirstOrDefault(candidate =>
                string.Equals(candidate?.name, assetName, StringComparison.OrdinalIgnoreCase));
            string assetPath = m_Config.AssetPath(asset?.path);
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
            {
                return null;
            }

            AudioType audioType = m_Config.AudioType(assetPath);
            if (audioType == AudioType.UNKNOWN)
            {
                return null;
            }

            Task<AudioClip> loadTask = LoadAsync(assetName, assetPath, audioType);
            m_LoadTasks[cacheKey] = loadTask;
            return loadTask;
        }

        private async Task<AudioClip> LoadAsync(string assetName, string assetPath, AudioType audioType)
        {
            return await OnMain(async () =>
            {
                using UnityWebRequest request = m_Config.AudioRequest(assetPath, audioType);
                DownloadHandlerAudioClip downloadHandler = request.downloadHandler as DownloadHandlerAudioClip;
                if (downloadHandler != null)
                {
                    downloadHandler.streamAudio = false;
                }

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result == UnityWebRequest.Result.ConnectionError
                    || request.result == UnityWebRequest.Result.ProtocolError
                    || request.result == UnityWebRequest.Result.DataProcessingError)
                {
                    return null;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    return null;
                }

                clip.name = assetName;
                return clip;
            });
        }

        internal static async Task<T> OnMain<T>(Func<Task<T>> action)
        {
            TaskCompletionSource<T> completion = new TaskCompletionSource<T>();
            MainThreadDispatcher.RunOnMainThread(async () =>
            {
                try
                {
                    completion.TrySetResult(await action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return await completion.Task;
        }

        internal void Prune(uint nowFrame)
        {
            if (m_Cache.Count <= Limit)
            {
                return;
            }

            List<KeyValuePair<string, ClipEntry>> evictionCandidates = m_Cache
                .OrderBy(entry => entry.Value?.LastAccessFrame ?? nowFrame)
                .ToList();
            for (int i = 0; i < evictionCandidates.Count && m_Cache.Count > Limit; i++)
            {
                KeyValuePair<string, ClipEntry> candidate = evictionCandidates[i];
                ClipEntry entry = candidate.Value;
                if (entry?.Clip == null || InUse(entry.Clip))
                {
                    continue;
                }

                Destroy(entry.Clip);
                m_Cache.Remove(candidate.Key);
            }
        }

        internal void CompleteDetached(uint nowFrame)
        {
            if (m_LoadTasks.Count == 0)
            {
                return;
            }

            List<string> completedKeys = null;
            foreach (KeyValuePair<string, Task<AudioClip>> entry in m_LoadTasks)
            {
                if (!entry.Value.IsCompleted || Pending(entry.Value))
                {
                    continue;
                }

                completedKeys ??= new List<string>();
                completedKeys.Add(entry.Key);
                if (entry.Value.Status == TaskStatus.RanToCompletion && entry.Value.Result != null)
                {
                    Cache(entry.Key, entry.Value.Result, nowFrame);
                }
            }

            if (completedKeys == null)
            {
                return;
            }

            for (int i = 0; i < completedKeys.Count; i++)
            {
                m_LoadTasks.Remove(completedKeys[i]);
            }
        }

        internal bool TryLiveTask(string assetName, out Task<AudioClip> task)
            => m_LoadTasks.TryGetValue(assetName, out task);

        internal void RemoveTask(string assetName) => m_LoadTasks.Remove(assetName);

        private void RemoveDetachedLoadTask(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName)
                || !m_LoadTasks.TryGetValue(assetName, out Task<AudioClip> task))
            {
                return;
            }

            m_LoadTasks.Remove(assetName);
            DisposeDetachedLoadResult(task);
        }

        private void DisposeDetachedLoadResult(Task<AudioClip> task)
        {
            if (task == null)
            {
                return;
            }

            if (task.IsCompleted)
            {
                MainThreadDispatcher.RunOnMainThread(() => DestroyCompletedLoadResult(task));
                return;
            }

            task.ContinueWith(
                completedTask => MainThreadDispatcher.RunOnMainThread(() => DestroyCompletedLoadResult(completedTask)),
                TaskScheduler.Default);
        }

        private void DestroyCompletedLoadResult(Task<AudioClip> task)
        {
            if (task == null || task.Status != TaskStatus.RanToCompletion)
            {
                return;
            }

            AudioClip clip = null;
            try
            {
                clip = task.Result;
            }
            catch
            {
                return;
            }

            if (clip != null && !InUse(clip))
            {
                Destroy(clip);
            }
        }

        private bool Pending(Task<AudioClip> task)
        {
            if (task == null)
            {
                return false;
            }

            foreach (Sequence state in m_VehicleSequences())
            {
                if (state?.PendingClipLoadTask != null && ReferenceEquals(state.PendingClipLoadTask, task))
                {
                    return true;
                }
            }

            foreach (Sequence state in m_PlatformSequences())
            {
                if (state?.PendingClipLoadTask != null && ReferenceEquals(state.PendingClipLoadTask, task))
                {
                    return true;
                }
            }

            return false;
        }

        private bool InUse(AudioClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            foreach (Sequence state in m_VehicleSequences())
            {
                if (state?.ActiveAudioSource != null && state.ActiveAudioSource.clip == clip)
                {
                    return true;
                }
            }

            foreach (Sequence state in m_PlatformSequences())
            {
                if (state?.ActiveAudioSource != null && state.ActiveAudioSource.clip == clip)
                {
                    return true;
                }
            }

            return false;
        }

        internal void RemoveAsset(string assetName)
        {
            RemoveDetachedLoadTask(assetName);
            if (!m_Cache.TryGetValue(assetName, out ClipEntry cacheEntry))
            {
                return;
            }

            if (!InUse(cacheEntry?.Clip))
            {
                Destroy(cacheEntry?.Clip);
            }

            m_Cache.Remove(assetName);
        }

        internal void RemoveMode(ModeScope scope)
        {
            string prefix = scope.Token + ":";
            foreach (string key in m_LoadTasks.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                RemoveDetachedLoadTask(key);
            }

            foreach (string key in m_Cache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (m_Cache.TryGetValue(key, out ClipEntry cacheEntry) && !InUse(cacheEntry?.Clip))
                {
                    Destroy(cacheEntry?.Clip);
                }

                m_Cache.Remove(key);
            }
        }

        internal void Clear()
        {
            m_LoadTasks.Clear();
            foreach (ClipEntry entry in m_Cache.Values)
            {
                Destroy(entry?.Clip);
            }

            m_Cache.Clear();
        }

        private static void Destroy(AudioClip clip)
        {
            if (clip == null)
            {
                return;
            }

            try
            {
                clip.UnloadAudioData();
            }
            catch
            {
            }

            UnityEngine.Object.Destroy(clip);
        }
    }
}
