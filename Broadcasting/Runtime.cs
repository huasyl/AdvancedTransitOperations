using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Colossal.Core;
using Colossal.Mathematics;
using Game;
using Game.Audio;
using Game.Common;
using Game.Net;
using Game.Rendering;
using Game.Routes;
using Game.Simulation;
using Game.UI.InGame;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;
using CursorAtomWindowRelation = RapidTransitMod.DispatchRuntimeSystem.CursorAtomWindowRelation;
using BroadcastWorkbenchStationGroup = RapidTransitMod.DispatchRuntimeSystem.BroadcastWorkbenchStationGroup;
using LineRunningVehicleSnapshot = RapidTransitMod.TrackProjection.LineRunningVehicleSnapshot;
using LineWaypointIndexLookup = RapidTransitMod.TrackModel.LineWaypointIndexLookup;

namespace RapidTransitMod.Broadcasting
{
    internal sealed class Runtime
    {
        private readonly DispatchRuntimeSystem m_Host;

        internal Runtime(DispatchRuntimeSystem host)
        {
            m_Host = host ?? throw new ArgumentNullException(nameof(host));
        }

        private EntityManager EntityManager => m_Host.EntityManager;
        private TimedLogger log => m_Host.log;
        private SimulationSystem m_SimulationSystem => m_Host.m_SimulationSystem;
        private CameraUpdateSystem m_CameraUpdateSystem => m_Host.m_CameraUpdateSystem;
        private VehicleRuntimeView m_VehicleView => m_Host.m_VehicleView;
        private SelectionPanel m_SelectionPanel => m_Host.m_SelectionPanel;
        private Dictionary<string, List<BroadcastWorkbenchRuleDto>> m_BroadcastLineRules => m_Host.m_BroadcastLineRules;
        private Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> m_BroadcastLinePlatformAnnouncements => m_Host.m_BroadcastLinePlatformAnnouncements;
        private List<BroadcastWorkbenchAssetDto> m_BroadcastAssetCatalog => m_Host.m_BroadcastAssetCatalog;
        private int m_BroadcastAppliedVolumePercent => m_Host.m_BroadcastAppliedVolumePercent;
        private Unity.Collections.NativeHashMap<Entity, int> m_CachedWpIdx => m_Host.m_CachedWpIdx;

        private const double SIM_FRAMES_PER_MINUTE = DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
        private const float BroadcastVolumeScalarMin = 5f;
        private const float BroadcastVolumeScalarMax = 20f;

        private bool TryGetCursorWaypointWindowRelation(
            LineTrackChain chain,
            int waypointIndex,
            int cursorAtomIndex,
            out CursorAtomWindowRelation relation,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            return m_Host.TryGetCursorWaypointWindowRelation(
                chain,
                waypointIndex,
                cursorAtomIndex,
                out relation,
                out startAtomIndex,
                out endAtomIndexExclusive);
        }

        private bool TryGetLineTrackChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            return m_Host.TrackModel.TryGetChainForLine(line, waypoints, out chain);
        }

        private bool TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            out int currentControlEdgeIndex,
            out float ownLineAtomCoordinate,
            out int phaseEndAtomExclusive,
            out int traversalPhaseIndex,
            out int traversalPhaseStartAtomIndex,
            out int traversalPhaseEndAtomExclusive,
            out int nextTurnbackBoundaryAtomIndex)
        {
            return m_Host.TrackProjection.TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
                vehicle,
                line,
                waypoints,
                chain,
                out cursor,
                out currentControlEdgeIndex,
                out ownLineAtomCoordinate,
                out phaseEndAtomExclusive,
                out traversalPhaseIndex,
                out traversalPhaseStartAtomIndex,
                out traversalPhaseEndAtomExclusive,
                out nextTurnbackBoundaryAtomIndex);
        }

        private bool TryGetVehicleTrackCursorCurrentFrame(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor)
        {
            return m_Host.TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out cursor);
        }

        private bool TryGetWaypointTraversalAtomWindow(
            LineTrackChain chain,
            int waypointIndex,
            int referenceAtomIndex,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            return m_Host.TryGetWaypointTraversalAtomWindow(
                chain,
                waypointIndex,
                referenceAtomIndex,
                out startAtomIndex,
                out endAtomIndexExclusive);
        }

        private bool TryGetTrackAtomWorldPosition(LineTrackChain chain, int atomIndex, out float3 position)
        {
            return m_Host.TrackProjection.TryGetTrackAtomWorldPosition(chain, atomIndex, out position);
        }

        private void LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
        {
            m_Host.LogVehicleStateOnce(cache, vehicle, key, message);
        }

        private bool ShouldEmitVehicleLogWithCooldown(
            Dictionary<Entity, string> keyCache,
            Dictionary<Entity, uint> lastFrameCache,
            Entity vehicle,
            string key,
            uint nowFrame,
            uint cooldownFrames)
        {
            return m_Host.ShouldEmitVehicleLogWithCooldown(keyCache, lastFrameCache, vehicle, key, nowFrame, cooldownFrames);
        }

        private Entity ResolveRuntimeControllerVehicle(Entity vehicle) => m_Host.ResolveRuntimeControllerVehicle(vehicle);
        private List<WorkbenchLineRuntime> BuildWorkbenchLinesStable() => m_Host.BuildWorkbenchLinesStable();
        private Entity ResolveWorkbenchStopEntity(Entity waypoint) => m_Host.ResolveWorkbenchStopEntity(waypoint);
        private Entity ResolveStationAnchor(Entity waypoint) => m_Host.ResolveStationAnchor(waypoint);
        private string EnsureStationAnchorKey(Entity anchor) => m_Host.EnsureStationAnchorKey(anchor);
        private string CreateWorkbenchStationId(int order) => DispatchRuntimeSystem.CreateWorkbenchStationId(order);
        private string ResolveWorkbenchStationName(Entity stopEntity) => m_Host.ResolveWorkbenchStationName(stopEntity);
        private string ResolveWorkbenchEntityName(Entity entity) => m_Host.ResolveWorkbenchEntityName(entity);
        private ulong ComputeLineWaypointSignature(DynamicBuffer<RouteWaypoint> waypoints) => m_Host.ComputeLineWaypointSignature(waypoints);
        private bool TryCollectTurnbackStationBoundaries(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries) => m_Host.TryCollectTurnbackStationBoundaries(chain, stationBoundaries);
        private Entity ResolveStationAnchorFromStop(Entity stopEntity) => m_Host.ResolveStationAnchorFromStop(stopEntity);
        private string GetStationAnchorKey(Entity anchor) => m_Host.GetStationAnchorKey(anchor);
        private bool TryGetLineWaypointIndexLookup(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineWaypointIndexLookup lookup) => m_Host.TryGetLineWaypointIndexLookup(line, waypoints, out lookup);
        private string GetDraftKey(string lineId) => DispatchRuntimeSystem.GetDraftKey(lineId);
        private string GetWorkbenchLineId(Entity line) => m_Host.GetWorkbenchLineId(line);
        private bool TryResolveTurnbackStationBoundary(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary) => m_Host.TryResolveTurnbackStationBoundary(chain, boundary, out stationBoundary);
        private bool IsBroadcastFeatureEnabled() => m_Host.IsBroadcastFeatureEnabled();
        private bool EnsureBroadcastLineStateMigrated(string lineId, Entity line, out List<BroadcastWorkbenchStationGroup> stationGroups) => m_Host.EnsureBroadcastLineStateMigrated(lineId, line, out stationGroups);
        private BroadcastWorkbenchRuleDto CloneBroadcastWorkbenchRule(BroadcastWorkbenchRuleDto rule) => DispatchRuntimeSystem.CloneBroadcastWorkbenchRule(rule);
        private BroadcastWorkbenchRuleNodeDto CloneBroadcastWorkbenchRuleNode(BroadcastWorkbenchRuleNodeDto node) => DispatchRuntimeSystem.CloneBroadcastWorkbenchRuleNode(node);
        private Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> GetBroadcastAppliedLineStationBindings(string lineId) => m_Host.GetBroadcastAppliedLineStationBindings(lineId);
        private string NormalizeBroadcastAssetFilePath(string filePath) => DispatchRuntimeSystem.NormalizeBroadcastAssetFilePath(filePath);
        private AudioType ResolveBroadcastAssetAudioType(string filePath) => DispatchRuntimeSystem.ResolveBroadcastAssetAudioType(filePath);
        private UnityWebRequest BuildBroadcastPreviewAudioRequest(string path, AudioType audioType) => DispatchRuntimeSystem.BuildBroadcastPreviewAudioRequest(path, audioType);
        private int ClampBroadcastVolumePercent(int volumePercent) => DispatchRuntimeSystem.ClampBroadcastVolumePercent(volumePercent);

        internal sealed class BroadcastResolvedStation
        {
            public Entity StopEntity;
            public Entity AnchorEntity;
            public int WaypointIndex;
            public int Order;
            public string StationId = string.Empty;
            public string LegacyStationId = string.Empty;
            public string Name = string.Empty;
        }

        private sealed class BroadcastRuntimeSequenceState
        {
            public Entity Vehicle;
            public Entity AudioPositionEntity;
            public string LineId = string.Empty;
            public string TriggerId = string.Empty;
            public BroadcastTriggerContext Context;
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

        private sealed class BroadcastRuntimeClipCacheEntry
        {
            public AudioClip Clip;
            public uint LastAccessFrame;
        }

        private struct BroadcastProgressTriggerState
        {
            public int CurrentStopWaypointIndex;
            public int NextStopWaypointIndex;
            public int LeaveTriggerAtomIndex;
            public int BroadcastLeaveAtomIndex;
            public int BroadcastApproachAtomIndex;
            public uint LeaveTriggeredFrame;
            public uint IdleRouteBlockedUntilFrame;
            public float IdleRouteBlockedUntilRealtime;
            public bool IdleRouteWaitingForLeaveSequenceEnd;
            public CursorAtomWindowRelation LastCurrentStopWindowRelation;
            public bool LeaveTriggered;
            public bool MidRouteTriggered;
            public bool ApproachTriggered;
        }

        private struct BroadcastPlatformApproachTriggerState
        {
            public string LineId;
            public string StationId;
            public int CurrentStopWaypointIndex;
            public int NextStopWaypointIndex;
            public int TriggerAtomIndex;
            public int CursorAtomIndex;
            public int TraversalPhaseIndex;
            public uint LastObservedFrame;
            public bool Triggered;
            public BroadcastVehicleStationContext StationContext;
        }

        internal readonly struct BroadcastVehicleRuntimeFrameContext
        {
            public readonly BroadcastVehicleStationContext StationContext;
            public readonly LineTrackChain Chain;
            public readonly VehicleTrackCursor Cursor;
            public readonly int TraversalPhaseIndex;

            public BroadcastVehicleRuntimeFrameContext(
                BroadcastVehicleStationContext stationContext,
                LineTrackChain chain,
                VehicleTrackCursor cursor,
                int traversalPhaseIndex)
            {
                StationContext = stationContext;
                Chain = chain;
                Cursor = cursor;
                TraversalPhaseIndex = traversalPhaseIndex;
            }
        }

        internal readonly struct BroadcastVehicleStationContext
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
            public readonly string TurnbackStationId;
            public readonly string TurnbackStationName;

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
                string terminalStationName,
                string turnbackStationId,
                string turnbackStationName)
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
                TurnbackStationId = turnbackStationId ?? string.Empty;
                TurnbackStationName = turnbackStationName ?? string.Empty;
            }
        }

        internal sealed class BroadcastLineStationContextCache
        {
            public ulong Signature;
            public BroadcastResolvedStation[] Stations = Array.Empty<BroadcastResolvedStation>();
            public BroadcastResolvedStation[] StationByWaypoint = Array.Empty<BroadcastResolvedStation>();
            public Dictionary<string, BroadcastResolvedStation> StationById =
                new Dictionary<string, BroadcastResolvedStation>(StringComparer.Ordinal);
            public int[] StationOrderByWaypoint = Array.Empty<int>();
            public int[] NormalizedStationWaypointByWaypoint = Array.Empty<int>();
            public int[] NextDistinctStationWaypointByWaypoint = Array.Empty<int>();
            public int TerminalStationWaypointIndex = -1;
            public BroadcastResolvedStation[] TurnbackStations = Array.Empty<BroadcastResolvedStation>();
        }

        private const int BroadcastRuntimeClipCacheLimit = 24;
        internal const int BroadcastApproachRemainingAtomThreshold = 4;
        private const uint BroadcastAnchorDiagnosticCooldownFrames = 30u;
        private const int BroadcastIdleRouteCooldownAfterLeaveSeconds = 3;
        private const string BroadcastPlatformIdleTriggerId = "platform_idle_clear";
        private const string BroadcastPlatformApproachTriggerId = "platform_approach_station";
        private const float BroadcastLeaveAnchorDistanceMeters = 100f;
        private const float BroadcastApproachAnchorDistanceMeters = 200f;
        private const float BroadcastPlatformApproachAnchorDistanceMeters = 600f;
        internal const float BroadcastPlatformPreparingApproachLeadMinutes = 10f;
        private const int BroadcastPlatformPreparingApproachPhaseIndex = 999999;
        private const int BroadcastPlatformIdleBusyGraceMinutes = 3;
        private const int BroadcastPlatformIdleQuietConfirmMinutes = 1;
        private static readonly FieldInfo s_AudioManagerWorldGroupField =
            typeof(AudioManager).GetField("m_WorldGroup", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Dictionary<Entity, string> m_BroadcastLastStopAndOpenStopByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BroadcastLastLeaveStationStopByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BroadcastLastBypassWaitingStopByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, BroadcastProgressTriggerState> m_BroadcastProgressTriggerStateByVehicle =
            new Dictionary<Entity, BroadcastProgressTriggerState>();
        private readonly Dictionary<Entity, BroadcastPlatformApproachTriggerState> m_BroadcastPlatformApproachTriggerStateByVehicle =
            new Dictionary<Entity, BroadcastPlatformApproachTriggerState>();
        private readonly Dictionary<Entity, string> m_BroadcastCurrentStationNameByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BroadcastNextStationNameByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BroadcastLastEventTextByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BroadcastAnchorDiagnosticKeyCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BroadcastAnchorDiagnosticLastLogFrameCache = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_BroadcastAnchorTriggerLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BroadcastPlatformApproachDiagnosticKeyCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BroadcastPlatformApproachDiagnosticLastLogFrameCache = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_BroadcastPlatformApproachTriggerLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, int> m_BroadcastCurrentStopWaypointIndexByVehicle = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_BroadcastNextStopWaypointIndexByVehicle = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, BroadcastLineStationContextCache> m_BroadcastLineStationContextCaches =
            new Dictionary<Entity, BroadcastLineStationContextCache>();
        private readonly HashSet<string> m_BroadcastRuntimeCheckedLineIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, BroadcastRuntimeSequenceState> m_BroadcastSequenceStateByVehicle =
            new Dictionary<Entity, BroadcastRuntimeSequenceState>();
        private readonly Dictionary<string, BroadcastRuntimeClipCacheEntry> m_BroadcastRuntimeClipCache =
            new Dictionary<string, BroadcastRuntimeClipCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<AudioClip>> m_BroadcastRuntimeClipLoadTasks =
            new Dictionary<string, Task<AudioClip>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, uint> m_BroadcastPlatformAnnouncementCooldownUntilFrame =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> m_BroadcastPlatformStationBusyUntilFrame =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> m_BroadcastPlatformStationQuietSinceFrame =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, BroadcastRuntimeSequenceState> m_BroadcastPlatformSequenceStateByKey =
            new Dictionary<string, BroadcastRuntimeSequenceState>(StringComparer.Ordinal);

        internal void StopOpened(
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
            int currentStopWaypointIndex)
        {
            EmitBroadcastWaypointTrigger(vehicle, line, waypoints, currentStopWaypointIndex, "leave_station");
        }

        internal void ServiceEnded(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int previousWaypointIndex)
        {
            if (!ShouldPlayForTracked(vehicle)
                || !TryBuildBroadcastTriggerContext(
                    vehicle,
                    line,
                    waypoints,
                    previousWaypointIndex,
                    out _,
                    out BroadcastVehicleStationContext stationContext))
            {
                return;
            }

            RememberBroadcastLeaveTriggerAtom(vehicle, line, waypoints, stationContext);
            if (m_BroadcastProgressTriggerStateByVehicle.TryGetValue(vehicle, out BroadcastProgressTriggerState state))
            {
                LogBroadcastAnchorState("arm", vehicle, line, waypoints, stationContext, state, false);
            }
        }

        internal void BypassWaiting(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex)
        {
            EmitBroadcastWaypointTrigger(vehicle, line, waypoints, currentWaypointIndex, "bypass_waiting");
        }

        internal void Preparing(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool atOrigin,
            uint nowFrame)
        {
            if (!HasPlatformAnnouncements(line))
                return;

            float etaFrames = atOrigin
                ? 0f
                : m_Host.EstimatePreparingArrivalFrames(
                    vehicle,
                    line,
                    waypoints,
                    nowFrame,
                    m_Host.ReadLineLapCache(line));

            WatchOriginBusy(
                line,
                waypoints,
                atOrigin || etaFrames <= BroadcastPlatformPreparingApproachLeadMinutes * (float)SIM_FRAMES_PER_MINUTE);

            if (HasApproachAnnouncements(line))
            {
                WatchPreparingApproach(
                    vehicle,
                    line,
                    waypoints,
                    atOrigin,
                    etaFrames);
            }
        }

        internal void Origin(Entity line, DynamicBuffer<RouteWaypoint> waypoints, bool busy)
        {
            if (HasPlatformAnnouncements(line))
            {
                WatchOriginBusy(line, waypoints, busy);
            }
        }

        internal void Running(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            bool boarding)
        {
            bool shouldPlayForTracked = ShouldPlayForTracked(vehicle);
            bool hasPlatformAnnouncements = HasPlatformAnnouncements(line);
            bool hasApproachWatch = HasApproachAnnouncements(line);
            bool needsContext = hasPlatformAnnouncements || shouldPlayForTracked;
            BroadcastVehicleRuntimeFrameContext context = default;
            bool hasContext = needsContext
                && TryContext(
                    vehicle,
                    line,
                    waypoints,
                    currentWaypointIndex,
                    out context);

            WatchBusy(
                line,
                waypoints,
                hasPlatformAnnouncements && hasContext,
                boarding,
                context);
            WatchApproach(
                vehicle,
                line,
                waypoints,
                hasApproachWatch && hasContext,
                context);
            Progress(
                vehicle,
                line,
                waypoints,
                boarding,
                shouldPlayForTracked,
                hasContext,
                context);
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

        private void Progress(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding,
            bool shouldBroadcastForTrackedVehicle,
            bool hasRuntimeContext,
            BroadcastVehicleRuntimeFrameContext runtimeContext)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length < 2
                || !shouldBroadcastForTrackedVehicle
                || !hasRuntimeContext
                || !TryGetCursorWaypointWindowRelation(
                    runtimeContext.Chain,
                    runtimeContext.StationContext.CurrentStopWaypointIndex,
                    runtimeContext.Cursor.AtomCursorIndex,
                    out CursorAtomWindowRelation currentStopWindowRelation,
                    out _,
                    out _))
            {
                m_BroadcastProgressTriggerStateByVehicle.Remove(vehicle);
                return;
            }

            BroadcastVehicleStationContext stationContext = runtimeContext.StationContext;
            LineTrackChain chain = runtimeContext.Chain;
            VehicleTrackCursor cursor = runtimeContext.Cursor;
            bool resetState = !m_BroadcastProgressTriggerStateByVehicle.TryGetValue(vehicle, out BroadcastProgressTriggerState state)
                || state.CurrentStopWaypointIndex != stationContext.CurrentStopWaypointIndex
                || state.NextStopWaypointIndex != stationContext.NextStopWaypointIndex;
            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            if (resetState)
            {
                state = new BroadcastProgressTriggerState
                {
                    CurrentStopWaypointIndex = stationContext.CurrentStopWaypointIndex,
                    NextStopWaypointIndex = stationContext.NextStopWaypointIndex,
                    LeaveTriggerAtomIndex = -1,
                    BroadcastLeaveAtomIndex = -1,
                    BroadcastApproachAtomIndex = -1,
                    LeaveTriggeredFrame = 0u,
                    IdleRouteBlockedUntilFrame = 0u,
                    IdleRouteBlockedUntilRealtime = 0f,
                    IdleRouteWaitingForLeaveSequenceEnd = false,
                    LastCurrentStopWindowRelation = currentStopWindowRelation,
                    LeaveTriggered = false,
                    MidRouteTriggered = false,
                    ApproachTriggered = false
                };

                TryResolveBroadcastDistanceAnchoredAtoms(
                    vehicle,
                    line,
                    waypoints,
                    state.CurrentStopWaypointIndex,
                    state.NextStopWaypointIndex,
                    out state.BroadcastLeaveAtomIndex,
                    out state.BroadcastApproachAtomIndex);
            }

            if (boarding)
            {
                state.LastCurrentStopWindowRelation = currentStopWindowRelation;
                LogBroadcastAnchorState("tick", vehicle, line, waypoints, stationContext, state, false);
                m_BroadcastProgressTriggerStateByVehicle[vehicle] = state;
                return;
            }

            if (!state.LeaveTriggered
                && state.LastCurrentStopWindowRelation == CursorAtomWindowRelation.Inside
                && currentStopWindowRelation == CursorAtomWindowRelation.After)
            {
                state.LeaveTriggerAtomIndex = cursor.AtomCursorIndex;
                HandleBroadcastLeaveStationTrigger(vehicle, line, waypoints, state.CurrentStopWaypointIndex);
                state.LeaveTriggered = true;
                state.LeaveTriggeredFrame = nowFrame;
                if (LineHasBroadcastRulesForTrigger(stationContext.LineId, "leave_station"))
                {
                    state.IdleRouteWaitingForLeaveSequenceEnd = true;
                    state.IdleRouteBlockedUntilFrame = 0u;
                    state.IdleRouteBlockedUntilRealtime = 0f;
                }
                LogBroadcastAnchorState("trigger-leave", vehicle, line, waypoints, stationContext, state, true);
            }

            UpdateIdleRouteLeaveProtection(vehicle, ref state, nowFrame);

            if (!state.MidRouteTriggered
                && state.LeaveTriggered
                && state.LeaveTriggeredFrame != nowFrame
                && IsIdleRouteLeaveProtectionSatisfied(state, nowFrame)
                && IsBroadcastVehicleWithinIdleRouteAtomWindow(
                    vehicle,
                    line,
                    waypoints,
                    state.NextStopWaypointIndex,
                    state.LeaveTriggerAtomIndex,
                    state.BroadcastLeaveAtomIndex,
                    state.BroadcastApproachAtomIndex))
            {
                HandleBroadcastMidRouteTrigger(vehicle, line, waypoints, state.CurrentStopWaypointIndex);
                state.MidRouteTriggered = true;
                LogBroadcastAnchorState("trigger-mid", vehicle, line, waypoints, stationContext, state, true);
            }

            if (!state.ApproachTriggered
                && (!state.LeaveTriggered || state.LeaveTriggeredFrame != nowFrame)
                && IsBroadcastVehicleWithinApproachAtomWindow(
                    vehicle,
                    line,
                    waypoints,
                    state.NextStopWaypointIndex,
                    state.BroadcastApproachAtomIndex))
            {
                HandleBroadcastApproachStationTrigger(vehicle, line, waypoints, state.CurrentStopWaypointIndex);
                state.ApproachTriggered = true;
                LogBroadcastAnchorState("trigger-approach", vehicle, line, waypoints, stationContext, state, true);
            }

            state.LastCurrentStopWindowRelation = currentStopWindowRelation;
            LogBroadcastAnchorState("tick", vehicle, line, waypoints, stationContext, state, false);
            m_BroadcastProgressTriggerStateByVehicle[vehicle] = state;
        }

        private bool TryContext(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int preferredCurrentStopWaypointIndex,
            out BroadcastVehicleRuntimeFrameContext runtimeContext)
        {
            runtimeContext = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length < 2
                || !TryResolveBroadcastVehicleStationContext(
                    vehicle,
                    line,
                    waypoints,
                    preferredCurrentStopWaypointIndex,
                    out BroadcastVehicleStationContext stationContext)
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    out VehicleTrackCursor cursor,
                    out _,
                    out _,
                    out _,
                    out int traversalPhaseIndex,
                    out _,
                    out _,
                    out _))
            {
                return false;
            }

            runtimeContext = new BroadcastVehicleRuntimeFrameContext(
                stationContext,
                chain,
                cursor,
                traversalPhaseIndex);
            return true;
        }

        private bool IsBroadcastVehicleWithinApproachAtomWindow(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int nextStopWaypointIndex,
            int broadcastApproachAtomIndex)
        {
            if (broadcastApproachAtomIndex < 0
                || vehicle == Entity.Null
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

            return cursor.AtomCursorIndex >= broadcastApproachAtomIndex;
        }

        private bool IsBroadcastVehicleWithinIdleRouteAtomWindow(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int nextStopWaypointIndex,
            int leaveTriggerAtomIndex,
            int broadcastLeaveAtomIndex,
            int broadcastApproachAtomIndex)
        {
            if (leaveTriggerAtomIndex < 0
                || broadcastLeaveAtomIndex < 0
                || broadcastApproachAtomIndex < 0
                || vehicle == Entity.Null
                || line == Entity.Null
                || nextStopWaypointIndex < 0
                || nextStopWaypointIndex >= waypoints.Length
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            int effectiveLeaveAtomIndex = GetEffectiveBroadcastLeaveAtomIndex(
                leaveTriggerAtomIndex,
                broadcastLeaveAtomIndex);
            if (effectiveLeaveAtomIndex >= broadcastApproachAtomIndex)
            {
                return false;
            }

            return cursor.AtomCursorIndex >= effectiveLeaveAtomIndex
                && cursor.AtomCursorIndex < broadcastApproachAtomIndex;
        }

        private static int GetEffectiveBroadcastLeaveAtomIndex(
            int leaveTriggerAtomIndex,
            int broadcastLeaveAtomIndex)
        {
            return math.max(leaveTriggerAtomIndex, broadcastLeaveAtomIndex);
        }

        private void LogBroadcastAnchorState(
            string phase,
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            BroadcastVehicleStationContext stationContext,
            BroadcastProgressTriggerState state,
            bool onceOnly)
        {
            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            if (!TryBuildBroadcastAnchorDiagnostic(
                    phase,
                    vehicle,
                    line,
                    waypoints,
                    stationContext,
                    state,
                    out string stableKey,
                    out string message))
            {
                return;
            }

            if (onceOnly)
            {
                LogVehicleStateOnce(
                    m_BroadcastAnchorTriggerLogCache,
                    vehicle,
                    phase + "|" + stableKey,
                    message);
                return;
            }

            if (ShouldEmitVehicleLogWithCooldown(
                    m_BroadcastAnchorDiagnosticKeyCache,
                    m_BroadcastAnchorDiagnosticLastLogFrameCache,
                    vehicle,
                    phase + "|" + stableKey,
                    nowFrame,
                    BroadcastAnchorDiagnosticCooldownFrames))
            {
                log.Info(message);
            }
        }

        private void LogBroadcastPlatformApproachState(
            string phase,
            Entity vehicle,
            LineTrackChain chain,
            BroadcastPlatformApproachTriggerState state,
            bool onceOnly)
        {
            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            if (!TryBuildBroadcastPlatformApproachDiagnostic(
                    phase,
                    vehicle,
                    chain,
                    state,
                    out string stableKey,
                    out string message))
            {
                return;
            }

            if (onceOnly)
            {
                LogVehicleStateOnce(
                    m_BroadcastPlatformApproachTriggerLogCache,
                    vehicle,
                    phase + "|" + stableKey,
                    message);
                return;
            }

            if (ShouldEmitVehicleLogWithCooldown(
                    m_BroadcastPlatformApproachDiagnosticKeyCache,
                    m_BroadcastPlatformApproachDiagnosticLastLogFrameCache,
                    vehicle,
                    phase + "|" + stableKey,
                    nowFrame,
                    BroadcastAnchorDiagnosticCooldownFrames))
            {
                log.Info(message);
            }
        }

        private bool TryBuildBroadcastPlatformApproachDiagnostic(
            string phase,
            Entity vehicle,
            LineTrackChain chain,
            BroadcastPlatformApproachTriggerState state,
            out string stableKey,
            out string message)
        {
            stableKey = string.Empty;
            message = string.Empty;
            if (vehicle == Entity.Null
                || chain == null
                || state.NextStopWaypointIndex < 0
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    state.NextStopWaypointIndex,
                    state.CursorAtomIndex,
                    out int nextWindowStart,
                    out _))
            {
                return false;
            }

            int currentWindowEndExclusive = 0;
            if (state.CurrentStopWaypointIndex >= 0
                && !TryGetWaypointTraversalAtomWindow(
                    chain,
                    state.CurrentStopWaypointIndex,
                    state.CursorAtomIndex,
                    out _,
                    out currentWindowEndExclusive))
            {
                return false;
            }

            currentWindowEndExclusive = math.clamp(currentWindowEndExclusive, 0, chain.TrackAtoms.Count);
            nextWindowStart = math.clamp(nextWindowStart, 0, math.max(0, chain.TrackAtoms.Count - 1));
            bool fallback = state.TriggerAtomIndex == currentWindowEndExclusive;
            stableKey = "line=" + (state.LineId ?? string.Empty)
                + "|station=" + (state.StationId ?? string.Empty)
                + "|phaseIndex=" + state.TraversalPhaseIndex
                + "|cur=" + state.CurrentStopWaypointIndex
                + "|next=" + state.NextStopWaypointIndex
                + "|cursor=" + state.CursorAtomIndex
                + "|trigger=" + state.TriggerAtomIndex
                + "|fallback=" + fallback;
            message = "[BroadcastPlatformApproach] line=" + (state.LineId ?? string.Empty)
                + " vehicle=" + vehicle.Index
                + " phase=" + phase
                + " station=\"" + (state.StationContext.NextStationName ?? string.Empty) + "\""
                + " current=\"" + (state.StationContext.CurrentStationName ?? string.Empty) + "\""
                + " next=\"" + (state.StationContext.NextStationName ?? string.Empty) + "\""
                + " wp=" + state.CurrentStopWaypointIndex + "->" + state.NextStopWaypointIndex
                + " cursor=" + state.CursorAtomIndex
                + " triggerAtom=" + state.TriggerAtomIndex
                + " currentWindowEnd=" + currentWindowEndExclusive
                + " nextWindowStart=" + nextWindowStart
                + " anchorMeters=" + BroadcastPlatformApproachAnchorDistanceMeters.ToString("F0")
                + " fallback=" + fallback
                + " triggered=" + state.Triggered
                + " phaseIndex=" + state.TraversalPhaseIndex;
            return true;
        }

        private bool TryBuildBroadcastAnchorDiagnostic(
            string phase,
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            BroadcastVehicleStationContext stationContext,
            BroadcastProgressTriggerState state,
            out string stableKey,
            out string message)
        {
            stableKey = string.Empty;
            message = string.Empty;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor)
                || state.CurrentStopWaypointIndex < 0
                || state.NextStopWaypointIndex < 0
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    state.CurrentStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out _,
                    out int currentWindowEndExclusive)
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    state.NextStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out int nextWindowStart,
                    out int nextWindowEndExclusive))
            {
                return false;
            }

            int effectiveLeaveAtomIndex = state.LeaveTriggerAtomIndex >= 0 && state.BroadcastLeaveAtomIndex >= 0
                ? GetEffectiveBroadcastLeaveAtomIndex(state.LeaveTriggerAtomIndex, state.BroadcastLeaveAtomIndex)
                : -1;
            int approachFallbackAtomIndex = math.max(currentWindowEndExclusive, nextWindowStart - 1);
            bool leaveFallback = state.BroadcastLeaveAtomIndex == currentWindowEndExclusive;
            bool approachFallback = state.BroadcastApproachAtomIndex == approachFallbackAtomIndex;

            stableKey = "cur=" + state.CurrentStopWaypointIndex
                + "|next=" + state.NextStopWaypointIndex
                + "|leaveTrig=" + state.LeaveTriggerAtomIndex
                + "|leaveAnchor=" + state.BroadcastLeaveAtomIndex
                + "|effectiveLeave=" + effectiveLeaveAtomIndex
                + "|approachAnchor=" + state.BroadcastApproachAtomIndex
                + "|currentEnd=" + currentWindowEndExclusive
                + "|nextStart=" + nextWindowStart
                + "|nextEnd=" + nextWindowEndExclusive
                + "|leaveDone=" + (state.LeaveTriggered ? "1" : "0")
                + "|midDone=" + (state.MidRouteTriggered ? "1" : "0")
                + "|approachDone=" + (state.ApproachTriggered ? "1" : "0");

            message = "[BroadcastAnchor] line=" + line.Index
                + " vehicle=" + vehicle.Index
                + " phase=" + phase
                + " current=\"" + (stationContext.CurrentStationName ?? string.Empty) + "\""
                + " next=\"" + (stationContext.NextStationName ?? string.Empty) + "\""
                + " wp=" + state.CurrentStopWaypointIndex + "->" + state.NextStopWaypointIndex
                + " cursor=" + cursor.AtomCursorIndex
                + " leaveTrigger=" + state.LeaveTriggerAtomIndex
                + " leaveAnchor=" + state.BroadcastLeaveAtomIndex
                + " effectiveLeave=" + effectiveLeaveAtomIndex
                + " approachAnchor=" + state.BroadcastApproachAtomIndex
                + " currentWindowEnd=" + currentWindowEndExclusive
                + " nextWindowStart=" + nextWindowStart
                + " nextWindowEnd=" + nextWindowEndExclusive
                + " leaveFallback=" + leaveFallback
                + " approachFallback=" + approachFallback
                + " fired=" + (state.LeaveTriggered ? "L" : "-")
                + (state.MidRouteTriggered ? "M" : "-")
                + (state.ApproachTriggered ? "A" : "-");
            return true;
        }

        private void RememberBroadcastLeaveTriggerAtom(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            BroadcastVehicleStationContext stationContext)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return;
            }

            if (m_BroadcastProgressTriggerStateByVehicle.TryGetValue(vehicle, out BroadcastProgressTriggerState existingState)
                && existingState.CurrentStopWaypointIndex == stationContext.CurrentStopWaypointIndex
                && existingState.NextStopWaypointIndex == stationContext.NextStopWaypointIndex)
            {
                return;
            }

            TryResolveBroadcastDistanceAnchoredAtoms(
                vehicle,
                line,
                waypoints,
                stationContext.CurrentStopWaypointIndex,
                stationContext.NextStopWaypointIndex,
                out int broadcastLeaveAtomIndex,
                out int broadcastApproachAtomIndex);

            CursorAtomWindowRelation currentStopWindowRelation = CursorAtomWindowRelation.Unknown;
            if (TryGetLineTrackChain(line, waypoints, out LineTrackChain relationChain)
                && relationChain != null
                && TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, relationChain, out VehicleTrackCursor relationCursor)
                && TryGetCursorWaypointWindowRelation(
                    relationChain,
                    stationContext.CurrentStopWaypointIndex,
                    relationCursor.AtomCursorIndex,
                    out CursorAtomWindowRelation liveRelation,
                    out _,
                    out _))
            {
                currentStopWindowRelation = liveRelation;
            }

            m_BroadcastProgressTriggerStateByVehicle[vehicle] = new BroadcastProgressTriggerState
            {
                CurrentStopWaypointIndex = stationContext.CurrentStopWaypointIndex,
                NextStopWaypointIndex = stationContext.NextStopWaypointIndex,
                LeaveTriggerAtomIndex = -1,
                BroadcastLeaveAtomIndex = broadcastLeaveAtomIndex,
                BroadcastApproachAtomIndex = broadcastApproachAtomIndex,
                IdleRouteBlockedUntilFrame = 0u,
                IdleRouteBlockedUntilRealtime = 0f,
                IdleRouteWaitingForLeaveSequenceEnd = false,
                LastCurrentStopWindowRelation = currentStopWindowRelation == CursorAtomWindowRelation.Unknown
                    ? CursorAtomWindowRelation.Inside
                    : currentStopWindowRelation,
                LeaveTriggeredFrame = 0u,
                LeaveTriggered = false,
                MidRouteTriggered = false,
                ApproachTriggered = false
            };
        }

        private bool TryResolveBroadcastDistanceAnchoredAtoms(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentStopWaypointIndex,
            int nextStopWaypointIndex,
            out int broadcastLeaveAtomIndex,
            out int broadcastApproachAtomIndex)
        {
            broadcastLeaveAtomIndex = -1;
            broadcastApproachAtomIndex = -1;

            if (vehicle == Entity.Null
                || line == Entity.Null
                || currentStopWaypointIndex < 0
                || currentStopWaypointIndex >= waypoints.Length
                || nextStopWaypointIndex < 0
                || nextStopWaypointIndex >= waypoints.Length
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor)
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    currentStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out _,
                    out int currentWindowEndExclusive)
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    nextStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out int nextWindowStart,
                    out _))
            {
                return false;
            }

            currentWindowEndExclusive = math.clamp(currentWindowEndExclusive, 0, chain.TrackAtoms.Count);
            nextWindowStart = math.clamp(nextWindowStart, 0, math.max(0, chain.TrackAtoms.Count - 1));
            if (currentWindowEndExclusive >= nextWindowStart)
            {
                broadcastLeaveAtomIndex = currentWindowEndExclusive;
                broadcastApproachAtomIndex = math.max(currentWindowEndExclusive, nextWindowStart - 1);
                return true;
            }

            broadcastLeaveAtomIndex = ResolveBroadcastDistanceAnchoredForwardAtom(
                chain,
                currentWindowEndExclusive,
                nextWindowStart,
                BroadcastLeaveAnchorDistanceMeters,
                currentWindowEndExclusive);
            broadcastApproachAtomIndex = ResolveBroadcastDistanceAnchoredBackwardAtom(
                chain,
                currentWindowEndExclusive,
                nextWindowStart,
                BroadcastApproachAnchorDistanceMeters,
                math.max(currentWindowEndExclusive, nextWindowStart - 1));
            return true;
        }

        private bool TryResolveBroadcastPlatformApproachAtom(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentStopWaypointIndex,
            int nextStopWaypointIndex,
            out int triggerAtomIndex,
            out int cursorAtomIndex)
        {
            triggerAtomIndex = -1;
            cursorAtomIndex = -1;

            if (vehicle == Entity.Null
                || line == Entity.Null
                || currentStopWaypointIndex < 0
                || currentStopWaypointIndex >= waypoints.Length
                || nextStopWaypointIndex < 0
                || nextStopWaypointIndex >= waypoints.Length
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor)
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    currentStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out _,
                    out int currentWindowEndExclusive)
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    nextStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out int nextWindowStart,
                    out _))
            {
                return false;
            }

            cursorAtomIndex = cursor.AtomCursorIndex;
            return TryResolveBroadcastPlatformApproachAtom(
                chain,
                cursor.AtomCursorIndex,
                currentStopWaypointIndex,
                nextStopWaypointIndex,
                out triggerAtomIndex);
        }

        private bool TryResolveBroadcastPlatformApproachAtom(
            LineTrackChain chain,
            int cursorAtomIndex,
            int currentStopWaypointIndex,
            int nextStopWaypointIndex,
            out int triggerAtomIndex)
        {
            triggerAtomIndex = -1;
            if (chain == null
                || currentStopWaypointIndex < 0
                || nextStopWaypointIndex < 0
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    currentStopWaypointIndex,
                    cursorAtomIndex,
                    out _,
                    out int currentWindowEndExclusive)
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    nextStopWaypointIndex,
                    cursorAtomIndex,
                    out int nextWindowStart,
                    out _))
            {
                return false;
            }

            currentWindowEndExclusive = math.clamp(currentWindowEndExclusive, 0, chain.TrackAtoms.Count);
            nextWindowStart = math.clamp(nextWindowStart, 0, math.max(0, chain.TrackAtoms.Count - 1));
            if (currentWindowEndExclusive >= nextWindowStart)
            {
                triggerAtomIndex = currentWindowEndExclusive;
                return true;
            }

            triggerAtomIndex = ResolveBroadcastDistanceAnchoredBackwardAtom(
                chain,
                currentWindowEndExclusive,
                nextWindowStart,
                BroadcastPlatformApproachAnchorDistanceMeters,
                currentWindowEndExclusive);
            return true;
        }

        private int ResolveBroadcastDistanceAnchoredForwardAtom(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            float anchorDistanceMeters,
            int fallbackAtomIndex)
        {
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || startAtomIndex < 0
                || startAtomIndex >= chain.TrackAtoms.Count
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return fallbackAtomIndex;
            }

            float traversedDistance = 0f;
            int lastAtomIndex = math.min(endAtomIndexExclusive - 1, chain.TrackAtoms.Count - 1);
            if (lastAtomIndex < startAtomIndex)
                return fallbackAtomIndex;

            for (int atomIndex = startAtomIndex; atomIndex <= lastAtomIndex; atomIndex++)
            {
                if (!TryGetBroadcastTrackAtomTraversalLengthMeters(chain, atomIndex, out float atomDistance))
                    return fallbackAtomIndex;

                traversedDistance += atomDistance;
                if (traversedDistance >= anchorDistanceMeters)
                    return atomIndex;
            }

            return fallbackAtomIndex;
        }

        private int ResolveBroadcastDistanceAnchoredBackwardAtom(
            LineTrackChain chain,
            int startAtomIndexInclusive,
            int endAtomIndexExclusive,
            float anchorDistanceMeters,
            int fallbackAtomIndex)
        {
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= 0
                || startAtomIndexInclusive >= endAtomIndexExclusive)
            {
                return fallbackAtomIndex;
            }

            float traversedDistance = 0f;
            int startAtomIndex = math.max(0, startAtomIndexInclusive);
            int lastAtomIndex = math.min(endAtomIndexExclusive - 1, chain.TrackAtoms.Count - 1);
            for (int atomIndex = lastAtomIndex; atomIndex >= startAtomIndex; atomIndex--)
            {
                if (!TryGetBroadcastTrackAtomTraversalLengthMeters(chain, atomIndex, out float atomDistance))
                    return fallbackAtomIndex;

                traversedDistance += atomDistance;
                if (traversedDistance >= anchorDistanceMeters)
                    return atomIndex;
            }

            return fallbackAtomIndex;
        }

        private bool TryGetBroadcastTrackAtomTraversalLengthMeters(
            LineTrackChain chain,
            int atomIndex,
            out float distanceMeters)
        {
            distanceMeters = 0f;
            if (chain == null || atomIndex < 0 || atomIndex >= chain.TrackAtoms.Count)
                return false;

            TrackAtom atom = chain.TrackAtoms[atomIndex];
            if (TryGetTrackAtomCurveTraversalLengthMeters(atom, out distanceMeters))
                return true;

            if (TryGetTrackAtomWorldPosition(chain, atomIndex, out float3 atomPosition))
            {
                int nextAtomIndex = atomIndex + 1;
                if (nextAtomIndex < chain.TrackAtoms.Count
                    && TryGetTrackAtomWorldPosition(chain, nextAtomIndex, out float3 nextAtomPosition))
                {
                    distanceMeters = math.distance(atomPosition, nextAtomPosition);
                    return true;
                }

                int previousAtomIndex = atomIndex - 1;
                if (previousAtomIndex >= 0
                    && TryGetTrackAtomWorldPosition(chain, previousAtomIndex, out float3 previousAtomPosition))
                {
                    distanceMeters = math.distance(previousAtomPosition, atomPosition);
                    return true;
                }
            }

            return false;
        }

        private bool TryGetTrackAtomCurveTraversalLengthMeters(
            TrackAtom atom,
            out float distanceMeters)
        {
            distanceMeters = 0f;
            if (TryGetEntityCurveTraversalLengthMeters(atom.SourceTarget, atom.TargetDelta, out distanceMeters))
                return true;

            return atom.Key.PhysicalLaneKey != atom.SourceTarget
                && TryGetEntityCurveTraversalLengthMeters(atom.Key.PhysicalLaneKey, atom.TargetDelta, out distanceMeters);
        }

        private bool TryGetEntityCurveTraversalLengthMeters(
            Entity entity,
            float2 targetDelta,
            out float distanceMeters)
        {
            distanceMeters = 0f;
            if (entity == Entity.Null
                || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<Curve>(entity))
            {
                return false;
            }

            float start = math.saturate(targetDelta.x);
            float end = math.saturate(targetDelta.y);
            if (math.abs(end - start) <= 0.0001f)
            {
                distanceMeters = 0f;
                return true;
            }

            Curve curve = EntityManager.GetComponentData<Curve>(entity);
            Bounds1 curveBounds = new Bounds1(math.min(start, end), math.max(start, end));
            distanceMeters = MathUtils.Length(curve.m_Bezier.xz, curveBounds);
            return true;
        }

        private void EmitBroadcastWaypointTrigger(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            string triggerId)
        {
            TryEmitBroadcastWaypointTrigger(vehicle, line, waypoints, waypointIndex, triggerId, out _, out _);
        }

        private bool TryEmitBroadcastWaypointTrigger(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            string triggerId,
            out BroadcastTriggerContext context,
            out BroadcastVehicleStationContext stationContext)
        {
            context = default;
            stationContext = default;
            if (!ShouldPlayForTracked(vehicle))
            {
                return false;
            }

            if (!TryBuildBroadcastTriggerContext(vehicle, line, waypoints, waypointIndex, out context, out stationContext))
            {
                return false;
            }

            if (IsDuplicateBroadcastTrigger(vehicle, triggerId, stationContext.CurrentStationId))
            {
                return false;
            }

            RememberBroadcastTriggerStop(vehicle, triggerId, stationContext.CurrentStationId);
            UpdateBroadcastVehiclePanelState(vehicle, context.CurrentStationName, context.NextStationName);
            TryStartBroadcastSequence(vehicle, context, triggerId);
            return true;
        }

        private bool ShouldPlayForTracked(Entity vehicle)
        {
            if (!IsBroadcastFeatureEnabled()
                || vehicle == Entity.Null
                || m_CameraUpdateSystem == null)
            {
                return false;
            }
            if (m_VehicleView.TryGetState(vehicle, out VehicleState state)
                && state == VehicleState.Retiring)
            {
                return false;
            }

            OrbitCameraController orbitCameraController = m_CameraUpdateSystem.orbitCameraController;
            if (orbitCameraController == null
                || !ReferenceEquals(m_CameraUpdateSystem.activeCameraController, orbitCameraController))
            {
                return false;
            }

            Entity followedVehicle = ResolveRuntimeControllerVehicle(orbitCameraController.followedEntity);
            Entity broadcastVehicle = ResolveRuntimeControllerVehicle(vehicle);
            return followedVehicle != Entity.Null
                && broadcastVehicle != Entity.Null
                && followedVehicle == broadcastVehicle;
        }

        private bool IsDuplicateBroadcastTrigger(Entity vehicle, string triggerId, string currentStationId)
        {
            if (vehicle == Entity.Null || string.IsNullOrWhiteSpace(currentStationId))
            {
                return false;
            }

            switch (triggerId ?? string.Empty)
            {
                case "stop_and_open":
                    return m_BroadcastLastStopAndOpenStopByVehicle.TryGetValue(vehicle, out string stopAndOpenStop)
                        && string.Equals(stopAndOpenStop, currentStationId, StringComparison.Ordinal);
                case "leave_station":
                    return m_BroadcastLastLeaveStationStopByVehicle.TryGetValue(vehicle, out string leaveStop)
                        && string.Equals(leaveStop, currentStationId, StringComparison.Ordinal);
                case "bypass_waiting":
                    return m_BroadcastLastBypassWaitingStopByVehicle.TryGetValue(vehicle, out string bypassStop)
                        && string.Equals(bypassStop, currentStationId, StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        private void RememberBroadcastTriggerStop(Entity vehicle, string triggerId, string currentStationId)
        {
            if (vehicle == Entity.Null || string.IsNullOrWhiteSpace(currentStationId))
            {
                return;
            }

            switch (triggerId ?? string.Empty)
            {
                case "stop_and_open":
                    m_BroadcastLastStopAndOpenStopByVehicle[vehicle] = currentStationId;
                    break;
                case "leave_station":
                    m_BroadcastLastLeaveStationStopByVehicle[vehicle] = currentStationId;
                    break;
                case "bypass_waiting":
                    m_BroadcastLastBypassWaitingStopByVehicle[vehicle] = currentStationId;
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

        private bool LineHasBroadcastRulesForTrigger(string lineId, string triggerId)
        {
            return !string.IsNullOrEmpty(lineId)
                && !string.IsNullOrEmpty(triggerId)
                && m_BroadcastLineRules.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                && rules != null
                && rules.Any(rule => rule != null
                    && string.Equals(rule.triggerId, triggerId, StringComparison.Ordinal)
                    && rule.nodes != null
                    && rule.nodes.Length > 0);
        }

        private void EnsureBroadcastRuntimeLineState(string lineId, Entity line)
        {
            if (!IsBroadcastFeatureEnabled()
                || string.IsNullOrWhiteSpace(lineId)
                || line == Entity.Null
                || m_BroadcastRuntimeCheckedLineIds.Contains(lineId))
            {
                return;
            }

            EnsureBroadcastLineStateMigrated(lineId, line, out _);
            m_BroadcastRuntimeCheckedLineIds.Add(lineId);
        }

        private bool IsBroadcastSequenceActiveForTrigger(Entity vehicle, string triggerId)
        {
            return vehicle != Entity.Null
                && !string.IsNullOrEmpty(triggerId)
                && m_BroadcastSequenceStateByVehicle.TryGetValue(vehicle, out BroadcastRuntimeSequenceState state)
                && state != null
                && string.Equals(state.TriggerId, triggerId, StringComparison.Ordinal);
        }

        private void UpdateIdleRouteLeaveProtection(
            Entity vehicle,
            ref BroadcastProgressTriggerState state,
            uint nowFrame)
        {
            if (!state.IdleRouteWaitingForLeaveSequenceEnd)
            {
                return;
            }

            if (IsBroadcastSequenceActiveForTrigger(vehicle, "leave_station"))
            {
                return;
            }

            state.IdleRouteWaitingForLeaveSequenceEnd = false;
            state.IdleRouteBlockedUntilFrame = nowFrame;
            state.IdleRouteBlockedUntilRealtime = UnityEngine.Time.realtimeSinceStartup + BroadcastIdleRouteCooldownAfterLeaveSeconds;
        }

        private static bool IsIdleRouteLeaveProtectionSatisfied(
            BroadcastProgressTriggerState state,
            uint nowFrame)
        {
            if (state.IdleRouteWaitingForLeaveSequenceEnd)
            {
                return false;
            }

            return UnityEngine.Time.realtimeSinceStartup >= state.IdleRouteBlockedUntilRealtime;
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
                AudioPositionEntity = vehicle,
                LineId = context.LineId,
                TriggerId = triggerId,
                Context = context,
                Rules = matchedRules,
                ResumeFrame = nowFrame,
                ResumeRealtime = 0f
            };
            m_BroadcastSequenceStateByVehicle[vehicle] = state;
            m_BroadcastLastEventTextByVehicle[vehicle] = BuildBroadcastEventText(triggerId, context);
            m_SelectionPanel.Invalidate();
            AdvanceBroadcastRuntimeSequence(state, nowFrame);
        }

        private void TickBroadcastPlatformAnnouncements(uint nowFrame)
        {
            if (m_BroadcastLinePlatformAnnouncements.Count == 0)
            {
                return;
            }

            PruneBroadcastPlatformApproachTriggerStates(nowFrame);

            List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
            for (int i = 0; i < runtimeLines.Count; i++)
            {
                WorkbenchLineRuntime runtime = runtimeLines[i];
                if (runtime == null
                    || string.IsNullOrWhiteSpace(runtime.Id)
                    || !m_BroadcastLinePlatformAnnouncements.TryGetValue(runtime.Id, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements)
                    || lineAnnouncements == null
                    || lineAnnouncements.Count == 0
                    || !EntityManager.HasBuffer<RouteWaypoint>(runtime.Entity))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(runtime.Entity, true);
                EnsureBroadcastRuntimeLineState(runtime.Id, runtime.Entity);
                Dictionary<string, Dictionary<int, Entity>> approachCandidatesByStation = null;
                foreach (KeyValuePair<string, BroadcastWorkbenchPlatformAnnouncementDto> entry in lineAnnouncements)
                {
                    BroadcastWorkbenchPlatformAnnouncementDto announcement = entry.Value;
                    string stationId = announcement?.stationId ?? string.Empty;
                    if (announcement == null
                        || string.IsNullOrWhiteSpace(stationId)
                        || !announcement.enabled
                        || announcement.nodes == null
                        || announcement.nodes.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(announcement.triggerId, BroadcastPlatformIdleTriggerId, StringComparison.Ordinal))
                    {
                        string normalizedIdleStationId = NormalizeBroadcastPlatformRepresentativeStationId(
                            runtime.Entity,
                            waypoints,
                            stationId);
                        string cooldownKey = runtime.Id + "|" + normalizedIdleStationId + "|" + BroadcastPlatformIdleTriggerId;
                        if (m_BroadcastPlatformAnnouncementCooldownUntilFrame.TryGetValue(cooldownKey, out uint cooldownUntil)
                            && nowFrame < cooldownUntil)
                        {
                            continue;
                        }

                        if (!IsBroadcastPlatformStationIdle(runtime.Entity, waypoints, normalizedIdleStationId)
                            || !TryResolveBroadcastStationById(runtime.Entity, waypoints, stationId, out BroadcastResolvedStation station)
                            || !TryBuildBroadcastStationOnlyContext(runtime.Id, station, out BroadcastTriggerContext context))
                        {
                            continue;
                        }

                        string sequenceKey = BuildBroadcastPlatformIdleSequenceKey(runtime.Id, stationId);
                        if (TryStartBroadcastPlatformSequence(sequenceKey, station.StopEntity, context, announcement))
                        {
                            uint cooldownFrames = (uint)Math.Max(1, announcement.cooldownGameMinutes) * (uint)SIM_FRAMES_PER_MINUTE;
                            m_BroadcastPlatformAnnouncementCooldownUntilFrame[cooldownKey] = nowFrame + cooldownFrames;
                        }

                        continue;
                    }

                    if (string.Equals(announcement.triggerId, BroadcastPlatformApproachTriggerId, StringComparison.Ordinal))
                    {
                        if (approachCandidatesByStation == null)
                        {
                            approachCandidatesByStation = BuildBroadcastPlatformApproachCandidatesByStation(runtime.Id, nowFrame);
                        }

                        TickBroadcastPlatformApproachAnnouncement(
                            nowFrame,
                            runtime,
                            waypoints,
                            stationId,
                            announcement,
                            approachCandidatesByStation);
                    }
                }
            }
        }

        private void PruneBroadcastPlatformApproachTriggerStates(uint nowFrame)
        {
            if (m_BroadcastPlatformApproachTriggerStateByVehicle.Count == 0)
            {
                return;
            }

            List<Entity> staleVehicles = null;
            foreach (KeyValuePair<Entity, BroadcastPlatformApproachTriggerState> entry in m_BroadcastPlatformApproachTriggerStateByVehicle)
            {
                Entity vehicle = entry.Key;
                if (vehicle == Entity.Null
                    || !EntityManager.Exists(vehicle)
                    || !m_VehicleView.TryGetState(vehicle, out VehicleState vehicleState)
                    || (vehicleState != VehicleState.Running && vehicleState != VehicleState.Preparing)
                    || entry.Value.LastObservedFrame != nowFrame)
                {
                    staleVehicles ??= new List<Entity>();
                    staleVehicles.Add(vehicle);
                }
            }

            if (staleVehicles == null)
            {
                return;
            }

            for (int i = 0; i < staleVehicles.Count; i++)
            {
                m_BroadcastPlatformApproachTriggerStateByVehicle.Remove(staleVehicles[i]);
            }
        }

        private void TickBroadcastPlatformApproachAnnouncement(
            uint nowFrame,
            WorkbenchLineRuntime runtime,
            DynamicBuffer<RouteWaypoint> waypoints,
            string stationId,
            BroadcastWorkbenchPlatformAnnouncementDto announcement,
            Dictionary<string, Dictionary<int, Entity>> approachCandidatesByStation)
        {
            LineTrackChain chain = null;
            if (runtime == null
                || runtime.Entity == Entity.Null
                || string.IsNullOrWhiteSpace(runtime.Id)
                || string.IsNullOrWhiteSpace(stationId)
                || announcement == null
                || approachCandidatesByStation == null
                || !approachCandidatesByStation.TryGetValue(stationId, out Dictionary<int, Entity> candidatesByPhase)
                || candidatesByPhase == null
                || candidatesByPhase.Count == 0
                || !TryGetLineTrackChain(runtime.Entity, waypoints, out chain)
                || chain == null
                || !TryResolveBroadcastStationById(runtime.Entity, waypoints, stationId, out BroadcastResolvedStation station))
            {
                return;
            }

            foreach (KeyValuePair<int, Entity> candidateEntry in candidatesByPhase)
            {
                Entity vehicle = candidateEntry.Value;
                if (vehicle == Entity.Null
                    || !m_BroadcastPlatformApproachTriggerStateByVehicle.TryGetValue(vehicle, out BroadcastPlatformApproachTriggerState state)
                    || state.Triggered
                    || state.CursorAtomIndex < state.TriggerAtomIndex
                    || IsBroadcastPlatformApproachPastTriggerWindow(chain, state))
                {
                    continue;
                }

                if (!TryBuildBroadcastTriggerContext(
                        state.StationContext,
                        out BroadcastTriggerContext vehicleContext))
                {
                    continue;
                }

                string sequenceKey = BuildBroadcastPlatformApproachSequenceKey(
                    runtime.Id,
                    stationId,
                    vehicle,
                    state.CurrentStopWaypointIndex,
                    state.NextStopWaypointIndex);
                if (TryStartBroadcastPlatformSequence(
                        sequenceKey,
                        station.StopEntity,
                        vehicleContext,
                        announcement))
                {
                    state.Triggered = true;
                    m_BroadcastPlatformApproachTriggerStateByVehicle[vehicle] = state;
                    LogBroadcastPlatformApproachState("trigger", vehicle, chain, state, true);
                }
            }
        }

        private bool IsBroadcastPlatformApproachPastTriggerWindow(
            LineTrackChain chain,
            BroadcastPlatformApproachTriggerState state)
        {
            if (chain == null
                || chain.TrackAtoms.Count <= 0
                || state.TriggerAtomIndex < 0
                || state.NextStopWaypointIndex < 0
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    state.NextStopWaypointIndex,
                    state.CursorAtomIndex,
                    out _,
                    out int nextWindowEndExclusive))
            {
                return false;
            }

            nextWindowEndExclusive = math.clamp(nextWindowEndExclusive, 1, chain.TrackAtoms.Count);
            return state.TriggerAtomIndex < nextWindowEndExclusive
                && state.CursorAtomIndex >= nextWindowEndExclusive;
        }

        private Dictionary<string, Dictionary<int, Entity>> BuildBroadcastPlatformApproachCandidatesByStation(
            string lineId,
            uint nowFrame)
        {
            Dictionary<string, Dictionary<int, Entity>> candidatesByStation =
                new Dictionary<string, Dictionary<int, Entity>>(StringComparer.Ordinal);

            foreach (KeyValuePair<Entity, BroadcastPlatformApproachTriggerState> entry in m_BroadcastPlatformApproachTriggerStateByVehicle)
            {
                BroadcastPlatformApproachTriggerState state = entry.Value;
                if (state.Triggered
                    || state.LastObservedFrame != nowFrame
                    || string.IsNullOrWhiteSpace(state.LineId)
                    || string.IsNullOrWhiteSpace(state.StationId)
                    || !string.Equals(state.LineId, lineId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!candidatesByStation.TryGetValue(state.StationId, out Dictionary<int, Entity> nearestByPhase))
                {
                    nearestByPhase = new Dictionary<int, Entity>();
                    candidatesByStation[state.StationId] = nearestByPhase;
                }

                if (!nearestByPhase.TryGetValue(state.TraversalPhaseIndex, out Entity currentVehicle)
                    || !m_BroadcastPlatformApproachTriggerStateByVehicle.TryGetValue(currentVehicle, out BroadcastPlatformApproachTriggerState currentState)
                    || state.CursorAtomIndex > currentState.CursorAtomIndex)
                {
                    nearestByPhase[state.TraversalPhaseIndex] = entry.Key;
                }
            }

            return candidatesByStation;
        }

        private bool TryStartBroadcastPlatformSequence(
            string sequenceKey,
            Entity audioPositionEntity,
            BroadcastTriggerContext context,
            BroadcastWorkbenchPlatformAnnouncementDto announcement)
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
                ? BroadcastPlatformIdleTriggerId
                : announcement.triggerId;
            string triggerLabel = ResolveBroadcastPlatformTriggerLabel(triggerId);

            BroadcastWorkbenchRuleDto rule = new BroadcastWorkbenchRuleDto
            {
                id = sequenceKey,
                title = string.IsNullOrWhiteSpace(announcement.title) ? (announcement.stationName ?? string.Empty) : announcement.title,
                triggerId = triggerId,
                trigger = triggerLabel,
                nodes = announcement.nodes
                    .Select(CloneBroadcastWorkbenchRuleNode)
                    .Where(node => node != null)
                    .ToArray()
            };
            if (rule.nodes.Length == 0)
            {
                return false;
            }

            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            StopBroadcastRuntimePlatformSequence(sequenceKey);
            BroadcastRuntimeSequenceState state = new BroadcastRuntimeSequenceState
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
            m_BroadcastPlatformSequenceStateByKey[sequenceKey] = state;
            return AdvanceBroadcastRuntimeSequence(state, nowFrame);
        }

        private bool HasApproachAnnouncements(Entity line)
        {
            string lineId = GetDraftKey(GetWorkbenchLineId(line));
            if (string.IsNullOrWhiteSpace(lineId)
                || !m_BroadcastLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements)
                || lineAnnouncements == null)
            {
                return false;
            }

            foreach (BroadcastWorkbenchPlatformAnnouncementDto announcement in lineAnnouncements.Values)
            {
                if (announcement != null
                    && announcement.enabled
                    && announcement.nodes != null
                    && announcement.nodes.Length > 0
                    && string.Equals(announcement.triggerId, BroadcastPlatformApproachTriggerId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetEnabledBroadcastPlatformApproachAnnouncement(
            Entity line,
            string lineId,
            string stationId,
            out BroadcastWorkbenchPlatformAnnouncementDto announcement)
        {
            announcement = null;
            EnsureBroadcastRuntimeLineState(lineId, line);
            if (string.IsNullOrWhiteSpace(lineId)
                || string.IsNullOrWhiteSpace(stationId)
                || !m_BroadcastLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements)
                || lineAnnouncements == null)
            {
                return false;
            }

            foreach (BroadcastWorkbenchPlatformAnnouncementDto candidate in lineAnnouncements.Values)
            {
                if (candidate != null
                    && string.Equals(candidate.stationId, stationId, StringComparison.Ordinal)
                    && candidate.enabled
                    && candidate.nodes != null
                    && candidate.nodes.Length > 0
                    && string.Equals(candidate.triggerId, BroadcastPlatformApproachTriggerId, StringComparison.Ordinal))
                {
                    announcement = candidate;
                    return true;
                }
            }

            return false;
        }

        private bool HasPlatformAnnouncements(Entity line)
        {
            if (!IsBroadcastFeatureEnabled())
                return false;

            string lineId = GetDraftKey(GetWorkbenchLineId(line));
            EnsureBroadcastRuntimeLineState(lineId, line);
            if (string.IsNullOrWhiteSpace(lineId)
                || !m_BroadcastLinePlatformAnnouncements.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements)
                || lineAnnouncements == null)
            {
                return false;
            }

            foreach (BroadcastWorkbenchPlatformAnnouncementDto announcement in lineAnnouncements.Values)
            {
                if (announcement != null
                    && announcement.enabled
                    && announcement.nodes != null
                    && announcement.nodes.Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void WatchBusy(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool hasRuntimeContext,
            bool boarding,
            BroadcastVehicleRuntimeFrameContext runtimeContext)
        {
            if (line == Entity.Null
                || !hasRuntimeContext
                || runtimeContext.Chain == null
                || runtimeContext.Chain.TrackAtoms.Count == 0
                || !TryLineCache(line, waypoints, out BroadcastLineStationContextCache cache)
                || cache?.Stations == null
                || cache.Stations.Length == 0)
            {
                return;
            }

            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            BroadcastVehicleStationContext stationContext = runtimeContext.StationContext;
            string lineId = stationContext.LineId;
            int cursorAtomIndex = runtimeContext.Cursor.AtomCursorIndex;
            if (string.IsNullOrWhiteSpace(lineId))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(stationContext.CurrentStationId)
                && (boarding
                    || IsCursorNearBroadcastWaypointWindow(
                        runtimeContext.Chain,
                        stationContext.CurrentStopWaypointIndex,
                        cursorAtomIndex,
                        2,
                        2)))
            {
                MarkBroadcastPlatformStationBusy(line, waypoints, lineId, stationContext.CurrentStationId, nowFrame);
            }

            if (!string.IsNullOrWhiteSpace(stationContext.NextStationId)
                && (IsCursorNearBroadcastWaypointWindow(
                        runtimeContext.Chain,
                        stationContext.NextStopWaypointIndex,
                        cursorAtomIndex,
                        2,
                        2)
                    || IsCursorPastBroadcastPlatformApproachAnchor(runtimeContext, stationContext)))
            {
                MarkBroadcastPlatformStationBusy(line, waypoints, lineId, stationContext.NextStationId, nowFrame);
            }

            for (int i = 0; i < cache.Stations.Length; i++)
            {
                BroadcastResolvedStation station = cache.Stations[i];
                if (station == null
                    || string.IsNullOrWhiteSpace(station.StationId)
                    || !IsCursorNearBroadcastWaypointWindow(
                        runtimeContext.Chain,
                        station.WaypointIndex,
                        cursorAtomIndex,
                        1,
                        1))
                {
                    continue;
                }

                MarkBroadcastPlatformStationBusy(line, waypoints, lineId, station.StationId, nowFrame);
            }
        }

        private void WatchOriginBusy(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool shouldMarkOrigin)
        {
            if (!shouldMarkOrigin
                || line == Entity.Null
                || waypoints.Length == 0
                || !TryLineCache(line, waypoints, out BroadcastLineStationContextCache cache)
                || cache?.Stations == null
                || cache.Stations.Length == 0
                || cache.Stations[0] == null)
            {
                return;
            }

            string lineId = GetDraftKey(GetWorkbenchLineId(line));
            if (string.IsNullOrWhiteSpace(lineId))
            {
                return;
            }

            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            MarkBroadcastPlatformStationBusy(line, waypoints, lineId, cache.Stations[0].StationId, nowFrame);
        }

        private bool IsCursorPastBroadcastPlatformApproachAnchor(
            BroadcastVehicleRuntimeFrameContext runtimeContext,
            BroadcastVehicleStationContext stationContext)
        {
            if (!TryResolveBroadcastPlatformApproachAtom(
                    runtimeContext.Chain,
                    runtimeContext.Cursor.AtomCursorIndex,
                    stationContext.CurrentStopWaypointIndex,
                    stationContext.NextStopWaypointIndex,
                    out int triggerAtomIndex))
            {
                return false;
            }

            BroadcastPlatformApproachTriggerState state = new BroadcastPlatformApproachTriggerState
            {
                CurrentStopWaypointIndex = stationContext.CurrentStopWaypointIndex,
                NextStopWaypointIndex = stationContext.NextStopWaypointIndex,
                TriggerAtomIndex = triggerAtomIndex,
                CursorAtomIndex = runtimeContext.Cursor.AtomCursorIndex
            };
            return runtimeContext.Cursor.AtomCursorIndex >= triggerAtomIndex
                && !IsBroadcastPlatformApproachPastTriggerWindow(runtimeContext.Chain, state);
        }

        private bool IsCursorNearBroadcastWaypointWindow(
            LineTrackChain chain,
            int waypointIndex,
            int cursorAtomIndex,
            int beforeMarginAtoms,
            int afterMarginAtoms)
        {
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || waypointIndex < 0
                || !TryGetWaypointTraversalAtomWindow(
                    chain,
                    waypointIndex,
                    cursorAtomIndex,
                    out int windowStart,
                    out int windowEndExclusive))
            {
                return false;
            }

            int start = math.max(0, windowStart - math.max(0, beforeMarginAtoms));
            int end = math.min(chain.TrackAtoms.Count, windowEndExclusive + math.max(0, afterMarginAtoms));
            return cursorAtomIndex >= start && cursorAtomIndex < end;
        }

        private void MarkBroadcastPlatformStationBusy(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            string lineId,
            string stationId,
            uint nowFrame)
        {
            string normalizedStationId = NormalizeBroadcastPlatformRepresentativeStationId(line, waypoints, stationId);
            if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(normalizedStationId))
            {
                return;
            }

            string key = BuildBroadcastPlatformStationStateKey(lineId, normalizedStationId);
            uint busyUntilFrame = nowFrame + BroadcastPlatformIdleBusyGraceMinutes * (uint)SIM_FRAMES_PER_MINUTE;
            if (!m_BroadcastPlatformStationBusyUntilFrame.TryGetValue(key, out uint existingUntil)
                || existingUntil < busyUntilFrame)
            {
                m_BroadcastPlatformStationBusyUntilFrame[key] = busyUntilFrame;
            }

            m_BroadcastPlatformStationQuietSinceFrame.Remove(key);
        }

        private void WatchApproach(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool hasRuntimeContext,
            BroadcastVehicleRuntimeFrameContext runtimeContext)
        {
            if (vehicle == Entity.Null
                || !hasRuntimeContext)
            {
                m_BroadcastPlatformApproachTriggerStateByVehicle.Remove(vehicle);
                return;
            }

            BroadcastVehicleStationContext stationContext = runtimeContext.StationContext;
            string representativeNextStationId = NormalizeBroadcastPlatformRepresentativeStationId(
                line,
                waypoints,
                stationContext.NextStationId);
            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            if (string.IsNullOrWhiteSpace(stationContext.LineId)
                || string.IsNullOrWhiteSpace(representativeNextStationId)
                || string.Equals(stationContext.CurrentStationId, stationContext.NextStationId, StringComparison.Ordinal)
                || IsBroadcastPlatformApproachSuppressedForOriginReturn(vehicle, stationContext)
                || !TryGetEnabledBroadcastPlatformApproachAnnouncement(
                    line,
                    stationContext.LineId,
                    representativeNextStationId,
                    out _)
                || !TryResolveBroadcastPlatformApproachAtom(
                    runtimeContext.Chain,
                    runtimeContext.Cursor.AtomCursorIndex,
                    stationContext.CurrentStopWaypointIndex,
                    stationContext.NextStopWaypointIndex,
                    out int triggerAtomIndex))
            {
                m_BroadcastPlatformApproachTriggerStateByVehicle.Remove(vehicle);
                return;
            }

            bool resetState =
                !m_BroadcastPlatformApproachTriggerStateByVehicle.TryGetValue(vehicle, out BroadcastPlatformApproachTriggerState state)
                || !string.Equals(state.LineId, stationContext.LineId, StringComparison.Ordinal)
                || !string.Equals(state.StationId, representativeNextStationId, StringComparison.Ordinal)
                || state.CurrentStopWaypointIndex != stationContext.CurrentStopWaypointIndex
                || state.NextStopWaypointIndex != stationContext.NextStopWaypointIndex
                || state.TraversalPhaseIndex != runtimeContext.TraversalPhaseIndex;

            if (resetState)
            {
                state = new BroadcastPlatformApproachTriggerState
                {
                    LineId = stationContext.LineId,
                    StationId = representativeNextStationId,
                    CurrentStopWaypointIndex = stationContext.CurrentStopWaypointIndex,
                    NextStopWaypointIndex = stationContext.NextStopWaypointIndex,
                    TriggerAtomIndex = triggerAtomIndex,
                    CursorAtomIndex = runtimeContext.Cursor.AtomCursorIndex,
                    TraversalPhaseIndex = runtimeContext.TraversalPhaseIndex,
                    LastObservedFrame = nowFrame,
                    Triggered = false,
                    StationContext = stationContext
                };
                LogBroadcastPlatformApproachState("resolve", vehicle, runtimeContext.Chain, state, true);
            }
            else
            {
                state.TriggerAtomIndex = triggerAtomIndex;
                state.CursorAtomIndex = runtimeContext.Cursor.AtomCursorIndex;
                state.TraversalPhaseIndex = runtimeContext.TraversalPhaseIndex;
                state.LastObservedFrame = nowFrame;
                state.StationContext = stationContext;
            }

            m_BroadcastPlatformApproachTriggerStateByVehicle[vehicle] = state;
        }

        private void WatchPreparingApproach(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool atOrigin,
            float preparingArrivalFrames)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || atOrigin
                || preparingArrivalFrames == float.MaxValue
                || preparingArrivalFrames > BroadcastPlatformPreparingApproachLeadMinutes * (float)SIM_FRAMES_PER_MINUTE
                || !TryLineCache(line, waypoints, out BroadcastLineStationContextCache cache)
                || cache?.Stations == null
                || cache.Stations.Length == 0)
            {
                m_BroadcastPlatformApproachTriggerStateByVehicle.Remove(vehicle);
                return;
            }

            BroadcastResolvedStation originStation = cache.Stations[0];
            if (originStation == null)
            {
                m_BroadcastPlatformApproachTriggerStateByVehicle.Remove(vehicle);
                return;
            }

            string lineId = GetDraftKey(GetWorkbenchLineId(line));
            string representativeOriginStationId = NormalizeBroadcastPlatformRepresentativeStationId(
                line,
                waypoints,
                originStation.StationId);
            if (originStation.StopEntity == Entity.Null
                || string.IsNullOrWhiteSpace(lineId)
                || string.IsNullOrWhiteSpace(representativeOriginStationId)
                || !TryGetEnabledBroadcastPlatformApproachAnnouncement(
                    line,
                    lineId,
                    representativeOriginStationId,
                    out _))
            {
                m_BroadcastPlatformApproachTriggerStateByVehicle.Remove(vehicle);
                return;
            }

            BroadcastResolvedStation turnbackStation =
                ResolveBroadcastTurnbackStationAfterWaypoint(cache, originStation);
            BroadcastVehicleStationContext stationContext = new BroadcastVehicleStationContext(
                lineId,
                originStation.StopEntity,
                originStation.WaypointIndex,
                string.Empty,
                string.Empty,
                originStation.WaypointIndex,
                originStation.StationId,
                originStation.Name,
                originStation.StationId,
                originStation.Name,
                turnbackStation?.StationId ?? string.Empty,
                turnbackStation?.Name ?? string.Empty);
            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            bool resetState =
                !m_BroadcastPlatformApproachTriggerStateByVehicle.TryGetValue(vehicle, out BroadcastPlatformApproachTriggerState state)
                || !string.Equals(state.LineId, lineId, StringComparison.Ordinal)
                || !string.Equals(state.StationId, representativeOriginStationId, StringComparison.Ordinal)
                || state.CurrentStopWaypointIndex != originStation.WaypointIndex
                || state.NextStopWaypointIndex != originStation.WaypointIndex
                || state.TraversalPhaseIndex != BroadcastPlatformPreparingApproachPhaseIndex;

            if (resetState)
            {
                state = new BroadcastPlatformApproachTriggerState
                {
                    LineId = lineId,
                    StationId = representativeOriginStationId,
                    CurrentStopWaypointIndex = originStation.WaypointIndex,
                    NextStopWaypointIndex = originStation.WaypointIndex,
                    TriggerAtomIndex = 1,
                    CursorAtomIndex = 1,
                    TraversalPhaseIndex = BroadcastPlatformPreparingApproachPhaseIndex,
                    LastObservedFrame = nowFrame,
                    Triggered = false,
                    StationContext = stationContext
                };
            }
            else
            {
                state.CursorAtomIndex = 1;
                state.LastObservedFrame = nowFrame;
                state.StationContext = stationContext;
            }

            m_BroadcastPlatformApproachTriggerStateByVehicle[vehicle] = state;
        }

        private bool IsBroadcastPlatformApproachSuppressedForOriginReturn(
            Entity vehicle,
            BroadcastVehicleStationContext stationContext)
        {
            return vehicle != Entity.Null
                && stationContext.NextStopWaypointIndex == 0
                && m_VehicleView.IsInbound(vehicle);
        }

        private static string BuildBroadcastPlatformIdleSequenceKey(string lineId, string stationId)
        {
            return "platform_idle|" + (lineId ?? string.Empty) + "|" + (stationId ?? string.Empty);
        }

        private static string BuildBroadcastPlatformStationStateKey(string lineId, string stationId)
        {
            return (lineId ?? string.Empty) + "|" + (stationId ?? string.Empty);
        }

        private static string BuildBroadcastPlatformApproachSequenceKey(
            string lineId,
            string stationId,
            Entity vehicle,
            int currentStopWaypointIndex,
            int nextStopWaypointIndex)
        {
            return "platform_approach|" + (lineId ?? string.Empty)
                + "|" + (stationId ?? string.Empty)
                + "|" + vehicle.Index
                + "|" + currentStopWaypointIndex
                + "|" + nextStopWaypointIndex;
        }

        private static string ResolveBroadcastPlatformTriggerLabel(string triggerId)
        {
            return string.Equals(triggerId, BroadcastPlatformApproachTriggerId, StringComparison.Ordinal)
                ? "即将进站"
                : "空闲时";
        }

        private bool IsBroadcastPlatformStationIdle(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            string stationId)
        {
            if (line == Entity.Null || string.IsNullOrWhiteSpace(stationId))
            {
                return false;
            }

            string normalizedStationId = NormalizeBroadcastPlatformRepresentativeStationId(
                line,
                waypoints,
                stationId);
            string lineId = GetDraftKey(GetWorkbenchLineId(line));
            if (string.IsNullOrWhiteSpace(normalizedStationId) || string.IsNullOrWhiteSpace(lineId))
            {
                return false;
            }

            uint nowFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0u;
            string key = BuildBroadcastPlatformStationStateKey(lineId, normalizedStationId);
            if (m_BroadcastPlatformStationBusyUntilFrame.TryGetValue(key, out uint busyUntilFrame)
                && nowFrame < busyUntilFrame)
            {
                m_BroadcastPlatformStationQuietSinceFrame.Remove(key);
                return false;
            }

            if (!m_BroadcastPlatformStationQuietSinceFrame.TryGetValue(key, out uint quietSinceFrame))
            {
                m_BroadcastPlatformStationQuietSinceFrame[key] = nowFrame;
                return false;
            }

            uint quietConfirmFrames = BroadcastPlatformIdleQuietConfirmMinutes * (uint)SIM_FRAMES_PER_MINUTE;
            if (nowFrame < quietSinceFrame + quietConfirmFrames)
            {
                return false;
            }

            return true;
        }

        internal void Tick(uint nowFrame)
        {
            if (!IsBroadcastFeatureEnabled())
            {
                if (m_BroadcastSequenceStateByVehicle.Count > 0
                    || m_BroadcastPlatformSequenceStateByKey.Count > 0
                    || m_BroadcastPlatformAnnouncementCooldownUntilFrame.Count > 0
                    || m_BroadcastPlatformStationBusyUntilFrame.Count > 0
                    || m_BroadcastPlatformStationQuietSinceFrame.Count > 0)
                {
                    Clear();
                }
                return;
            }

            TickBroadcastPlatformAnnouncements(nowFrame);

            if (m_BroadcastSequenceStateByVehicle.Count == 0
                && m_BroadcastPlatformSequenceStateByKey.Count == 0)
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

            List<string> completedPlatformKeys = null;
            foreach (KeyValuePair<string, BroadcastRuntimeSequenceState> entry in m_BroadcastPlatformSequenceStateByKey)
            {
                BroadcastRuntimeSequenceState state = entry.Value;
                if (state == null || state.Vehicle == Entity.Null || !EntityManager.Exists(state.Vehicle))
                {
                    completedPlatformKeys ??= new List<string>();
                    completedPlatformKeys.Add(entry.Key);
                    continue;
                }

                UpdateBroadcastRuntimeAudioSourcePosition(state);
                if (AdvanceBroadcastRuntimeSequence(state, nowFrame))
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
                    StopBroadcastRuntimeSequence(completedVehicle);
                    m_BroadcastLastEventTextByVehicle.Remove(completedVehicle);
                }

                m_SelectionPanel.Invalidate();
            }

            if (completedPlatformKeys != null)
            {
                for (int i = 0; i < completedPlatformKeys.Count; i++)
                {
                    StopBroadcastRuntimePlatformSequence(completedPlatformKeys[i]);
                }
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

            if (state.ResumeRealtime > 0f)
            {
                if (UnityEngine.Time.realtimeSinceStartup < state.ResumeRealtime)
                {
                    return true;
                }

                state.ResumeRealtime = 0f;
            }
            else if (nowFrame < state.ResumeFrame)
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
                    float delaySeconds = node.delaySeconds > 0f ? node.delaySeconds : 0f;
                    if (delaySeconds > 0f)
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

        private void StopBroadcastRuntimePlatformSequence(string sequenceKey)
        {
            if (string.IsNullOrWhiteSpace(sequenceKey)
                || !m_BroadcastPlatformSequenceStateByKey.TryGetValue(sequenceKey, out BroadcastRuntimeSequenceState state))
            {
                return;
            }

            ReleaseBroadcastRuntimeAudioSource(state);
            state.PendingClipLoadTask = null;
            state.PendingAssetName = string.Empty;
            m_BroadcastPlatformSequenceStateByKey.Remove(sequenceKey);
        }

        internal void RemoveVehicle(Entity vehicle)
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
            m_BroadcastPlatformApproachTriggerStateByVehicle.Remove(vehicle);
            m_BroadcastCurrentStationNameByVehicle.Remove(vehicle);
            m_BroadcastNextStationNameByVehicle.Remove(vehicle);
            m_BroadcastCurrentStopWaypointIndexByVehicle.Remove(vehicle);
            m_BroadcastNextStopWaypointIndexByVehicle.Remove(vehicle);
            m_BroadcastLastEventTextByVehicle.Remove(vehicle);
            m_BroadcastAnchorDiagnosticKeyCache.Remove(vehicle);
            m_BroadcastAnchorDiagnosticLastLogFrameCache.Remove(vehicle);
            m_BroadcastAnchorTriggerLogCache.Remove(vehicle);
            m_BroadcastPlatformApproachDiagnosticKeyCache.Remove(vehicle);
            m_BroadcastPlatformApproachDiagnosticLastLogFrameCache.Remove(vehicle);
            m_BroadcastPlatformApproachTriggerLogCache.Remove(vehicle);
            m_SelectionPanel.Invalidate();
        }

        internal void Clear()
        {
            foreach (KeyValuePair<Entity, BroadcastRuntimeSequenceState> entry in m_BroadcastSequenceStateByVehicle)
            {
                ReleaseBroadcastRuntimeAudioSource(entry.Value);
            }

            foreach (KeyValuePair<string, BroadcastRuntimeSequenceState> entry in m_BroadcastPlatformSequenceStateByKey)
            {
                ReleaseBroadcastRuntimeAudioSource(entry.Value);
            }

            m_BroadcastSequenceStateByVehicle.Clear();
            m_BroadcastPlatformSequenceStateByKey.Clear();
            m_BroadcastLastStopAndOpenStopByVehicle.Clear();
            m_BroadcastLastLeaveStationStopByVehicle.Clear();
            m_BroadcastLastBypassWaitingStopByVehicle.Clear();
            m_BroadcastProgressTriggerStateByVehicle.Clear();
            m_BroadcastPlatformApproachTriggerStateByVehicle.Clear();
            m_BroadcastCurrentStationNameByVehicle.Clear();
            m_BroadcastNextStationNameByVehicle.Clear();
            m_BroadcastCurrentStopWaypointIndexByVehicle.Clear();
            m_BroadcastNextStopWaypointIndexByVehicle.Clear();
            m_BroadcastLineStationContextCaches.Clear();
            m_BroadcastRuntimeCheckedLineIds.Clear();
            m_BroadcastPlatformAnnouncementCooldownUntilFrame.Clear();
            m_BroadcastPlatformStationBusyUntilFrame.Clear();
            m_BroadcastPlatformStationQuietSinceFrame.Clear();
            m_BroadcastLastEventTextByVehicle.Clear();
            m_BroadcastAnchorDiagnosticKeyCache.Clear();
            m_BroadcastAnchorDiagnosticLastLogFrameCache.Clear();
            m_BroadcastAnchorTriggerLogCache.Clear();
            m_BroadcastPlatformApproachDiagnosticKeyCache.Clear();
            m_BroadcastPlatformApproachDiagnosticLastLogFrameCache.Clear();
            m_BroadcastPlatformApproachTriggerLogCache.Clear();
            m_BroadcastRuntimeClipLoadTasks.Clear();

            foreach (BroadcastRuntimeClipCacheEntry entry in m_BroadcastRuntimeClipCache.Values)
            {
                DestroyBroadcastRuntimeClip(entry?.Clip);
            }

            m_BroadcastRuntimeClipCache.Clear();
            m_SelectionPanel.Invalidate();
        }

        internal void ClearLineChecks()
        {
            m_BroadcastRuntimeCheckedLineIds.Clear();
        }

        internal void RemoveAsset(string assetName)
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

            List<string> affectedPlatformKeys = null;
            foreach (KeyValuePair<string, BroadcastRuntimeSequenceState> entry in m_BroadcastPlatformSequenceStateByKey)
            {
                BroadcastRuntimeSequenceState state = entry.Value;
                if (state == null)
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
                    StopBroadcastRuntimeSequence(affectedVehicles[i]);
                }
            }

            if (affectedPlatformKeys != null)
            {
                for (int i = 0; i < affectedPlatformKeys.Count; i++)
                {
                    StopBroadcastRuntimePlatformSequence(affectedPlatformKeys[i]);
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

        internal void RemoveAllAssets()
        {
            foreach (KeyValuePair<Entity, BroadcastRuntimeSequenceState> entry in m_BroadcastSequenceStateByVehicle)
            {
                ReleaseBroadcastRuntimeAudioSource(entry.Value);
            }

            foreach (KeyValuePair<string, BroadcastRuntimeSequenceState> entry in m_BroadcastPlatformSequenceStateByKey)
            {
                ReleaseBroadcastRuntimeAudioSource(entry.Value);
            }

            m_BroadcastSequenceStateByVehicle.Clear();
            m_BroadcastPlatformSequenceStateByKey.Clear();
            m_BroadcastPlatformAnnouncementCooldownUntilFrame.Clear();
            m_BroadcastPlatformStationBusyUntilFrame.Clear();
            m_BroadcastPlatformStationQuietSinceFrame.Clear();
            m_BroadcastPlatformApproachTriggerStateByVehicle.Clear();
            m_BroadcastPlatformApproachDiagnosticKeyCache.Clear();
            m_BroadcastPlatformApproachDiagnosticLastLogFrameCache.Clear();
            m_BroadcastPlatformApproachTriggerLogCache.Clear();
            m_BroadcastRuntimeClipLoadTasks.Clear();
            foreach (BroadcastRuntimeClipCacheEntry entry in m_BroadcastRuntimeClipCache.Values)
            {
                DestroyBroadcastRuntimeClip(entry?.Clip);
            }

            m_BroadcastRuntimeClipCache.Clear();
        }

        private bool TryStartBroadcastWorldAudio(BroadcastRuntimeSequenceState state, string assetName, AudioClip clip)
        {
            Entity audioPositionEntity = state?.AudioPositionEntity ?? Entity.Null;
            if (state == null
                || clip == null
                || audioPositionEntity == Entity.Null
                || !TryGetBroadcastWorldAudioPosition(audioPositionEntity, out Vector3 position))
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
            Entity audioPositionEntity = state?.AudioPositionEntity ?? Entity.Null;
            if (state?.ActiveAudioSource == null || audioPositionEntity == Entity.Null)
            {
                return;
            }

            if (!TryGetBroadcastWorldAudioPosition(audioPositionEntity, out Vector3 position))
            {
                return;
            }

            state.ActiveAudioSource.transform.position = position;
        }

        internal void ApplyVolume()
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

            foreach (KeyValuePair<string, BroadcastRuntimeSequenceState> entry in m_BroadcastPlatformSequenceStateByKey)
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
            return await OnMainThreadAsync(async () =>
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

        internal async Task<T> OnMainThreadAsync<T>(Func<Task<T>> action)
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

            foreach (BroadcastRuntimeSequenceState state in m_BroadcastPlatformSequenceStateByKey.Values)
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

            foreach (BroadcastRuntimeSequenceState state in m_BroadcastPlatformSequenceStateByKey.Values)
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

        internal string AssetName(BroadcastWorkbenchRuleNodeDto node, BroadcastTriggerContext context)
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
                    return ResolveBoundAssetName(context.CurrentStationBindings, langIndex);
                case "broadcast.variable.next":
                    return ResolveBoundAssetName(context.NextStationBindings, langIndex);
                case "broadcast.variable.terminal":
                    return ResolveBoundAssetName(context.TerminalStationBindings, langIndex);
                case "broadcast.variable.turnback":
                    return ResolveBoundAssetName(context.TurnbackStationBindings, langIndex);
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
            return TryBuildBroadcastTriggerContext(
                vehicle,
                line,
                waypoints,
                currentStopWaypointIndex,
                out context,
                out _);
        }

        private bool TryBuildBroadcastTriggerContext(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentStopWaypointIndex,
            out BroadcastTriggerContext context,
            out BroadcastVehicleStationContext stationContext)
        {
            context = default;
            stationContext = default;
            if (!IsBroadcastFeatureEnabled())
            {
                return false;
            }
            if (!TryResolveBroadcastVehicleStationContext(
                    vehicle,
                    line,
                    waypoints,
                    currentStopWaypointIndex,
                    out stationContext))
            {
                return false;
            }

            EnsureBroadcastRuntimeLineState(stationContext.LineId, line);
            return TryBuildBroadcastTriggerContext(stationContext, out context);
        }

        private bool TryBuildBroadcastTriggerContext(
            BroadcastVehicleStationContext stationContext,
            out BroadcastTriggerContext context)
        {
            return TryBuildBroadcastTriggerContext(
                stationContext,
                string.Empty,
                null,
                out context);
        }

        private bool TryBuildBroadcastTriggerContext(
            BroadcastVehicleStationContext stationContext,
            string overrideTurnbackStationName,
            List<BroadcastWorkbenchStationBindingDto> overrideTurnbackStationBindings,
            out BroadcastTriggerContext context)
        {
            context = default;
            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                GetBroadcastAppliedLineStationBindings(stationContext.LineId);
            List<BroadcastWorkbenchStationBindingDto> currentStationBindings =
                ResolveBoundBindings(lineBindings, stationContext.CurrentStationId);
            List<BroadcastWorkbenchStationBindingDto> nextStationBindings =
                ResolveBoundBindings(lineBindings, stationContext.NextStationId);
            List<BroadcastWorkbenchStationBindingDto> terminalStationBindings =
                ResolveBoundBindings(lineBindings, stationContext.TerminalStationId);
            List<BroadcastWorkbenchStationBindingDto> turnbackStationBindings =
                overrideTurnbackStationBindings ?? ResolveBoundBindings(lineBindings, stationContext.TurnbackStationId);
            string turnbackStationName = !string.IsNullOrEmpty(overrideTurnbackStationName)
                ? overrideTurnbackStationName
                : stationContext.TurnbackStationName;

            context = new BroadcastTriggerContext(
                stationContext.LineId,
                stationContext.CurrentStopEntity,
                stationContext.CurrentStationName,
                stationContext.NextStationName,
                stationContext.TerminalStationName,
                turnbackStationName,
                ResolveBoundAssetName(currentStationBindings, 1),
                ResolveBoundAssetName(nextStationBindings, 1),
                ResolveBoundAssetName(terminalStationBindings, 1),
                ResolveBoundAssetName(turnbackStationBindings, 1),
                currentStationBindings,
                nextStationBindings,
                terminalStationBindings,
                turnbackStationBindings);
            return true;
        }

        private bool TryResolveBroadcastStationById(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            string stationId,
            out BroadcastResolvedStation station)
        {
            station = null;
            if (line == Entity.Null
                || string.IsNullOrWhiteSpace(stationId)
                || !TryLineCache(line, waypoints, out BroadcastLineStationContextCache cache))
            {
                return false;
            }

            if (cache.StationById != null
                && cache.StationById.TryGetValue(stationId, out BroadcastResolvedStation cachedStation))
            {
                station = cachedStation;
            }
            else
            {
                station = cache.Stations
                    .FirstOrDefault(station => station != null && string.Equals(station.StationId, stationId, StringComparison.Ordinal));
            }
            return station != null;
        }

        private bool TryBuildBroadcastStationOnlyContext(
            string lineId,
            BroadcastResolvedStation station,
            out BroadcastTriggerContext context)
        {
            context = default;
            if (string.IsNullOrWhiteSpace(lineId)
                || station == null
                || station.StopEntity == Entity.Null)
            {
                return false;
            }

            Dictionary<string, List<BroadcastWorkbenchStationBindingDto>> lineBindings =
                GetBroadcastAppliedLineStationBindings(lineId);
            List<BroadcastWorkbenchStationBindingDto> currentStationBindings =
                ResolveBoundBindings(lineBindings, station.StationId);

            context = new BroadcastTriggerContext(
                lineId,
                station.StopEntity,
                station.Name,
                string.Empty,
                string.Empty,
                string.Empty,
                ResolveBoundAssetName(currentStationBindings, 1),
                string.Empty,
                string.Empty,
                string.Empty,
                currentStationBindings,
                null,
                null,
                null);
            return true;
        }

        private static BroadcastResolvedStation ResolveBroadcastTurnbackStationAfterWaypoint(
            BroadcastLineStationContextCache cache,
            BroadcastResolvedStation currentStation)
        {
            if (cache?.TurnbackStations == null || cache.TurnbackStations.Length == 0)
            {
                return null;
            }

            int waypointIndex = currentStation?.WaypointIndex ?? -1;
            BroadcastResolvedStation terminalStation =
                cache.Stations != null && cache.Stations.Length > 0 ? cache.Stations[0] : null;
            BroadcastResolvedStation firstStation = null;
            BroadcastResolvedStation nextStation = null;
            List<BroadcastResolvedStation> candidates = new List<BroadcastResolvedStation>();
            for (int i = 0; i < cache.TurnbackStations.Length; i++)
            {
                BroadcastResolvedStation station = cache.TurnbackStations[i];
                if (station != null)
                {
                    candidates.Add(station);
                }
            }

            if (terminalStation != null
                && !candidates.Any(candidate => IsSameBroadcastStation(candidate, terminalStation)))
            {
                candidates.Add(terminalStation);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                BroadcastResolvedStation station = candidates[i];
                if (firstStation == null || station.WaypointIndex < firstStation.WaypointIndex)
                {
                    firstStation = station;
                }

                if (station.WaypointIndex > waypointIndex
                    && !IsSameBroadcastStation(station, currentStation)
                    && (nextStation == null || station.WaypointIndex < nextStation.WaypointIndex))
                {
                    nextStation = station;
                }
            }

            if (nextStation != null)
            {
                return nextStation;
            }

            if (firstStation != null && !IsSameBroadcastStation(firstStation, currentStation))
            {
                return firstStation;
            }

            for (int i = 0; i < cache.TurnbackStations.Length; i++)
            {
                BroadcastResolvedStation station = cache.TurnbackStations[i];
                if (!IsSameBroadcastStation(station, currentStation))
                {
                    return station;
                }
            }

            return firstStation;
        }

        private List<BroadcastResolvedStation> BuildBroadcastResolvedStations(DynamicBuffer<RouteWaypoint> waypoints)
        {
            List<BroadcastResolvedStation> stations = new List<BroadcastResolvedStation>();
            HashSet<Entity> seenStations = new HashSet<Entity>();
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoint);
                Entity anchorEntity = ResolveStationAnchor(waypoint);
                Entity identityEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity;
                if (anchorEntity == Entity.Null || identityEntity == Entity.Null || !seenStations.Add(identityEntity))
                {
                    continue;
                }

                int order = stations.Count;
                string stationId = EnsureStationAnchorKey(anchorEntity);
                stations.Add(new BroadcastResolvedStation
                {
                    StopEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity,
                    AnchorEntity = anchorEntity,
                    WaypointIndex = i,
                    Order = order,
                    StationId = stationId,
                    LegacyStationId = CreateWorkbenchStationId(order),
                    Name = stopEntity != Entity.Null
                        ? ResolveWorkbenchStationName(stopEntity)
                        : ResolveWorkbenchEntityName(anchorEntity)
                });
            }

            return stations;
        }

        internal bool TryLineCache(
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
            Dictionary<Entity, int> stationOrderByIdentity = new Dictionary<Entity, int>();
            for (int waypointIndex = 0; waypointIndex < waypointCount; waypointIndex++)
            {
                Entity waypoint = waypoints[waypointIndex].m_Waypoint;
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoint);
                Entity anchorEntity = ResolveStationAnchor(waypoint);
                Entity identityEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity;
                if (anchorEntity == Entity.Null || identityEntity == Entity.Null)
                {
                    continue;
                }

                if (!stationOrderByIdentity.TryGetValue(identityEntity, out int order))
                {
                    order = stations.Count;
                    stationOrderByIdentity[identityEntity] = order;
                    string stationId = EnsureStationAnchorKey(anchorEntity);
                    stations.Add(new BroadcastResolvedStation
                    {
                        StopEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity,
                        AnchorEntity = anchorEntity,
                        WaypointIndex = waypointIndex,
                        Order = order,
                        StationId = stationId,
                        LegacyStationId = CreateWorkbenchStationId(order),
                        Name = stopEntity != Entity.Null
                            ? ResolveWorkbenchStationName(stopEntity)
                            : ResolveWorkbenchEntityName(anchorEntity)
                    });
                }

                stationOrderByWaypoint[waypointIndex] = order;
                normalizedStationWaypointByWaypoint[waypointIndex] = stations[order].WaypointIndex;
                stationByWaypoint[waypointIndex] = new BroadcastResolvedStation
                {
                    StopEntity = stopEntity != Entity.Null ? stopEntity : anchorEntity,
                    AnchorEntity = anchorEntity,
                    WaypointIndex = waypointIndex,
                    Order = order,
                    StationId = stations[order].StationId,
                    LegacyStationId = stations[order].LegacyStationId,
                    Name = stations[order].Name
                };
            }

            BroadcastResolvedStation[] stationArray = stations.ToArray();
            Dictionary<string, BroadcastResolvedStation> stationById =
                new Dictionary<string, BroadcastResolvedStation>(StringComparer.Ordinal);
            for (int i = 0; i < stationArray.Length; i++)
            {
                BroadcastResolvedStation station = stationArray[i];
                if (station != null
                    && !string.IsNullOrWhiteSpace(station.StationId)
                    && !stationById.ContainsKey(station.StationId))
                {
                    stationById[station.StationId] = station;
                }
            }
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
                StationById = stationById,
                TerminalStationWaypointIndex = stationArray.Length > 0
                    ? stationArray[0].WaypointIndex
                    : -1
            };
            cache.TurnbackStations = ResolveBroadcastTurnbackStations(line, waypoints, cache);
            m_BroadcastLineStationContextCaches[line] = cache;
            return stationArray.Length > 0;
        }

        private string NormalizeBroadcastPlatformRepresentativeStationId(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            string stationId)
        {
            return stationId ?? string.Empty;
        }

        private BroadcastResolvedStation[] ResolveBroadcastTurnbackStations(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            BroadcastLineStationContextCache cache)
        {
            if (line == Entity.Null
                || waypoints.Length == 0
                || cache == null
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null)
            {
                return Array.Empty<BroadcastResolvedStation>();
            }

            List<TrackTurnbackStationBoundary> stationBoundaries = new List<TrackTurnbackStationBoundary>();
            if (!TryCollectTurnbackStationBoundaries(chain, stationBoundaries))
            {
                return Array.Empty<BroadcastResolvedStation>();
            }

            List<BroadcastResolvedStation> stations = new List<BroadcastResolvedStation>();
            for (int i = 0; i < stationBoundaries.Count; i++)
            {
                TryResolveBroadcastTurnbackStationFromBoundary(cache, stationBoundaries[i], out BroadcastResolvedStation station);
                stations.Add(station);
            }

            return stations.ToArray();
        }

        private static bool IsSameBroadcastStation(BroadcastResolvedStation left, BroadcastResolvedStation right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return left.Order == right.Order
                || left.StopEntity == right.StopEntity
                || string.Equals(left.StationId, right.StationId, StringComparison.Ordinal);
        }

        private bool TryResolveBroadcastTurnbackStationFromBoundary(
            BroadcastLineStationContextCache cache,
            TrackTurnbackStationBoundary stationBoundary,
            out BroadcastResolvedStation station)
        {
            station = null;
            if (cache == null)
            {
                return false;
            }

            if (TryGetBroadcastStationByWaypointIndex(cache, stationBoundary.WaypointIndex, out station))
            {
                return true;
            }

            if (stationBoundary.StationEntity == Entity.Null || cache.Stations == null)
            {
                station = null;
                return false;
            }

            Entity anchorEntity = ResolveStationAnchorFromStop(stationBoundary.StationEntity);
            string stationId = GetStationAnchorKey(anchorEntity);
            if (!string.IsNullOrWhiteSpace(stationId)
                && cache.StationById != null
                && cache.StationById.TryGetValue(stationId, out station))
            {
                return true;
            }

            for (int i = 0; i < cache.Stations.Length; i++)
            {
                BroadcastResolvedStation candidate = cache.Stations[i];
                if (candidate != null
                    && candidate.AnchorEntity == anchorEntity)
                {
                    station = candidate;
                    return true;
                }
            }

            station = null;
            return false;
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
            if (!TryLineCache(line, waypoints, out BroadcastLineStationContextCache cache))
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

            BroadcastResolvedStation terminalStation = cache.Stations[0];
            BroadcastResolvedStation turnbackStation = TryResolveBroadcastTurnbackStation(
                vehicle,
                line,
                waypoints,
                cache,
                out BroadcastResolvedStation resolvedTurnbackStation)
                ? resolvedTurnbackStation
                : null;
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
                terminalStation?.Name ?? string.Empty,
                turnbackStation?.StationId ?? string.Empty,
                turnbackStation?.Name ?? string.Empty);

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

        private bool TryResolveBroadcastTurnbackStation(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            BroadcastLineStationContextCache cache,
            out BroadcastResolvedStation station)
        {
            station = null;
            if (cache == null
                || line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain == null)
            {
                return false;
            }

            int atomCursorIndex = -1;
            if (vehicle != Entity.Null
                && TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                atomCursorIndex = cursor.AtomCursorIndex;
            }

            BroadcastResolvedStation terminalStation =
                cache.Stations != null && cache.Stations.Length > 0 ? cache.Stations[0] : null;
            if (!TryResolveBroadcastTurnbackStationBoundaryWithWrap(
                    chain,
                    atomCursorIndex,
                    out TrackTurnbackStationBoundary stationBoundary))
            {
                return false;
            }

            bool resolved = TryResolveBroadcastTurnbackStationFromBoundary(cache, stationBoundary, out station);
            bool wrappedToStart = atomCursorIndex >= 0 && stationBoundary.AtomIndex <= atomCursorIndex;
            if (wrappedToStart
                && resolved
                && terminalStation != null
                && !IsSameBroadcastStation(station, terminalStation))
            {
                station = terminalStation;
                return true;
            }

            return resolved;
        }

        private bool TryResolveBroadcastTurnbackStationBoundaryWithWrap(
            LineTrackChain chain,
            int atomCursorIndex,
            out TrackTurnbackStationBoundary stationBoundary)
        {
            stationBoundary = default;
            if (chain == null
                || chain.TurnbackBoundaries == null
                || chain.TurnbackBoundaries.Count == 0)
            {
                return false;
            }

            int cursorAtomIndex = atomCursorIndex >= 0 ? atomCursorIndex : -1;
            bool hasFirstResolvedBoundary = false;
            TrackTurnbackStationBoundary firstResolvedBoundary = default;
            for (int boundaryIndex = 0; boundaryIndex < chain.TurnbackBoundaries.Count; boundaryIndex++)
            {
                TurnbackBoundary boundary = chain.TurnbackBoundaries[boundaryIndex];
                if (!TryResolveTurnbackStationBoundary(chain, boundary, out TrackTurnbackStationBoundary candidate))
                {
                    continue;
                }

                if (!hasFirstResolvedBoundary)
                {
                    firstResolvedBoundary = candidate;
                    hasFirstResolvedBoundary = true;
                }

                if (cursorAtomIndex < 0 || boundary.AtomIndex > cursorAtomIndex)
                {
                    stationBoundary = candidate;
                    return true;
                }
            }

            if (hasFirstResolvedBoundary)
            {
                stationBoundary = firstResolvedBoundary;
                return true;
            }

            return false;
        }

        internal static string ResolveBoundAssetName(
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

        internal static List<BroadcastWorkbenchStationBindingDto> ResolveBoundBindings(
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

        internal static string ResolveBoundAssetName(
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
                    triggerLabel = SelectionPanel.IsChineseLocale() ? "停站上客" : "stop_and_open";
                    break;
                case "leave_station":
                    triggerLabel = SelectionPanel.IsChineseLocale() ? "列车离站" : "leave_station";
                    break;
                case "approach_station":
                    triggerLabel = SelectionPanel.IsChineseLocale() ? "即将进站" : "approach_station";
                    break;
                case BroadcastPlatformApproachTriggerId:
                    triggerLabel = SelectionPanel.IsChineseLocale() ? "站台即将进站" : BroadcastPlatformApproachTriggerId;
                    break;
                case BroadcastPlatformIdleTriggerId:
                    triggerLabel = SelectionPanel.IsChineseLocale() ? "站台空闲时" : BroadcastPlatformIdleTriggerId;
                    break;
                case "mid_route":
                    triggerLabel = SelectionPanel.IsChineseLocale() ? "区间运行中" : "mid_route";
                    break;
                case "bypass_waiting":
                    triggerLabel = SelectionPanel.IsChineseLocale() ? "待避中" : "bypass_waiting";
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

        internal bool TryPanelContext(
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

        internal string EventText(Entity vehicle)
        {
            return m_BroadcastLastEventTextByVehicle.TryGetValue(vehicle, out string text)
                ? text ?? string.Empty
                : string.Empty;
        }

        internal readonly struct BroadcastTriggerContext
        {
            public readonly string LineId;
            public readonly Entity CurrentStopEntity;
            public readonly string CurrentStationName;
            public readonly string NextStationName;
            public readonly string TerminalStationName;
            public readonly string TurnbackStationName;
            public readonly string CurrentStationAssetName;
            public readonly string NextStationAssetName;
            public readonly string TerminalStationAssetName;
            public readonly string TurnbackStationAssetName;
            public readonly List<BroadcastWorkbenchStationBindingDto> CurrentStationBindings;
            public readonly List<BroadcastWorkbenchStationBindingDto> NextStationBindings;
            public readonly List<BroadcastWorkbenchStationBindingDto> TerminalStationBindings;
            public readonly List<BroadcastWorkbenchStationBindingDto> TurnbackStationBindings;

            public BroadcastTriggerContext(
                string lineId,
                Entity currentStopEntity,
                string currentStationName,
                string nextStationName,
                string terminalStationName,
                string turnbackStationName,
                string currentStationAssetName,
                string nextStationAssetName,
                string terminalStationAssetName,
                string turnbackStationAssetName,
                List<BroadcastWorkbenchStationBindingDto> currentStationBindings,
                List<BroadcastWorkbenchStationBindingDto> nextStationBindings,
                List<BroadcastWorkbenchStationBindingDto> terminalStationBindings,
                List<BroadcastWorkbenchStationBindingDto> turnbackStationBindings)
            {
                LineId = lineId;
                CurrentStopEntity = currentStopEntity;
                CurrentStationName = currentStationName;
                NextStationName = nextStationName;
                TerminalStationName = terminalStationName;
                TurnbackStationName = turnbackStationName;
                CurrentStationAssetName = currentStationAssetName;
                NextStationAssetName = nextStationAssetName;
                TerminalStationAssetName = terminalStationAssetName;
                TurnbackStationAssetName = turnbackStationAssetName;
                CurrentStationBindings = currentStationBindings;
                NextStationBindings = nextStationBindings;
                TerminalStationBindings = terminalStationBindings;
                TurnbackStationBindings = turnbackStationBindings;
            }
        }
    }
}
