using System;
using System.Collections.Concurrent;
using System.Threading;
using Unity.Jobs;

namespace RapidTransitMod.RailEtaHost
{
    internal sealed class RailEtaBridgeService : IDisposable
    {
        private static RailEtaBridgeService s_Current;
        private readonly ConcurrentDictionary<long, RailEtaPublicStatus> m_Status = new ConcurrentDictionary<long, RailEtaPublicStatus>();
        private readonly RailEtaWorker m_Worker;
        private RailEtaHotRuntime m_HotRuntime;
        private RailEtaPublicResult m_LastAppliedResult;
        private long m_LastTerminalTicket;
        private long m_NextTicket;
        private int m_Generation = 1;
        private int m_Disposed;

        public RailEtaBridgeService(RailEtaWorker worker) => m_Worker = worker ?? throw new ArgumentNullException(nameof(worker));
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
            JobHandle output = m_HotRuntime.Tick(frame, dependency);
            RailEtaPublicResult result = DispatchRuntimeSystem.Instance?.LastRailEtaPublicResult;
            if (result != null && !ReferenceEquals(result, m_LastAppliedResult))
            {
                m_LastAppliedResult = result;
                if (m_Status.TryGetValue(result.Ticket, out RailEtaPublicStatus publishedStatus)) Apply(publishedStatus, result);
                if (IsTerminal(result.State)) m_LastTerminalTicket = result.Ticket;
            }
            long terminalTicket = Volatile.Read(ref m_LastTerminalTicket);
            if (terminalTicket != 0 && m_Status.TryGetValue(terminalTicket, out RailEtaPublicStatus terminalStatus)
                && m_HotRuntime.TryGetComparisonSummary(terminalTicket, out string summary))
                terminalStatus.ComparisonSummary = summary;
            return output;
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
                Generation = selection?.Generation ?? 0
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
            RailEtaPublicResult result = DispatchRuntimeSystem.Instance?.LastRailEtaPublicResult;
            if (result != null && result.Ticket == ticket.Value) Apply(status, result);
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

        public void ResetCity()
        {
            int generation = Interlocked.Increment(ref m_Generation);
            m_Status.Clear();
            Interlocked.Exchange(ref m_LastTerminalTicket, 0);
            m_HotRuntime?.Clear(generation);
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
                || state == "NotConverged" || state == "Unavailable" || state == "Busy";
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
