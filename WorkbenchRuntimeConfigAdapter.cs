using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class WorkbenchRuntimeConfigAdapter
    {
        private readonly AppliedTimetableStore m_AppliedTimetables;
        private readonly LineSettingsStore m_LineSettings;
        private readonly AppliedTimetableValidator m_Validator;
        private readonly Func<string, LineKey> m_GetLineKey;
        private readonly Func<Entity, string, LineKey> m_GetEntityLineKey;
        private readonly Func<LineKey, string> m_GetLineId;
        private readonly Func<int, int> m_NormalizeOriginHoldLimit;
        private readonly Func<int, int> m_NormalizeMaxStationDwell;
        private readonly Func<string, string> m_NormalizeAllowedDepotId;
        private readonly Func<string, string> m_NormalizeServiceKind;
        private readonly Func<IEnumerable<DispatchWorkbenchStagedRowDto>, string, int[]> m_BuildDepartureMinutes;
        private readonly Func<string, int> m_ParseTimeMinutes;
        private readonly Action<string> m_LogInfo;

        public WorkbenchRuntimeConfigAdapter(
            AppliedTimetableStore appliedTimetables,
            LineSettingsStore lineSettings,
            AppliedTimetableValidator validator,
            Func<string, LineKey> getLineKey,
            Func<Entity, string, LineKey> getEntityLineKey,
            Func<LineKey, string> getLineId,
            Func<int, int> normalizeOriginHoldLimit,
            Func<int, int> normalizeMaxStationDwell,
            Func<string, string> normalizeAllowedDepotId,
            Func<string, string> normalizeServiceKind,
            Func<IEnumerable<DispatchWorkbenchStagedRowDto>, string, int[]> buildDepartureMinutes,
            Func<string, int> parseTimeMinutes,
            Action<string> logInfo)
        {
            m_AppliedTimetables = appliedTimetables ?? throw new ArgumentNullException(nameof(appliedTimetables));
            m_LineSettings = lineSettings ?? throw new ArgumentNullException(nameof(lineSettings));
            m_Validator = validator ?? throw new ArgumentNullException(nameof(validator));
            m_GetLineKey = getLineKey ?? throw new ArgumentNullException(nameof(getLineKey));
            m_GetEntityLineKey = getEntityLineKey ?? throw new ArgumentNullException(nameof(getEntityLineKey));
            m_GetLineId = getLineId ?? throw new ArgumentNullException(nameof(getLineId));
            m_NormalizeOriginHoldLimit = normalizeOriginHoldLimit ?? throw new ArgumentNullException(nameof(normalizeOriginHoldLimit));
            m_NormalizeMaxStationDwell = normalizeMaxStationDwell ?? throw new ArgumentNullException(nameof(normalizeMaxStationDwell));
            m_NormalizeAllowedDepotId = normalizeAllowedDepotId ?? throw new ArgumentNullException(nameof(normalizeAllowedDepotId));
            m_NormalizeServiceKind = normalizeServiceKind ?? throw new ArgumentNullException(nameof(normalizeServiceKind));
            m_BuildDepartureMinutes = buildDepartureMinutes ?? throw new ArgumentNullException(nameof(buildDepartureMinutes));
            m_ParseTimeMinutes = parseTimeMinutes ?? throw new ArgumentNullException(nameof(parseTimeMinutes));
            m_LogInfo = logInfo;
        }

        public string GetLineId(LineKey lineKey)
        {
            return m_GetLineId(lineKey);
        }

        public string GetLineId(string lineId, TransitMode mode)
        {
            return LineIdentityService.NormalizeForMode(lineId, mode);
        }

        public LineKey GetLineKey(string lineId)
        {
            return m_GetLineKey(lineId);
        }

        public LineKey GetLineKey(string lineId, TransitMode mode)
        {
            return LineIdentityService.GetKey(lineId, mode);
        }

        public LineKey GetLineKey(Entity line, string fallbackLineId = null)
        {
            return m_GetEntityLineKey(line, fallbackLineId);
        }

        public LineKey GetLineKey(Entity line, TransitMode mode, string fallbackLineId = null)
        {
            return GetLineKey(line, fallbackLineId).NormalizeForMode(mode);
        }

        public bool PromoteLegacy(TransitMode mode, string lineId)
        {
            bool changed = m_AppliedTimetables.PromoteLegacy(mode, lineId);
            if (m_LineSettings.PromoteLegacy(mode, lineId))
                changed = true;

            return changed;
        }

        public IEnumerable<string> EnumerateSettingIds(
            IReadOnlyDictionary<string, int> originHoldLimits,
            IReadOnlyDictionary<string, int> maxStationDwellMinutes,
            IReadOnlyDictionary<string, string> allowedDepots,
            IReadOnlyDictionary<string, string> serviceKinds)
        {
            HashSet<string> lineIds = new HashSet<string>(StringComparer.Ordinal);
            if (originHoldLimits != null)
                lineIds.UnionWith(originHoldLimits.Keys);
            if (maxStationDwellMinutes != null)
                lineIds.UnionWith(maxStationDwellMinutes.Keys);
            if (allowedDepots != null)
                lineIds.UnionWith(allowedDepots.Keys);
            if (serviceKinds != null)
                lineIds.UnionWith(serviceKinds.Keys);

            foreach (KeyValuePair<LineKey, LineSettingsState> entry in m_LineSettings.GetAll())
            {
                string lineId = GetLineId(entry.Key);
                if (!string.IsNullOrEmpty(lineId))
                {
                    lineIds.Add(lineId);
                }
            }

            return lineIds;
        }

        public ulong SyncSettings(
            IReadOnlyDictionary<string, int> originHoldLimits,
            IReadOnlyDictionary<string, int> maxStationDwellMinutes,
            IReadOnlyDictionary<string, string> allowedDepots,
            IReadOnlyDictionary<string, string> serviceKinds,
            ulong currentVersion)
        {
            m_LineSettings.Clear();
            foreach (string lineId in EnumerateSettingIds(
                originHoldLimits,
                maxStationDwellMinutes,
                allowedDepots,
                serviceKinds).OrderBy(value => value, StringComparer.Ordinal))
            {
                LineKey key = GetLineKey(lineId);
                if (key.IsEmpty)
                    continue;

                m_LineSettings.Set(key, new LineSettingsState
                {
                    OriginHoldLimitMinutes = originHoldLimits != null
                        && originHoldLimits.TryGetValue(lineId, out int holdLimit)
                        ? m_NormalizeOriginHoldLimit(holdLimit)
                        : RuntimeConfigStoreDefaults.DefaultOriginHoldLimitMinutes,
                    MaxStationDwellMinutes = maxStationDwellMinutes != null
                        && maxStationDwellMinutes.TryGetValue(lineId, out int dwellLimit)
                        ? m_NormalizeMaxStationDwell(dwellLimit)
                        : RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes,
                    AllowedDepotId = allowedDepots != null
                        && allowedDepots.TryGetValue(lineId, out string depotId)
                        ? m_NormalizeAllowedDepotId(depotId)
                        : string.Empty,
                    ConfiguredServiceKind = serviceKinds != null
                        && serviceKinds.TryGetValue(lineId, out string serviceKind)
                        ? m_NormalizeServiceKind(serviceKind)
                        : string.Empty
                });
            }

            return Math.Max(currentVersion, m_LineSettings.Version);
        }

        public void SyncApplied(IReadOnlyDictionary<string, AppliedWorkbenchLineState> appliedLines)
        {
            m_AppliedTimetables.Clear();
            if (appliedLines == null)
                return;

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in appliedLines)
            {
                SyncApplied(entry.Key, entry.Value);
            }
        }

        public void SyncApplied(string lineId, AppliedWorkbenchLineState applied)
        {
            LineKey key = GetLineKey(lineId);
            if (key.IsEmpty)
            {
                key = GetLineKey(applied?.LineEntity ?? Entity.Null, lineId);
            }

            if (key.IsEmpty)
                return;

            AppliedTimetableState state = BuildAppliedState(lineId, applied);
            if (state == null || !state.Managed)
            {
                m_AppliedTimetables.Clear(key);
                return;
            }

            AppliedTimetableValidationResult validation = m_Validator.Validate(key, state);
            if (!validation.IsValid)
            {
                m_LogInfo?.Invoke(
                    "[AppliedTimetableStoreSync] line="
                    + lineId
                    + " errors="
                    + string.Join(",", validation.Errors));
            }

            m_AppliedTimetables.Apply(key, state);
        }

        public AppliedTimetableState BuildAppliedState(string lineId, AppliedWorkbenchLineState applied)
        {
            if (applied == null || applied.StagedRows == null || applied.StagedRows.Count == 0)
                return AppliedTimetableState.Empty();

            int[] departureMinutes = applied.DepartureMinutesCache != null && applied.DepartureMinutesCache.Length > 0
                ? applied.DepartureMinutesCache.ToArray()
                : m_BuildDepartureMinutes(applied.StagedRows, lineId);
            return new AppliedTimetableState
            {
                Managed = true,
                DepartureMinutes = departureMinutes,
                ServiceKind = GetAppliedKind(applied),
                AppliedRows = BuildAppliedRows(applied.StagedRows, lineId)
            };
        }

        public AppliedTimetableRow[] BuildAppliedRows(
            IEnumerable<DispatchWorkbenchStagedRowDto> rows,
            string lineId)
        {
            if (rows == null)
                return Array.Empty<AppliedTimetableRow>();

            return rows
                .Where(row => row != null
                    && (string.IsNullOrEmpty(row.lineId)
                        || string.Equals(row.lineId, lineId, StringComparison.Ordinal)))
                .Select(row => new
                {
                    Row = row,
                    DepartureMinute = m_ParseTimeMinutes(row.time)
                })
                .Where(item => item.DepartureMinute >= 0)
                .OrderBy(item => item.DepartureMinute)
                .ThenBy(item => item.Row.id ?? string.Empty, StringComparer.Ordinal)
                .Select(item => new AppliedTimetableRow
                {
                    RowId = item.Row.id ?? string.Empty,
                    DepartureMinute = item.DepartureMinute,
                    ServiceKind = item.Row.kind ?? string.Empty,
                    OriginKey = string.Empty,
                    Source = item.Row.source ?? string.Empty
                })
                .ToArray();
        }

        private static string GetAppliedKind(AppliedWorkbenchLineState applied)
        {
            if (applied == null || applied.StagedRows == null || applied.StagedRows.Count == 0)
                return string.Empty;

            bool sawExpress = false;
            bool sawLocal = false;
            for (int i = 0; i < applied.StagedRows.Count; i++)
            {
                string kind = applied.StagedRows[i]?.kind;
                if (string.Equals(kind, RuntimeConfigStoreDefaults.ExpressServiceKind, StringComparison.Ordinal))
                {
                    sawExpress = true;
                }
                else
                {
                    sawLocal = true;
                }

                if (sawExpress && sawLocal)
                    return RuntimeConfigStoreDefaults.LocalServiceKind;
            }

            return sawExpress
                ? RuntimeConfigStoreDefaults.ExpressServiceKind
                : RuntimeConfigStoreDefaults.LocalServiceKind;
        }
    }
}
