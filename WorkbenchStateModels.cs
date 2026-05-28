using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class DispatchWorkbenchDraftState
    {
        public string SelectedLineId = string.Empty;
        public string SelectedEditLine = "local";
        public DispatchWorkbenchMergedView MergedView = new DispatchWorkbenchMergedView
        {
            localLineIds = Array.Empty<string>(),
            expressLineIds = Array.Empty<string>(),
            isLoop = true,
            turnbackStationId = string.Empty,
            direction = "up",
            windowStart = string.Empty,
            windowEnd = string.Empty
        };
        public List<DispatchWorkbenchManualRowDto> ManualRows = new List<DispatchWorkbenchManualRowDto>();
        public List<DispatchWorkbenchAutoRuleDto> AutoRules = new List<DispatchWorkbenchAutoRuleDto>();
        public List<DispatchWorkbenchStagedRowDto> StagedRows = new List<DispatchWorkbenchStagedRowDto>();
        public bool RulesApplied;
        public bool DraftApplied;
        public DispatchWorkbenchPlannerImportContractDto PlannerImportContract;
        public readonly Dictionary<string, int[]> AppliedDepartureMinutesCache =
            new Dictionary<string, int[]>(StringComparer.Ordinal);
    }

    internal sealed class AppliedWorkbenchLineState
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
    }

    internal sealed class WorkbenchRealtimeStopRecord
    {
        public Entity StopEntity;
        public ResolvedStopKind Kind;
        public string ArrivalTime = string.Empty;
        public string DepartureTime = string.Empty;
        public uint LastUpdatedFrame;
    }

    internal sealed class WorkbenchRealtimeTripRecord
    {
        public int Sequence;
        public readonly List<WorkbenchRealtimeStopRecord> Stops = new List<WorkbenchRealtimeStopRecord>();
        public uint LastUpdatedFrame;
    }

    internal sealed class WorkbenchRealtimeVehicleRecord
    {
        public Entity Vehicle;
        public Entity Line;
        public string Kind = "local";
        public int NextSequence = 1;
        public readonly List<WorkbenchRealtimeTripRecord> Trips = new List<WorkbenchRealtimeTripRecord>();
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

    internal readonly struct WorkbenchLineFrameSnapshot
    {
        public readonly Entity Line;
        public readonly uint Frame;
        public readonly string LineId;
        public readonly string LineKey;
        public readonly bool TimetableApplied;
        public readonly string ConfiguredServiceKind;
        public readonly string AppliedServiceKind;
        public readonly string EffectiveServiceKind;

        public WorkbenchLineFrameSnapshot(
            Entity line,
            uint frame,
            string lineId,
            string lineKey,
            bool timetableApplied,
            string configuredServiceKind,
            string appliedServiceKind,
            string effectiveServiceKind)
        {
            Line = line;
            Frame = frame;
            LineId = lineId ?? string.Empty;
            LineKey = lineKey ?? string.Empty;
            TimetableApplied = timetableApplied;
            ConfiguredServiceKind = configuredServiceKind ?? string.Empty;
            AppliedServiceKind = appliedServiceKind ?? string.Empty;
            EffectiveServiceKind = effectiveServiceKind ?? string.Empty;
        }
    }

    internal readonly struct ConfiguredAllowedDepotCacheEntry
    {
        public readonly Entity Line;
        public readonly string LineId;
        public readonly string AllowedDepotId;
        public readonly Entity CanonicalDepot;
        public readonly ulong SettingsVersion;

        public ConfiguredAllowedDepotCacheEntry(
            Entity line,
            string lineId,
            string allowedDepotId,
            Entity canonicalDepot,
            ulong settingsVersion)
        {
            Line = line;
            LineId = lineId ?? string.Empty;
            AllowedDepotId = allowedDepotId ?? string.Empty;
            CanonicalDepot = canonicalDepot;
            SettingsVersion = settingsVersion;
        }
    }

    internal sealed class WorkbenchSavePrepareContext
    {
        public string RequestJson = string.Empty;
        public ulong SnapshotVersion;
        public List<WorkbenchLineRuntime> RuntimeLines = new List<WorkbenchLineRuntime>();
        public List<DispatchWorkbenchDepotDto> Depots = new List<DispatchWorkbenchDepotDto>();
        public Dictionary<string, string> ServiceKinds =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal sealed class PreparedWorkbenchSave
    {
        public ulong SnapshotVersion;
        public DispatchWorkbenchSaveRequest Request;
        public List<WorkbenchLineRuntime> RuntimeLines = new List<WorkbenchLineRuntime>();
        public List<string> Errors = new List<string>();
        public bool ShouldReturnSnapshot;
        public bool LineSettingsChanged;

        public bool HasErrors => Errors != null && Errors.Count > 0;

        public DispatchWorkbenchSaveResult ToResult()
        {
            return new DispatchWorkbenchSaveResult
            {
                success = !HasErrors,
                errors = Errors?.ToArray() ?? Array.Empty<string>(),
                warnings = Array.Empty<string>(),
                version = SnapshotVersion.ToString(),
                appliedLineIds = Array.Empty<string>(),
                snapshot = null
            };
        }
    }

    internal sealed class WorkbenchSavePersistencePayload
    {
        public DispatchWorkbenchPersistentState WorkbenchState;
        public List<AppliedWorkbenchLineStateElement> AppliedLineElements =
            new List<AppliedWorkbenchLineStateElement>();
        public List<AppliedWorkbenchStagedRowElement> AppliedRowElements =
            new List<AppliedWorkbenchStagedRowElement>();
    }

    internal sealed class PreparedWorkbenchSavePersistence
    {
        public List<string> WorkbenchPersistenceChunks = new List<string>();
        public List<AppliedWorkbenchLineStateElement> AppliedLineElements =
            new List<AppliedWorkbenchLineStateElement>();
        public List<AppliedWorkbenchStagedRowElement> AppliedRowElements =
            new List<AppliedWorkbenchStagedRowElement>();
    }

    internal sealed class WorkbenchSaveOperationState
    {
        private readonly object m_Sync = new object();
        private DispatchWorkbenchSaveOperationStatusDto m_Status;

        public WorkbenchSaveOperationState(string operationId, string requestJson, int generation)
        {
            OperationId = operationId ?? string.Empty;
            RequestJson = requestJson ?? string.Empty;
            Generation = generation;
            IsApplyDraft = LooksLikeApplyDraftRequest(RequestJson);
            LastUpdatedUtc = DateTime.UtcNow;
            m_Status = new DispatchWorkbenchSaveOperationStatusDto
            {
                success = true,
                operationId = OperationId,
                state = "queued",
                error = string.Empty,
                result = null
            };
        }

        public string OperationId { get; }

        public string RequestJson { get; }

        public int Generation { get; }

        public bool IsApplyDraft { get; }

        public DateTime LastUpdatedUtc { get; private set; }

        public bool IsTerminal
        {
            get
            {
                lock (m_Sync)
                {
                    return IsTerminalState(m_Status?.state);
                }
            }
        }

        public DispatchWorkbenchSaveOperationStatusDto CreateStatusCopy()
        {
            lock (m_Sync)
            {
                return new DispatchWorkbenchSaveOperationStatusDto
                {
                    success = m_Status?.success ?? false,
                    operationId = m_Status?.operationId ?? string.Empty,
                    state = m_Status?.state ?? string.Empty,
                    error = m_Status?.error ?? string.Empty,
                    result = m_Status?.result
                };
            }
        }

        public void UpdateStatus(
            string state,
            bool success,
            string error,
            DispatchWorkbenchSaveResult result)
        {
            lock (m_Sync)
            {
                m_Status = new DispatchWorkbenchSaveOperationStatusDto
                {
                    success = success,
                    operationId = OperationId,
                    state = state ?? string.Empty,
                    error = error ?? string.Empty,
                    result = result
                };
                LastUpdatedUtc = DateTime.UtcNow;
            }
        }

        private static bool IsTerminalState(string state)
        {
            return string.Equals(state, "completed", StringComparison.Ordinal)
                || string.Equals(state, "failed", StringComparison.Ordinal)
                || string.Equals(state, "missing", StringComparison.Ordinal)
                || string.Equals(state, "superseded", StringComparison.Ordinal);
        }

        private static bool LooksLikeApplyDraftRequest(string requestJson)
        {
            if (string.IsNullOrEmpty(requestJson))
                return false;

            int keyIndex = requestJson.IndexOf("\"applyDraft\"", StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
                return false;

            int colonIndex = requestJson.IndexOf(':', keyIndex);
            if (colonIndex < 0)
                return false;

            int valueIndex = colonIndex + 1;
            while (valueIndex < requestJson.Length && char.IsWhiteSpace(requestJson[valueIndex]))
            {
                valueIndex++;
            }

            return valueIndex + 4 <= requestJson.Length
                && string.Compare(requestJson, valueIndex, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;
        }
    }
}
