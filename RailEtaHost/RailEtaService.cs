using System;
using System.Collections.Concurrent;
using System.Threading;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Jobs;

namespace RapidTransitMod.RailEtaHost
{
    internal sealed class RailEtaBridgeService : IDisposable
    {
        private static RailEtaBridgeService s_Current;
        private readonly ConcurrentDictionary<long, RailEtaPublicStatus> m_Status = new ConcurrentDictionary<long, RailEtaPublicStatus>();
        private readonly ConcurrentDictionary<long, ulong> m_LatestSourceGenerations = new ConcurrentDictionary<long, ulong>();
        private readonly ConcurrentDictionary<long, ulong> m_RuntimeFactGenerations = new ConcurrentDictionary<long, ulong>();
        private readonly RailEtaWorker m_Worker;
        private readonly Func<ClockSnapshot> m_ClockSnapshot;
        private RailEtaHotRuntime m_HotRuntime;
        private RailEtaPublicResult m_LastAppliedResult;
        private long m_LastTerminalTicket;
        private long m_NextTicket;
        private int m_Generation = 1;
        private int m_Disposed;

        public RailEtaBridgeService(RailEtaWorker worker, Func<ClockSnapshot> clockSnapshot)
        {
            m_Worker = worker ?? throw new ArgumentNullException(nameof(worker));
            m_ClockSnapshot = clockSnapshot ?? throw new ArgumentNullException(nameof(clockSnapshot));
        }
        public static RailEtaBridgeService Current => Volatile.Read(ref s_Current);
        public static void Bind(RailEtaBridgeService service) => Volatile.Write(ref s_Current, service);
        public bool IsDisposed => Volatile.Read(ref m_Disposed) != 0;
        public bool WorkerLost => m_Worker.WorkerLost;
        internal bool CanSubmit => !IsDisposed && !WorkerLost && m_HotRuntime?.Current != null && !m_HotRuntime.ModuleBusy;
        internal long HotGeneration => m_HotRuntime?.Current?.Generation ?? 0;
        internal string HotBuildId => m_HotRuntime?.Current?.BuildId ?? string.Empty;

        internal void SetHotRuntime(RailEtaHotRuntime runtime) => m_HotRuntime = runtime;
        internal JobHandle TickHot(uint frame, JobHandle dependency)
        {
            if (m_HotRuntime == null) return dependency;
            ConsumePublishedResult();
            RefreshTerminalStatus();
            if (!m_HotRuntime.NeedsTick) return dependency;

            JobHandle output = m_HotRuntime.Tick(frame, dependency);
            ConsumePublishedResult();
            RefreshTerminalStatus();
            return output;
        }

        private void ConsumePublishedResult()
        {
            RailEtaPublicResult result = ModRuntimeHostSystem.Instance?.LastRailEtaPublicResult;
            if (result != null && !ReferenceEquals(result, m_LastAppliedResult))
            {
                m_LastAppliedResult = result;
                bool accepted = false;
                if (m_Status.TryGetValue(result.Ticket, out RailEtaPublicStatus publishedStatus)
                    && publishedStatus.ClockEpoch == m_ClockSnapshot().ClockEpoch
                    && !IsTerminal(publishedStatus.State))
                {
                    Apply(publishedStatus, result);
                    accepted = true;
                }
                if (accepted && IsTerminal(result.State)) m_LastTerminalTicket = result.Ticket;
            }
        }

        private void RefreshTerminalStatus()
        {
            long terminalTicket = Volatile.Read(ref m_LastTerminalTicket);
            if (terminalTicket != 0 && m_Status.TryGetValue(terminalTicket, out RailEtaPublicStatus terminalStatus)
                && m_HotRuntime.TryGetComparisonSummary(terminalTicket, out string summary))
                terminalStatus.ComparisonSummary = summary;
            if (terminalTicket != 0 && !m_HotRuntime.NeedsTick)
                Interlocked.CompareExchange(ref m_LastTerminalTicket, 0, terminalTicket);
        }

        public RailEtaPublicTicket RequestEta(RailEtaPublicRequest descriptor)
        {
            if (IsDisposed || WorkerLost) return default;
            RailEtaPublicTicket ticket = new RailEtaPublicTicket(Interlocked.Increment(ref m_NextTicket));
            RailEtaHotRuntime.Selection selection = m_HotRuntime?.Current;
            var status = new RailEtaPublicStatus
            {
                Ticket = ticket,
                State = selection == null ? "Unavailable" : "Queued",
                TargetVehicle = ((long)(uint)descriptor.VehicleIndex << 32) | (uint)descriptor.VehicleVersion,
                TargetWaypoint = descriptor.TargetCheckpointId,
                Mode = descriptor.Mode,
                Generation = selection?.Generation ?? 0,
                ClockEpoch = m_ClockSnapshot().ClockEpoch,
                RuntimeFactGeneration = m_RuntimeFactGenerations.GetOrAdd(
                    ((long)(uint)descriptor.VehicleIndex << 32) | (uint)descriptor.VehicleVersion,
                    0UL)
            };
            m_Status[ticket.Value] = status;
            if (selection == null)
            {
                status.Failure = "HotModuleUnavailable";
                status.Detail = "Rail ETA hot module is not loaded.";
                return ticket;
            }
            if (!m_HotRuntime.Submit(new RailEtaHotCommand(ticket.Value, checked((int)selection.Generation), descriptor.VehicleIndex,
                descriptor.VehicleVersion, descriptor.TargetCheckpointId, descriptor.Mode, descriptor.DepotIndex,
                descriptor.DepotVersion, descriptor.ModelIndex, descriptor.ModelVersion,
                descriptor.SecondaryModelIndex, descriptor.SecondaryModelVersion)))
            {
                status.State = "Busy";
                status.Failure = "Busy";
                status.Detail = "Rail ETA hot reload is active.";
            }
            return ticket;
        }

        public bool TryGetState(RailEtaPublicTicket ticket, out RailEtaPublicStatus status)
        {
            if (!m_Status.TryGetValue(ticket.Value, out status)) return false;
            RailEtaPublicResult result = ModRuntimeHostSystem.Instance?.LastRailEtaPublicResult;
            if (result != null && result.Ticket == ticket.Value
                && status.ClockEpoch == m_ClockSnapshot().ClockEpoch
                && !IsTerminal(status.State)) Apply(status, result);
            if (m_HotRuntime != null && m_HotRuntime.TryGetComparisonSummary(ticket.Value, out string summary))
                status.ComparisonSummary = summary;
            return true;
        }

        public bool Cancel(RailEtaPublicTicket ticket)
        {
            if (!m_Status.TryGetValue(ticket.Value, out RailEtaPublicStatus status)) return false;
            m_HotRuntime?.Cancel(ticket.Value);
            status.State = "Cancelled";
            status.Failure = "Cancelled";
            return true;
        }

        internal void InvalidateRuntimeFact(Unity.Entities.Entity vehicle, VehicleFactKind kind, ulong sourceGeneration)
        {
            InvalidateRuntimeFact(vehicle, IsRelevant(kind), sourceGeneration, kind.ToString());
        }

        internal void InvalidateRuntimeFact(Unity.Entities.Entity vehicle, StopFactKind kind, ulong sourceGeneration)
        {
            InvalidateRuntimeFact(vehicle, IsRelevant(kind), sourceGeneration, kind.ToString());
        }

        internal void InvalidateRuntimeFact(Unity.Entities.Entity vehicle, BypassFactKind kind, ulong sourceGeneration)
        {
            InvalidateRuntimeFact(vehicle, IsRelevant(kind), sourceGeneration, kind.ToString());
        }

        internal void InvalidateRuntimeFact(Unity.Entities.Entity vehicle, DispatchFactKind kind, ulong sourceGeneration)
        {
            InvalidateRuntimeFact(vehicle, IsRelevant(kind), sourceGeneration, kind.ToString());
        }

        internal void InvalidateRebind(Unity.Entities.Entity vehicle, ulong sourceGeneration)
        {
            if (vehicle == Unity.Entities.Entity.Null)
                return;

            long targetVehicle = ((long)(uint)vehicle.Index << 32) | (uint)vehicle.Version;
            m_LatestSourceGenerations.AddOrUpdate(
                targetVehicle,
                sourceGeneration,
                (_, current) => current > sourceGeneration ? current : sourceGeneration);
            InvalidateRuntimeFactGeneration(targetVehicle, "Rebound");
        }

        public void ResetCity()
        {
            int generation = Interlocked.Increment(ref m_Generation);
            m_Status.Clear();
            m_LatestSourceGenerations.Clear();
            m_RuntimeFactGenerations.Clear();
            Interlocked.Exchange(ref m_LastTerminalTicket, 0);
            m_HotRuntime?.Clear(generation);
        }

        internal void OnClockChanged(ClockSnapshot oldClockSnapshot, ClockSnapshot newClockSnapshot)
        {
            foreach (RailEtaPublicStatus status in m_Status.Values)
            {
                if (status.ClockEpoch != oldClockSnapshot.ClockEpoch || IsTerminal(status.State)) continue;
                m_HotRuntime?.Cancel(status.Ticket.Value);
                status.State = "ClockChanged";
                status.Failure = "ClockChanged";
                status.Detail = "Rail ETA request clock epoch changed before completion.";
            }
        }

        private void InvalidateRuntimeFact(
            Unity.Entities.Entity vehicle,
            bool relevant,
            ulong sourceGeneration,
            string factKind)
        {
            if (vehicle == Unity.Entities.Entity.Null || !relevant)
                return;

            long targetVehicle = ((long)(uint)vehicle.Index << 32) | (uint)vehicle.Version;
            if (sourceGeneration != 0UL)
            {
                ulong latest = m_LatestSourceGenerations.GetOrAdd(targetVehicle, 0UL);
                if (sourceGeneration < latest)
                    return;
                m_LatestSourceGenerations.AddOrUpdate(targetVehicle, sourceGeneration,
                    (_, current) => current > sourceGeneration ? current : sourceGeneration);
            }

            InvalidateRuntimeFactGeneration(targetVehicle, factKind);
        }

        private void InvalidateRuntimeFactGeneration(long targetVehicle, string factKind)
        {
            ulong currentGeneration = m_RuntimeFactGenerations.AddOrUpdate(
                targetVehicle,
                1UL,
                (_, current) => current + 1UL);
            foreach (RailEtaPublicStatus status in m_Status.Values)
            {
                if (status.TargetVehicle != targetVehicle
                    || IsTerminal(status.State)
                    || status.RuntimeFactGeneration >= currentGeneration)
                {
                    continue;
                }

                m_HotRuntime?.Cancel(status.Ticket.Value);
                status.State = "Cancelled";
                status.Failure = "RuntimeFactChanged";
                status.Detail = "Rail runtime fact changed: " + factKind + ".";
            }
        }

        private static bool IsRelevant(VehicleFactKind kind)
        {
            return kind == VehicleFactKind.Registered
                || kind == VehicleFactKind.Rebound
                || kind == VehicleFactKind.Removed
                || kind == VehicleFactKind.Boarding
                || kind == VehicleFactKind.Target
                || kind == VehicleFactKind.Route
                || kind == VehicleFactKind.Waypoint
                || kind == VehicleFactKind.Moving
                || kind == VehicleFactKind.PathReady
                || kind == VehicleFactKind.OriginRange;
        }

        private static bool IsRelevant(StopFactKind kind) => kind != StopFactKind.None;

        private static bool IsRelevant(BypassFactKind kind)
        {
            return kind == BypassFactKind.Held
                || kind == BypassFactKind.Released
                || kind == BypassFactKind.Cleared
                || kind == BypassFactKind.Expired
                || kind == BypassFactKind.Rescued;
        }

        private static bool IsRelevant(DispatchFactKind kind)
        {
            return kind == DispatchFactKind.State
                || kind == DispatchFactKind.Target
                || kind == DispatchFactKind.Slot
                || kind == DispatchFactKind.LaunchConfirmed
                || kind == DispatchFactKind.UnplannedRun
                || kind == DispatchFactKind.RetireRequested
                || kind == DispatchFactKind.PathFault
                || kind == DispatchFactKind.RunningRecovery;
        }

        private static void Apply(RailEtaPublicStatus status, RailEtaPublicResult result)
        {
            status.State = result.State ?? string.Empty;
            status.Failure = result.Failure ?? string.Empty;
            status.Detail = result.Detail ?? string.Empty;
            status.TargetVehicle = result.TargetVehicle;
            status.TargetWaypoint = result.TargetWaypoint;
            status.EtaFrame = result.EtaFrame;
            status.OriginFrame = result.OriginFrame;
            status.Source = result.Source ?? string.Empty;
            status.Build = result.Build ?? string.Empty;
            status.Generation = result.Generation;
            status.Incomplete = result.Incomplete;
            status.Mode = result.Mode;
            if (!String.IsNullOrEmpty(result.ComparisonSummary)) status.ComparisonSummary = result.ComparisonSummary;
        }

        private static bool IsTerminal(string state)
        {
            return state == "Completed" || state == "Incomplete" || state == "Failed" || state == "Cancelled"
                || state == "NotConverged" || state == "Unavailable" || state == "Busy"
                || state == "ClockChanged";
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_Disposed, 1) != 0) return;
            m_HotRuntime?.Dispose();
            if (ReferenceEquals(Current, this)) Volatile.Write(ref s_Current, null);
            m_Worker.Dispose();
        }
    }

#if RT_DEBUG_TOOLS
    internal static class RailEtaDebugApi
    {
        // Selection UI deliberately stays on the default Full mode. Compact modes are backend
        // call-site choices (for example depot diagnostics), not user-facing UI state.
        public static RailEtaPublicTicket RequestSnapshot(int vehicleIndex, int vehicleVersion, long checkpointId = 0)
            => RailEtaBridgeService.Current?.RequestEta(new RailEtaPublicRequest(vehicleIndex, vehicleVersion, checkpointId)) ?? default;

        public static bool TryGetState(RailEtaPublicTicket ticket, out RailEtaPublicStatus status)
        {
            RailEtaBridgeService service = RailEtaBridgeService.Current;
            if (service != null) return service.TryGetState(ticket, out status);
            status = null;
            return false;
        }
    }
#endif
}
