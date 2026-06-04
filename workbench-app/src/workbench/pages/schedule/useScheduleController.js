import { useEffect, useMemo, useRef, useState } from "react";
import { buildAutoStagedPlan, getLineKinds, hasMinimumDepartureGapForOrigin } from "../../../lib/auto-schedule";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { minutesToTime, timeToMinutes } from "../../../lib/time";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import { validateManualRows } from "../../../lib/validation";
import {
  DEFAULT_LINE_OPTIONS,
  DEFAULT_RUNTIME_FEATURE_SETTINGS,
  DEPOT_OPTIONS,
  LINE_OPTIONS,
  MIN_LINE_SETTING_MINUTES,
  buildPlanLineOptions,
  buildRuntimeCatalog,
  directionFromOffsetMode,
  getReferenceLineIdsForLine,
  normalizeKind,
  normalizeRuntimeFeatureSettings,
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
  mapSnapshotAutoRules,
  mapSnapshotManualRows,
  mapSnapshotPlanRefs,
  mapSnapshotSummaryRows,
  serializeNativeAutoRules,
  serializeNativeLineDraftRowsByLineId,
  serializeNativeLineSettings,
  serializeNativeManualRows,
  serializePlanRefs,
  serializeRuntimeFeatureSettings
} from "./schedule-serialization";
import { readPersistedNativeScheduleState, writePersistedNativeScheduleState } from "./schedule-persistence";
import { runNativeSaveOperation } from "./schedule-save-operation";

export default function useScheduleController({ registerHostActions } = {}) {

  const { t } = useNativeScheduleI18n();
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
  const [featureSettings, setFeatureSettings] = useState(() => ({ ...DEFAULT_RUNTIME_FEATURE_SETTINGS }));
  const holdMinutesValue = Number(holdMinutes);
  const dwellMinutesValue = Number(dwellMinutes);
  const holdMinutesTooSmall =
    holdMinutes !== "" && Number.isFinite(holdMinutesValue) && holdMinutesValue < MIN_LINE_SETTING_MINUTES;
  const dwellMinutesTooSmall =
    dwellMinutes !== "" && Number.isFinite(dwellMinutesValue) && dwellMinutesValue < MIN_LINE_SETTING_MINUTES;
  const [summaryEntries, setSummaryEntries] = useState(() => normalizeSummaryEntries([], t));
  const [autoRules, setAutoRules] = useState([]);
  const [manualDrafts, setManualDrafts] = useState([]);
  const [planRefsByLine, setPlanRefsByLine] = useState({});
  const [manualInput, setManualInput] = useState("12:00");
  const [editorStart, setEditorStart] = useState("08:00");
  const [editorEnd, setEditorEnd] = useState("10:00");
  const [autoFrequencyText, setAutoFrequencyText] = useState("4");
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
  const suppressNextSnapshotRef = useRef(false);
  const skipNextBackendSaveRef = useRef(false);
  const latestDraftSaveOperationRunIdRef = useRef(0);
  const latestApplySaveOperationRunIdRef = useRef(0);
  const applyingSaveOperationRef = useRef(false);

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
  const hasAppliedSchedule = summaryEntries.length > 0 && currentSummarySignature === appliedSummarySignature;
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

  function applyHydratedState(snapshot, metadataSnapshot = null) {
    lastHydratedSnapshotRef.current = snapshot ?? null;
    skipNextBackendSaveRef.current = true;
    const persistedState = readPersistedNativeScheduleState();
    const runtimeCatalog = buildRuntimeCatalog(
      snapshot,
      metadataSnapshot,
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

    const nextManualDrafts = mapSnapshotManualRows(snapshot?.manualRows, sourceLine.id);
    const nextAutoRules = mapSnapshotAutoRules(snapshot?.autoRules, sourceLine.id);
    const nextSummaryEntries = normalizeSummaryEntries(
      mapSnapshotSummaryRows(
        Array.isArray(snapshot?.combinedDraftRows)
          ? snapshot.combinedDraftRows
          : Array.isArray(snapshot?.lineDraftRows)
            ? snapshot.lineDraftRows
            : []
      ),
      t
    );
    const nextSummarySignature = getSummaryRowsSignature(nextSummaryEntries);
    const currentSummaryRowKeys = nextSummaryEntries.map((row) => getSummaryRowKey(row));
    const nextAppliedEntries = normalizeSummaryEntries(
      mapSnapshotSummaryRows(Array.isArray(snapshot?.appliedRows) ? snapshot.appliedRows : []),
      t
    );
    const nextAppliedRowKeysFromRuntime = new Set(nextAppliedEntries.map((row) => getSummaryRowKey(row)));
    const previousAppliedRowKeySet = new Set(
      Array.isArray(appliedSummaryRowKeys) ? appliedSummaryRowKeys : []
    );
    const nextAppliedSummarySignature =
      nextAppliedEntries.length > 0
        ? getSummaryRowsSignature(nextAppliedEntries)
        : snapshot?.rulesApplied || snapshot?.draftApplied
          ? nextSummarySignature
          : (appliedSummarySignature || "");
    const nextAppliedSummaryRowKeys =
      nextAppliedEntries.length > 0
        ? currentSummaryRowKeys.filter((rowKey) => nextAppliedRowKeysFromRuntime.has(rowKey))
        : snapshot?.rulesApplied || snapshot?.draftApplied
          ? currentSummaryRowKeys
          : currentSummaryRowKeys.filter((rowKey) => previousAppliedRowKeySet.has(rowKey));

    setActiveRightTab((current) => (current === "manual" ? "manual" : "auto"));
    setSelectedLineId(sourceLine.id);
    setSelectedLineType(sourceLine.kind);
    setSelectedDepot(sourceLine.depotId);
    setOrigin(sourceLine.originId);
    setHoldMinutes(sourceLine.hold);
    setDwellMinutes(sourceLine.dwell);
    setFeatureSettings(normalizeRuntimeFeatureSettings(snapshot?.featureSettings));
    setSummaryEntries(nextSummaryEntries);
    setAutoRules(nextAutoRules);
    setManualDrafts(nextManualDrafts);
    setPlanRefsByLine(mapSnapshotPlanRefs(snapshot?.planRefs));
    setManualInput(typeof persistedState?.manualInput === "string" ? persistedState.manualInput : "12:00");
    setEditorStart(typeof persistedState?.editorStart === "string" ? persistedState.editorStart : "08:00");
    setEditorEnd(typeof persistedState?.editorEnd === "string" ? persistedState.editorEnd : "10:00");
    setAutoFrequencyText(typeof persistedState?.autoFrequencyText === "string" ? persistedState.autoFrequencyText : "4");
    setAutoOffsetDirection(typeof persistedState?.autoOffsetDirection === "string" ? persistedState.autoOffsetDirection : "");
    setAutoOffsetMinutesText(typeof persistedState?.autoOffsetMinutesText === "string" ? persistedState.autoOffsetMinutesText : "");
    setAppliedSummarySignature(nextAppliedSummarySignature);
    setAppliedSummaryRowKeys(nextAppliedSummaryRowKeys);
    setSummaryFilter(
      persistedState?.summaryFilter === "current" || persistedState?.summaryFilter === "local" || persistedState?.summaryFilter === "express"
        ? persistedState.summaryFilter
        : "all"
    );
    setPanelMessage(null);
    hasHydratedRuntimeRef.current = true;
  }

  useEffect(() => {
    let disposed = false;

    async function hydrateFromBackend({ forceRefresh = false } = {}) {
      try {
        const snapshot = forceRefresh
          ? await workbenchApi.refreshSnapshot?.()
          : await workbenchApi.loadSnapshot?.();
        let metadata = null;

        try {
          metadata = await workbenchApi.refreshMetadata?.();
        } catch {}

        if (!disposed) {
          applyHydratedState(snapshot, metadata);
        }
      } catch (error) {
        if (!disposed) {
          console.error("[RT Native Schedule] backend hydrate failed", error);
        }
      }
    }

    hydrateFromBackend();
    const unsubscribe = workbenchApi.onSnapshotChanged?.((snapshot) => {
      if (suppressNextSnapshotRef.current) {
        suppressNextSnapshotRef.current = false;
        return;
      }

      if (!disposed) {
        applyHydratedState(snapshot, null);
      }
    });

    return () => {
      disposed = true;
      unsubscribe?.();
    };
  }, [t, workbenchApi]);

  useEffect(() => {
    if (typeof registerHostActions !== "function") {
      return undefined;
    }

    registerHostActions({
      refreshData: async () => {
        const snapshot = await workbenchApi.refreshSnapshot?.();
        let metadata = null;

        try {
          metadata = await workbenchApi.refreshMetadata?.();
        } catch {}

        applyHydratedState(snapshot, metadata);
      }
    });

    return () => {
      registerHostActions(null);
    };
  }, [registerHostActions, t, workbenchApi]);

  useEffect(() => {
    if (!hasHydratedRuntimeRef.current) {
      return;
    }

    writePersistedNativeScheduleState({
      manualInput,
      editorStart,
      editorEnd,
      autoFrequencyText,
      autoOffsetDirection,
      autoOffsetMinutesText,
      summaryFilter
    });
  }, [
    autoFrequencyText,
    autoOffsetDirection,
    autoOffsetMinutesText,
    editorEnd,
    editorStart,
    manualInput,
    summaryFilter
  ]);

  async function saveNativeWorkbenchDraft({ applyDraft = false } = {}) {
    if (!applyDraft && applyingSaveOperationRef.current) {
      return { success: true, errors: [], warnings: [], version: "", snapshot: null, superseded: true };
    }

    const runRef = applyDraft ? latestApplySaveOperationRunIdRef : latestDraftSaveOperationRunIdRef;
    const runId = runRef.current + 1;
    runRef.current = runId;
    if (applyDraft) {
      applyingSaveOperationRef.current = true;
      setIsApplyingSchedule(true);
      latestDraftSaveOperationRunIdRef.current += 1;
    }

    const currentManualRows = manualDrafts.filter((row) => row?.lineId === selectedLineId || row?.serviceId === selectedLineId);
    const currentAutoRows = autoRules.filter((row) => row?.lineId === selectedLineId || row?.serviceId === selectedLineId);
    const request = {
      selectedLineId,
      selectedEditLine: selectedLineId,
      mergedView: createNativeMergedViewForSave(selectedLineId, lastHydratedSnapshotRef.current?.mergedView),
      manualRows: serializeNativeManualRows(currentManualRows),
      autoRules: serializeNativeAutoRules(currentAutoRows),
      lineDraftRowsByLineId: serializeNativeLineDraftRowsByLineId(summaryEntries),
      planRefs: serializePlanRefs(planRefsByLine),
      lineSettings: serializeNativeLineSettings(LINE_OPTIONS),
      featureSettings: serializeRuntimeFeatureSettings(featureSettings),
      applyDraft,
      nativeScheduleWriter: true,
      returnSnapshot: false
    };

    suppressNextSnapshotRef.current = true;
    try {
      const operationResult = await runNativeSaveOperation(workbenchApi, request, {
        applyDraft,
        shouldContinue: () => runRef.current === runId
      });

      if (operationResult.interrupted) {
        suppressNextSnapshotRef.current = false;
        if (applyDraft) {
          throw new Error("apply-operation-interrupted");
        }

        return { success: true, errors: [], warnings: [], version: "", snapshot: null, superseded: true };
      }

      if (operationResult.superseded) {
        suppressNextSnapshotRef.current = false;
        if (applyDraft) {
          throw new Error("apply-operation-superseded");
        }

        return { success: true, errors: [], warnings: [], version: "", snapshot: null, superseded: true };
      }

      const result = operationResult.result;
      if (result?.snapshot) {
        applyHydratedState(result.snapshot, null);
      } else {
        suppressNextSnapshotRef.current = false;
      }

      return result;
    } catch (error) {
      suppressNextSnapshotRef.current = false;
      throw error;
    } finally {
      if (applyDraft && runRef.current === runId) {
        applyingSaveOperationRef.current = false;
        setIsApplyingSchedule(false);
      }
    }
  }

  useEffect(() => {
    if (!hasHydratedRuntimeRef.current) {
      return undefined;
    }

    if (skipNextBackendSaveRef.current) {
      skipNextBackendSaveRef.current = false;
      return undefined;
    }

    if (applyingSaveOperationRef.current) {
      return undefined;
    }

    const timeoutId = window.setTimeout(async () => {
      try {
        if (applyingSaveOperationRef.current) {
          return;
        }

        await saveNativeWorkbenchDraft({ applyDraft: false });
      } catch {}
    }, 1800);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [
    autoRules,
    featureSettings,
    catalogRevision,
    manualDrafts,
    selectedLineId,
    summaryEntries,
    t,
    planRefsByLine,
    workbenchApi
  ]);

  function clearPanelMessage() {
    setPanelMessage(null);
  }

  function dropPlanRefs(lineIds) {
    const ids = [...new Set((Array.isArray(lineIds) ? lineIds : [lineIds]).filter(Boolean))];
    if (ids.length === 0) {
      return;
    }

    setPlanRefsByLine((current) => {
      let changed = false;
      const next = { ...(current || {}) };
      ids.forEach((lineId) => {
        if (Object.prototype.hasOwnProperty.call(next, lineId)) {
          delete next[lineId];
          changed = true;
        }
      });
      return changed ? next : current;
    });
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
    dropPlanRefs(selectedLine.id);
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

  function handleFeatureToggle(featureKey) {
    if (!Object.prototype.hasOwnProperty.call(DEFAULT_RUNTIME_FEATURE_SETTINGS, featureKey)) {
      return;
    }

    markLocalDataDirty();
    setFeatureSettings((current) => ({
      ...normalizeRuntimeFeatureSettings(current),
      [featureKey]: !normalizeRuntimeFeatureSettings(current)[featureKey]
    }));
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
    const target = summaryEntries.find((row) => row.id === rowId);
    markLocalDataDirty();
    dropPlanRefs(target?.lineId || target?.serviceId || "");
    setSummaryEntries((current) => current.filter((row) => row.id !== rowId));
  }

  function clearSummaryTable() {
    markLocalDataDirty();
    dropPlanRefs(selectedLine.id);
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
    dropPlanRefs(selectedLine.id);
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
    dropPlanRefs(selectedLine.id);
    setSummaryEntries((current) => normalizeSummaryEntries([...current, ...importedRows], t));
    setPanelMessage({
      scope: "auto",
      tone: "neutral",
      text: plan.skippedCount > 0
        ? t("nativeSchedule.message.auto.importedWithSkipped", { count: importedRows.length, skipped: plan.skippedCount })
        : t("nativeSchedule.message.auto.imported", { count: importedRows.length })
    });
  }

  async function handleApplySchedule() {
    setPanelMessage({ scope: "summary", tone: "neutral", text: t("nativeSchedule.message.summary.applying") });
    try {
      const result = await saveNativeWorkbenchDraft({ applyDraft: true });
      if (result?.superseded) {
        throw new Error("apply-operation-superseded");
      }

      if (!result?.success) {
        const message = Array.isArray(result?.errors) && result.errors.length > 0
          ? result.errors.join("; ")
          : t("nativeSchedule.message.summary.saveFailed", { message: "unknown" });
        setPanelMessage({ scope: "summary", tone: "error", text: t("nativeSchedule.message.summary.applyFailed", { message }) });
        return;
      }

      if (!result.version) {
        throw new Error("apply-operation-missing-version");
      }

      setPanelMessage({
        scope: "summary",
        tone: "neutral",
        text: t("nativeSchedule.message.summary.applySuccess", { version: result.version || "" })
      });
      setAppliedSummarySignature(getSummaryRowsSignature(summaryEntries));
      setAppliedSummaryRowKeys(summaryEntries.map((row) => getSummaryRowKey(row)));
    } catch (error) {
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
      featureSettings,
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
      toggleFeature: handleFeatureToggle,
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
