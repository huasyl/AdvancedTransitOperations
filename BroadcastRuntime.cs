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

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private sealed class BroadcastResolvedStation
        {
            public Entity StopEntity;
            public int WaypointIndex;
            public int Order;
            public string StationId = string.Empty;
            public string Name = string.Empty;
        }

        private sealed class BroadcastRuntimeSequenceState
        {
            public Entity Vehicle;
            public string LineId = string.Empty;
            public BroadcastTriggerContext Context;
            public List<BroadcastWorkbenchRuleDto> Rules = new List<BroadcastWorkbenchRuleDto>();
            public int RuleIndex;
            public int NodeIndex;
            public uint ResumeFrame;
            public string PendingAssetName = string.Empty;
            public Task<AudioClip> PendingClipLoadTask;
            public AudioSource ActiveAudioSource;
            public string ActiveAudioAssetName = string.Empty;
        }

        private sealed class BroadcastRuntimeClipCacheEntry
        {
            public AudioClip Clip;
            public uint LastAccessFrame;
        }

        private struct BroadcastProgressTriggerState
        {
            public int CurrentStopWaypointIndex;
            public int NextStopWaypointIndex;
            public bool MidRouteTriggered;
            public bool ApproachTriggered;
        }

        private readonly struct BroadcastVehicleStationContext
        {
            public readonly string LineId;
            public readonly Entity CurrentStopEntity;
            public readonly int CurrentStopWaypointIndex;
            public readonly string CurrentStationId;
            public readonly string CurrentStationName;
            public readonly int NextStopWaypointIndex;
            public readonly string NextStationId;
            public readonly string NextStationName;
            public readonly string TerminalStationId;
            public readonly string TerminalStationName;

            public BroadcastVehicleStationContext(
                string lineId,
                Entity currentStopEntity,
                int currentStopWaypointIndex,
                string currentStationId,
                string currentStationName,
                int nextStopWaypointIndex,
                string nextStationId,
                string nextStationName,
                string terminalStationId,
                string terminalStationName)
            {
                LineId = lineId;
                CurrentStopEntity = currentStopEntity;
                CurrentStopWaypointIndex = currentStopWaypointIndex;
                CurrentStationId = currentStationId ?? string.Empty;
                CurrentStationName = currentStationName ?? string.Empty;
                NextStopWaypointIndex = nextStopWaypointIndex;
                NextStationId = nextStationId ?? string.Empty;
                NextStationName = nextStationName ?? string.Empty;
                TerminalStationId = terminalStationId ?? string.Empty;
                TerminalStationName = terminalStationName ?? string.Empty;
            }
        }

        private sealed class BroadcastLineStationContextCache
        {
            public ulong Signature;
            public BroadcastResolvedStation[] Stations = Array.Empty<BroadcastResolvedStation>();
            public BroadcastResolvedStation[] StationByWaypoint = Array.Empty<BroadcastResolvedStation>();
            public int[] StationOrderByWaypoint = Array.Empty<int>();
            public int[] NormalizedStationWaypointByWaypoint = Array.Empty<int>();
            public int[] NextDistinctStationWaypointByWaypoint = Array.Empty<int>();
            public int TerminalStationWaypointIndex = -1;
        }

        private const int BroadcastRuntimeClipCacheLimit = 24;
        private const float BroadcastMidRouteProgressThreshold = 0.5f;
        private const int BroadcastApproachRemainingAtomThreshold = 4;
        private static readonly FieldInfo s_AudioManagerWorldGroupField =
            typeof(AudioManager).GetField("m_WorldGroup", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Dictionary<Entity, Entity> m_BroadcastLastStopAndOpenStopByVehicle = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, Entity> m_BroadcastLastLeaveStationStopByVehicle = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, Entity> m_BroadcastLastBypassWaitingStopByVehicle = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, BroadcastProgressTriggerState> m_BroadcastProgressTriggerStateByVehicle =
            new Dictionary<Entity, BroadcastProgressTriggerState>();
        private readonly Dictionary<Entity, string> m_BroadcastCurrentStationNameByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BroadcastNextStationNameByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BroadcastLastEventTextByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, int> m_BroadcastCurrentStopWaypointIndexByVehicle = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_BroadcastNextStopWaypointIndexByVehicle = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, BroadcastLineStationContextCache> m_BroadcastLineStationContextCaches =
            new Dictionary<Entity, BroadcastLineStationContextCache>();
        private readonly Dictionary<Entity, BroadcastRuntimeSequenceState> m_BroadcastSequenceStateByVehicle =
            new Dictionary<Entity, BroadcastRuntimeSequenceState>();
        private readonly Dictionary<string, BroadcastRuntimeClipCacheEntry> m_BroadcastRuntimeClipCache =
            new Dictionary<string, BroadcastRuntimeClipCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<AudioClip>> m_BroadcastRuntimeClipLoadTasks =
            new Dictionary<string, Task<AudioClip>>(StringComparer.OrdinalIgnoreCase);

        private void HandleBroadcastStopAndOpenTrigger(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex)
        {
            EmitBroadcastWaypointTrigger(vehicle, line, waypoints, currentWaypointIndex, "stop_and_open");
        }

        private void HandleBroadcastLeaveStationTrigger(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int previousWaypointIndex)
        {
            EmitBroadcastWaypointTrigger(vehicle, line, waypoints, previousWaypointIndex, "leave_station");
        }

        private void HandleBroadcastBypassWaitingTrigger(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex)
        {
            EmitBroadcastWaypointTrigger(vehicle, line, waypoints, currentWaypointIndex, "bypass_waiting");
        }

        private void HandleBroadcastApproachStationTrigger(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int previousWaypointIndex)
        {
            EmitBroadcastWaypointTrigger(vehicle, line, waypoints, previousWaypointIndex, "approach_station");
        }

        private void HandleBroadcastMidRouteTrigger(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int previousWaypointIndex)
        {
            EmitBroadcastWaypointTrigger(vehicle, line, waypoints, previousWaypointIndex, "mid_route");
        }

        private void TickBroadcastProgressTriggers(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || boarding
                || waypoints.Length < 2
                || !ShouldBroadcastForTrackedVehicle(vehicle)
                || !TryGetRouteProgress(vehicle, out int progressNextWaypointIndex, out float segmentPosition)
                || !TryResolveBroadcastVehicleStationContext(vehicle, line, waypoints, -1, out BroadcastVehicleStationContext stationContext))
            {
                m_BroadcastProgressTriggerStateByVehicle.Remove(vehicle);
                return;
            }

            progressNextWaypointIndex = Mathf.Clamp(progressNextWaypointIndex, 0, waypoints.Length - 1);
            float normalizedProgress = Mathf.Clamp01(segmentPosition);
            if (!m_BroadcastProgressTriggerStateByVehicle.TryGetValue(vehicle, out BroadcastProgressTriggerState state)
                || state.CurrentStopWaypointIndex != stationContext.CurrentStopWaypointIndex
                || state.NextStopWaypointIndex != stationContext.NextStopWaypointIndex)
            {
                state = new BroadcastProgressTriggerState
                {
                    CurrentStopWaypointIndex = stationContext.CurrentStopWaypointIndex,
                    NextStopWaypointIndex = stationContext.NextStopWaypointIndex,
                    MidRouteTriggered = false,
                    ApproachTriggered = false
                };
            }

            if (!state.MidRouteTriggered
                && progressNextWaypointIndex == stationContext.NextStopWaypointIndex
                && normalizedProgress >= BroadcastMidRouteProgressThreshold)
            {
                HandleBroadcastMidRouteTrigger(vehicle, line, waypoints, stationContext.CurrentStopWaypointIndex);
                state.MidRouteTriggered = true;
            }

            if (!state.ApproachTriggered
                && IsBroadcastVehicleWithinApproachAtomWindow(
                    vehicle,
                    line,
                    waypoints,
                    stationContext.NextStopWaypointIndex))
            {
                HandleBroadcastApproachStationTrigger(vehicle, line, waypoints, stationContext.CurrentStopWaypointIndex);
                state.ApproachTriggered = true;
            }

            m_BroadcastProgressTriggerStateByVehicle[vehicle] = state;
        }

        private bool IsBroadcastVehicleWithinApproachAtomWindow(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int nextStopWaypointIndex)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || nextStopWaypointIndex < 0
                || nextStopWaypointIndex >= waypoints.Length
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor)
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    nextStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out int windowStart,
                    out _))
            {
                return false;
            }

            if (cursor.AtomCursorIndex >= windowStart)
            {
                return false;
            }

            int remainingAtoms = windowStart - cursor.AtomCursorIndex;
            return remainingAtoms <= BroadcastApproachRemainingAtomThreshold;
        }

        private void EmitBroadcastWaypointTrigger(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            string triggerId)
        {
            if (!ShouldBroadcastForTrackedVehicle(vehicle))
            {
                return;
            }

            if (!TryBuildBroadcastTriggerContext(vehicle, line, waypoints, waypointIndex, out BroadcastTriggerContext context))
            {
                return;
            }

            if (IsDuplicateBroadcastTrigger(vehicle, triggerId, context.CurrentStopEntity))
            {
                return;
            }

            RememberBroadcastTriggerStop(vehicle, triggerId, context.CurrentStopEntity);
            UpdateBroadcastVehiclePanelState(vehicle, context.CurrentStationName, context.NextStationName);
            TryStartBroadcastSequence(vehicle, context, triggerId);
        }

        private bool ShouldBroadcastForTrackedVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null || m_CameraUpdateSystem == null)
            {
                return false;
            }

            OrbitCameraController orbitCameraController = m_CameraUpdateSystem.orbitCameraController;
            if (orbitCameraController == null
                || !ReferenceEquals(m_CameraUpdateSystem.activeCameraController, orbitCameraController))
            {
                return false;
            }

            return orbitCameraController.followedEntity == vehicle;
        }

        private bool IsDuplicateBroadcastTrigger(Entity vehicle, string triggerId, Entity currentStop)
        {
            if (vehicle == Entity.Null || currentStop == Entity.Null)
            {
                return false;
            }

            switch (triggerId ?? string.Empty)
            {
                case "stop_and_open":
                    return m_BroadcastLastStopAndOpenStopByVehicle.TryGetValue(vehicle, out Entity stopAndOpenStop)
                        && stopAndOpenStop == currentStop;
                case "leave_station":
                    return m_BroadcastLastLeaveStationStopByVehicle.TryGetValue(vehicle, out Entity leaveStop)
                        && leaveStop == currentStop;
                case "bypass_waiting":
                    return m_BroadcastLastBypassWaitingStopByVehicle.TryGetValue(vehicle, out Entity bypassStop)
                        && bypassStop == currentStop;
                default:
                    return false;
            }
        }

        private void RememberBroadcastTriggerStop(Entity vehicle, string triggerId, Entity currentStop)
        {
            if (vehicle == Entity.Null || currentStop == Entity.Null)
            {
                return;
            }

            switch (triggerId ?? string.Empty)
            {
                case "stop_and_open":
                    m_BroadcastLastStopAndOpenStopByVehicle[vehicle] = currentStop;
                    break;
                case "leave_station":
                    m_BroadcastLastLeaveStationStopByVehicle[vehicle] = currentStop;
                    break;
                case "bypass_waiting":
                    m_BroadcastLastBypassWaitingStopByVehicle[vehicle] = currentStop;
                    break;
            }
        }

        private void UpdateBroadcastVehiclePanelState(Entity vehicle, string currentStationName, string nextStationName)
        {
            if (vehicle == Entity.Null)
            {
                return;
            }

            m_BroadcastCurrentStationNameByVehicle[vehicle] = currentStationName ?? string.Empty;
            m_BroadcastNextStationNameByVehicle[vehicle] = nextStationName ?? string.Empty;
        }

        private void TryStartBroadcastSequence(Entity vehicle, BroadcastTriggerContext context, string triggerId)
        {
            if (vehicle == Entity.Null || string.IsNullOrEmpty(context.LineId) || string.IsNullOrEmpty(triggerId))
            {
                return;
            }

            if (!m_BroadcastLineRules.TryGetValue(context.LineId, out List<BroadcastWorkbenchRuleDto> rules)
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
                .Select(CloneBroadcastWorkbenchRule)
                .Where(rule => rule != null)
                .ToList();
            if (matchedRules.Count == 0)
            {
                return;
            }

            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            StopBroadcastRuntimeSequence(vehicle);
            BroadcastRuntimeSequenceState state = new BroadcastRuntimeSequenceState
            {
                Vehicle = vehicle,
                LineId = context.LineId,
                Context = context,
                Rules = matchedRules,
                ResumeFrame = nowFrame
            };
            m_BroadcastSequenceStateByVehicle[vehicle] = state;
            m_BroadcastLastEventTextByVehicle[vehicle] = BuildBroadcastEventText(triggerId, context);
            InvalidatePanelData();
            AdvanceBroadcastRuntimeSequence(state, nowFrame);
        }

        private void TickBroadcastRuntime(uint nowFrame)
        {
            if (m_BroadcastSequenceStateByVehicle.Count == 0)
            {
                PruneBroadcastRuntimeClipCache(nowFrame);
                return;
            }

            List<Entity> completedVehicles = null;
            foreach (KeyValuePair<Entity, BroadcastRuntimeSequenceState> entry in m_BroadcastSequenceStateByVehicle)
            {
                BroadcastRuntimeSequenceState state = entry.Value;
                if (state == null || state.Vehicle == Entity.Null || !EntityManager.Exists(state.Vehicle))
                {
                    completedVehicles ??= new List<Entity>();
                    completedVehicles.Add(entry.Key);
                    continue;
                }

                UpdateBroadcastRuntimeAudioSourcePosition(state);
                if (AdvanceBroadcastRuntimeSequence(state, nowFrame))
                {
                    continue;
                }

                completedVehicles ??= new List<Entity>();
                completedVehicles.Add(entry.Key);
            }

            if (completedVehicles != null)
            {
                for (int i = 0; i < completedVehicles.Count; i++)
                {
                    Entity completedVehicle = completedVehicles[i];
                    StopBroadcastRuntimeSequence(completedVehicle);
                    m_BroadcastLastEventTextByVehicle.Remove(completedVehicle);
                }

                InvalidatePanelData();
            }

            CompleteDetachedBroadcastRuntimeClipLoads(nowFrame);
            PruneBroadcastRuntimeClipCache(nowFrame);
        }

        private bool AdvanceBroadcastRuntimeSequence(BroadcastRuntimeSequenceState state, uint nowFrame)
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

                ReleaseBroadcastRuntimeAudioSource(state);
            }

            if (state.PendingClipLoadTask != null)
            {
                if (!state.PendingClipLoadTask.IsCompleted)
                {
                    return true;
                }

                Task<AudioClip> completedTask = state.PendingClipLoadTask;
                string pendingAssetName = state.PendingAssetName;
                state.PendingClipLoadTask = null;
                state.PendingAssetName = string.Empty;
                if (!string.IsNullOrEmpty(pendingAssetName)
                    && m_BroadcastRuntimeClipLoadTasks.TryGetValue(pendingAssetName, out Task<AudioClip> liveTask)
                    && ReferenceEquals(liveTask, completedTask))
                {
                    m_BroadcastRuntimeClipLoadTasks.Remove(pendingAssetName);
                }

                AudioClip loadedClip = null;
                if (completedTask.Status == TaskStatus.RanToCompletion)
                {
                    loadedClip = completedTask.Result;
                }

                if (loadedClip == null)
                {
                    return AdvanceBroadcastRuntimeSequence(state, nowFrame);
                }

                CacheBroadcastRuntimeClip(pendingAssetName, loadedClip, nowFrame);
                if (TryStartBroadcastWorldAudio(state, pendingAssetName, loadedClip))
                {
                    return true;
                }

                return AdvanceBroadcastRuntimeSequence(state, nowFrame);
            }

            if (nowFrame < state.ResumeFrame)
            {
                return true;
            }

            while (state.RuleIndex < state.Rules.Count)
            {
                BroadcastWorkbenchRuleDto rule = state.Rules[state.RuleIndex];
                if (rule?.nodes == null || state.NodeIndex >= rule.nodes.Length)
                {
                    state.RuleIndex++;
                    state.NodeIndex = 0;
                    continue;
                }

                BroadcastWorkbenchRuleNodeDto node = rule.nodes[state.NodeIndex++];
                if (node == null)
                {
                    continue;
                }

                if (string.Equals(node.type, "delay", StringComparison.Ordinal))
                {
                    int delaySeconds = node.delaySeconds > 0 ? node.delaySeconds : 0;
                    if (delaySeconds > 0)
                    {
                        state.ResumeFrame = nowFrame + ConvertBroadcastDelaySecondsToFrames(delaySeconds);
                        return true;
                    }

                    continue;
                }

                string assetName = ResolveBroadcastRuntimeAssetName(node, state.Context);
                if (string.IsNullOrEmpty(assetName))
                {
                    continue;
                }

                if (TryGetBroadcastRuntimeCachedClip(assetName, nowFrame, out AudioClip cachedClip))
                {
                    if (TryStartBroadcastWorldAudio(state, assetName, cachedClip))
                    {
                        return true;
                    }

                    continue;
                }

                Task<AudioClip> loadTask = BeginBroadcastRuntimeClipLoad(assetName);
                if (loadTask == null)
                {
                    continue;
                }

                state.PendingAssetName = assetName;
                state.PendingClipLoadTask = loadTask;
                return true;
            }

            return false;
        }

        private void StopBroadcastRuntimeSequence(Entity vehicle)
        {
            if (vehicle == Entity.Null)
            {
                return;
            }

            if (!m_BroadcastSequenceStateByVehicle.TryGetValue(vehicle, out BroadcastRuntimeSequenceState state))
            {
                return;
            }

            ReleaseBroadcastRuntimeAudioSource(state);
            state.PendingClipLoadTask = null;
            state.PendingAssetName = string.Empty;
            m_BroadcastSequenceStateByVehicle.Remove(vehicle);
        }

        private void ClearBroadcastRuntimeState(Entity vehicle)
        {
            if (vehicle == Entity.Null)
            {
                return;
            }

            StopBroadcastRuntimeSequence(vehicle);
            m_BroadcastLastStopAndOpenStopByVehicle.Remove(vehicle);
            m_BroadcastLastLeaveStationStopByVehicle.Remove(vehicle);
            m_BroadcastLastBypassWaitingStopByVehicle.Remove(vehicle);
            m_BroadcastProgressTriggerStateByVehicle.Remove(vehicle);
            m_BroadcastCurrentStationNameByVehicle.Remove(vehicle);
            m_BroadcastNextStationNameByVehicle.Remove(vehicle);
            m_BroadcastCurrentStopWaypointIndexByVehicle.Remove(vehicle);
            m_BroadcastNextStopWaypointIndexByVehicle.Remove(vehicle);
            m_BroadcastLastEventTextByVehicle.Remove(vehicle);
            InvalidatePanelData();
        }

        private void ClearAllBroadcastRuntimeState()
        {
            foreach (KeyValuePair<Entity, BroadcastRuntimeSequenceState> entry in m_BroadcastSequenceStateByVehicle)
            {
                ReleaseBroadcastRuntimeAudioSource(entry.Value);
            }

            m_BroadcastSequenceStateByVehicle.Clear();
            m_BroadcastLastStopAndOpenStopByVehicle.Clear();
            m_BroadcastLastLeaveStationStopByVehicle.Clear();
            m_BroadcastLastBypassWaitingStopByVehicle.Clear();
            m_BroadcastProgressTriggerStateByVehicle.Clear();
            m_BroadcastCurrentStationNameByVehicle.Clear();
            m_BroadcastNextStationNameByVehicle.Clear();
            m_BroadcastCurrentStopWaypointIndexByVehicle.Clear();
            m_BroadcastNextStopWaypointIndexByVehicle.Clear();
            m_BroadcastLineStationContextCaches.Clear();
            m_BroadcastLastEventTextByVehicle.Clear();
            m_BroadcastRuntimeClipLoadTasks.Clear();

            foreach (BroadcastRuntimeClipCacheEntry entry in m_BroadcastRuntimeClipCache.Values)
            {
                DestroyBroadcastRuntimeClip(entry?.Clip);
            }

            m_BroadcastRuntimeClipCache.Clear();
            InvalidatePanelData();
        }

        private void RemoveBroadcastRuntimeAsset(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return;
            }

            List<Entity> affectedVehicles = null;
            foreach (KeyValuePair<Entity, BroadcastRuntimeSequenceState> entry in m_BroadcastSequenceStateByVehicle)
            {
                BroadcastRuntimeSequenceState state = entry.Value;
                if (state == null)
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

            if (affectedVehicles != null)
            {
                for (int i = 0; i < affectedVehicles.Count; i++)
                {
                    StopBroadcastRuntimeSequence(affectedVehicles[i]);
                }
            }

            m_BroadcastRuntimeClipLoadTasks.Remove(assetName);
            if (!m_BroadcastRuntimeClipCache.TryGetValue(assetName, out BroadcastRuntimeClipCacheEntry cacheEntry))
            {
                return;
            }

            if (!IsBroadcastRuntimeClipInUse(cacheEntry?.Clip))
            {
                DestroyBroadcastRuntimeClip(cacheEntry?.Clip);
            }

            m_BroadcastRuntimeClipCache.Remove(assetName);
        }

        private void RemoveAllBroadcastRuntimeAssets()
        {
            foreach (KeyValuePair<Entity, BroadcastRuntimeSequenceState> entry in m_BroadcastSequenceStateByVehicle)
            {
                ReleaseBroadcastRuntimeAudioSource(entry.Value);
            }

            m_BroadcastSequenceStateByVehicle.Clear();
            m_BroadcastRuntimeClipLoadTasks.Clear();
            foreach (BroadcastRuntimeClipCacheEntry entry in m_BroadcastRuntimeClipCache.Values)
            {
                DestroyBroadcastRuntimeClip(entry?.Clip);
            }

            m_BroadcastRuntimeClipCache.Clear();
        }

        private uint ConvertBroadcastDelaySecondsToFrames(int delaySeconds)
        {
            float framesPerSecond = (float)SIM_FRAMES_PER_MINUTE / 60f;
            return (uint)Mathf.Max(1, Mathf.RoundToInt(delaySeconds * framesPerSecond));
        }

        private bool TryStartBroadcastWorldAudio(BroadcastRuntimeSequenceState state, string assetName, AudioClip clip)
        {
            if (state == null || clip == null || !TryGetBroadcastWorldAudioPosition(state.Vehicle, out Vector3 position))
            {
                return false;
            }

            AudioSource audioSource = AudioManager.AudioSourcePool.Get();
            audioSource.clip = clip;
            audioSource.outputAudioMixerGroup = ResolveBroadcastRuntimeWorldMixerGroup();
            audioSource.transform.position = position;
            audioSource.pitch = 1f;
            audioSource.volume = Mathf.Lerp(
                BroadcastVolumeScalarMin,
                BroadcastVolumeScalarMax,
                ClampBroadcastVolumePercent(m_BroadcastAppliedVolumePercent) / 100f);
            audioSource.loop = false;
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 1f;
            audioSource.spread = 140f;
            audioSource.dopplerLevel = 0f;
            audioSource.minDistance = 90f;
            audioSource.maxDistance = 450f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.ignoreListenerPause = false;
            AudioManager.AudioSourcePool.Play(audioSource);
            state.ActiveAudioSource = audioSource;
            state.ActiveAudioAssetName = assetName ?? string.Empty;
            return true;
        }

        private void ReleaseBroadcastRuntimeAudioSource(BroadcastRuntimeSequenceState state)
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

        private void UpdateBroadcastRuntimeAudioSourcePosition(BroadcastRuntimeSequenceState state)
        {
            if (state?.ActiveAudioSource == null || state.Vehicle == Entity.Null)
            {
                return;
            }

            if (!TryGetBroadcastWorldAudioPosition(state.Vehicle, out Vector3 position))
            {
                return;
            }

            state.ActiveAudioSource.transform.position = position;
        }

        private void ApplyBroadcastAppliedVolumeToRuntime()
        {
            float volume = Mathf.Lerp(
                BroadcastVolumeScalarMin,
                BroadcastVolumeScalarMax,
                ClampBroadcastVolumePercent(m_BroadcastAppliedVolumePercent) / 100f);
            foreach (KeyValuePair<Entity, BroadcastRuntimeSequenceState> entry in m_BroadcastSequenceStateByVehicle)
            {
                if (entry.Value?.ActiveAudioSource != null)
                {
                    entry.Value.ActiveAudioSource.volume = volume;
                }
            }
        }

        private bool TryGetBroadcastWorldAudioPosition(Entity vehicle, out Vector3 position)
        {
            position = default;
            if (vehicle == Entity.Null || !EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                return false;
            }

            Game.Objects.Transform transform = EntityManager.GetComponentData<Game.Objects.Transform>(vehicle);
            position = new Vector3(transform.m_Position.x, transform.m_Position.y, transform.m_Position.z);
            return true;
        }

        private AudioMixerGroup ResolveBroadcastRuntimeWorldMixerGroup()
        {
            AudioManager audioManager = AudioManager.instance;
            if (audioManager == null || s_AudioManagerWorldGroupField == null)
            {
                return null;
            }

            try
            {
                return s_AudioManagerWorldGroupField.GetValue(audioManager) as AudioMixerGroup;
            }
            catch
            {
                return null;
            }
        }

        private bool TryGetBroadcastRuntimeCachedClip(string assetName, uint nowFrame, out AudioClip clip)
        {
            clip = null;
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return false;
            }

            if (!m_BroadcastRuntimeClipCache.TryGetValue(assetName, out BroadcastRuntimeClipCacheEntry entry)
                || entry?.Clip == null)
            {
                return false;
            }

            entry.LastAccessFrame = nowFrame;
            clip = entry.Clip;
            return true;
        }

        private void CacheBroadcastRuntimeClip(string assetName, AudioClip clip, uint nowFrame)
        {
            if (string.IsNullOrWhiteSpace(assetName) || clip == null)
            {
                return;
            }

            m_BroadcastRuntimeClipCache[assetName] = new BroadcastRuntimeClipCacheEntry
            {
                Clip = clip,
                LastAccessFrame = nowFrame
            };
        }

        private Task<AudioClip> BeginBroadcastRuntimeClipLoad(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return null;
            }

            if (m_BroadcastRuntimeClipLoadTasks.TryGetValue(assetName, out Task<AudioClip> existingTask))
            {
                return existingTask;
            }

            BroadcastWorkbenchAssetDto asset = m_BroadcastAssetCatalog.FirstOrDefault(candidate =>
                string.Equals(candidate?.name, assetName, StringComparison.OrdinalIgnoreCase));
            string assetPath = NormalizeBroadcastAssetFilePath(asset?.path);
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
            {
                return null;
            }

            AudioType audioType = ResolveBroadcastAssetAudioType(assetPath);
            if (audioType == AudioType.UNKNOWN)
            {
                return null;
            }

            Task<AudioClip> loadTask = LoadBroadcastRuntimeClipAsync(assetName, assetPath, audioType);
            m_BroadcastRuntimeClipLoadTasks[assetName] = loadTask;
            return loadTask;
        }

        private async Task<AudioClip> LoadBroadcastRuntimeClipAsync(string assetName, string assetPath, AudioType audioType)
        {
            return await RunBroadcastMainThreadTaskAsync(async () =>
            {
                using UnityWebRequest request = BuildBroadcastPreviewAudioRequest(assetPath, audioType);
                DownloadHandlerAudioClip downloadHandler = request.downloadHandler as DownloadHandlerAudioClip;
                if (downloadHandler != null)
                {
                    downloadHandler.streamAudio = false;
                }

                await request.SendWebRequest();
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

        private async Task<T> RunBroadcastMainThreadTaskAsync<T>(Func<Task<T>> action)
        {
            TaskCompletionSource<T> completion = new TaskCompletionSource<T>();
            MainThreadDispatcher.RunOnMainThread(async () =>
            {
                try
                {
                    completion.SetResult(await action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return await completion.Task;
        }

        private void PruneBroadcastRuntimeClipCache(uint nowFrame)
        {
            if (m_BroadcastRuntimeClipCache.Count <= BroadcastRuntimeClipCacheLimit)
            {
                return;
            }

            List<KeyValuePair<string, BroadcastRuntimeClipCacheEntry>> evictionCandidates = m_BroadcastRuntimeClipCache
                .OrderBy(entry => entry.Value?.LastAccessFrame ?? nowFrame)
                .ToList();
            for (int i = 0; i < evictionCandidates.Count && m_BroadcastRuntimeClipCache.Count > BroadcastRuntimeClipCacheLimit; i++)
            {
                KeyValuePair<string, BroadcastRuntimeClipCacheEntry> candidate = evictionCandidates[i];
                BroadcastRuntimeClipCacheEntry entry = candidate.Value;
                if (entry?.Clip == null || IsBroadcastRuntimeClipInUse(entry.Clip))
                {
                    continue;
                }

                DestroyBroadcastRuntimeClip(entry.Clip);
                m_BroadcastRuntimeClipCache.Remove(candidate.Key);
            }
        }

        private void CompleteDetachedBroadcastRuntimeClipLoads(uint nowFrame)
        {
            if (m_BroadcastRuntimeClipLoadTasks.Count == 0)
            {
                return;
            }

            List<string> completedKeys = null;
            foreach (KeyValuePair<string, Task<AudioClip>> entry in m_BroadcastRuntimeClipLoadTasks)
            {
                if (!entry.Value.IsCompleted || IsBroadcastRuntimeClipLoadTaskPending(entry.Value))
                {
                    continue;
                }

                completedKeys ??= new List<string>();
                completedKeys.Add(entry.Key);
                if (entry.Value.Status == TaskStatus.RanToCompletion && entry.Value.Result != null)
                {
                    CacheBroadcastRuntimeClip(entry.Key, entry.Value.Result, nowFrame);
                }
            }

            if (completedKeys == null)
            {
                return;
            }

            for (int i = 0; i < completedKeys.Count; i++)
            {
                m_BroadcastRuntimeClipLoadTasks.Remove(completedKeys[i]);
            }
        }

        private bool IsBroadcastRuntimeClipLoadTaskPending(Task<AudioClip> task)
        {
            if (task == null)
            {
                return false;
            }

            foreach (BroadcastRuntimeSequenceState state in m_BroadcastSequenceStateByVehicle.Values)
            {
                if (state?.PendingClipLoadTask != null && ReferenceEquals(state.PendingClipLoadTask, task))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsBroadcastRuntimeClipInUse(AudioClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            foreach (BroadcastRuntimeSequenceState state in m_BroadcastSequenceStateByVehicle.Values)
            {
                if (state?.ActiveAudioSource != null && state.ActiveAudioSource.clip == clip)
                {
                    return true;
                }
            }

            return false;
        }

        private void DestroyBroadcastRuntimeClip(AudioClip clip)
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

        private string ResolveBroadcastRuntimeAssetName(BroadcastWorkbenchRuleNodeDto node, BroadcastTriggerContext context)
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
                    return ResolveBroadcastBoundAssetName(context.CurrentStationBindings, langIndex);
                case "broadcast.variable.next":
                    return ResolveBroadcastBoundAssetName(context.NextStationBindings, langIndex);
                case "broadcast.variable.terminal":
                    return ResolveBroadcastBoundAssetName(context.TerminalStationBindings, langIndex);
                default:
                    return string.Empty;
            }
        }

        private bool TryBuildBroadcastTriggerContext(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentStopWaypointIndex,
            out BroadcastTriggerContext context)
        {
            context = default;
            if (!TryResolveBroadcastVehicleStationContext(
                    vehicle,
                    line,
                    waypoints,
                    currentStopWaypointIndex,
                    out BroadcastVehicleStationContext stationContext))
            {
                return false;
            }

            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                GetBroadcastAppliedLineStationBindings(stationContext.LineId);
            List<BroadcastWorkbenchStationBindingDto> currentStationBindings =
                ResolveBroadcastBoundBindings(lineBindings, stationContext.CurrentStationId);
            List<BroadcastWorkbenchStationBindingDto> nextStationBindings =
                ResolveBroadcastBoundBindings(lineBindings, stationContext.NextStationId);
            List<BroadcastWorkbenchStationBindingDto> terminalStationBindings =
                ResolveBroadcastBoundBindings(lineBindings, stationContext.TerminalStationId);

            context = new BroadcastTriggerContext(
                stationContext.LineId,
                stationContext.CurrentStopEntity,
                stationContext.CurrentStationName,
                stationContext.NextStationName,
                stationContext.TerminalStationName,
                ResolveBroadcastBoundAssetName(currentStationBindings, 1),
                ResolveBroadcastBoundAssetName(nextStationBindings, 1),
                ResolveBroadcastBoundAssetName(terminalStationBindings, 1),
                currentStationBindings,
                nextStationBindings,
                terminalStationBindings);
            return true;
        }

        private List<BroadcastResolvedStation> BuildBroadcastResolvedStations(DynamicBuffer<RouteWaypoint> waypoints)
        {
            List<BroadcastResolvedStation> stations = new List<BroadcastResolvedStation>();
            HashSet<Entity> seenStops = new HashSet<Entity>();
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[i].m_Waypoint);
                if (stopEntity == Entity.Null || !seenStops.Add(stopEntity))
                {
                    continue;
                }

                int order = stations.Count;
                stations.Add(new BroadcastResolvedStation
                {
                    StopEntity = stopEntity,
                    WaypointIndex = i,
                    Order = order,
                    StationId = CreateWorkbenchStationId(order),
                    Name = ResolveWorkbenchStationName(stopEntity)
                });
            }

            return stations;
        }

        private bool TryGetBroadcastLineStationContextCache(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out BroadcastLineStationContextCache cache)
        {
            cache = null;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || waypoints.Length == 0)
            {
                return false;
            }

            ulong signature = ComputeLineWaypointSignature(waypoints);
            if (m_BroadcastLineStationContextCaches.TryGetValue(line, out cache)
                && cache != null
                && cache.Signature == signature)
            {
                return cache.Stations.Length > 0;
            }

            int waypointCount = waypoints.Length;
            int[] stationOrderByWaypoint = new int[waypointCount];
            int[] normalizedStationWaypointByWaypoint = new int[waypointCount];
            int[] nextDistinctStationWaypointByWaypoint = new int[waypointCount];
            BroadcastResolvedStation[] stationByWaypoint = new BroadcastResolvedStation[waypointCount];
            for (int i = 0; i < waypointCount; i++)
            {
                stationOrderByWaypoint[i] = -1;
                normalizedStationWaypointByWaypoint[i] = -1;
                nextDistinctStationWaypointByWaypoint[i] = -1;
            }

            List<BroadcastResolvedStation> stations = new List<BroadcastResolvedStation>();
            Dictionary<Entity, int> stationOrderByStop = new Dictionary<Entity, int>();
            for (int waypointIndex = 0; waypointIndex < waypointCount; waypointIndex++)
            {
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint);
                if (stopEntity == Entity.Null)
                {
                    continue;
                }

                if (!stationOrderByStop.TryGetValue(stopEntity, out int order))
                {
                    order = stations.Count;
                    stationOrderByStop[stopEntity] = order;
                    stations.Add(new BroadcastResolvedStation
                    {
                        StopEntity = stopEntity,
                        WaypointIndex = waypointIndex,
                        Order = order,
                        StationId = CreateWorkbenchStationId(order),
                        Name = ResolveWorkbenchStationName(stopEntity)
                    });
                }

                stationOrderByWaypoint[waypointIndex] = order;
                normalizedStationWaypointByWaypoint[waypointIndex] = stations[order].WaypointIndex;
                stationByWaypoint[waypointIndex] = new BroadcastResolvedStation
                {
                    StopEntity = stopEntity,
                    WaypointIndex = waypointIndex,
                    Order = order,
                    StationId = stations[order].StationId,
                    Name = stations[order].Name
                };
            }

            BroadcastResolvedStation[] stationArray = stations.ToArray();
            for (int waypointIndex = 0; waypointIndex < waypointCount; waypointIndex++)
            {
                for (int offset = 0; offset < waypointCount; offset++)
                {
                    int candidateWaypointIndex = (waypointIndex + offset) % waypointCount;
                    if (stationByWaypoint[candidateWaypointIndex] == null)
                    {
                        continue;
                    }

                    nextDistinctStationWaypointByWaypoint[waypointIndex] = candidateWaypointIndex;
                    break;
                }
            }

            cache = new BroadcastLineStationContextCache
            {
                Signature = signature,
                Stations = stationArray,
                StationByWaypoint = stationByWaypoint,
                StationOrderByWaypoint = stationOrderByWaypoint,
                NormalizedStationWaypointByWaypoint = normalizedStationWaypointByWaypoint,
                NextDistinctStationWaypointByWaypoint = nextDistinctStationWaypointByWaypoint,
                TerminalStationWaypointIndex = stationArray.Length > 0
                    ? stationArray[stationArray.Length - 1].WaypointIndex
                    : -1
            };
            m_BroadcastLineStationContextCaches[line] = cache;
            return stationArray.Length > 0;
        }

        private static bool TryGetBroadcastStationByOrder(
            BroadcastLineStationContextCache cache,
            int order,
            out BroadcastResolvedStation station)
        {
            station = null;
            if (cache == null
                || cache.Stations == null
                || order < 0
                || order >= cache.Stations.Length)
            {
                return false;
            }

            station = cache.Stations[order];
            return station != null;
        }

        private static bool TryGetBroadcastStationByWaypointIndex(
            BroadcastLineStationContextCache cache,
            int waypointIndex,
            out BroadcastResolvedStation station)
        {
            station = null;
            if (cache == null
                || cache.StationByWaypoint == null
                || waypointIndex < 0
                || waypointIndex >= cache.StationByWaypoint.Length)
            {
                return false;
            }

            station = cache.StationByWaypoint[waypointIndex];
            return station != null;
        }

        private static bool TryGetBroadcastNextStationAtOrAfterWaypointIndex(
            BroadcastLineStationContextCache cache,
            int waypointIndex,
            out BroadcastResolvedStation station)
        {
            station = null;
            if (cache == null
                || cache.NextDistinctStationWaypointByWaypoint == null
                || waypointIndex < 0
                || waypointIndex >= cache.NextDistinctStationWaypointByWaypoint.Length)
            {
                return false;
            }

            int stationWaypointIndex = cache.NextDistinctStationWaypointByWaypoint[waypointIndex];
            return TryGetBroadcastStationByWaypointIndex(cache, stationWaypointIndex, out station);
        }

        private static bool TryGetBroadcastNextStationAfterWaypointIndex(
            BroadcastLineStationContextCache cache,
            int waypointIndex,
            out BroadcastResolvedStation station)
        {
            station = null;
            if (cache == null
                || cache.NextDistinctStationWaypointByWaypoint == null
                || cache.NextDistinctStationWaypointByWaypoint.Length == 0)
            {
                return false;
            }

            int nextWaypointIndex = (waypointIndex + 1) % cache.NextDistinctStationWaypointByWaypoint.Length;
            return TryGetBroadcastNextStationAtOrAfterWaypointIndex(cache, nextWaypointIndex, out station);
        }

        private static bool TryGetBroadcastPreviousStationBeforeWaypointIndex(
            BroadcastLineStationContextCache cache,
            int waypointIndex,
            out BroadcastResolvedStation station)
        {
            station = null;
            if (cache == null
                || cache.StationByWaypoint == null
                || cache.StationByWaypoint.Length == 0)
            {
                return false;
            }

            int waypointCount = cache.StationByWaypoint.Length;
            waypointIndex = Mathf.Clamp(waypointIndex, 0, waypointCount - 1);
            for (int offset = 1; offset <= waypointCount; offset++)
            {
                int candidateWaypointIndex = (waypointIndex - offset + waypointCount) % waypointCount;
                station = cache.StationByWaypoint[candidateWaypointIndex];
                if (station != null)
                {
                    return true;
                }
            }

            station = null;
            return false;
        }

        private bool TryResolveBroadcastVehicleTargetWaypointIndex(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out int waypointIndex)
        {
            waypointIndex = -1;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !EntityManager.HasComponent<Target>(vehicle))
            {
                return false;
            }

            Entity target = EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (target == Entity.Null)
            {
                return false;
            }

            if (TryGetLineWaypointIndexLookup(line, waypoints, out LineWaypointIndexLookup lookup)
                && lookup != null)
            {
                if (lookup.WaypointIndexByWaypoint.TryGetValue(target, out waypointIndex))
                {
                    return true;
                }

                if (lookup.WaypointIndexByStop.TryGetValue(target, out waypointIndex))
                {
                    return true;
                }
            }

            if (!EntityManager.HasComponent<Waypoint>(target))
            {
                return false;
            }

            waypointIndex = EntityManager.GetComponentData<Waypoint>(target).m_Index;
            return waypointIndex >= 0 && waypointIndex < waypoints.Length;
        }

        private bool TryResolveBroadcastVehicleStationContext(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int preferredCurrentStopWaypointIndex,
            out BroadcastVehicleStationContext context)
        {
            context = default;
            if (!TryGetBroadcastLineStationContextCache(line, waypoints, out BroadcastLineStationContextCache cache))
            {
                return false;
            }

            BroadcastResolvedStation currentStation = null;
            BroadcastResolvedStation nextStation = null;
            BroadcastResolvedStation cachedCurrentStation = null;

            if (preferredCurrentStopWaypointIndex >= 0)
            {
                TryGetBroadcastStationByWaypointIndex(cache, preferredCurrentStopWaypointIndex, out currentStation);
            }

            if (currentStation == null
                && m_CachedWpIdx.TryGetValue(vehicle, out int liveStopWaypointIndex)
                && liveStopWaypointIndex >= 0)
            {
                TryGetBroadcastStationByWaypointIndex(cache, liveStopWaypointIndex, out currentStation);
            }

            if (m_BroadcastCurrentStopWaypointIndexByVehicle.TryGetValue(vehicle, out int cachedCurrentWaypointIndex))
            {
                TryGetBroadcastStationByWaypointIndex(cache, cachedCurrentWaypointIndex, out cachedCurrentStation);
            }

            if (currentStation == null
                && TryResolveBroadcastVehicleTargetWaypointIndex(vehicle, line, waypoints, out int targetWaypointIndex)
                && TryGetBroadcastNextStationAtOrAfterWaypointIndex(cache, targetWaypointIndex, out BroadcastResolvedStation targetNextStation))
            {
                if (cachedCurrentStation != null && targetNextStation.Order <= cachedCurrentStation.Order)
                {
                    currentStation = cachedCurrentStation;
                    if (targetNextStation.WaypointIndex != cachedCurrentStation.WaypointIndex)
                    {
                        nextStation = targetNextStation;
                    }
                }
                else if (TryGetBroadcastPreviousStationBeforeWaypointIndex(cache, targetWaypointIndex, out BroadcastResolvedStation targetCurrentStation))
                {
                    currentStation = targetCurrentStation;
                    nextStation = targetNextStation;
                }
            }

            if (currentStation == null)
            {
                currentStation = cachedCurrentStation;
            }

            if (nextStation == null
                && m_BroadcastNextStopWaypointIndexByVehicle.TryGetValue(vehicle, out int cachedNextWaypointIndex))
            {
                TryGetBroadcastStationByWaypointIndex(cache, cachedNextWaypointIndex, out nextStation);
            }

            if (currentStation != null
                && (nextStation == null || nextStation.WaypointIndex == currentStation.WaypointIndex))
            {
                TryGetBroadcastNextStationAfterWaypointIndex(cache, currentStation.WaypointIndex, out nextStation);
            }

            if (currentStation == null)
            {
                return false;
            }

            BroadcastResolvedStation terminalStation = cache.Stations[cache.Stations.Length - 1];
            string lineId = GetDraftKey(GetWorkbenchLineId(line));
            context = new BroadcastVehicleStationContext(
                lineId,
                currentStation.StopEntity,
                currentStation.WaypointIndex,
                currentStation.StationId,
                currentStation.Name,
                nextStation?.WaypointIndex ?? -1,
                nextStation?.StationId ?? string.Empty,
                nextStation?.Name ?? string.Empty,
                terminalStation?.StationId ?? string.Empty,
                terminalStation?.Name ?? string.Empty);

            if (vehicle != Entity.Null)
            {
                m_BroadcastCurrentStopWaypointIndexByVehicle[vehicle] = context.CurrentStopWaypointIndex;
                if (context.NextStopWaypointIndex >= 0)
                {
                    m_BroadcastNextStopWaypointIndexByVehicle[vehicle] = context.NextStopWaypointIndex;
                }
                else
                {
                    m_BroadcastNextStopWaypointIndexByVehicle.Remove(vehicle);
                }

                m_BroadcastCurrentStationNameByVehicle[vehicle] = context.CurrentStationName;
                m_BroadcastNextStationNameByVehicle[vehicle] = context.NextStationName;
            }

            return true;
        }

        private static string ResolveBroadcastBoundAssetName(
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings,
            string stationId)
        {
            if (lineBindings == null || string.IsNullOrEmpty(stationId))
            {
                return string.Empty;
            }

            if (!lineBindings.TryGetValue(stationId, out List<BroadcastWorkbenchStationBindingDto> bindings)
                || bindings == null
                || bindings.Count == 0)
            {
                return string.Empty;
            }

            return bindings
                .OrderBy(binding => binding?.langIndex ?? int.MaxValue)
                .Select(binding => binding?.assetName ?? string.Empty)
                .FirstOrDefault(assetName => !string.IsNullOrWhiteSpace(assetName))
                ?? string.Empty;
        }

        private static List<BroadcastWorkbenchStationBindingDto> ResolveBroadcastBoundBindings(
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings,
            string stationId)
        {
            if (lineBindings == null || string.IsNullOrEmpty(stationId))
            {
                return null;
            }

            if (!lineBindings.TryGetValue(stationId, out List<BroadcastWorkbenchStationBindingDto> bindings)
                || bindings == null
                || bindings.Count == 0)
            {
                return null;
            }

            return bindings;
        }

        private static string ResolveBroadcastBoundAssetName(
            List<BroadcastWorkbenchStationBindingDto> bindings,
            int langIndex)
        {
            if (bindings == null || bindings.Count == 0)
            {
                return string.Empty;
            }

            int targetLangIndex = langIndex > 0 ? langIndex : 1;
            BroadcastWorkbenchStationBindingDto exactMatch = bindings.FirstOrDefault(binding =>
                binding != null
                && binding.langIndex == targetLangIndex
                && !string.IsNullOrWhiteSpace(binding.assetName));
            if (exactMatch != null)
            {
                return exactMatch.assetName ?? string.Empty;
            }

            if (targetLangIndex != 1)
            {
                BroadcastWorkbenchStationBindingDto fallbackMatch = bindings.FirstOrDefault(binding =>
                    binding != null
                    && binding.langIndex == 1
                    && !string.IsNullOrWhiteSpace(binding.assetName));
                if (fallbackMatch != null)
                {
                    return fallbackMatch.assetName ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private string BuildBroadcastEventText(string triggerId, BroadcastTriggerContext context)
        {
            string triggerLabel;
            switch (triggerId)
            {
                case "stop_and_open":
                    triggerLabel = IsChineseLocale() ? "停站上客" : "stop_and_open";
                    break;
                case "leave_station":
                    triggerLabel = IsChineseLocale() ? "列车离站" : "leave_station";
                    break;
                case "approach_station":
                    triggerLabel = IsChineseLocale() ? "即将进站" : "approach_station";
                    break;
                case "mid_route":
                    triggerLabel = IsChineseLocale() ? "区间运行中" : "mid_route";
                    break;
                case "bypass_waiting":
                    triggerLabel = IsChineseLocale() ? "待避中" : "bypass_waiting";
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

        private bool TryGetBroadcastPanelStationContext(
            Entity vehicle,
            Entity line,
            out string currentStationName,
            out string nextStationName,
            out string terminalStationName)
        {
            currentStationName = m_BroadcastCurrentStationNameByVehicle.TryGetValue(vehicle, out string currentName)
                ? currentName ?? string.Empty
                : string.Empty;
            nextStationName = m_BroadcastNextStationNameByVehicle.TryGetValue(vehicle, out string nextName)
                ? nextName ?? string.Empty
                : string.Empty;
            terminalStationName = string.Empty;

            if (line == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return !string.IsNullOrEmpty(currentStationName) || !string.IsNullOrEmpty(nextStationName);
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (TryResolveBroadcastVehicleStationContext(
                    vehicle,
                    line,
                    waypoints,
                    -1,
                    out BroadcastVehicleStationContext context))
            {
                currentStationName = context.CurrentStationName;
                nextStationName = context.NextStationName;
                terminalStationName = context.TerminalStationName;
            }

            return !string.IsNullOrEmpty(currentStationName)
                || !string.IsNullOrEmpty(nextStationName)
                || !string.IsNullOrEmpty(terminalStationName);
        }

        private string BuildVehicleBroadcastEventValue(Entity vehicle)
        {
            return m_BroadcastLastEventTextByVehicle.TryGetValue(vehicle, out string text)
                ? text ?? string.Empty
                : string.Empty;
        }

        private readonly struct BroadcastTriggerContext
        {
            public readonly string LineId;
            public readonly Entity CurrentStopEntity;
            public readonly string CurrentStationName;
            public readonly string NextStationName;
            public readonly string TerminalStationName;
            public readonly string CurrentStationAssetName;
            public readonly string NextStationAssetName;
            public readonly string TerminalStationAssetName;
            public readonly List<BroadcastWorkbenchStationBindingDto> CurrentStationBindings;
            public readonly List<BroadcastWorkbenchStationBindingDto> NextStationBindings;
            public readonly List<BroadcastWorkbenchStationBindingDto> TerminalStationBindings;

            public BroadcastTriggerContext(
                string lineId,
                Entity currentStopEntity,
                string currentStationName,
                string nextStationName,
                string terminalStationName,
                string currentStationAssetName,
                string nextStationAssetName,
                string terminalStationAssetName,
                List<BroadcastWorkbenchStationBindingDto> currentStationBindings,
                List<BroadcastWorkbenchStationBindingDto> nextStationBindings,
                List<BroadcastWorkbenchStationBindingDto> terminalStationBindings)
            {
                LineId = lineId;
                CurrentStopEntity = currentStopEntity;
                CurrentStationName = currentStationName;
                NextStationName = nextStationName;
                TerminalStationName = terminalStationName;
                CurrentStationAssetName = currentStationAssetName;
                NextStationAssetName = nextStationAssetName;
                TerminalStationAssetName = terminalStationAssetName;
                CurrentStationBindings = currentStationBindings;
                NextStationBindings = nextStationBindings;
                TerminalStationBindings = terminalStationBindings;
            }
        }
    }
}
