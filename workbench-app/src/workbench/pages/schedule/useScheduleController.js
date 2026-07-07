import { useEffect, useMemo, useRef, useState } from "react";
import { buildAutoStagedPlan, getLineKinds, hasMinimumDepartureGapForOrigin } from "../../../lib/auto-schedule";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { minutesToTime, timeToMinutes } from "../../../lib/time";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import { validateManualRows } from "../../../lib/validation";
import {
  DEFAULT_LINE_OPTIONS,
  DEPOT_OPTIONS,
  LINE_OPTIONS,
  MIN_LINE_SETTING_MINUTES,
  buildPlanLineOptions,
  buildCatalog,
  buildRuntimeCatalog,
  directionFromOffsetMode,
  getReferenceLineIdsForLine,
  normalizeKind,
  offsetModeFromDirection,
  patchRuntimeLineOption,
  replaceRuntimeCatalog
} from "./schedule-catalog";
import {
  buildCombinedNote,
  buildPreviewMetaText,
  createSummaryEntry,
  formatOffsetLabel,
  isValidTimeValue,
  normalizeFrequencyInput,
  normalizeSummaryEntries,
  normalizeTimeInput,
  parseFrequencyValue,
  resolveOffsetMinutes,
  sortAutoRuleRows,
  sortManualDraftRows
} from "./schedule-normalize";
import { buildSummaryRowsWithConflicts, getSummaryRowKey, getSummaryRowsSignature } from "./schedule-conflicts";
import {
  createNativeMergedViewForSave,
  flattenSnapshotLineDraftRowsByLineId,
  mapSnapshotSummaryRows,
  serializeNativeLineDraftRowsByLineId,
  serializeNativeLineSettings,
  serializeRemovedLineIds
} from "./schedule-serialization";
import { runNativeSaveOperation } from "./schedule-save-operation";

const DEFAULT_SCHEDULE_MODE = "train";

function normalizeScheduleMode(mode) {
  const token = String(mode || "").trim().toLowerCase();
  return token || DEFAULT_SCHEDULE_MODE;
}

function getPayloadMode(payload) {
  return typeof payload?.mode === "string" ? normalizeScheduleMode(payload.mode) : "";
}

function shouldConsumeSchedulePayload(payload, expectedMode) {
  if (!payload || typeof payload !== "object") {
    return false;
  }

  const payloadMode = getPayloadMode(payload);
  if (payloadMode) {
    return payloadMode === normalizeScheduleMode(expectedMode);
  }

  return normalizeScheduleMode(expectedMode) === DEFAULT_SCHEDULE_MODE;
}

function isTrustedCatalogPayload(payload, expectedMode) {
  return shouldConsumeSchedulePayload(payload, expectedMode)
    && getPayloadMode(payload) === normalizeScheduleMode(expectedMode)
    && payload?.sourceMode !== "backend-fallback";
}

function getLineRuntimeToken(line) {
  if (!line || typeof line !== "object") {
    return "";
  }

  if (typeof line.sourceLineId === "string" && line.sourceLineId) {
    return line.sourceLineId;
  }

  if (typeof line.corridorId === "string" && line.corridorId) {
    return line.corridorId;
  }

  return typeof line.id === "string" ? line.id : "";
}

function getSnapshotRequestSequence(snapshot) {
  const numeric = Number(snapshot?.clientRequestSequence ?? 0);
  return Number.isFinite(numeric) ? Math.max(0, Math.trunc(numeric)) : 0;
}

export default function useScheduleController({ registerHostActions, activeTransportMode = "train", isActive = false } = {}) {

  const { t } = useNativeScheduleI18n();
  const scheduleMode = normalizeScheduleMode(activeTransportMode);
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const [activeRightTab, setActiveRightTab] = useState("auto");
  const [catalogRevision, setCatalogRevision] = useState(0);
  const dropdownPortalHostRef = useRef(null);
  const [selectedLineId, setSelectedLineId] = useState(LINE_OPTIONS[0]?.id || "");
  const [selectedLineType, setSelectedLineType] = useState(LINE_OPTIONS[0]?.kind || "local");
  const [selectedDepot, setSelectedDepot] = useState(LINE_OPTIONS[0]?.depotId || "");
  const [origin, setOrigin] = useState(LINE_OPTIONS[0]?.originId || "");
  const [holdMinutes, setHoldMinutes] = useState(LINE_OPTIONS[0]?.hold || "");
  const [dwellMinutes, setDwellMinutes] = useState(LINE_OPTIONS[0]?.dwell || "");
  const holdMinutesValue = Number(holdMinutes);
  const dwellMinutesValue = Number(dwellMinutes);
  const holdMinutesTooSmall =
    holdMinutes !== "" && Number.isFinite(holdMinutesValue) && holdMinutesValue < MIN_LINE_SETTING_MINUTES;
  const dwellMinutesTooSmall =
    dwellMinutes !== "" && Number.isFinite(dwellMinutesValue) && dwellMinutesValue < MIN_LINE_SETTING_MINUTES;
  const [summaryEntries, setSummaryEntries] = useState(() => normalizeSummaryEntries([], t));
  const [autoRules, setAutoRules] = useState([]);
  const [manualDrafts, setManualDrafts] = useState([]);
  const [pendingRemovedLineIds, setPendingRemovedLineIds] = useState([]);
  const [manualInput, setManualInput] = useState("12:00");
  const [editorStart, setEditorStart] = useState("08:00");
  const [editorEnd, setEditorEnd] = useState("10:00");
  const [autoFrequencyText, setAutoFrequencyText] = useState("1");
  const [autoOffsetDirection, setAutoOffsetDirection] = useState("");
  const [autoOffsetMinutesText, setAutoOffsetMinutesText] = useState("");
  const [appliedSummarySignature, setAppliedSummarySignature] = useState("");
  const [appliedSummaryRowKeys, setAppliedSummaryRowKeys] = useState([]);
  const [isApplyingSchedule, setIsApplyingSchedule] = useState(false);
  const [summaryFilter, setSummaryFilter] = useState("all");
  const [panelMessage, setPanelMessage] = useState(null);
  const summaryScrollRef = useRef(null);
  const manualInputRef = useRef(null);
  const editorEndInputRef = useRef(null);
  const frequencyInputRef = useRef(null);
  const hasHydratedRuntimeRef = useRef(false);
  const lastHydratedSnapshotRef = useRef(null);
  const suppressedSnapshotRequestSequenceRef = useRef(0);
  const suppressNextSnapshotModeRef = useRef("");
  const latestSaveRequestSequenceRef = useRef(0);
  const ignoredLateSnapshotRequestSequenceRef = useRef(0);
  const latestDraftSaveOperationRunIdRef = useRef(0);
  const latestApplySaveOperationRunIdRef = useRef(0);
  const applyingSaveOperationRef = useRef(false);
  const activeModeRef = useRef(scheduleMode);
  const scheduleModeGenerationRef = useRef(0);
  activeModeRef.current = scheduleMode;

  const planLineOptions = useMemo(
    () => buildPlanLineOptions(LINE_OPTIONS),
    [catalogRevision]
  );

  const selectedLine = useMemo(
    () => LINE_OPTIONS.find((line) => line?.id === selectedLineId) ?? LINE_OPTIONS[0] ?? DEFAULT_LINE_OPTIONS[0],
    [catalogRevision, selectedLineId]
  );
  const availableDepots = useMemo(() => {
    if (!selectedLine?.transportType) {
      return DEPOT_OPTIONS;
    }

    return DEPOT_OPTIONS.filter((depot) => !depot?.transportType || depot.transportType === selectedLine.transportType);
  }, [catalogRevision, selectedLine]);
  const autoFrequencyPerHour = useMemo(
    () => parseFrequencyValue(autoFrequencyText),
    [autoFrequencyText]
  );
  const autoOffsetMinutes = useMemo(
    () => resolveOffsetMinutes(autoOffsetDirection, autoOffsetMinutesText),
    [autoOffsetDirection, autoOffsetMinutesText]
  );
  const currentKind = useMemo(() => normalizeKind(selectedLineType), [selectedLineType]);
  const normalizedManualInput = useMemo(
    () => normalizeTimeInput(String(manualInput || "").trim()),
    [manualInput]
  );
  const manualInputError = useMemo(() => {
    if (!normalizedManualInput) {
      return "";
    }

    if (normalizedManualInput.length < 5) {
      return "";
    }

    return isValidTimeValue(normalizedManualInput) ? "" : t("nativeSchedule.manual.inputError");
  }, [normalizedManualInput, t]);
  const isAddManualDisabled = !!manualInputError || !isValidTimeValue(normalizedManualInput);
  const currentManualDrafts = useMemo(
    () => sortManualDraftRows(manualDrafts.filter((draft) => draft?.serviceId === selectedLine.id)),
    [manualDrafts, selectedLine.id]
  );
  const validatedManualDrafts = useMemo(
    () => validateManualRows(currentManualDrafts, t),
    [currentManualDrafts, t]
  );
  const currentAutoRules = useMemo(
    () => sortAutoRuleRows(autoRules.filter((rule) => rule?.serviceId === selectedLine.id)),
    [autoRules, selectedLine.id]
  );
  const currentAutoPlan = useMemo(() => {
    if (currentAutoRules.length === 0) {
      return {
        retainedRows: summaryEntries,
        plannedRows: [],
        skippedCount: 0,
        previewsByRule: {},
        hasKindConflict: false
      };
    }

    return buildAutoStagedPlan({
      currentRows: summaryEntries,
      rowsForLine: currentAutoRules,
      selectedEditLine: selectedLine.id,
      referenceLineIds: getReferenceLineIdsForLine(selectedLine, currentKind),
      lineOptions: planLineOptions,
      replaceExistingAutoRows: false
    });
  }, [currentAutoRules, currentKind, planLineOptions, selectedLine, summaryEntries]);
  const renderedAutoRules = useMemo(
    () => currentAutoRules.map((rule) => {
      const preview = currentAutoPlan.previewsByRule[rule.id] || { times: [], entries: [], skippedCount: 0, skipReasons: [], reason: "" };
      return {
        ...rule,
        windowLabel: `${rule.start} - ${rule.end}`,
        rateLabel: t("nativeSchedule.preview.rateLabel.compact", { count: rule.departuresPerHour }),
        offsetLabel: rule.kind === "express" ? formatOffsetLabel(directionFromOffsetMode(rule.expressOffsetMode), String(rule.expressOffsetMinutes || ""), t, "compact") : t("nativeSchedule.offset.none.compact"),
        previewTimes: preview.times,
        previewEntries: preview.entries,
        previewMeta: buildPreviewMetaText(preview, currentAutoPlan.hasKindConflict, t, { detailedSkipReason: true })
      };
    }),
    [currentAutoPlan.hasKindConflict, currentAutoPlan.previewsByRule, currentAutoRules, t]
  );
  const liveAutoPreview = useMemo(() => {
    const previewRule = {
      id: "editor-preview",
      lineId: selectedLine.id,
      serviceId: selectedLine.id,
      kind: currentKind,
      enabled: true,
      start: editorStart,
      end: editorEnd,
      departuresPerHour: autoFrequencyPerHour,
      expressOffsetMode: offsetModeFromDirection(autoOffsetDirection),
      expressOffsetMinutes: currentKind === "express" ? Math.abs(autoOffsetMinutes) : 0
    };
    const plan = buildAutoStagedPlan({
      currentRows: summaryEntries,
      rowsForLine: [previewRule],
      selectedEditLine: selectedLine.id,
      referenceLineIds: getReferenceLineIdsForLine(selectedLine, currentKind),
      lineOptions: planLineOptions,
      replaceExistingAutoRows: false
    });
    const preview = plan.previewsByRule[previewRule.id] || { times: [], entries: [], skippedCount: 0, skipReasons: [], reason: "" };
    return {
      times: preview.times,
      entries: preview.entries,
      meta: buildPreviewMetaText(preview, plan.hasKindConflict, t)
    };
  }, [
    autoFrequencyPerHour,
    autoOffsetDirection,
    autoOffsetMinutes,
    currentKind,
    editorEnd,
    editorStart,
    selectedLine.id,
    selectedLine,
    summaryEntries,
    planLineOptions,
    t
  ]);
  const currentSummarySignature = useMemo(
    () => getSummaryRowsSignature(summaryEntries),
    [summaryEntries]
  );
  const appliedSummaryRowKeySet = useMemo(
    () => new Set(Array.isArray(appliedSummaryRowKeys) ? appliedSummaryRowKeys : []),
    [appliedSummaryRowKeys]
  );
  const hasAppliedSchedule = currentSummarySignature === appliedSummarySignature && pendingRemovedLineIds.length === 0;
  const summaryRows = useMemo(
    () => buildSummaryRowsWithConflicts(summaryEntries, t, appliedSummaryRowKeySet),
    [appliedSummaryRowKeySet, summaryEntries, t]
  );
  const visibleSummaryRows = useMemo(() => {
    if (summaryFilter === "current") {
      return summaryRows.filter((row) => row.serviceId === selectedLine.id);
    }

    if (summaryFilter === "local") {
      return summaryRows.filter((row) => row.kind === "local");
    }

    if (summaryFilter === "express") {
      return summaryRows.filter((row) => row.kind === "express");
    }

    return summaryRows;
  }, [selectedLine.id, summaryFilter, summaryRows]);
  const conflictCount = summaryRows.filter((row) => row.isConflict).length;
  const earliestStart = visibleSummaryRows[0]?.time || "--:--";
  const summaryStateLabel = hasAppliedSchedule ? t("nativeSchedule.summary.section.applied") : t("nativeSchedule.summary.section.pending");
  const summaryFooterNote = panelMessage?.scope === "summary" ? panelMessage : null;
  const autoFooterNote =
    panelMessage?.scope === "auto"
      ? panelMessage
      : currentAutoPlan.hasKindConflict
        ? { scope: "auto", tone: "error", text: t("nativeSchedule.message.auto.kindConflict") }
        : null;
  const manualFooterNote = panelMessage?.scope === "manual" ? panelMessage : null;

  function bumpCatalogRevision() {
    setCatalogRevision((current) => current + 1);
  }

  function updateRuntimeLineOption(lineId, updates) {
    if (patchRuntimeLineOption(lineId, updates)) {
      bumpCatalogRevision();
    }
  }

  function suppressNextSnapshotForMode(mode, requestSequence) {
    suppressedSnapshotRequestSequenceRef.current = Math.max(0, Number(requestSequence) || 0);
    suppressNextSnapshotModeRef.current = normalizeScheduleMode(mode);
  }

  function clearSnapshotSuppression(mode, requestSequence = null) {
    if (suppressedSnapshotRequestSequenceRef.current <= 0) {
      return;
    }

    if (suppressNextSnapshotModeRef.current && suppressNextSnapshotModeRef.current !== normalizeScheduleMode(mode)) {
      return;
    }

    if (requestSequence != null && suppressedSnapshotRequestSequenceRef.current !== Math.max(0, Number(requestSequence) || 0)) {
      return;
    }

    suppressedSnapshotRequestSequenceRef.current = 0;
    suppressNextSnapshotModeRef.current = "";
  }

  function isCurrentModeRequest(mode, generation) {
    return activeModeRef.current === normalizeScheduleMode(mode)
      && scheduleModeGenerationRef.current === generation;
  }

  function ignoreLateSnapshotForRequest(requestSequence) {
    const normalizedSequence = Math.max(0, Number(requestSequence) || 0);
    if (normalizedSequence <= 0) {
      return;
    }

    ignoredLateSnapshotRequestSequenceRef.current = Math.max(
      ignoredLateSnapshotRequestSequenceRef.current,
      normalizedSequence
    );
  }

  function hasBackendCleanupInfo(cleanupInfo) {
    return Array.isArray(cleanupInfo?.removedAppliedLineIds) && cleanupInfo.removedAppliedLineIds.length > 0
      || Array.isArray(cleanupInfo?.removedDraftLineIds) && cleanupInfo.removedDraftLineIds.length > 0
      || Array.isArray(cleanupInfo?.removedLineSettingIds) && cleanupInfo.removedLineSettingIds.length > 0
      || Array.isArray(cleanupInfo?.reasons) && cleanupInfo.reasons.length > 0;
  }

  function logBackendCleanup(cleanupInfo, source) {
    if (!hasBackendCleanupInfo(cleanupInfo)) {
      return;
    }

    let detail = "";
    try {
      detail = ` ${JSON.stringify(cleanupInfo)}`;
    } catch {
      detail = "";
    }

    console.info(`[RT Native Schedule] backend cleanup via ${source}${detail}`);
  }

  function filterAppliedSummaryRowKeys(rowKeys, removedLineIdSet) {
    return (Array.isArray(rowKeys) ? rowKeys : []).filter((rowKey) => {
      if (typeof rowKey !== "string" || rowKey.length === 0) {
        return false;
      }

      const separatorIndex = rowKey.indexOf("|");
      const lineId = separatorIndex >= 0 ? rowKey.slice(0, separatorIndex) : rowKey;
      return !removedLineIdSet.has(lineId);
    });
  }

  function getSummarySignatureFromRowKeys(rowKeys) {
    return [...new Set((Array.isArray(rowKeys) ? rowKeys : []).filter((rowKey) => typeof rowKey === "string" && rowKey.length > 0))]
      .sort()
      .join("||");
  }

  function cleanupInvalidatedLinesLocally(lineIds) {
    const nextLineIds = serializeRemovedLineIds(lineIds);
    if (nextLineIds.length === 0) {
      return;
    }

    const removedLineIdSet = new Set(nextLineIds);
    clearPanelMessage();
    setPendingRemovedLineIds((current) => (current || []).filter((lineId) => !removedLineIdSet.has(lineId)));
    setAppliedSummaryRowKeys((current) => {
      const nextRowKeys = filterAppliedSummaryRowKeys(current, removedLineIdSet);
      setAppliedSummarySignature(getSummarySignatureFromRowKeys(nextRowKeys));
      return nextRowKeys;
    });
    setSummaryEntries((current) => {
      return current.filter((row) => !removedLineIdSet.has(row?.lineId || row?.serviceId || ""));
    });
    setManualDrafts((current) => current.filter((draft) => !removedLineIdSet.has(draft?.lineId || draft?.serviceId || "")));
    setAutoRules((current) => current.filter((rule) => !removedLineIdSet.has(rule?.lineId || rule?.serviceId || "")));
  }

  function applyHydratedState(snapshot, metadataSnapshot = null, expectedMode = scheduleMode, options = {}) {
    const { preservePendingRemovedLineIds = false } = options;
    const targetMode = normalizeScheduleMode(expectedMode);
    if (!shouldConsumeSchedulePayload(snapshot, targetMode)) {
      return false;
    }

    const scopedMetadata = shouldConsumeSchedulePayload(metadataSnapshot, targetMode)
      ? metadataSnapshot
      : null;
    lastHydratedSnapshotRef.current = snapshot ?? null;
    const runtimeCatalog = buildRuntimeCatalog(
      snapshot,
      scopedMetadata,
      null,
      t
    );
    replaceRuntimeCatalog({
      lines: runtimeCatalog.lineOptions,
      depots: runtimeCatalog.depotOptions,
      origins: runtimeCatalog.originOptions
    });
    bumpCatalogRevision();
    const sourceLineId =
      (snapshot?.selectedEditLine && runtimeCatalog.lineOptions.some((line) => line?.id === snapshot.selectedEditLine)
        ? snapshot.selectedEditLine
        : "") ||
      (snapshot?.selectedLineId && runtimeCatalog.lineOptions.some((line) => line?.id === snapshot.selectedLineId)
        ? snapshot.selectedLineId
        : "") ||
      runtimeCatalog.lineOptions[0]?.id ||
      DEFAULT_LINE_OPTIONS[0].id;
    const sourceLine =
      runtimeCatalog.lineOptions.find((line) => line?.id === sourceLineId) ??
      runtimeCatalog.lineOptions[0] ??
      DEFAULT_LINE_OPTIONS[0];

    const restoredDraftRows = flattenSnapshotLineDraftRowsByLineId(snapshot?.lineDraftRowsByLineId);
    const nextSummaryEntries = normalizeSummaryEntries(
      restoredDraftRows.length > 0
        ? restoredDraftRows
        : mapSnapshotSummaryRows(Array.isArray(snapshot?.appliedRows) ? snapshot.appliedRows : []),
      t
    );
    const appliedSummaryEntries = normalizeSummaryEntries(
      mapSnapshotSummaryRows(Array.isArray(snapshot?.appliedRows) ? snapshot.appliedRows : []),
      t
    );
    const nextSummarySignature = getSummaryRowsSignature(appliedSummaryEntries);
    const currentSummaryRowKeys = appliedSummaryEntries.map((row) => getSummaryRowKey(row));
    logBackendCleanup(snapshot?.cleanupInfo, "snapshot");

    setActiveRightTab((current) => (current === "manual" ? "manual" : "auto"));
    setSelectedLineId(sourceLine.id);
    setSelectedLineType(sourceLine.kind);
    setSelectedDepot(sourceLine.depotId);
    setOrigin(sourceLine.originId);
    setHoldMinutes(sourceLine.hold);
    setDwellMinutes(sourceLine.dwell);
    setSummaryEntries(nextSummaryEntries);
    setAutoRules([]);
    setManualDrafts([]);
    if (!preservePendingRemovedLineIds) {
      setPendingRemovedLineIds([]);
    }
    setManualInput("12:00");
    setEditorStart("08:00");
    setEditorEnd("10:00");
    setAutoFrequencyText("1");
    setAutoOffsetDirection("");
    setAutoOffsetMinutesText("");
    setAppliedSummarySignature(nextSummarySignature);
    setAppliedSummaryRowKeys(currentSummaryRowKeys);
    setSummaryFilter("all");
    setPanelMessage(null);
    hasHydratedRuntimeRef.current = true;
    return true;
  }

  async function refreshCatalog(modeAtRequest = scheduleMode) {
    let metadata = null;

    try {
      metadata = await workbenchApi.refreshMetadata?.({ mode: modeAtRequest });
    } catch {
      return;
    }

    if (!isTrustedCatalogPayload(metadata, modeAtRequest)) {
      return;
    }

    const previousSelectedLine = LINE_OPTIONS.find((line) => line?.id === selectedLineId) ?? null;
    const runtimeCatalog = buildCatalog(metadata, t, { allowDefaultFallback: false });
    replaceRuntimeCatalog({
      lines: runtimeCatalog.lineOptions,
      depots: runtimeCatalog.depotOptions,
      origins: runtimeCatalog.originOptions
    });
    bumpCatalogRevision();

    const nextLine =
      runtimeCatalog.lineOptions.find((line) => line?.id === selectedLineId) ??
      runtimeCatalog.lineOptions[0] ??
      null;

    if (!nextLine) {
      setSelectedLineId("");
      setSelectedLineType("local");
      setSelectedDepot("");
      setOrigin("");
      setHoldMinutes("");
      setDwellMinutes("");
      return;
    }

    const selectedLineReplaced = !!previousSelectedLine
      && nextLine.id === selectedLineId
      && getLineRuntimeToken(nextLine) !== getLineRuntimeToken(previousSelectedLine);
    const changedLine = nextLine.id !== selectedLineId || selectedLineReplaced;
    if (changedLine) {
      setSelectedLineId(nextLine.id);
      setSelectedLineType(nextLine.kind);
      setSelectedDepot(nextLine.depotId);
      setOrigin(nextLine.originId);
      setHoldMinutes(nextLine.hold);
      setDwellMinutes(nextLine.dwell);
    }
  }

  useEffect(() => {
    let disposed = false;
    const modeAtRequest = scheduleMode;
    const generation = scheduleModeGenerationRef.current + 1;
    scheduleModeGenerationRef.current = generation;
    hasHydratedRuntimeRef.current = false;
    suppressedSnapshotRequestSequenceRef.current = 0;
    suppressNextSnapshotModeRef.current = "";
    latestSaveRequestSequenceRef.current = 0;
    ignoredLateSnapshotRequestSequenceRef.current = 0;
    latestDraftSaveOperationRunIdRef.current += 1;
    latestApplySaveOperationRunIdRef.current += 1;
    applyingSaveOperationRef.current = false;
    lastHydratedSnapshotRef.current = null;
    setIsApplyingSchedule(false);
    replaceRuntimeCatalog({ lines: [], depots: [], origins: [] });
    bumpCatalogRevision();
    setSelectedLineId("");
    setSelectedLineType("local");
    setSelectedDepot("");
    setOrigin("");
    setHoldMinutes("");
    setDwellMinutes("");
    setSummaryEntries([]);
    setAutoRules([]);
    setManualDrafts([]);
    setPendingRemovedLineIds([]);
    setAppliedSummarySignature("");
    setAppliedSummaryRowKeys([]);
    setSummaryFilter("all");
    setPanelMessage(null);

    async function hydrateFromBackend({ forceRefresh = false } = {}) {
      try {
        const snapshot = forceRefresh
          ? await workbenchApi.refreshSnapshot?.({ mode: modeAtRequest })
          : await workbenchApi.loadSnapshot?.({ mode: modeAtRequest });
        let metadata = null;

        try {
          metadata = await workbenchApi.refreshMetadata?.({ mode: modeAtRequest });
        } catch {}

        if (!disposed && isCurrentModeRequest(modeAtRequest, generation)) {
          applyHydratedState(snapshot, metadata, modeAtRequest);
        }
      } catch (error) {
        if (!disposed && isCurrentModeRequest(modeAtRequest, generation)) {
          console.error("[RT Native Schedule] backend hydrate failed", error);
        }
      }
    }

    hydrateFromBackend();
    const unsubscribe = workbenchApi.onSnapshotChanged?.((snapshot) => {
      if (!isCurrentModeRequest(modeAtRequest, generation)
        || !shouldConsumeSchedulePayload(snapshot, modeAtRequest)) {
        return;
      }

      const snapshotRequestSequence = getSnapshotRequestSequence(snapshot);
      if (snapshotRequestSequence > 0) {
        if (snapshotRequestSequence <= ignoredLateSnapshotRequestSequenceRef.current
          || snapshotRequestSequence < latestSaveRequestSequenceRef.current) {
          return;
        }
      }

      if (suppressedSnapshotRequestSequenceRef.current > 0
        && suppressNextSnapshotModeRef.current === modeAtRequest
        && snapshotRequestSequence === suppressedSnapshotRequestSequenceRef.current) {
        clearSnapshotSuppression(modeAtRequest, snapshotRequestSequence);
        return;
      }

      if (!disposed) {
        applyHydratedState(snapshot, null, modeAtRequest);
      }
    });

    return () => {
      disposed = true;
      unsubscribe?.();
    };
  }, [scheduleMode, t, workbenchApi]);

  useEffect(() => {
    const unsubscribe = workbenchApi.onLineInvalidated?.((event) => {
      const modeAtRequest = normalizeScheduleMode(event?.mode || scheduleMode);
      if (modeAtRequest !== scheduleMode) {
        return;
      }

      const lineIds = serializeRemovedLineIds(
        Array.isArray(event?.lineIds)
          ? event.lineIds
          : []
      );
      if (lineIds.length === 0) {
        return;
      }

      console.info(`[RT Native Schedule] backend line invalidated ${JSON.stringify({
        mode: modeAtRequest,
        lineIds,
        reasons: Array.isArray(event?.reasons) ? event.reasons : []
      })}`);
      cleanupInvalidatedLinesLocally(lineIds);
      refreshCatalog(modeAtRequest);
    });

    return () => {
      unsubscribe?.();
    };
  }, [scheduleMode, selectedLineId, t, workbenchApi]);

  useEffect(() => {
    const unsubscribe = workbenchApi.onCatalogChanged?.((event) => {
      const modeAtRequest = normalizeScheduleMode(event?.mode || scheduleMode);
      if (modeAtRequest !== scheduleMode) {
        return;
      }

      refreshCatalog(modeAtRequest);
    });

    return () => {
      unsubscribe?.();
    };
  }, [scheduleMode, selectedLineId, t, workbenchApi]);

  useEffect(() => {
    if (!isActive) {
      return undefined;
    }

    const selectedEditLineId = selectedLine?.id || selectedLineId || "";
    if (typeof window !== "undefined") {
      window.__RT_WORKBENCH_ACTIVE_PAGE__ = "schedule";
      window.__RT_WORKBENCH_SELECTED_LINE_ID__ = selectedLineId || "";
      window.__RT_WORKBENCH_SELECTED_EDIT_LINE__ = selectedEditLineId;
    }
    workbenchApi.setHostState?.({
      mode: scheduleMode,
      activePage: "schedule",
      selectedLineId: selectedLineId || "",
      selectedEditLine: selectedEditLineId
    });
  }, [isActive, scheduleMode, selectedLine, selectedLineId, workbenchApi]);

  useEffect(() => {
    if (!isActive || typeof registerHostActions !== "function") {
      return undefined;
    }

    registerHostActions({
      refreshData: async () => {
        const modeAtRequest = scheduleMode;
        const generation = scheduleModeGenerationRef.current;
        const snapshot = await workbenchApi.refreshSnapshot?.({ mode: modeAtRequest });
        let metadata = null;

        try {
          metadata = await workbenchApi.refreshMetadata?.({ mode: modeAtRequest });
        } catch {}

        if (isCurrentModeRequest(modeAtRequest, generation)) {
          applyHydratedState(snapshot, metadata, modeAtRequest);
        }
      }
    });

    return () => {
      registerHostActions(null);
    };
  }, [isActive, scheduleMode, registerHostActions, t, workbenchApi]);

  async function saveNativeWorkbenchDraft({ applyDraft = false } = {}) {
    if (!applyDraft && applyingSaveOperationRef.current) {
      return { success: true, errors: [], warnings: [], version: "", snapshot: null, superseded: true };
    }

    const requestMode = scheduleMode;
    const requestGeneration = scheduleModeGenerationRef.current;
    const runRef = applyDraft ? latestApplySaveOperationRunIdRef : latestDraftSaveOperationRunIdRef;
    const runId = runRef.current + 1;
    const requestSequence = latestSaveRequestSequenceRef.current + 1;
    runRef.current = runId;
    latestSaveRequestSequenceRef.current = requestSequence;
    if (!isCurrentModeRequest(requestMode, requestGeneration)) {
      return { success: true, errors: [], warnings: [], version: "", snapshot: null, superseded: true };
    }

    if (applyDraft) {
      applyingSaveOperationRef.current = true;
      setIsApplyingSchedule(true);
      latestDraftSaveOperationRunIdRef.current += 1;
    }

    const lineDraftRowsByLineId = serializeNativeLineDraftRowsByLineId(summaryEntries);
    if (applyDraft && selectedLineId && !lineDraftRowsByLineId.some((block) => block?.lineId === selectedLineId)) {
      lineDraftRowsByLineId.push({ lineId: selectedLineId, lineDraftRows: [] });
    }
    const request = {
      mode: requestMode,
      selectedLineId,
      selectedEditLine: selectedLineId,
      mergedView: createNativeMergedViewForSave(selectedLineId, lastHydratedSnapshotRef.current?.mergedView),
      lineDraftRowsByLineId,
      lineSettings: serializeNativeLineSettings(LINE_OPTIONS),
      clientRequestSequence: requestSequence,
      applyDraft,
      nativeScheduleWriter: true,
      returnSnapshot: true
    };

    suppressNextSnapshotForMode(requestMode, requestSequence);
    try {
      const operationResult = await runNativeSaveOperation(workbenchApi, request, {
        applyDraft,
        expectedMode: requestMode,
        shouldContinue: () => runRef.current === runId && isCurrentModeRequest(requestMode, requestGeneration)
      });

      if (operationResult.interrupted) {
        ignoreLateSnapshotForRequest(requestSequence);
        clearSnapshotSuppression(requestMode, requestSequence);
        if (applyDraft) {
          throw new Error("apply-operation-interrupted");
        }

        return { success: true, errors: [], warnings: [], version: "", snapshot: null, superseded: true };
      }

      if (operationResult.superseded) {
        ignoreLateSnapshotForRequest(requestSequence);
        clearSnapshotSuppression(requestMode, requestSequence);
        if (applyDraft) {
          throw new Error("apply-operation-superseded");
        }

        return { success: true, errors: [], warnings: [], version: "", snapshot: null, superseded: true };
      }

      const result = operationResult.result;
      if (result && !shouldConsumeSchedulePayload(result, requestMode)) {
        ignoreLateSnapshotForRequest(requestSequence);
        clearSnapshotSuppression(requestMode, requestSequence);
        if (applyDraft) {
          throw new Error("apply-operation-mode-mismatch");
        }

        return { success: true, errors: [], warnings: [], version: "", snapshot: null, superseded: true };
      }

      if (result?.snapshot) {
        if (isCurrentModeRequest(requestMode, requestGeneration)) {
          applyHydratedState(result.snapshot, null, requestMode, {
            preservePendingRemovedLineIds: result?.success !== true
          });
        }
      } else {
        clearSnapshotSuppression(requestMode, requestSequence);
        if (result?.success) {
          logBackendCleanup(result?.cleanupInfo, "save-result");
          setPendingRemovedLineIds([]);
        }
      }

      return result;
    } catch (error) {
      ignoreLateSnapshotForRequest(requestSequence);
      clearSnapshotSuppression(requestMode, requestSequence);
      throw error;
    } finally {
      if (applyDraft && runRef.current === runId) {
        applyingSaveOperationRef.current = false;
        setIsApplyingSchedule(false);
      }
    }
  }

  function clearPanelMessage() {
    setPanelMessage(null);
  }

  function markLocalDataDirty() {
    clearPanelMessage();
  }

  function applySelectedLine(nextLine) {
    if (!nextLine) {
      return;
    }

    setSelectedLineId(nextLine.id);
    setSelectedLineType(nextLine.kind);
    setSelectedDepot(nextLine.depotId);
    setOrigin(nextLine.originId);
    setHoldMinutes(nextLine.hold);
    setDwellMinutes(nextLine.dwell);
  }

  function handleSelectLine(lineId) {
    const nextLine = LINE_OPTIONS.find((line) => line.id === lineId);
    if (!nextLine) {
      return;
    }

    clearPanelMessage();
    if (nextLine.dispatchSupported === false) {
      setPanelMessage({ scope: "summary", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
    }
    applySelectedLine(nextLine);
  }

  function handleLineTypeSelect(nextType) {
    if (nextType !== "local" && nextType !== "express") {
      return;
    }

    if (selectedLine.kind === nextType) {
      clearPanelMessage();
      return;
    }

    markLocalDataDirty();
    updateRuntimeLineOption(selectedLine.id, { kind: nextType });
    setSelectedLineType(nextType);
    setManualDrafts((current) => sortManualDraftRows(
      current.map((draft) => (
        draft?.lineId === selectedLine.id
          ? { ...draft, kind: nextType }
          : draft
      ))
    ));
    setAutoRules((current) => sortAutoRuleRows(
      current.map((rule) => (
        rule?.lineId === selectedLine.id
          ? { ...rule, kind: nextType }
          : rule
      ))
    ));
    setSummaryEntries((current) => normalizeSummaryEntries(
      current.map((row) => (
        row?.lineId === selectedLine.id || row?.serviceId === selectedLine.id
          ? { ...row, kind: nextType }
          : row
      )),
      t
    ));
  }

  function handleDepotChange(value) {
    markLocalDataDirty();
    setSelectedDepot(value);
    updateRuntimeLineOption(selectedLine.id, { depotId: value });
  }

  function handleHoldMinutesChange(value) {
    const numeric = Number(value);
    if (value !== "" && Number.isFinite(numeric) && numeric < MIN_LINE_SETTING_MINUTES) {
      setHoldMinutes(value);
      return;
    }
    markLocalDataDirty();
    setHoldMinutes(value);
    updateRuntimeLineOption(selectedLine.id, { hold: value });
  }

  function handleDwellMinutesChange(value) {
    const numeric = Number(value);
    if (value !== "" && Number.isFinite(numeric) && numeric < MIN_LINE_SETTING_MINUTES) {
      setDwellMinutes(value);
      return;
    }
    markLocalDataDirty();
    setDwellMinutes(value);
    updateRuntimeLineOption(selectedLine.id, { dwell: value });
  }

  function handleEditorStartChange(value) {
    clearPanelMessage();
    if (!value || (value.length === 5 && isValidTimeValue(value))) {
      setEditorStart(value);
    }
  }

  function handleEditorEndChange(value) {
    clearPanelMessage();
    if (!value || (value.length === 5 && isValidTimeValue(value))) {
      setEditorEnd(value);
    }
  }

  function handleAutoFrequencyChange(value) {
    clearPanelMessage();
    setAutoFrequencyText(normalizeFrequencyInput(value));
  }

  function handleManualInputChange(value) {
    clearPanelMessage();
    setManualInput(normalizeTimeInput(value));
  }

  function handleAutoOffsetDirectionChange(nextDirection) {
    clearPanelMessage();
    setAutoOffsetDirection(nextDirection);
  }

  function handleAutoOffsetMinutesChange(nextValue) {
    clearPanelMessage();
    setAutoOffsetMinutesText(nextValue);
  }

  function addAutoRule() {
    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    if (!isValidTimeValue(editorStart) || !isValidTimeValue(editorEnd)) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.auto.invalidWindow") });
      return;
    }

    if ((!Array.isArray(liveAutoPreview.entries) || liveAutoPreview.entries.length === 0) && liveAutoPreview.meta) {
      setPanelMessage({ scope: "auto", tone: "warning", text: liveAutoPreview.meta });
      return;
    }

    markLocalDataDirty();
    setAutoRules((current) => sortAutoRuleRows([
      ...current,
      {
        id: Date.now(),
        lineId: selectedLine.id,
        serviceId: selectedLine.id,
        kind: currentKind,
        enabled: true,
        start: editorStart,
        end: editorEnd,
        departuresPerHour: autoFrequencyPerHour,
        expressOffsetMode: offsetModeFromDirection(autoOffsetDirection),
        expressOffsetMinutes: currentKind === "express" ? Math.abs(autoOffsetMinutes) : 0
      }
    ]));
  }

  function removeAutoRule(ruleId) {
    markLocalDataDirty();
    setAutoRules((current) => current.filter((rule) => rule.id !== ruleId));
  }

  function addManualDraft() {
    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    if (isAddManualDisabled || !isValidTimeValue(normalizedManualInput)) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.message.manual.invalidTime") });
      return;
    }

    markLocalDataDirty();
    setManualDrafts((current) => sortManualDraftRows([
      ...current,
      {
        id: Date.now(),
        lineId: selectedLine.id,
        serviceId: selectedLine.id,
        kind: currentKind,
        time: normalizedManualInput,
        offsetMode: "none",
        offsetMinutes: ""
      }
    ]));
    setManualInput("");
    if (manualInputRef.current) {
      manualInputRef.current.value = "";
    }
  }

  function removeManualDraft(draftId) {
    markLocalDataDirty();
    setManualDrafts((current) => current.filter((draft) => draft.id !== draftId));
  }

  function removeSummaryRow(rowId) {
    markLocalDataDirty();
    setSummaryEntries((current) => current.filter((row) => row.id !== rowId));
  }

  function clearSummaryTable() {
    markLocalDataDirty();
    setSummaryEntries((current) => {
      if (summaryFilter === "current") {
        return current.filter((row) => row.lineId !== selectedLine.id && row.serviceId !== selectedLine.id);
      }

      if (summaryFilter === "local") {
        return current.filter((row) => row.kind !== "local" || (row.lineId !== selectedLine.id && row.serviceId !== selectedLine.id));
      }

      if (summaryFilter === "express") {
        return current.filter((row) => row.kind !== "express" || (row.lineId !== selectedLine.id && row.serviceId !== selectedLine.id));
      }

      return current.filter((row) => row.lineId !== selectedLine.id && row.serviceId !== selectedLine.id);
    });
  }

  function importManualToSummary() {
    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    const sortedDrafts = [...currentManualDrafts].sort((left, right) => (left.time || "").localeCompare(right.time || ""));
    const validatedRows = validateManualRows(sortedDrafts, t);
    const validRows = validatedRows.filter((row) => row.validation.status !== "error");
    const invalidRows = validatedRows.length - validRows.length;
    if (validRows.length === 0) {
      setPanelMessage({ scope: "manual", tone: "neutral", text: t("nativeSchedule.message.manual.noValid") });
      return;
    }

    const nextKinds = new Set(validRows.map((row) => row.kind));
    const existingKinds = getLineKinds(summaryEntries, selectedLine.id);
    const hasKindConflict = [...nextKinds].some((kind) => existingKinds.size > 0 && !existingKinds.has(kind));
    if (hasKindConflict) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.message.manual.kindConflict") });
      return;
    }

    const selectedOriginStationId = selectedLine.originStationId || "";
    const occupiedRows = summaryEntries
      .map((row) => ({
        minute: timeToMinutes(row.time),
        originStationId: row.originStationId || ""
      }))
      .filter((row) => row.minute !== null);
    const importedRows = [];
    let blockedRows = 0;

    validRows.forEach((row) => {
      const candidateMinute = timeToMinutes(row.time);
      if (candidateMinute === null) {
        blockedRows += 1;
        return;
      }

      if (!hasMinimumDepartureGapForOrigin(candidateMinute, selectedOriginStationId, occupiedRows)) {
        blockedRows += 1;
        return;
      }

      occupiedRows.push({
        minute: candidateMinute,
        originStationId: selectedOriginStationId
      });
      importedRows.push(createSummaryEntry({
        id: `summary-manual-${selectedLine.id}-${row.id}`,
        time: row.time,
        serviceId: row.serviceId,
        kind: row.kind,
        source: "manual",
        note: buildCombinedNote("direct", t)
      }, t));
    });

    if (importedRows.length === 0) {
      setPanelMessage({
        scope: "manual",
        tone: "neutral",
        text: blockedRows > 0
          ? t("nativeSchedule.message.manual.blockedAll", { count: blockedRows })
          : t("nativeSchedule.message.manual.noValid")
      });
      return;
    }

    markLocalDataDirty();
    setSummaryEntries((current) => normalizeSummaryEntries([...current, ...importedRows], t));
    setPanelMessage({
      scope: "manual",
      tone: "neutral",
      text: blockedRows > 0 || invalidRows > 0
        ? t("nativeSchedule.message.manual.importedWithCounts", {
          count: importedRows.length,
          skipped: blockedRows,
          invalid: invalidRows
        })
        : t("nativeSchedule.message.manual.imported", { count: importedRows.length })
    });
  }

  function importAutoToSummary() {
    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    if (currentAutoRules.length === 0) {
      setPanelMessage({ scope: "auto", tone: "warning", text: t("nativeSchedule.message.auto.noRules") });
      return;
    }

    const plan = buildAutoStagedPlan({
      currentRows: summaryEntries,
      rowsForLine: currentAutoRules,
      selectedEditLine: selectedLine.id,
      referenceLineIds: getReferenceLineIdsForLine(selectedLine, currentKind),
      lineOptions: planLineOptions,
      replaceExistingAutoRows: false
    });
    if (plan.hasKindConflict) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.auto.kindConflict") });
      return;
    }

    const importedRows = plan.plannedRows.map((row) => {
      const sourceRule = currentAutoRules.find((rule) => rule.id === row.ruleId);
      return createSummaryEntry({
        id: `summary-auto-${selectedLine.id}-${row.ruleId}-${row.generatedIndex}`,
        time: minutesToTime(row.timeMinutes),
        serviceId: sourceRule?.serviceId || selectedLine.id,
        kind: row.kind,
        source: "auto",
        note: buildCombinedNote(row.noteType, t, {
          minutes: row.offsetMinutes,
          start: row.start,
          end: row.end
        })
      }, t);
    });
    if (importedRows.length === 0) {
      const issuePreview = currentAutoRules
        .map((rule) => currentAutoPlan.previewsByRule[rule.id])
        .find((preview) => preview?.reason);
      setPanelMessage({
        scope: "auto",
        tone: "warning",
        text: issuePreview
          ? buildPreviewMetaText(issuePreview, false, t)
          : plan.skippedCount > 0
            ? t("nativeSchedule.message.auto.noTrips.skipped", { count: plan.skippedCount })
            : t("nativeSchedule.message.auto.noRules")
      });
      return;
    }

    markLocalDataDirty();
    setSummaryEntries((current) => normalizeSummaryEntries([...current, ...importedRows], t));
    setAutoRules((current) => current.filter((rule) => (
      rule?.lineId !== selectedLine.id && rule?.serviceId !== selectedLine.id
    )));
    setPanelMessage({
      scope: "auto",
      tone: "neutral",
      text: plan.skippedCount > 0
        ? t("nativeSchedule.message.auto.importedWithSkipped", { count: importedRows.length, skipped: plan.skippedCount })
        : t("nativeSchedule.message.auto.imported", { count: importedRows.length })
    });
  }

  async function handleApplySchedule() {
    const unsupportedRow = summaryEntries.find((row) => {
      const rowLineId = row?.lineId || row?.serviceId;
      const lineOption = LINE_OPTIONS.find((line) => line?.id === rowLineId);
      return lineOption?.dispatchSupported === false;
    });
    if (unsupportedRow) {
      setPanelMessage({ scope: "summary", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    const modeAtRequest = scheduleMode;
    const generation = scheduleModeGenerationRef.current;
    setPanelMessage(null);
    try {
      const result = await saveNativeWorkbenchDraft({ applyDraft: true });
      if (!isCurrentModeRequest(modeAtRequest, generation)) {
        return;
      }

      if (result?.superseded) {
        throw new Error("apply-operation-superseded");
      }

      if (!result?.success) {
        const errors = Array.isArray(result?.errors) && result.errors.length > 0 ? result.errors : [];
        const mappedErrors = errors.map((err) => {
          if (typeof err === "string" && err.startsWith("line-unsupported:")) {
            return t("nativeSchedule.message.lineUnsupported");
          }
          return err;
        });
        const message = mappedErrors.length > 0 ? mappedErrors.join("; ") : t("nativeSchedule.message.summary.saveFailed", { message: "unknown" });
        setPanelMessage({ scope: "summary", tone: "error", text: t("nativeSchedule.message.summary.applyFailed", { message }) });
        return;
      }

      if (!result.version) {
        throw new Error("apply-operation-missing-version");
      }

      setPanelMessage(null);
      if (!result?.snapshot) {
        setAppliedSummarySignature(getSummaryRowsSignature(summaryEntries));
        setAppliedSummaryRowKeys(summaryEntries.map((row) => getSummaryRowKey(row)));
      }
    } catch (error) {
      if (!isCurrentModeRequest(modeAtRequest, generation)) {
        return;
      }

      setPanelMessage({
        scope: "summary",
        tone: "error",
        text: t("nativeSchedule.message.summary.applyFailed", { message: error?.message || "unknown" })
      });
    }
  }

  function handleLocateConflict() {
    if (summaryFilter !== "all") {
      setSummaryFilter("all");
    }

    window.setTimeout(() => {
      const scrollContainer = summaryScrollRef.current;
      const firstConflictRow = scrollContainer?.querySelector(".dw-demo-summary-row.is-conflict");
      if (!scrollContainer || !firstConflictRow) {
        return;
      }

      const containerRect = scrollContainer.getBoundingClientRect();
      const rowRect = firstConflictRow.getBoundingClientRect();
      const deltaTop = rowRect.top - containerRect.top;
      const nextScrollTop =
        scrollContainer.scrollTop + deltaTop - Math.max(0, Math.round((scrollContainer.clientHeight - firstConflictRow.clientHeight) / 2));
      scrollContainer.scrollTop = Math.max(0, nextScrollTop);
    }, 0);
  }

  return {
    topbar: {
      selectedLineId,
      selectedLine,
      selectedLineType,
      selectedDepot,
      origin,
      holdMinutes,
      dwellMinutes,
      holdMinutesTooSmall,
      dwellMinutesTooSmall,
      availableDepots,
      lineOptions: LINE_OPTIONS
    },
    summary: {
      summaryStateLabel,
      hasAppliedSchedule,
      rows: visibleSummaryRows,
      editableLineId: selectedLine.id,
      earliestStart,
      conflictCount,
      summaryFilter,
      footerNote: summaryFooterNote,
      isApplyingSchedule
    },
    auto: {
      activeRightTab,
      editorStart,
      editorEnd,
      autoFrequencyText,
      autoFrequencyPerHour,
      selectedLineType,
      autoOffsetDirection,
      autoOffsetMinutesText,
      liveAutoPreview,
      autoRules: renderedAutoRules,
      footerNote: autoFooterNote
    },
    manual: {
      activeRightTab,
      manualInput,
      manualDrafts: validatedManualDrafts,
      manualInputError,
      isAddManualDisabled,
      footerNote: manualFooterNote
    },
    refs: {
      dropdownPortalHostRef,
      summaryScrollRef,
      manualInputRef,
      editorEndInputRef,
      frequencyInputRef
    },
    actions: {
      setActiveRightTab,
      selectLine: handleSelectLine,
      selectLineType: handleLineTypeSelect,
      changeDepot: handleDepotChange,
      changeHoldMinutes: handleHoldMinutesChange,
      changeDwellMinutes: handleDwellMinutesChange,
      setHoldMinutes,
      setDwellMinutes,
      setSummaryFilter,
      removeSummaryRow,
      clearSummaryTable,
      applySchedule: handleApplySchedule,
      locateConflict: handleLocateConflict,
      changeEditorStart: handleEditorStartChange,
      changeEditorEnd: handleEditorEndChange,
      changeAutoFrequency: handleAutoFrequencyChange,
      changeAutoOffsetDirection: handleAutoOffsetDirectionChange,
      changeAutoOffsetMinutes: handleAutoOffsetMinutesChange,
      addAutoRule,
      removeAutoRule,
      importAutoToSummary,
      changeManualInput: handleManualInputChange,
      addManualDraft,
      removeManualDraft,
      importManualToSummary
    }
  };
}
