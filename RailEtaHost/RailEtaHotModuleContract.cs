using System;
using Unity.Entities;
using Unity.Jobs;

namespace RapidTransitMod.RailEtaHost
{
    public interface IRailEtaHotModule : IDisposable
    {
        string BuildId { get; }
        bool Busy { get; }
        void Attach(RailEtaHotContext context);
        void Submit(RailEtaHotCommand command);
        JobHandle Tick(uint simulationFrame, JobHandle inputDependency);
        void Cancel(long ticket);
        void Clear(int generation);
    }

    public sealed class RailEtaHotContext
    {
        public RailEtaHotContext(
            World world,
            Func<uint> simulationFrame,
            object railTravel,
            RailEtaWorker worker,
            Action<RailEtaPublicResult> publishResult,
            Action<string> log)
        {
            World = world ?? throw new ArgumentNullException(nameof(world));
            SimulationFrame = simulationFrame ?? throw new ArgumentNullException(nameof(simulationFrame));
            RailTravel = railTravel ?? throw new ArgumentNullException(nameof(railTravel));
            Worker = worker ?? throw new ArgumentNullException(nameof(worker));
            PublishResult = publishResult ?? throw new ArgumentNullException(nameof(publishResult));
            Log = log ?? (_ => { });
        }

        public World World { get; }
        public Func<uint> SimulationFrame { get; }
        public object RailTravel { get; }
        public RailEtaWorker Worker { get; }
        public Action<RailEtaPublicResult> PublishResult { get; }
        public Action<string> Log { get; }
    }

    public readonly struct RailEtaHotCommand
    {
        public RailEtaHotCommand(long ticket, int generation, int vehicleIndex, int vehicleVersion, long targetWaypoint)
        {
            Ticket = ticket;
            Generation = generation;
            VehicleIndex = vehicleIndex;
            VehicleVersion = vehicleVersion;
            TargetWaypoint = targetWaypoint;
        }

        public long Ticket { get; }
        public int Generation { get; }
        public int VehicleIndex { get; }
        public int VehicleVersion { get; }
        public long TargetWaypoint { get; }
    }

    public sealed class RailEtaPublicResult
    {
        public long Ticket { get; set; }
        public string State { get; set; } = "Idle";
        public string Failure { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public long TargetVehicle { get; set; }
        public long TargetWaypoint { get; set; }
        public uint EtaFrame { get; set; }
        public string Source { get; set; } = "hot";
        public string Build { get; set; } = string.Empty;
        public long Generation { get; set; }
        public bool Incomplete { get; set; }
        public string ComparisonSummary { get; set; } = string.Empty;
    }
}
