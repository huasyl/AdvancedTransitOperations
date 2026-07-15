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
        private long m_NextTicket;
        private int m_Generation = 1;
        private int m_Disposed;

        public RailEtaBridgeService(RailEtaWorker worker) => m_Worker = worker ?? throw new ArgumentNullException(nameof(worker));
        public static RailEtaBridgeService Current => Volatile.Read(ref s_Current);
        public static void Bind(RailEtaBridgeService service) => Volatile.Write(ref s_Current, service);
        public bool IsDisposed => Volatile.Read(ref m_Disposed) != 0;
        public bool WorkerLost => m_Worker.WorkerLost;

        internal void SetHotRuntime(RailEtaHotRuntime runtime) => m_HotRuntime = runtime;
        internal JobHandle TickHot(uint frame, JobHandle dependency) => m_HotRuntime == null ? dependency : m_HotRuntime.Tick(frame, dependency);

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
                Generation = selection?.Generation ?? 0
            };
            m_Status[ticket.Value] = status;
            if (selection == null)
            {
                status.Failure = "HotModuleUnavailable";
                status.Detail = "Rail ETA hot module is not loaded.";
                return ticket;
            }
            m_HotRuntime.Submit(new RailEtaHotCommand(ticket.Value, checked((int)selection.Generation), descriptor.VehicleIndex, descriptor.VehicleVersion, descriptor.TargetCheckpointId));
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
            status.Source = result.Source ?? string.Empty;
            status.Build = result.Build ?? string.Empty;
            status.Generation = result.Generation;
            status.Incomplete = result.Incomplete;
            if (!String.IsNullOrEmpty(result.ComparisonSummary)) status.ComparisonSummary = result.ComparisonSummary;
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
