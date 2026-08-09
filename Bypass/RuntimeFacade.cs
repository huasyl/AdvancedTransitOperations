using System;
using System.Collections.Generic;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
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
            m_Admission.ClearWatchLine(line);
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

        internal List<Entity> ForgetBlocker(Entity blocker)
        {
            return m_Admission.ForgetBlocker(blocker);
        }

        internal void ClearRescue(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_Admission.ClearRescue(vehicle);
        }

        internal void ClearVehicle(Entity vehicle, string releaseReason = null)
        {
            if (vehicle == Entity.Null)
                return;

            bool hadBypassHoldSkipped = m_Admission.TryGetBypassHoldSkipped(vehicle, out _);
            bool hadEpisode = m_Admission.Get(vehicle, out BypassConflictEpisode _);
            bool hadCadence = m_Admission.Get(vehicle, out BypassHoldCadenceSnapshot _);
            bool hadBlocker = m_Admission.TryGetLatchedBlocker(vehicle, out Entity blocker);
            m_Admission.ClearVehicle(vehicle);
            if (hadBlocker)
                m_Runtime.RecordRelease(vehicle, blocker, releaseReason);
            if (hadBlocker || hadBypassHoldSkipped || hadEpisode || hadCadence)
            {
                ((IControlContext)m_Runtime).RecordBypassFact(new BypassFact(
                    BypassFactKind.Cleared,
                    vehicle,
                    m_Runtime.ResolveLine(vehicle),
                    hadBlocker ? blocker : Entity.Null,
                    -1,
                    false,
                    true,
                    releaseReason));
            }
            if (!hadBlocker)
            {
                RemoveVehicleLogs(vehicle);
                return;
            }
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

        internal BypassControlResult UpdateVehicle(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            bool boarding,
            uint nowFrame)
        {
            return m_Control.Update(
                vehicle,
                line,
                waypoints,
                waypointIndex,
                boarding,
                nowFrame);
        }

        internal void ApplyControl(
            BypassControlResult control,
            bool boarding,
            ref PublicTransport publicTransport,
            EntityCommandBuffer ecb,
            DynamicBuffer<RouteWaypoint> waypoints,
            string lineTag,
            uint nowFrame)
        {
            m_Control.Apply(
                control,
                boarding,
                ref publicTransport,
                ecb,
                waypoints,
                lineTag,
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

        internal void UpdateWatch(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            bool boarding,
            bool sceneKnown,
            bool sceneEligible)
        {
            m_Admission.UpdateWatch(
                vehicle,
                line,
                waypoints,
                waypointIndex,
                boarding,
                sceneKnown,
                sceneEligible);
        }

        internal void HandleStopFact(StopFact fact)
        {
            if (fact.Kind == StopFactKind.Departed
                || fact.Kind == StopFactKind.Cancelled
                || fact.Kind == StopFactKind.Removed)
            {
                ClearVehicle(fact.Vehicle, fact.Reason);
            }
        }

        internal void ClearBypassHoldSkipped(Entity vehicle)
        {
            m_Admission.ClearBypassHoldSkipped(vehicle);
        }

        internal void MarkBypassHoldSkipped(Entity vehicle, Entity blocker)
        {
            m_Admission.MarkBypassHoldSkipped(vehicle, blocker);
        }

        internal bool TryResolveVanillaBlockerRescue(
            Entity expressVehicle,
            Entity line,
            uint nowFrame,
            out Entity localVehicle)
        {
            return m_Admission.TryFindBypassHeldLocalBlockingExpress(
                expressVehicle,
                line,
                nowFrame,
                out localVehicle);
        }

        internal void CommitVanillaBlockerRescue(Entity localVehicle, Entity expressVehicle)
        {
            ClearVehicle(localVehicle, "vanilla-blocker-chain-stall");
            m_Admission.MarkBypassHoldSkipped(localVehicle, expressVehicle);
        }

        internal void ArmExpressRescue(Entity vehicle, Entity line, uint nowFrame)
        {
            m_Admission.ArmVanillaBlockerRescue(vehicle, line, nowFrame);
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
