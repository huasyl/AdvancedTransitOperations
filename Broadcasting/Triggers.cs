using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Rendering;
using Game.Routes;
using RapidTransitMod.Core;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RapidTransitMod.Broadcasting
{
    internal static class TriggerConstants
    {
        internal const uint AnchorDiagnosticCooldownFrames = 30u;
        internal const int IdleRouteCooldownAfterLeaveSeconds = 3;
        internal const string PlatformIdleTriggerId = "platform_idle_clear";
        internal const string PlatformApproachTriggerId = "platform_approach_station";
        internal const float LeaveAnchorDistanceMeters = 100f;
        internal const float ApproachAnchorDistanceMeters = 200f;
        internal const float PlatformApproachAnchorDistanceMeters = 600f;
        internal const float PlatformPreparingApproachLeadMinutes = 10f;
        internal const int PlatformPreparingApproachPhaseIndex = 999999;
        internal const int PlatformIdleBusyGraceMinutes = 3;
        internal const int PlatformIdleQuietConfirmMinutes = 1;
    }

        internal struct ProgressState
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



        internal struct ApproachState
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
            public VehicleStation StationContext;
        }



        internal readonly struct FrameContext
        {
            public readonly VehicleStation StationContext;
            public readonly LineTrackChain Chain;
            public readonly VehicleTrackCursor Cursor;
            public readonly int TraversalPhaseIndex;

            public FrameContext(
                VehicleStation stationContext,
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



    internal static class FrameContexts
    {
        internal static bool TryBuild(
            BroadcastAccess access,
            Stations stations,
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int preferredCurrentStopWaypointIndex,
            out FrameContext runtimeContext)
        {
            runtimeContext = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length < 2
                || !stations.TryVehicle(
                    vehicle,
                    line,
                    waypoints,
                    preferredCurrentStopWaypointIndex,
                    out VehicleStation stationContext)
                || !access.TryChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !access.TrySnapshot(
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

            runtimeContext = new FrameContext(
                stationContext,
                chain,
                cursor,
                traversalPhaseIndex);
            return true;
        }
    }

    internal sealed class Diagnostics
    {
        private readonly BroadcastAccess m_Access;
        private readonly Dictionary<Entity, string> m_AnchorDiagnosticKeys = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_AnchorDiagnosticLastFrames = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_AnchorTriggerLogs = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_PlatformApproachDiagnosticKeys = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_PlatformApproachDiagnosticLastFrames = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_PlatformApproachTriggerLogs = new Dictionary<Entity, string>();

        internal Diagnostics(BroadcastAccess access)
        {
            m_Access = access ?? throw new ArgumentNullException(nameof(access));
        }

        internal void Remove(Entity vehicle)
        {
            m_AnchorDiagnosticKeys.Remove(vehicle);
            m_AnchorDiagnosticLastFrames.Remove(vehicle);
            m_AnchorTriggerLogs.Remove(vehicle);
            m_PlatformApproachDiagnosticKeys.Remove(vehicle);
            m_PlatformApproachDiagnosticLastFrames.Remove(vehicle);
            m_PlatformApproachTriggerLogs.Remove(vehicle);
        }

        internal void Clear()
        {
            m_AnchorDiagnosticKeys.Clear();
            m_AnchorDiagnosticLastFrames.Clear();
            m_AnchorTriggerLogs.Clear();
            m_PlatformApproachDiagnosticKeys.Clear();
            m_PlatformApproachDiagnosticLastFrames.Clear();
            m_PlatformApproachTriggerLogs.Clear();
        }

        internal void ClearPlatformApproach()
        {
            m_PlatformApproachDiagnosticKeys.Clear();
            m_PlatformApproachDiagnosticLastFrames.Clear();
            m_PlatformApproachTriggerLogs.Clear();
        }

        internal void Anchor(
            string phase,
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            VehicleStation stationContext,
            ProgressState state,
            bool onceOnly)
        {
            if (!RtLog.VerboseEnabled)
                return;

            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            if (!TryBuildAnchorDiagnostic(
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
                m_Access.LogOnce(
                    m_AnchorTriggerLogs,
                    vehicle,
                    phase + "|" + stableKey,
                    message);
                return;
            }

            if (m_Access.ShouldLog(
                    m_AnchorDiagnosticKeys,
                    m_AnchorDiagnosticLastFrames,
                    vehicle,
                    phase + "|" + stableKey,
                    nowFrame,
                    TriggerConstants.AnchorDiagnosticCooldownFrames))
            {
                m_Access.Log.Info(message);
            }
        }


        internal void PlatformApproach(
            string phase,
            Entity vehicle,
            LineTrackChain chain,
            ApproachState state,
            bool onceOnly)
        {
            if (!RtLog.VerboseEnabled)
                return;

            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            if (!TryBuildPlatformApproachDiagnostic(
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
                m_Access.LogOnce(
                    m_PlatformApproachTriggerLogs,
                    vehicle,
                    phase + "|" + stableKey,
                    message);
                return;
            }

            if (m_Access.ShouldLog(
                    m_PlatformApproachDiagnosticKeys,
                    m_PlatformApproachDiagnosticLastFrames,
                    vehicle,
                    phase + "|" + stableKey,
                    nowFrame,
                    TriggerConstants.AnchorDiagnosticCooldownFrames))
            {
                m_Access.Log.Info(message);
            }
        }


        private bool TryBuildPlatformApproachDiagnostic(
            string phase,
            Entity vehicle,
            LineTrackChain chain,
            ApproachState state,
            out string stableKey,
            out string message)
        {
            stableKey = string.Empty;
            message = string.Empty;
            if (vehicle == Entity.Null
                || chain == null
                || state.NextStopWaypointIndex < 0
                || !m_Access.TryWindow(
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
                && !m_Access.TryWindow(
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
                + " anchorMeters=" + TriggerConstants.PlatformApproachAnchorDistanceMeters.ToString("F0")
                + " fallback=" + fallback
                + " triggered=" + state.Triggered
                + " phaseIndex=" + state.TraversalPhaseIndex;
            return true;
        }


        private bool TryBuildAnchorDiagnostic(
            string phase,
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            VehicleStation stationContext,
            ProgressState state,
            out string stableKey,
            out string message)
        {
            stableKey = string.Empty;
            message = string.Empty;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !m_Access.TryChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !m_Access.TryCursor(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor)
                || state.CurrentStopWaypointIndex < 0
                || state.NextStopWaypointIndex < 0
                || !m_Access.TryWindow(
                    chain,
                    state.CurrentStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out _,
                    out int currentWindowEndExclusive)
                || !m_Access.TryWindow(
                    chain,
                    state.NextStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out int nextWindowStart,
                    out int nextWindowEndExclusive))
            {
                return false;
            }

            int effectiveLeaveAtomIndex = state.LeaveTriggerAtomIndex >= 0 && state.BroadcastLeaveAtomIndex >= 0
                ? Anchors.EffectiveLeaveAtomIndex(state.LeaveTriggerAtomIndex, state.BroadcastLeaveAtomIndex)
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


    }

    internal sealed class Vehicles
    {
        private readonly BroadcastAccess m_Access;
        private readonly Config m_Config;
        private readonly Stations m_Stations;
        private readonly Playback m_Playback;
        private readonly Diagnostics m_Diagnostics;
        private readonly Dictionary<Entity, string> m_LastStopAndOpenStopByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LastLeaveStopByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LastBypassStopByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LastApproachStopByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LastMidRouteStopByVehicle = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, ProgressState> m_ProgressStateByVehicle =
            new Dictionary<Entity, ProgressState>();

        internal Vehicles(BroadcastAccess access, Config config, Stations stations, Playback playback, Diagnostics diagnostics)
        {
            m_Access = access ?? throw new ArgumentNullException(nameof(access));
            m_Config = config ?? throw new ArgumentNullException(nameof(config));
            m_Stations = stations ?? throw new ArgumentNullException(nameof(stations));
            m_Playback = playback ?? throw new ArgumentNullException(nameof(playback));
            m_Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        internal void Running(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding,
            bool shouldPlayForTracked,
            bool hasContext,
            FrameContext context)
        {
            Progress(vehicle, line, waypoints, boarding, shouldPlayForTracked, hasContext, context);
        }

        internal bool ShouldPlay(Entity vehicle) => ShouldPlayForTracked(vehicle);

        internal void Remove(Entity vehicle)
        {
            m_LastStopAndOpenStopByVehicle.Remove(vehicle);
            m_LastLeaveStopByVehicle.Remove(vehicle);
            m_LastBypassStopByVehicle.Remove(vehicle);
            m_LastApproachStopByVehicle.Remove(vehicle);
            m_LastMidRouteStopByVehicle.Remove(vehicle);
            m_ProgressStateByVehicle.Remove(vehicle);
        }

        internal void Clear()
        {
            m_LastStopAndOpenStopByVehicle.Clear();
            m_LastLeaveStopByVehicle.Clear();
            m_LastBypassStopByVehicle.Clear();
            m_LastApproachStopByVehicle.Clear();
            m_LastMidRouteStopByVehicle.Clear();
            m_ProgressStateByVehicle.Clear();
        }

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
                || !m_Stations.TryTriggerContext(
                    vehicle,
                    line,
                    waypoints,
                    previousWaypointIndex,
                    out _,
                    out VehicleStation stationContext))
            {
                return;
            }

            RememberBroadcastLeaveTriggerAtom(vehicle, line, waypoints, stationContext);
            if (m_ProgressStateByVehicle.TryGetValue(vehicle, out ProgressState state))
            {
                m_Diagnostics.Anchor("arm", vehicle, line, waypoints, stationContext, state, false);
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
            FrameContext runtimeContext)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length < 2
                || !shouldBroadcastForTrackedVehicle
                || !hasRuntimeContext
                || !m_Access.TryRelation(
                    runtimeContext.Chain,
                    runtimeContext.StationContext.CurrentStopWaypointIndex,
                    runtimeContext.Cursor.AtomCursorIndex,
                    out CursorAtomWindowRelation currentStopWindowRelation,
                    out _,
                    out _))
            {
                m_ProgressStateByVehicle.Remove(vehicle);
                return;
            }

            VehicleStation stationContext = runtimeContext.StationContext;
            LineTrackChain chain = runtimeContext.Chain;
            VehicleTrackCursor cursor = runtimeContext.Cursor;
            bool resetState = !m_ProgressStateByVehicle.TryGetValue(vehicle, out ProgressState state)
                || state.CurrentStopWaypointIndex != stationContext.CurrentStopWaypointIndex
                || state.NextStopWaypointIndex != stationContext.NextStopWaypointIndex;
            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            if (resetState)
            {
                state = new ProgressState
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

                Anchors.TryResolveDistanceAnchoredAtoms(m_Access, 
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
                m_Diagnostics.Anchor("tick", vehicle, line, waypoints, stationContext, state, false);
                m_ProgressStateByVehicle[vehicle] = state;
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
                m_Diagnostics.Anchor("trigger-leave", vehicle, line, waypoints, stationContext, state, true);
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
                m_Diagnostics.Anchor("trigger-mid", vehicle, line, waypoints, stationContext, state, true);
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
                m_Diagnostics.Anchor("trigger-approach", vehicle, line, waypoints, stationContext, state, true);
            }

            state.LastCurrentStopWindowRelation = currentStopWindowRelation;
            m_Diagnostics.Anchor("tick", vehicle, line, waypoints, stationContext, state, false);
            m_ProgressStateByVehicle[vehicle] = state;
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
                || !m_Access.TryChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !m_Access.TryCursor(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor)
                || !m_Access.TryWindow(
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
                || !m_Access.TryChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !m_Access.TryCursor(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            int effectiveLeaveAtomIndex = Anchors.EffectiveLeaveAtomIndex(
                leaveTriggerAtomIndex,
                broadcastLeaveAtomIndex);
            if (effectiveLeaveAtomIndex >= broadcastApproachAtomIndex)
            {
                return false;
            }

            return cursor.AtomCursorIndex >= effectiveLeaveAtomIndex
                && cursor.AtomCursorIndex < broadcastApproachAtomIndex;
        }


        private void RememberBroadcastLeaveTriggerAtom(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            VehicleStation stationContext)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !m_Access.TryChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !m_Access.TryCursor(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return;
            }

            if (m_ProgressStateByVehicle.TryGetValue(vehicle, out ProgressState existingState)
                && existingState.CurrentStopWaypointIndex == stationContext.CurrentStopWaypointIndex
                && existingState.NextStopWaypointIndex == stationContext.NextStopWaypointIndex)
            {
                return;
            }

            Anchors.TryResolveDistanceAnchoredAtoms(m_Access, 
                vehicle,
                line,
                waypoints,
                stationContext.CurrentStopWaypointIndex,
                stationContext.NextStopWaypointIndex,
                out int broadcastLeaveAtomIndex,
                out int broadcastApproachAtomIndex);

            CursorAtomWindowRelation currentStopWindowRelation = CursorAtomWindowRelation.Unknown;
            if (m_Access.TryChain(line, waypoints, out LineTrackChain relationChain)
                && relationChain != null
                && m_Access.TryCursor(vehicle, line, waypoints, relationChain, out VehicleTrackCursor relationCursor)
                && m_Access.TryRelation(
                    relationChain,
                    stationContext.CurrentStopWaypointIndex,
                    relationCursor.AtomCursorIndex,
                    out CursorAtomWindowRelation liveRelation,
                    out _,
                    out _))
            {
                currentStopWindowRelation = liveRelation;
            }

            m_ProgressStateByVehicle[vehicle] = new ProgressState
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
            out TriggerContext context,
            out VehicleStation stationContext)
        {
            context = default;
            stationContext = default;
            if (!ShouldPlayForTracked(vehicle))
            {
                return false;
            }

            if (!m_Stations.TryTriggerContext(vehicle, line, waypoints, waypointIndex, out context, out stationContext))
            {
                return false;
            }

            if (IsDuplicateBroadcastTrigger(vehicle, triggerId, stationContext.CurrentStationId))
            {
                return false;
            }

            RememberBroadcastTriggerStop(vehicle, triggerId, stationContext.CurrentStationId);
            m_Stations.UpdatePanelState(vehicle, context.CurrentStationName, context.NextStationName);
            m_Playback.Start(vehicle, context, triggerId);
            return true;
        }


        private bool ShouldPlayForTracked(Entity vehicle)
        {
            if (!m_Config.Enabled
                || vehicle == Entity.Null
                || m_Access.CameraUpdateSystem == null)
            {
                return false;
            }
            if (m_Access.VehicleView.TryGetState(vehicle, out VehicleState state)
                && state == VehicleState.Retiring)
            {
                return false;
            }

            OrbitCameraController orbitCameraController = m_Access.CameraUpdateSystem.orbitCameraController;
            if (orbitCameraController == null
                || !ReferenceEquals(m_Access.CameraUpdateSystem.activeCameraController, orbitCameraController))
            {
                return false;
            }

            Entity followedVehicle = m_Access.Vehicle(orbitCameraController.followedEntity);
            Entity broadcastVehicle = m_Access.Vehicle(vehicle);
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
                    return m_LastStopAndOpenStopByVehicle.TryGetValue(vehicle, out string stopAndOpenStop)
                        && string.Equals(stopAndOpenStop, currentStationId, StringComparison.Ordinal);
                case "leave_station":
                    return m_LastLeaveStopByVehicle.TryGetValue(vehicle, out string leaveStop)
                        && string.Equals(leaveStop, currentStationId, StringComparison.Ordinal);
                case "bypass_waiting":
                    return m_LastBypassStopByVehicle.TryGetValue(vehicle, out string bypassStop)
                        && string.Equals(bypassStop, currentStationId, StringComparison.Ordinal);
                case "approach_station":
                    return m_LastApproachStopByVehicle.TryGetValue(vehicle, out string approachStop)
                        && string.Equals(approachStop, currentStationId, StringComparison.Ordinal);
                case "mid_route":
                    return m_LastMidRouteStopByVehicle.TryGetValue(vehicle, out string midRouteStop)
                        && string.Equals(midRouteStop, currentStationId, StringComparison.Ordinal);
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
                    m_LastStopAndOpenStopByVehicle[vehicle] = currentStationId;
                    break;
                case "leave_station":
                    m_LastLeaveStopByVehicle[vehicle] = currentStationId;
                    break;
                case "bypass_waiting":
                    m_LastBypassStopByVehicle[vehicle] = currentStationId;
                    break;
                case "approach_station":
                    m_LastApproachStopByVehicle[vehicle] = currentStationId;
                    break;
                case "mid_route":
                    m_LastMidRouteStopByVehicle[vehicle] = currentStationId;
                    break;
            }
        }


        private bool LineHasBroadcastRulesForTrigger(string lineId, string triggerId)
        {
            return !string.IsNullOrEmpty(lineId)
                && !string.IsNullOrEmpty(triggerId)
                && m_Config.RulesByLine.TryGetValue(lineId, out List<BroadcastWorkbenchRuleDto> rules)
                && rules != null
                && rules.Any(rule => rule != null
                    && string.Equals(rule.triggerId, triggerId, StringComparison.Ordinal)
                    && rule.nodes != null
                    && rule.nodes.Length > 0);
        }


        private void UpdateIdleRouteLeaveProtection(
            Entity vehicle,
            ref ProgressState state,
            uint nowFrame)
        {
            if (!state.IdleRouteWaitingForLeaveSequenceEnd)
            {
                return;
            }

            if (m_Playback.ActiveForTrigger(vehicle, "leave_station"))
            {
                return;
            }

            state.IdleRouteWaitingForLeaveSequenceEnd = false;
            state.IdleRouteBlockedUntilFrame = nowFrame;
            state.IdleRouteBlockedUntilRealtime = UnityEngine.Time.realtimeSinceStartup + TriggerConstants.IdleRouteCooldownAfterLeaveSeconds;
        }


        private static bool IsIdleRouteLeaveProtectionSatisfied(
            ProgressState state,
            uint nowFrame)
        {
            if (state.IdleRouteWaitingForLeaveSequenceEnd)
            {
                return false;
            }

            return UnityEngine.Time.realtimeSinceStartup >= state.IdleRouteBlockedUntilRealtime;
        }


    }

    internal sealed class Platforms
    {
        private readonly BroadcastAccess m_Access;
        private readonly Config m_Config;
        private readonly Stations m_Stations;
        private readonly Playback m_Playback;
        private readonly Diagnostics m_Diagnostics;
        private readonly Dictionary<Entity, ApproachState> m_ApproachStateByVehicle =
            new Dictionary<Entity, ApproachState>();
        private readonly HashSet<string> m_CheckedLineIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> m_AnnouncementCooldownUntilFrame =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> m_StationBusyUntilFrame =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> m_StationQuietSinceFrame =
            new Dictionary<string, uint>(StringComparer.Ordinal);
        internal Platforms(BroadcastAccess access, Config config, Stations stations, Playback playback, Diagnostics diagnostics)
        {
            m_Access = access ?? throw new ArgumentNullException(nameof(access));
            m_Config = config ?? throw new ArgumentNullException(nameof(config));
            m_Stations = stations ?? throw new ArgumentNullException(nameof(stations));
            m_Playback = playback ?? throw new ArgumentNullException(nameof(playback));
            m_Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            m_Access.SubscribeClockChanged(OnClockChanged);
        }

        private void OnClockChanged(ClockSnapshot oldClockSnapshot, ClockSnapshot newClockSnapshot)
        {
            _ = oldClockSnapshot;
            _ = newClockSnapshot;
            m_AnnouncementCooldownUntilFrame.Clear();
            m_StationBusyUntilFrame.Clear();
        }

        internal bool HasState => m_AnnouncementCooldownUntilFrame.Count > 0
            || m_StationBusyUntilFrame.Count > 0
            || m_StationQuietSinceFrame.Count > 0
            || m_ApproachStateByVehicle.Count > 0;

        internal void Running(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding,
            Config.LineFlags flags,
            bool hasContext,
            FrameContext context)
        {
            if (!m_Config.Enabled || !flags.HasPlatform)
            {
                m_ApproachStateByVehicle.Remove(vehicle);
                return;
            }

            if (flags.HasIdle)
            {
                WatchBusy(line, waypoints, hasContext, boarding, context);
            }

            if (flags.HasApproach)
            {
                WatchApproach(vehicle, line, waypoints, hasContext, context);
            }
        }

        internal void StateChanged(
            Entity vehicle,
            VehicleState previousState,
            VehicleState currentState)
        {
            if ((previousState == VehicleState.Preparing && currentState != VehicleState.Preparing)
                || (previousState == VehicleState.Running && currentState != VehicleState.Running))
            {
                m_ApproachStateByVehicle.Remove(vehicle);
            }
        }

        internal void Remove(Entity vehicle)
        {
            m_ApproachStateByVehicle.Remove(vehicle);
        }

        internal void Clear()
        {
            m_CheckedLineIds.Clear();
            m_ApproachStateByVehicle.Clear();
            m_AnnouncementCooldownUntilFrame.Clear();
            m_StationBusyUntilFrame.Clear();
            m_StationQuietSinceFrame.Clear();
        }

        internal void ClearLineChecks()
        {
            m_CheckedLineIds.Clear();
            m_Config.ClearFlags();
            m_ApproachStateByVehicle.Clear();
            m_AnnouncementCooldownUntilFrame.Clear();
            m_StationBusyUntilFrame.Clear();
            m_StationQuietSinceFrame.Clear();
            m_Diagnostics.ClearPlatformApproach();
        }

        internal void ClearAssetState()
        {
            m_AnnouncementCooldownUntilFrame.Clear();
            m_StationBusyUntilFrame.Clear();
            m_StationQuietSinceFrame.Clear();
            m_ApproachStateByVehicle.Clear();
            m_Diagnostics.ClearPlatformApproach();
        }

        internal void ClearAssetState(ModeScope scope)
        {
            foreach (string key in m_AnnouncementCooldownUntilFrame.Keys
                .Where(key => ScopeMatchesStateKey(scope, key))
                .ToArray())
            {
                m_AnnouncementCooldownUntilFrame.Remove(key);
            }

            foreach (string key in m_StationBusyUntilFrame.Keys
                .Where(key => ScopeMatchesStateKey(scope, key))
                .ToArray())
            {
                m_StationBusyUntilFrame.Remove(key);
            }

            foreach (string key in m_StationQuietSinceFrame.Keys
                .Where(key => ScopeMatchesStateKey(scope, key))
                .ToArray())
            {
                m_StationQuietSinceFrame.Remove(key);
            }

            foreach (Entity vehicle in m_ApproachStateByVehicle
                .Where(entry => MatchesRuntimeScope(scope, entry.Value.LineId))
                .Select(entry => entry.Key)
                .ToArray())
            {
                m_ApproachStateByVehicle.Remove(vehicle);
            }

            m_Diagnostics.ClearPlatformApproach();
        }

        internal void Preparing(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool atOrigin,
            uint nowFrame)
        {
            Config.LineFlags flags = PlatformFlags(line);
            if (!flags.HasPlatform)
                return;

            float etaFrames = atOrigin
                ? 0f
                : m_Access.EstimatePreparing(vehicle, line, waypoints, nowFrame);

            if (flags.HasIdle)
            {
                WatchOriginBusy(
                    line,
                    waypoints,
                    atOrigin || etaFrames <= m_Access.ClockSnapshot.ToFramesCeil(
                        TriggerConstants.PlatformPreparingApproachLeadMinutes));
            }

            if (flags.HasApproach)
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
            if (PlatformFlags(line).HasIdle)
            {
                WatchOriginBusy(line, waypoints, busy);
            }
        }


        internal void Tick(uint nowFrame, bool sourceSweep)
        {
            if (!sourceSweep || m_Config.PlatformsByLine.Count == 0)
            {
                return;
            }

            PruneRunningApproachStates(nowFrame);

            List<WorkbenchLineRuntime> runtimeLines = m_Access.Lines();
            for (int i = 0; i < runtimeLines.Count; i++)
            {
                WorkbenchLineRuntime runtime = runtimeLines[i];
                if (runtime == null
                    || string.IsNullOrWhiteSpace(runtime.Id)
                    || !m_Config.PlatformsByLine.TryGetValue(runtime.Id, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements)
                    || lineAnnouncements == null
                    || lineAnnouncements.Count == 0
                    || !m_Access.EntityManager.HasBuffer<RouteWaypoint>(runtime.Entity))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = m_Access.EntityManager.GetBuffer<RouteWaypoint>(runtime.Entity, true);
                EnsureBroadcastRuntimeLineState(runtime.Id, runtime.Entity);
                Config.LineFlags flags = m_Config.Flags(runtime.Id);
                if (!flags.HasPlatform)
                {
                    continue;
                }

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

                    if (string.Equals(announcement.triggerId, TriggerConstants.PlatformIdleTriggerId, StringComparison.Ordinal))
                    {
                        if (!flags.HasIdle)
                        {
                            continue;
                        }

                        string normalizedIdleStationId = m_Stations.NormalizeRepresentativeStationId(
                            runtime.Entity,
                            waypoints,
                            stationId);
                        string cooldownKey = runtime.Id + "|" + normalizedIdleStationId + "|" + TriggerConstants.PlatformIdleTriggerId;
                        if (m_AnnouncementCooldownUntilFrame.TryGetValue(cooldownKey, out uint cooldownUntilFrame)
                            && nowFrame < cooldownUntilFrame)
                        {
                            continue;
                        }

                        if (!IsBroadcastPlatformStationIdle(runtime.Entity, waypoints, normalizedIdleStationId)
                            || !m_Stations.TryStation(runtime.Entity, waypoints, stationId, out ResolvedStation station)
                            || !m_Stations.TryStationOnlyContext(runtime.Id, station, out TriggerContext context))
                        {
                            continue;
                        }

                        string sequenceKey = IdleSequenceKey(runtime.Id, stationId);
                        if (m_Playback.StartPlatform(
                                sequenceKey,
                                station.StopEntity,
                                context,
                                announcement,
                                TriggerConstants.PlatformIdleTriggerId,
                                TriggerLabel))
                        {
                            uint cooldownFrames = m_Access.ClockSnapshot.ToFramesCeil(
                                Math.Max(1, announcement.cooldownGameMinutes));
                            m_AnnouncementCooldownUntilFrame[cooldownKey] = nowFrame + cooldownFrames;
                        }

                        continue;
                    }

                    if (string.Equals(announcement.triggerId, TriggerConstants.PlatformApproachTriggerId, StringComparison.Ordinal))
                    {
                        if (!flags.HasApproach)
                        {
                            continue;
                        }

                        if (approachCandidatesByStation == null)
                        {
                            approachCandidatesByStation = ApproachCandidatesByStation(runtime.Id, nowFrame);
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


        private void PruneRunningApproachStates(uint nowFrame)
        {
            if (m_ApproachStateByVehicle.Count == 0)
            {
                return;
            }

            List<Entity> staleVehicles = null;
            foreach (KeyValuePair<Entity, ApproachState> entry in m_ApproachStateByVehicle)
            {
                Entity vehicle = entry.Key;
                if (entry.Value.TraversalPhaseIndex == TriggerConstants.PlatformPreparingApproachPhaseIndex)
                {
                    continue;
                }

                if (vehicle == Entity.Null
                    || !m_Access.EntityManager.Exists(vehicle)
                    || !m_Access.VehicleView.TryGetState(vehicle, out VehicleState vehicleState)
                    || vehicleState != VehicleState.Running
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
                m_ApproachStateByVehicle.Remove(staleVehicles[i]);
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
                || !m_Access.TryChain(runtime.Entity, waypoints, out chain)
                || chain == null
                || !m_Stations.TryStation(runtime.Entity, waypoints, stationId, out ResolvedStation station))
            {
                return;
            }

            foreach (KeyValuePair<int, Entity> candidateEntry in candidatesByPhase)
            {
                Entity vehicle = candidateEntry.Value;
                if (vehicle == Entity.Null
                    || !m_ApproachStateByVehicle.TryGetValue(vehicle, out ApproachState state)
                    || state.Triggered
                    || state.CursorAtomIndex < state.TriggerAtomIndex
                    || IsBroadcastPlatformApproachPastTriggerWindow(chain, state))
                {
                    continue;
                }

                if (!m_Stations.TryTriggerContext(
                        state.StationContext,
                        out TriggerContext vehicleContext))
                {
                    continue;
                }

                string sequenceKey = ApproachSequenceKey(
                    runtime.Id,
                    stationId,
                    vehicle,
                    state.CurrentStopWaypointIndex,
                    state.NextStopWaypointIndex);
                if (m_Playback.StartPlatform(
                        sequenceKey,
                        station.StopEntity,
                        vehicleContext,
                        announcement,
                        TriggerConstants.PlatformIdleTriggerId,
                        TriggerLabel))
                {
                    state.Triggered = true;
                    m_ApproachStateByVehicle[vehicle] = state;
                    m_Diagnostics.PlatformApproach("trigger", vehicle, chain, state, true);
                }
            }
        }


        private bool IsBroadcastPlatformApproachPastTriggerWindow(
            LineTrackChain chain,
            ApproachState state)
        {
            if (chain == null
                || chain.TrackAtoms.Count <= 0
                || state.TriggerAtomIndex < 0
                || state.NextStopWaypointIndex < 0
                || !m_Access.TryWindow(
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


        private Dictionary<string, Dictionary<int, Entity>> ApproachCandidatesByStation(
            string lineId,
            uint nowFrame)
        {
            Dictionary<string, Dictionary<int, Entity>> candidatesByStation =
                new Dictionary<string, Dictionary<int, Entity>>(StringComparer.Ordinal);

            foreach (KeyValuePair<Entity, ApproachState> entry in m_ApproachStateByVehicle)
            {
                ApproachState state = entry.Value;
                if (state.Triggered
                    || (state.TraversalPhaseIndex != TriggerConstants.PlatformPreparingApproachPhaseIndex
                        && state.LastObservedFrame != nowFrame)
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
                    || !m_ApproachStateByVehicle.TryGetValue(currentVehicle, out ApproachState currentState)
                    || state.CursorAtomIndex > currentState.CursorAtomIndex)
                {
                    nearestByPhase[state.TraversalPhaseIndex] = entry.Key;
                }
            }

            return candidatesByStation;
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
                || !m_Config.PlatformsByLine.TryGetValue(lineId, out Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto> lineAnnouncements)
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
                    && string.Equals(candidate.triggerId, TriggerConstants.PlatformApproachTriggerId, StringComparison.Ordinal))
                {
                    announcement = candidate;
                    return true;
                }
            }

            return false;
        }


        private Config.LineFlags PlatformFlags(Entity line)
        {
            if (!m_Config.Enabled || line == Entity.Null)
            {
                return default;
            }

            string lineId = m_Access.DraftKey(m_Access.LineId(line));
            EnsureBroadcastRuntimeLineState(lineId, line);
            return m_Config.Flags(lineId);
        }


        private void WatchBusy(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool hasRuntimeContext,
            bool boarding,
            FrameContext runtimeContext)
        {
            if (line == Entity.Null
                || !hasRuntimeContext
                || runtimeContext.Chain == null
                || runtimeContext.Chain.TrackAtoms.Count == 0
                || !m_Stations.TryCache(line, waypoints, out LineCache cache)
                || cache?.Stations == null
                || cache.Stations.Length == 0)
            {
                return;
            }

            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            VehicleStation stationContext = runtimeContext.StationContext;
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
                ResolvedStation station = cache.Stations[i];
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
                || !m_Stations.TryCache(line, waypoints, out LineCache cache)
                || cache?.Stations == null
                || cache.Stations.Length == 0
                || cache.Stations[0] == null)
            {
                return;
            }

            string lineId = m_Access.DraftKey(m_Access.LineId(line));
            if (string.IsNullOrWhiteSpace(lineId))
            {
                return;
            }

            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            MarkBroadcastPlatformStationBusy(line, waypoints, lineId, cache.Stations[0].StationId, nowFrame);
        }


        private bool IsCursorPastBroadcastPlatformApproachAnchor(
            FrameContext runtimeContext,
            VehicleStation stationContext)
        {
            if (!Anchors.TryResolvePlatformApproachAtom(m_Access, 
                    runtimeContext.Chain,
                    runtimeContext.Cursor.AtomCursorIndex,
                    stationContext.CurrentStopWaypointIndex,
                    stationContext.NextStopWaypointIndex,
                    out int triggerAtomIndex))
            {
                return false;
            }

            ApproachState state = new ApproachState
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
                || !m_Access.TryWindow(
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
            string normalizedStationId = m_Stations.NormalizeRepresentativeStationId(line, waypoints, stationId);
            if (string.IsNullOrWhiteSpace(lineId) || string.IsNullOrWhiteSpace(normalizedStationId))
            {
                return;
            }

            string key = StationStateKey(lineId, normalizedStationId);
            uint busyUntilFrame = nowFrame + m_Access.ClockSnapshot.ToFramesCeil(
                TriggerConstants.PlatformIdleBusyGraceMinutes);
            if (!m_StationBusyUntilFrame.TryGetValue(key, out uint existingUntil)
                || existingUntil < busyUntilFrame)
            {
                m_StationBusyUntilFrame[key] = busyUntilFrame;
            }

            m_StationQuietSinceFrame.Remove(key);
        }


        private void WatchApproach(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool hasRuntimeContext,
            FrameContext runtimeContext)
        {
            if (vehicle == Entity.Null
                || !hasRuntimeContext)
            {
                m_ApproachStateByVehicle.Remove(vehicle);
                return;
            }

            VehicleStation stationContext = runtimeContext.StationContext;
            string representativeNextStationId = m_Stations.NormalizeRepresentativeStationId(
                line,
                waypoints,
                stationContext.NextStationId);
            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            if (string.IsNullOrWhiteSpace(stationContext.LineId)
                || string.IsNullOrWhiteSpace(representativeNextStationId)
                || string.Equals(stationContext.CurrentStationId, stationContext.NextStationId, StringComparison.Ordinal)
                || IsBroadcastPlatformApproachSuppressedForOriginReturn(vehicle, stationContext)
                || !TryGetEnabledBroadcastPlatformApproachAnnouncement(
                    line,
                    stationContext.LineId,
                    representativeNextStationId,
                    out _)
                || !Anchors.TryResolvePlatformApproachAtom(m_Access, 
                    runtimeContext.Chain,
                    runtimeContext.Cursor.AtomCursorIndex,
                    stationContext.CurrentStopWaypointIndex,
                    stationContext.NextStopWaypointIndex,
                    out int triggerAtomIndex))
            {
                m_ApproachStateByVehicle.Remove(vehicle);
                return;
            }

            bool resetState =
                !m_ApproachStateByVehicle.TryGetValue(vehicle, out ApproachState state)
                || !string.Equals(state.LineId, stationContext.LineId, StringComparison.Ordinal)
                || !string.Equals(state.StationId, representativeNextStationId, StringComparison.Ordinal)
                || state.CurrentStopWaypointIndex != stationContext.CurrentStopWaypointIndex
                || state.NextStopWaypointIndex != stationContext.NextStopWaypointIndex
                || state.TraversalPhaseIndex != runtimeContext.TraversalPhaseIndex;

            if (resetState)
            {
                state = new ApproachState
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
                m_Diagnostics.PlatformApproach("resolve", vehicle, runtimeContext.Chain, state, true);
            }
            else
            {
                state.TriggerAtomIndex = triggerAtomIndex;
                state.CursorAtomIndex = runtimeContext.Cursor.AtomCursorIndex;
                state.TraversalPhaseIndex = runtimeContext.TraversalPhaseIndex;
                state.LastObservedFrame = nowFrame;
                state.StationContext = stationContext;
            }

            m_ApproachStateByVehicle[vehicle] = state;
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
                || preparingArrivalFrames > m_Access.ClockSnapshot.ToFramesCeil(
                    TriggerConstants.PlatformPreparingApproachLeadMinutes)
                || !m_Stations.TryCache(line, waypoints, out LineCache cache)
                || cache?.Stations == null
                || cache.Stations.Length == 0)
            {
                m_ApproachStateByVehicle.Remove(vehicle);
                return;
            }

            ResolvedStation originStation = cache.Stations[0];
            if (originStation == null)
            {
                m_ApproachStateByVehicle.Remove(vehicle);
                return;
            }

            string lineId = m_Access.DraftKey(m_Access.LineId(line));
            string representativeOriginStationId = m_Stations.NormalizeRepresentativeStationId(
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
                m_ApproachStateByVehicle.Remove(vehicle);
                return;
            }

            ResolvedStation turnbackStation =
                Stations.TurnbackAfterWaypoint(cache, originStation);
            VehicleStation stationContext = new VehicleStation(
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
            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            bool resetState =
                !m_ApproachStateByVehicle.TryGetValue(vehicle, out ApproachState state)
                || !string.Equals(state.LineId, lineId, StringComparison.Ordinal)
                || !string.Equals(state.StationId, representativeOriginStationId, StringComparison.Ordinal)
                || state.CurrentStopWaypointIndex != originStation.WaypointIndex
                || state.NextStopWaypointIndex != originStation.WaypointIndex
                || state.TraversalPhaseIndex != TriggerConstants.PlatformPreparingApproachPhaseIndex;

            if (resetState)
            {
                state = new ApproachState
                {
                    LineId = lineId,
                    StationId = representativeOriginStationId,
                    CurrentStopWaypointIndex = originStation.WaypointIndex,
                    NextStopWaypointIndex = originStation.WaypointIndex,
                    TriggerAtomIndex = 1,
                    CursorAtomIndex = 1,
                    TraversalPhaseIndex = TriggerConstants.PlatformPreparingApproachPhaseIndex,
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

            m_ApproachStateByVehicle[vehicle] = state;
        }


        private bool IsBroadcastPlatformApproachSuppressedForOriginReturn(
            Entity vehicle,
            VehicleStation stationContext)
        {
            return vehicle != Entity.Null
                && stationContext.NextStopWaypointIndex == 0
                && m_Access.VehicleView.IsInbound(vehicle);
        }


        private static string IdleSequenceKey(string lineId, string stationId)
        {
            return "platform_idle|" + (lineId ?? string.Empty) + "|" + (stationId ?? string.Empty);
        }


        private static string StationStateKey(string lineId, string stationId)
        {
            return (lineId ?? string.Empty) + "|" + (stationId ?? string.Empty);
        }

        private static bool ScopeMatchesStateKey(ModeScope scope, string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            int separator = key.IndexOf('|');
            string lineId = separator >= 0 ? key.Substring(0, separator) : key;
            return MatchesRuntimeScope(scope, lineId);
        }

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


        private static string ApproachSequenceKey(
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


        private static string TriggerLabel(string triggerId)
        {
            return string.Equals(triggerId, TriggerConstants.PlatformApproachTriggerId, StringComparison.Ordinal)
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

            string normalizedStationId = m_Stations.NormalizeRepresentativeStationId(
                line,
                waypoints,
                stationId);
            string lineId = m_Access.DraftKey(m_Access.LineId(line));
            if (string.IsNullOrWhiteSpace(normalizedStationId) || string.IsNullOrWhiteSpace(lineId))
            {
                return false;
            }

            uint nowFrame = m_Access.SimulationSystem != null ? m_Access.SimulationSystem.frameIndex : 0u;
            string key = StationStateKey(lineId, normalizedStationId);
            if (m_StationBusyUntilFrame.TryGetValue(key, out uint busyUntilFrame)
                && nowFrame < busyUntilFrame)
            {
                m_StationQuietSinceFrame.Remove(key);
                return false;
            }

            if (!m_StationQuietSinceFrame.TryGetValue(key, out uint quietSinceFrame))
            {
                m_StationQuietSinceFrame[key] = nowFrame;
                return false;
            }

            uint quietConfirmFrames = m_Access.ClockSnapshot.ToFramesCeil(
                TriggerConstants.PlatformIdleQuietConfirmMinutes);
            if (nowFrame < quietSinceFrame + quietConfirmFrames)
            {
                return false;
            }

            return true;
        }


        private void EnsureBroadcastRuntimeLineState(string lineId, Entity line)
        {
            if (!m_Config.Enabled
                || string.IsNullOrWhiteSpace(lineId)
                || line == Entity.Null
                || m_CheckedLineIds.Contains(lineId))
            {
                return;
            }

            m_Config.EnsureLine(lineId, line, out _);
            m_CheckedLineIds.Add(lineId);
        }


    }

    internal static class Anchors
    {
        internal static int EffectiveLeaveAtomIndex(
            int leaveTriggerAtomIndex,
            int broadcastLeaveAtomIndex)
        {
            return math.max(leaveTriggerAtomIndex, broadcastLeaveAtomIndex);
        }


        internal static bool TryResolveDistanceAnchoredAtoms(BroadcastAccess m_Access, 
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
                || !m_Access.TryChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !m_Access.TryCursor(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor)
                || !m_Access.TryWindow(
                    chain,
                    currentStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out _,
                    out int currentWindowEndExclusive)
                || !m_Access.TryWindow(
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

            broadcastLeaveAtomIndex = ResolveDistanceAnchoredForwardAtom(
                m_Access,
                chain,
                currentWindowEndExclusive,
                nextWindowStart,
                TriggerConstants.LeaveAnchorDistanceMeters,
                currentWindowEndExclusive);
            broadcastApproachAtomIndex = ResolveDistanceAnchoredBackwardAtom(
                m_Access,
                chain,
                currentWindowEndExclusive,
                nextWindowStart,
                TriggerConstants.ApproachAnchorDistanceMeters,
                math.max(currentWindowEndExclusive, nextWindowStart - 1));
            return true;
        }


        internal static bool TryResolvePlatformApproachAtom(BroadcastAccess m_Access, 
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
                || !m_Access.TryChain(line, waypoints, out LineTrackChain chain)
                || chain == null
                || !m_Access.TryCursor(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor)
                || !m_Access.TryWindow(
                    chain,
                    currentStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out _,
                    out int currentWindowEndExclusive)
                || !m_Access.TryWindow(
                    chain,
                    nextStopWaypointIndex,
                    cursor.AtomCursorIndex,
                    out int nextWindowStart,
                    out _))
            {
                return false;
            }

            cursorAtomIndex = cursor.AtomCursorIndex;
            return Anchors.TryResolvePlatformApproachAtom(m_Access, 
                chain,
                cursor.AtomCursorIndex,
                currentStopWaypointIndex,
                nextStopWaypointIndex,
                out triggerAtomIndex);
        }


        internal static bool TryResolvePlatformApproachAtom(BroadcastAccess m_Access, 
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
                || !m_Access.TryWindow(
                    chain,
                    currentStopWaypointIndex,
                    cursorAtomIndex,
                    out _,
                    out int currentWindowEndExclusive)
                || !m_Access.TryWindow(
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

            triggerAtomIndex = ResolveDistanceAnchoredBackwardAtom(
                m_Access,
                chain,
                currentWindowEndExclusive,
                nextWindowStart,
                TriggerConstants.PlatformApproachAnchorDistanceMeters,
                currentWindowEndExclusive);
            return true;
        }


        private static int ResolveDistanceAnchoredForwardAtom(
            BroadcastAccess m_Access,
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
                if (!TryTrackAtomTraversalLengthMeters(m_Access, chain, atomIndex, out float atomDistance))
                    return fallbackAtomIndex;

                traversedDistance += atomDistance;
                if (traversedDistance >= anchorDistanceMeters)
                    return atomIndex;
            }

            return fallbackAtomIndex;
        }


        private static int ResolveDistanceAnchoredBackwardAtom(
            BroadcastAccess m_Access,
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
                if (!TryTrackAtomTraversalLengthMeters(m_Access, chain, atomIndex, out float atomDistance))
                    return fallbackAtomIndex;

                traversedDistance += atomDistance;
                if (traversedDistance >= anchorDistanceMeters)
                    return atomIndex;
            }

            return fallbackAtomIndex;
        }


        private static bool TryTrackAtomTraversalLengthMeters(
            BroadcastAccess m_Access,
            LineTrackChain chain,
            int atomIndex,
            out float distanceMeters)
        {
            distanceMeters = 0f;
            if (chain == null || atomIndex < 0 || atomIndex >= chain.TrackAtoms.Count)
                return false;

            TrackAtom atom = chain.TrackAtoms[atomIndex];
            if (TryTrackAtomCurveTraversalLengthMeters(m_Access, atom, out distanceMeters))
                return true;

            if (m_Access.TryPosition(chain, atomIndex, out float3 atomPosition))
            {
                int nextAtomIndex = atomIndex + 1;
                if (nextAtomIndex < chain.TrackAtoms.Count
                    && m_Access.TryPosition(chain, nextAtomIndex, out float3 nextAtomPosition))
                {
                    distanceMeters = math.distance(atomPosition, nextAtomPosition);
                    return true;
                }

                int previousAtomIndex = atomIndex - 1;
                if (previousAtomIndex >= 0
                    && m_Access.TryPosition(chain, previousAtomIndex, out float3 previousAtomPosition))
                {
                    distanceMeters = math.distance(previousAtomPosition, atomPosition);
                    return true;
                }
            }

            return false;
        }


        private static bool TryTrackAtomCurveTraversalLengthMeters(
            BroadcastAccess m_Access,
            TrackAtom atom,
            out float distanceMeters)
        {
            distanceMeters = 0f;
            if (TryEntityCurveTraversalLengthMeters(m_Access, atom.SourceTarget, atom.TargetDelta, out distanceMeters))
                return true;

            return atom.Key.PhysicalLaneKey != atom.SourceTarget
                && TryEntityCurveTraversalLengthMeters(m_Access, atom.Key.PhysicalLaneKey, atom.TargetDelta, out distanceMeters);
        }


        private static bool TryEntityCurveTraversalLengthMeters(
            BroadcastAccess m_Access,
            Entity entity,
            float2 targetDelta,
            out float distanceMeters)
        {
            distanceMeters = 0f;
            if (entity == Entity.Null
                || !m_Access.EntityManager.Exists(entity)
                || !m_Access.EntityManager.HasComponent<Curve>(entity))
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

            Curve curve = m_Access.EntityManager.GetComponentData<Curve>(entity);
            Bounds1 curveBounds = new Bounds1(math.min(start, end), math.max(start, end));
            distanceMeters = MathUtils.Length(curve.m_Bezier.xz, curveBounds);
            return true;
        }


    }
}
