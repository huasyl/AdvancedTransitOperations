using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod
{
    // Shared dispatch contracts consumed outside the dispatch workbench module.
    internal sealed class AppliedLine
    {
        public Entity LineEntity = Entity.Null;
        public int OriginHoldLimitMinutes = RuntimeConfigStoreDefaults.DefaultOriginHoldLimitMinutes;
        public int MaxStationDwellMinutes = RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes;
        public List<DispatchWorkbenchStagedRowDto> AppliedRows = new List<DispatchWorkbenchStagedRowDto>();

        public List<DispatchWorkbenchStagedRowDto> StagedRows
        {
            get => AppliedRows;
            set => AppliedRows = value ?? new List<DispatchWorkbenchStagedRowDto>();
        }

        public int[] DepartureMinutesCache = Array.Empty<int>();
    }

    internal sealed class WorkbenchLineRuntime
    {
        public Entity Entity;
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Kind = "local";
        public string TransportType = string.Empty;
        public int RouteNumber = int.MaxValue;
        public int StationCount;
        public string Color = string.Empty;
        public string OriginStationId = string.Empty;
        public string OriginStationName = string.Empty;
        public bool DispatchSupported = true;
        public string UnsupportedReason = string.Empty;
        public string OriginStatus = string.Empty;
        public string OriginMessageKey = string.Empty;
    }

    internal enum ResolvedStopKind
    {
        Stop = 0,
        Building = 1
    }

    internal readonly struct StopRef
    {
        public readonly Entity Ent;
        public readonly ResolvedStopKind Kind;

        public StopRef(Entity ent, ResolvedStopKind kind)
        {
            Ent = ent;
            Kind = kind;
        }
    }

}
