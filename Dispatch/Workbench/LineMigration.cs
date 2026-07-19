using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal static class LineMigration
    {
        private static DraftStore s_Drafts;

        internal static void Attach(DraftStore drafts)
        {
            s_Drafts = drafts;
        }

        internal static void Run(LineAnchorCatalog catalog, MigrationReport report)
        {
            DraftStore drafts = s_Drafts;
            if (catalog == null || report == null || drafts == null || drafts.Count == 0)
                return;

            MigrateDrafts(drafts, catalog, report);
        }

        internal static HashSet<string> MigrateDrafts(
            DraftStore drafts,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (drafts == null || catalog == null || report == null || drafts.Count == 0)
                return new HashSet<string>(StringComparer.Ordinal);

            HashSet<string> occupiedDrafts = FindOccupiedDrafts(drafts, catalog, report);
            MigrateDraftFields(drafts, catalog, report, occupiedDrafts);
            MigrateDraftKeysAndPreferred(drafts, catalog, report, occupiedDrafts);
            return occupiedDrafts;
        }

        internal static DispatchWorkbenchLineSettingDto[] PromoteLineSettings(
            DispatchWorkbenchLineSettingDto[] settings,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (settings == null || catalog == null)
                return settings;

            bool changed = false;
            HashSet<LineKey> stablePresent = new HashSet<LineKey>();
            for (int i = 0; i < settings.Length; i++)
            {
                LineKey key = LineIdentityService.GetKey(settings[i]?.lineId);
                if (LineKey.IsStableGuidKey(key))
                    stablePresent.Add(key);
            }

            DispatchWorkbenchLineSettingDto[] result = new DispatchWorkbenchLineSettingDto[settings.Length];
            for (int i = 0; i < settings.Length; i++)
            {
                DispatchWorkbenchLineSettingDto setting = settings[i];
                if (setting == null)
                {
                    result[i] = null;
                    continue;
                }

                string promoted = setting.lineId;
                LineKey legacy = LineIdentityService.GetKey(setting.lineId);
                if (LineKey.IsLegacyNumericKey(legacy)
                    && !catalog.IsLegacyConflict(legacy)
                    && catalog.TryLegacy(legacy, out LineKey stable)
                    && stablePresent.Contains(stable))
                {
                    report?.Record("line-settings", legacy, stable, MigrationResult.TargetOccupied);
                }
                else
                {
                    promoted = PromoteLineId(setting.lineId, "line-settings", catalog, report);
                    if (!string.Equals(promoted, setting.lineId, StringComparison.Ordinal))
                        report?.Record("line-settings", legacy, LineIdentityService.GetKey(promoted), MigrationResult.Migrated);
                }
                if (!string.Equals(promoted, setting.lineId, StringComparison.Ordinal))
                    changed = true;

                result[i] = new DispatchWorkbenchLineSettingDto
                {
                    lineId = promoted,
                    originHoldLimitMinutes = setting.originHoldLimitMinutes,
                    maxStationDwellMinutes = setting.maxStationDwellMinutes,
                    allowedDepotId = setting.allowedDepotId,
                    serviceKind = setting.serviceKind
                };
            }

            return changed ? result : settings;
        }

        private static void MigrateDraftFields(
            DraftStore drafts,
            LineAnchorCatalog catalog,
            MigrationReport report,
            HashSet<string> occupiedDrafts)
        {
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in drafts)
            {
                if (occupiedDrafts.Contains(entry.Key))
                    continue;

                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null)
                    continue;

                draft.SelectedLineId = PromoteLineId(draft.SelectedLineId, "selection", catalog, report);
                draft.SelectedEditLine = PromoteReference(draft.SelectedEditLine, "selection", catalog, report);
                PromoteMergedView(draft.MergedView, "merged-view", catalog, report);
                PromoteManualRows(draft.ManualRows, "manual-rows", catalog, report);
                PromoteAutoRules(draft.AutoRules, "auto-rules", catalog, report);
                PromoteStagedRows(draft.StagedRows, "staged-rows", catalog, report);
                PromotePlanContract(draft.PlannerImportContract, "planner-contract", catalog, report);
            }
        }

        private static void MigrateDraftKeysAndPreferred(
            DraftStore drafts,
            LineAnchorCatalog catalog,
            MigrationReport report,
            HashSet<string> occupiedDrafts)
        {
            string savedPreferred = drafts.GetPreferredLineId();
            List<KeyValuePair<TransitMode, string>> savedModePreferreds =
                drafts.GetPreferredLineIdsByMode().ToList();

            List<KeyValuePair<string, DispatchWorkbenchDraftState>> snapshot = drafts.ToList();
            for (int i = 0; i < snapshot.Count; i++)
            {
                string oldKey = snapshot[i].Key;
                if (string.IsNullOrEmpty(oldKey)
                    || string.Equals(oldKey, "__default__", StringComparison.Ordinal)
                    || occupiedDrafts.Contains(oldKey))
                    continue;

                string newKey = PromoteLineId(oldKey, "draft-key", catalog, report);
                if (string.Equals(newKey, oldKey, StringComparison.Ordinal))
                    continue;

                if (drafts.TryGetValue(newKey, out _))
                {
                    RecordOccupied("draft-key", oldKey, newKey, report);
                    continue;
                }

                DispatchWorkbenchDraftState draft = snapshot[i].Value;
                drafts.Remove(oldKey);
                drafts[newKey] = draft;
            }

            if (!string.IsNullOrEmpty(savedPreferred))
            {
                string promotedPreferred = PromoteLineId(savedPreferred, "preferred", catalog, report);
                if (!string.Equals(promotedPreferred, savedPreferred, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(drafts.GetPreferredLineId()))
                {
                    drafts.SetPreferredLineId(promotedPreferred);
                }
            }

            for (int i = 0; i < savedModePreferreds.Count; i++)
            {
                TransitMode mode = savedModePreferreds[i].Key;
                string oldId = savedModePreferreds[i].Value;
                if (string.IsNullOrEmpty(oldId))
                    continue;

                string newId = PromoteLineId(oldId, "preferred", catalog, report);
                if (!string.Equals(newId, oldId, StringComparison.Ordinal))
                {
                    drafts.SetPreferredLineId(mode, newId);
                }
            }
        }

        private static HashSet<string> FindOccupiedDrafts(
            DraftStore drafts,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            HashSet<string> occupied = new HashSet<string>(StringComparer.Ordinal);
            foreach (string oldKey in drafts.Keys.ToList())
            {
                LineKey legacy = LineIdentityService.GetKey(oldKey);
                if (!LineKey.IsLegacyNumericKey(legacy)
                    || catalog.IsLegacyConflict(legacy)
                    || !catalog.TryLegacy(legacy, out LineKey stable))
                {
                    continue;
                }

                string stableId = LineIdentityService.GetId(stable);
                if (!drafts.TryGetValue(stableId, out _))
                    continue;

                occupied.Add(oldKey);
                report.Record("draft-key", legacy, stable, MigrationResult.TargetOccupied);
            }

            return occupied;
        }

        private static string PromoteLineId(
            string lineId,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return string.Empty;

            LineKey key = LineIdentityService.GetKey(lineId);
            if (key.IsEmpty || LineKey.IsStableGuidKey(key))
                return lineId;

            if (!LineKey.IsLegacyNumericKey(key))
                return lineId;

            if (catalog.IsLegacyConflict(key))
            {
                report.Record(domain, key, LineKey.Empty, MigrationResult.LegacyConflict);
                return lineId;
            }

            if (catalog.TryLegacy(key, out LineKey stable))
            {
                report?.Record(domain, key, stable, MigrationResult.Migrated);
                return LineIdentityService.GetId(stable);
            }

            report.Record(domain, key, LineKey.Empty, MigrationResult.ZeroMatch);
            return lineId;
        }

        private static void RecordOccupied(
            string domain,
            string oldId,
            string newId,
            MigrationReport report)
        {
            report.Record(
                domain,
                LineIdentityService.GetKey(oldId),
                LineIdentityService.GetKey(newId),
                MigrationResult.TargetOccupied);
        }

        private static string PromoteReference(
            string lineId,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (string.Equals(lineId, "local", StringComparison.Ordinal)
                || string.Equals(lineId, "express", StringComparison.Ordinal)
                || string.Equals(lineId, "__default__", StringComparison.Ordinal))
                return lineId;

            return PromoteLineId(lineId, domain, catalog, report);
        }

        private static void PromoteMergedView(
            DispatchWorkbenchMergedView view,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (view == null)
                return;

            view.localLineId = PromoteLineId(view.localLineId, domain, catalog, report);
            view.expressLineId = PromoteLineId(view.expressLineId, domain, catalog, report);
            view.localLineIds = PromoteLineIds(view.localLineIds, domain, catalog, report);
            view.expressLineIds = PromoteLineIds(view.expressLineIds, domain, catalog, report);
        }

        private static string[] PromoteLineIds(
            string[] lineIds,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (lineIds == null || lineIds.Length == 0)
                return lineIds ?? Array.Empty<string>();

            bool changed = false;
            string[] normalized = new string[lineIds.Length];
            for (int i = 0; i < lineIds.Length; i++)
            {
                string original = lineIds[i] ?? string.Empty;
                normalized[i] = PromoteLineId(original, domain, catalog, report);
                if (!string.Equals(normalized[i], original, StringComparison.Ordinal))
                    changed = true;
            }

            return changed ? normalized : lineIds;
        }

        private static void PromoteManualRows(
            List<DispatchWorkbenchManualRowDto> rows,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null)
                    rows[i].lineId = PromoteLineId(rows[i].lineId, domain, catalog, report);
            }
        }

        private static void PromoteAutoRules(
            List<DispatchWorkbenchAutoRuleDto> rules,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (rules == null)
                return;

            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i] != null)
                    rules[i].lineId = PromoteLineId(rules[i].lineId, domain, catalog, report);
            }
        }

        private static void PromoteStagedRows(
            List<DispatchWorkbenchStagedRowDto> rows,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null)
                    rows[i].lineId = PromoteLineId(rows[i].lineId, domain, catalog, report);
            }
        }

        private static void PromotePlanContract(
            DispatchWorkbenchPlannerImportContractDto contract,
            string domain,
            LineAnchorCatalog catalog,
            MigrationReport report)
        {
            if (contract == null)
                return;

            contract.draftKey = PromoteLineId(contract.draftKey, domain, catalog, report);
            contract.importedLineIds = PromoteLineIds(contract.importedLineIds, domain, catalog, report);

            DispatchPlannerRequestEchoDto echo = contract.requestEcho;
            if (echo == null)
                return;

            echo.draftKey = PromoteLineId(echo.draftKey, domain, catalog, report);
            echo.localLineIds = PromoteLineIds(echo.localLineIds, domain, catalog, report);
            echo.adjustableLineIds = PromoteLineIds(echo.adjustableLineIds, domain, catalog, report);
            echo.expressLineId = PromoteLineId(echo.expressLineId, domain, catalog, report);
            echo.virtualExpressBaseLineId = PromoteLineId(echo.virtualExpressBaseLineId, domain, catalog, report);
        }
    }
}
