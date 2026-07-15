using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal sealed class RuntimeFacade : IDisposable
    {
        private readonly IRuntimeContext m_Runtime;
        private readonly AdmissionService m_Admission;
        private readonly ControlService m_Control;
        private bool m_Enabled = true;
        private bool m_ToggleKeyArmed = true;
        private readonly Dictionary<Entity, string> m_DepartureGateLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_ReleaseDiagLogCache = new Dictionary<Entity, string>();

        internal RuntimeFacade(IRuntimeContext runtime)
        {
            m_Runtime = runtime;
            m_Admission = new AdmissionService(runtime);
            m_Control = new ControlService(runtime, m_Admission);
        }

        internal bool Enabled => m_Enabled;
        internal bool RuntimeEnabled() => m_Enabled;

        public void Dispose()
        {
            m_Admission.Dispose();
        }

        internal void SetEnabled(bool enabled)
        {
            if (m_Enabled == enabled)
                return;

            m_Enabled = enabled;
            ClearAll();
            m_Runtime.Log.Info(enabled
                ? "[F5] 已启用快慢车待避"
                : "[F5] 已禁用快慢车待避（仅用于性能排查）");
        }

        internal bool ToggleKey(bool pressed)
        {
            if (!pressed)
            {
                m_ToggleKeyArmed = true;
                return false;
            }

            if (!m_ToggleKeyArmed)
                return false;

            m_ToggleKeyArmed = false;
            SetEnabled(!m_Enabled);
            return true;
        }

        internal void ClearAll()
        {
            m_Admission.Clear();
            m_Control.Clear();
            m_DepartureGateLogCache.Clear();
            m_ReleaseDiagLogCache.Clear();
            m_Runtime.TrackModel.ClearAllStaticCaches();
        }

        internal void ClearLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            List<Entity> yieldVehiclesToRelease = m_Admission.ReleaseLine(line, m_Runtime.ResolveLine);
            if (yieldVehiclesToRelease != null)
            {
                for (int i = 0; i < yieldVehiclesToRelease.Count; i++)
                    ClearVehicle(yieldVehiclesToRelease[i], "线路运行态失效");
            }

            m_Admission.ClearLineStaticCaches(line);
            m_Admission.InvalidateStaticSceneIndex();
            m_Runtime.TrackModel.ClearStaticCachesForLine(line);
            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                int releasedVehicleCount = yieldVehiclesToRelease != null ? yieldVehiclesToRelease.Count : 0;
                m_Runtime.Log.Info("[BypassLineInvalidated] line=" + line.Index
                    + " mode=clear-line"
                    + " releasedVehicles=" + releasedVehicleCount
                    + " clearAdmissionStaticCaches=1"
                    + " invalidateStaticSceneIndex=1"
                    + " clearStaticCachesForLine=1");
            }
        }

        internal void ExpireLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_Runtime.ClearLineTimeProfiles();
            m_Admission.InvalidateStaticSceneIndex();

            List<Entity> expiredVehicles = m_Admission.ExpireLine(line);
            if (expiredVehicles == null)
            {
                if (RtLog.CacheInvalidationDiagnosticsEnabled)
                {
                    m_Runtime.Log.Info("[BypassLineInvalidated] line=" + line.Index
                        + " mode=expire-line"
                        + " expiredVehicles=0"
                        + " clearLineTimeProfiles=1"
                        + " invalidateStaticSceneIndex=1");
                }
                return;
            }

            for (int i = 0; i < expiredVehicles.Count; i++)
                RemoveVehicleLogs(expiredVehicles[i]);

            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                int expiredVehicleCount = expiredVehicles.Count;
                m_Runtime.Log.Info("[BypassLineInvalidated] line=" + line.Index
                    + " mode=expire-line"
                    + " expiredVehicles=" + expiredVehicleCount
                    + " clearLineTimeProfiles=1"
                    + " invalidateStaticSceneIndex=1");
            }
        }

        internal void ForgetBlocker(Entity blocker)
        {
            m_Admission.ForgetBlocker(blocker);
        }

        internal void ClearVehicle(Entity vehicle, string releaseReason = null)
        {
            ClearVehicle(vehicle, releaseReason, true);
        }

        internal void ClearVehiclePreservingBypassHoldSkipped(Entity vehicle, string releaseReason = null)
        {
            ClearVehicle(vehicle, releaseReason, false);
        }

        private void ClearVehicle(Entity vehicle, string releaseReason, bool clearBypassHoldSkipped)
        {
            if (vehicle == Entity.Null)
                return;

            if (!m_Admission.TryGetLatchedBlocker(vehicle, out Entity blocker))
            {
                if (clearBypassHoldSkipped)
                    m_Admission.ClearVehicle(vehicle);
                else
                    m_Admission.ClearVehiclePreservingBypassHoldSkipped(vehicle);
                return;
            }

            m_Admission.ClearBlocker(vehicle);
            m_Admission.RemoveCadence(vehicle);
            m_Admission.RemoveEpisode(vehicle);
            m_Runtime.RecordRelease(vehicle, blocker, releaseReason);
            if (RtLog.VerboseEnabled)
            {
                ((IControlContext)m_Runtime).LogVehicleStateOnce(
                    m_ReleaseDiagLogCache,
                    vehicle,
                    "release|blocker=" + blocker.Index + "|reason=" + (releaseReason ?? "-"),
                    "[待避释放诊断] vehicle=" + vehicle.Index
                        + " blocker=" + blocker.Index
                        + " reason=" + (releaseReason ?? "-")
                        + " frame=" + ((IControlContext)m_Runtime).Frame);
            }
            if (m_Runtime.IsBypassRuntimeLoggingEnabled())
            {
                Entity line = m_Runtime.ResolveLine(vehicle);
                string lineTag = line != Entity.Null ? "线路" + line.Index : "线路?";
                m_Runtime.Log.Info("[待避解除] " + lineTag + " 车辆" + vehicle.Index
                    + " 解除快车待避"
                    + (!string.IsNullOrWhiteSpace(releaseReason) ? " reason=" + releaseReason : string.Empty)
                    + (blocker != Entity.Null ? " blocker=" + blocker.Index : string.Empty));
            }

            RemoveVehicleLogs(vehicle);
        }

        internal BypassControlResult TickVehicle<TTransport, TCommandBuffer>(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            bool boarding,
            ref TTransport publicTransport,
            TCommandBuffer ecb,
            string lineTag,
            bool midStopDwellTimedOut,
            uint nowFrame)
        {
            return m_Control.TickVehicle(
                vehicle,
                line,
                waypoints,
                waypointIndex,
                boarding,
                ref publicTransport,
                ecb,
                lineTag,
                midStopDwellTimedOut,
                nowFrame);
        }

        internal BypassDecisionResult EvaluateDepartureGate(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            uint nowFrame)
        {
            return m_Admission.EvaluateDepartureGate(vehicle, line, waypoints, waypointIndex, nowFrame);
        }

        internal Entity FindBlocker(BypassDecisionResult result)
        {
            return m_Admission.FindBlocker(result);
        }

        internal bool TryGetLatchedBlocker(Entity vehicle, out Entity blocker)
        {
            return m_Admission.TryGetLatchedBlocker(vehicle, out blocker);
        }

        internal bool TryGetConflictEpisode(Entity vehicle, out BypassConflictEpisode episode)
        {
            return m_Admission.Get(vehicle, out episode);
        }

        internal bool TryGetHoldCadence(Entity vehicle, out BypassHoldCadenceSnapshot cadence)
        {
            return m_Admission.Get(vehicle, out cadence);
        }

        internal bool TryGetBypassHoldSkipped(Entity vehicle, out Entity blocker)
        {
            return m_Admission.TryGetBypassHoldSkipped(vehicle, out blocker);
        }

        internal bool IsStopSceneEligible(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            out bool known)
        {
            return m_Admission.IsStopSceneEligible(line, waypoints, waypointIndex, out known);
        }

        internal void ClearBypassHoldSkipped(Entity vehicle)
        {
            m_Admission.ClearBypassHoldSkipped(vehicle);
        }

        internal void MarkBypassHoldSkipped(Entity vehicle, Entity blocker)
        {
            m_Admission.MarkBypassHoldSkipped(vehicle, blocker);
        }

        internal Entity TickExpressVanillaBlockerRescue(Entity vehicle, Entity line, uint nowFrame)
        {
            if (m_Admission.TryFindBypassHeldLocalBlockingExpress(vehicle, line, nowFrame, out Entity localVehicle))
            {
                ClearVehicle(localVehicle, "vanilla-blocker-chain-stall");
                m_Admission.MarkBypassHoldSkipped(localVehicle, vehicle);
                return localVehicle;
            }

            return Entity.Null;
        }

        internal void LogDepartureGate(Entity vehicle, string key, string message)
        {
            ((IControlContext)m_Runtime).LogVehicleStateOnce(m_DepartureGateLogCache, vehicle, key, message);
        }

        internal void FlushProbeLogs(uint nowFrame)
        {
            m_Admission.FlushPerfProbeIfDue(nowFrame);
            m_Admission.FlushLineOrderedProbeIfDue(nowFrame);
        }

        internal void WarmStaticSceneIndex()
        {
            m_Admission.WarmStaticSceneIndex();
        }

        internal void RequestLineOrderedRuntimeForceRefresh(Entity line, string reason)
        {
            m_Admission.RequestLineOrderedRuntimeForceRefresh(line, reason);
        }


        internal GlobalSharedTrunkSnapshot GetGlobalSharedTrunkSnapshotCurrent(LineTrackChain left, LineTrackChain right)
        {
            return m_Admission.GetGlobalSharedTrunkSnapshotCurrent(left, right);
        }

        private void RemoveVehicleLogs(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_DepartureGateLogCache.Remove(vehicle);
            m_ReleaseDiagLogCache.Remove(vehicle);
            m_Control.RemoveVehicleLogs(vehicle);
        }

        internal void RemoveDiagnostics(Entity vehicle)
        {
            RemoveVehicleLogs(vehicle);
        }
    }
}
