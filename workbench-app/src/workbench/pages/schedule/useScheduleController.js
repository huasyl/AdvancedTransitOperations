import { useEffect, useMemo, useRef, useState } from "react";
import {
  buildAutoStagedPlan,
  buildQuickAddPlan,
  getLineKinds,
  getQuickAddSegmentCapacity,
  hasMinimumDepartureGapForOrigin,
  pickMostCrowdedMinute,
  QUICK_ADD_STEP_MINUTES,
  quickMinutesToTime
} from "../../../lib/auto-schedule";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { DEFAULT_SERVICE_PERIODS, moveServiceBoundary } from "../../../lib/service-periods";
import { minutesToTime, timeToMinutes } from "../../../lib/time";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import { validateManualRows } from "../../../lib/validation";
import {
  DEPOT_OPTIONS,
  LINE_OPTIONS,
  ORIGIN_OPTIONS,
  buildPlanLineOptions,
  buildCatalog,
  buildRuntimeCatalog,
  directionFromOffsetMode,
  getLocalizedLineName,
  getReferenceLineIdsForLine,
  normalizeKind,
  offsetModeFromDirection,
  patchRuntimeLineOption,
  replaceRuntimeCatalog,
  sortLineOptions
} from "./schedule-catalog";
import {
  buildCombinedNote,
  buildPreviewMetaText,
  createShortRowId,
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
import { buildCopyPlan } from "./schedule-copy";
import {
  createNativeMergedViewForSave,
  flattenSnapshotLineDraftRowsByLineId,
  mapSnapshotSummaryRows,
  serializeNativeLineDraftRowsByLineId,
  serializeNativeLineSettings,
  serializeRemovedLineIds
} from "./schedule-serialization";
import { runNativeSaveOperation } from "./schedule-save-operation";
import { buildDepartureSegments, buildLineIssues, buildLineRows, rowsInSegment } from "./schedule-view-model";

const DEFAULT_SCHEDULE_MODE = "train";
const RIGHT_TAB_IDS = new Set(["auto", "manual", "copy"]);
const EMPTY_LINE_OPTION = {
  id: "",
  kind: "local",
  depotId: "",
  originId: "",
  signalPriorityEnabled: false,
  hold: "",
  dwell: ""
};

function normalizeScheduleMode(mode) {
  const token = String(mode || "").trim().toLowerCase();
  return token === "subway" || token === "tram" || token === "bus" ? token : DEFAULT_SCHEDULE_MODE;
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


function getQuickLineRowId(row) {
  return row?.lineId || row?.serviceId || "";
}

function getQuickLineSignature(rows, lineId) {
  return (Array.isArray(rows) ? rows : [])
    .filter((row) => getQuickLineRowId(row) === lineId)
    .map((row) => `${row?.time || ""}:${normalizeKind(row?.kind || row?.type)}`)
    .sort()
    .join("|");
}

function getQuickInputSignature(segments) {
  return (Array.isArray(segments) ? segments : [])
    .map((segment) => `${segment?.id || ""}:${segment?.start ?? ""}-${segment?.end ?? ""}:${segment?.count ?? ""}`)
    .join("|");
}

function getQuickPlanSignature(plan) {
  return (Array.isArray(plan?.plannedRows) ? plan.plannedRows : [])
    .map((row) => `${row?.timeMinutes ?? ""}:${normalizeKind(row?.kind)}`)
    .sort((left, right) => left.localeCompare(right))
    .join("|");
}

function placeManualDraftRows(drafts, occupiedRows, lineId, originStationId, minimumScheduleMinutes, t) {
  const rows = [];
  const blockedIds = new Set();
  drafts.forEach((draft) => {
    const candidateMinute = timeToMinutes(draft.time);
    if (candidateMinute === null
      || !hasMinimumDepartureGapForOrigin(candidateMinute, originStationId, occupiedRows, minimumScheduleMinutes)) {
      blockedIds.add(draft.id);
      return;
    }

    occupiedRows.push({
      minute: candidateMinute,
      originStationId
    });
    rows.push(createSummaryEntry({
      id: createShortRowId("m", lineId, draft.id),
      time: draft.time,
      serviceId: draft.serviceId,
      kind: draft.kind,
      source: "manual",
      note: buildCombinedNote("direct", t)
    }, t));
  });
  return { rows, blockedIds };
}

function buildAutoImportedRows(plannedRows, rules, lineId, t) {
  return plannedRows.map((row) => {
    const sourceRule = rules.find((rule) => rule.id === row.ruleId);
    return createSummaryEntry({
      id: createShortRowId("a", lineId, row.ruleId, row.generatedIndex),
      time: minutesToTime(row.timeMinutes),
      serviceId: sourceRule?.serviceId || lineId,
      kind: row.kind,
      source: "auto",
      note: buildCombinedNote(row.noteType, t, {
        minutes: row.offsetMinutes,
        start: row.start,
        end: row.end
      })
    }, t);
  });
}

export default function useScheduleController({ registerHostActions, activeTransportMode = "train", isActive = false, onSnapshot, readServicePeriods, writeServicePeriods } = {}) {

  const { t } = useNativeScheduleI18n();
  const scheduleMode = normalizeScheduleMode(activeTransportMode);
  const supportsExpress = scheduleMode === "train" || scheduleMode === "subway";
  const supportsSignalPriority = scheduleMode === "bus" || scheduleMode === "tram";
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const [activeRightTab, setActiveRightTab] = useState("auto");
  const [autoSubMode, setAutoSubMode] = useState("quick");
  const [scheduleView, setScheduleView] = useState("line");
  const [selectedSegment, setSelectedSegment] = useState(-1);
  const [manualMode, setManualMode] = useState("single");
  const [quickConfigsByLine, setQuickConfigsByLine] = useState({});
  const [quickPeriods, setQuickPeriods] = useState(DEFAULT_SERVICE_PERIODS);
  const [quickImportRecordsByLine, setQuickImportRecordsByLine] = useState({});
  const [catalogRevision, setCatalogRevision] = useState(0);
  const [minimumScheduleMinutes, setMinimumScheduleMinutes] = useState(5);
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
    !Number.isFinite(holdMinutesValue) || holdMinutesValue < minimumScheduleMinutes;
  const dwellMinutesTooSmall =
    !Number.isFinite(dwellMinutesValue) || dwellMinutesValue < minimumScheduleMinutes;
  const [summaryEntries, setSummaryEntries] = useState(() => normalizeSummaryEntries([], t));
  const [autoRules, setAutoRules] = useState([]);
  const [manualDrafts, setManualDrafts] = useState([]);
  const [pendingRemovedLineIds, setPendingRemovedLineIds] = useState([]);
  const [manualInput, setManualInput] = useState("12:00");
  const [copySourceLineId, setCopySourceLineId] = useState("");
  const [copyExcludedSourceRowIds, setCopyExcludedSourceRowIds] = useState([]);
  const [copyPreviewSnapshot, setCopyPreviewSnapshot] = useState(null);
  const [copyImportLocked, setCopyImportLocked] = useState(false);
  const [editorStart, setEditorStart] = useState("08:00");
  const [editorEnd, setEditorEnd] = useState("10:00");
  const [autoFrequencyText, setAutoFrequencyText] = useState("1");
  const [autoOffsetDirection, setAutoOffsetDirection] = useState("");
  const [autoOffsetMinutesText, setAutoOffsetMinutesText] = useState("");
  const [appliedSummarySignature, setAppliedSummarySignature] = useState("");
  const [appliedLineSettingsSignature, setAppliedLineSettingsSignature] = useState("");
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
  const namesRefreshRef = useRef(false);
  const activeModeRef = useRef(scheduleMode);
  const scheduleModeGenerationRef = useRef(0);
  activeModeRef.current = scheduleMode;

  const planLineOptions = useMemo(
    () => buildPlanLineOptions(LINE_OPTIONS),
    [catalogRevision]
  );

  const selectedLine = useMemo(
    () => LINE_OPTIONS.find((line) => line?.id === selectedLineId)
      ?? LINE_OPTIONS[0]
      ?? EMPTY_LINE_OPTION,
    [catalogRevision, scheduleMode, selectedLineId]
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
  const currentKind = useMemo(
    () => (supportsExpress ? normalizeKind(selectedLineType) : "local"),
    [selectedLineType, supportsExpress]
  );
  useEffect(() => {
    if (!isActive) return;
    setQuickPeriods(readServicePeriods?.(scheduleMode, selectedLine.id) || DEFAULT_SERVICE_PERIODS);
  }, [isActive, scheduleMode, selectedLine.id, readServicePeriods]);
  const lineEntries = useMemo(
    () => summaryEntries.filter((row) => getQuickLineRowId(row) === selectedLine.id),
    [summaryEntries, selectedLine.id]
  );
  function withAppliedOccupancy(rows, targetLineId) {
    const keys = new Set(rows.map(getSummaryRowKey));
    const applied = normalizeSummaryEntries(mapSnapshotSummaryRows(lastHydratedSnapshotRef.current?.appliedRows, scheduleMode), t);
    return [...rows, ...applied.filter((row) => getQuickLineRowId(row) !== targetLineId && !keys.has(getSummaryRowKey(row)))];
  }
  const planningRows = useMemo(
    () => scheduleView === "line" ? withAppliedOccupancy(summaryEntries, selectedLine.id) : summaryEntries,
    [scheduleView, summaryEntries, selectedLine.id, catalogRevision, t]
  );
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
  const timedRowIds = useMemo(
    () => scheduleView === "line"
      ? new Set(buildLineRows(lineEntries, lastHydratedSnapshotRef.current, selectedLine.id).filter((row) => row.hasTimedStops).map((row) => row.id))
      : new Set(),
    [scheduleView, lineEntries, catalogRevision, selectedLine.id]
  );
  const removedTimedIds = useMemo(
    () => new Set((quickConfigsByLine[selectedLine.id] || []).flatMap((config) => config.removedIds || [])),
    [quickConfigsByLine, selectedLine.id]
  );
  const retainedLineRows = useMemo(() => {
    const editedPeriods = quickPeriods.filter((_, index) => quickConfigsByLine[selectedLine.id]?.[index]?.edited);
    return lineEntries.filter((row) => !removedTimedIds.has(row.id)
      && (timedRowIds.has(row.id) || !editedPeriods.some((period) => rowsInSegment([row], period).length)));
  }, [lineEntries, removedTimedIds, timedRowIds, quickPeriods, quickConfigsByLine, selectedLine.id]);
  const lineFixedRows = useMemo(
    () => retainedLineRows.filter((row) => timedRowIds.has(row.id)),
    [retainedLineRows, timedRowIds]
  );
  const currentQuickSegments = useMemo(
    () => quickPeriods.map((period, index) => {
      const config = quickConfigsByLine[selectedLine.id]?.[index];
      return config?.edited
        ? {
          ...period,
          edited: true,
          removedIds: config.removedIds,
          count: Math.min(
            config.count,
            Math.max(0, getQuickAddSegmentCapacity(period.start, period.end, minimumScheduleMinutes) - rowsInSegment(lineFixedRows, period).length)
          )
        }
        : { ...period, count: rowsInSegment(lineEntries, period).filter((row) => !timedRowIds.has(row.id)).length };
    }),
    [quickPeriods, quickConfigsByLine, selectedLine.id, lineEntries, lineFixedRows, timedRowIds, minimumScheduleMinutes]
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
      minimumScheduleMinutes,
      currentRows: planningRows,
      rowsForLine: currentAutoRules,
      selectedEditLine: selectedLine.id,
      referenceLineIds: getReferenceLineIdsForLine(selectedLine, currentKind),
      lineOptions: planLineOptions,
      replaceExistingAutoRows: false
    });
  }, [currentAutoRules, currentKind, planLineOptions, selectedLine, summaryEntries, planningRows, minimumScheduleMinutes]);
  const currentQuickPlan = useMemo(
    () => buildQuickAddPlan({
      minimumScheduleMinutes,
      segments: currentQuickSegments,
      currentRows: planningRows,
      selectedLineId: selectedLine.id,
      originStationId: selectedLine.originStationId || "",
      kind: currentKind,
      fixedMinutes: lineFixedRows.map((row) => timeToMinutes(row.time))
    }),
    [currentKind, currentQuickSegments, selectedLine.id, selectedLine.originStationId, planningRows, lineFixedRows, minimumScheduleMinutes]
  );
  const quickLineCandidate = useMemo(() => {
    const edited = currentQuickSegments.filter((segment) => segment.edited);
    const usedIds = new Set(retainedLineRows.map((row) => row.id));
    const generated = currentQuickPlan.plannedRows.filter((row) => edited.some((segment) => segment.id === row.segmentId))
      .map((row) => {
        const signature = getQuickInputSignature(edited);
        let id = createShortRowId("q", selectedLine.id, row.segmentId, row.generatedIndex, signature);
        for (let attempt = 1; usedIds.has(id); attempt += 1) {
          id = createShortRowId("q", selectedLine.id, row.segmentId, row.generatedIndex, signature, attempt);
        }
        usedIds.add(id);
        return createSummaryEntry({
          id,
          time: minutesToTime(row.timeMinutes),
          lineId: selectedLine.id,
          kind: row.kind,
          source: "auto",
          note: buildCombinedNote("generated", t, {
            start: quickMinutesToTime(currentQuickSegments.find((segment) => segment.id === row.segmentId)?.start),
            end: quickMinutesToTime(currentQuickSegments.find((segment) => segment.id === row.segmentId)?.end)
          })
        }, t);
      });
    return [...retainedLineRows, ...generated];
  }, [currentQuickSegments, currentQuickPlan, retainedLineRows, selectedLine.id, t]);
  const lineDraftRows = useMemo(
    () => scheduleView === "line"
      ? [...planningRows.filter((row) => getQuickLineRowId(row) !== selectedLine.id), ...quickLineCandidate]
      : planningRows,
    [scheduleView, planningRows, selectedLine.id, quickLineCandidate]
  );
  const quickLineConflict = useMemo(() => buildSummaryRowsWithConflicts([
    ...planningRows.filter((row) => getQuickLineRowId(row) !== selectedLine.id),
    ...quickLineCandidate
  ], t, null, minimumScheduleMinutes).find((row) => row.serviceId === selectedLine.id && row.isConflict), [planningRows, selectedLine.id, quickLineCandidate, t, minimumScheduleMinutes]);
  const quickInputSignature = useMemo(
    () => getQuickInputSignature(currentQuickSegments),
    [currentQuickSegments]
  );
  const quickPlanSignature = useMemo(
    () => getQuickPlanSignature(currentQuickPlan),
    [currentQuickPlan]
  );
  const quickActualLineSignature = useMemo(
    () => getQuickLineSignature(summaryEntries, selectedLine.id),
    [selectedLine.id, summaryEntries]
  );
  const quickDay = useMemo(() => {
    const times = quickLineCandidate.map((row) => row.time).sort();
    return { count: times.length, first: times[0], last: times[times.length - 1] };
  }, [quickLineCandidate]);
  const quickPending = useMemo(
    () => getSummarySignatureFromRowKeys(quickLineCandidate.map(getSummaryRowKey))
      !== getSummarySignatureFromRowKeys(appliedSummaryRowKeys.filter((key) => key.startsWith(`${selectedLine.id}|`))),
    [quickLineCandidate, appliedSummaryRowKeys, selectedLine.id]
  );
  const quickImportRecord = quickImportRecordsByLine[selectedLine.id] || null;
  const isQuickImported = !!quickImportRecord
    && quickImportRecord.inputSignature === quickInputSignature
    && quickImportRecord.planSignature === quickPlanSignature
    && quickImportRecord.outputSignature === quickActualLineSignature;
  const isQuickAllZero = currentQuickPlan.targetCount <= 0;
  const isQuickImportDisabled = selectedLine.dispatchSupported === false
    || isQuickAllZero
    || currentQuickPlan.successCount <= 0
    || isQuickImported;
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
        previewMeta: buildPreviewMetaText(preview, currentAutoPlan.hasKindConflict, t, { detailedSkipReason: true, minimumScheduleMinutes })
      };
    }),
    [currentAutoPlan.hasKindConflict, currentAutoPlan.previewsByRule, currentAutoRules, minimumScheduleMinutes, t]
  );
  const liveAutoPreview = useMemo(() => {
    if (String(editorStart || "").length < 5 || String(editorEnd || "").length < 5) {
      return { times: [], entries: [], meta: "" };
    }

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
      minimumScheduleMinutes,
      currentRows: lineDraftRows,
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
      meta: buildPreviewMetaText(preview, plan.hasKindConflict, t, { minimumScheduleMinutes })
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
    lineDraftRows,
    planLineOptions,
    minimumScheduleMinutes,
    t
  ]);
  const currentSummarySignature = useMemo(
    () => getSummaryRowsSignature(summaryEntries),
    [summaryEntries]
  );
  const currentLineSettingsSignature = useMemo(
    () => JSON.stringify(serializeNativeLineSettings(LINE_OPTIONS, scheduleMode)),
    [catalogRevision, scheduleMode]
  );
  const appliedSummaryRowKeySet = useMemo(
    () => new Set(Array.isArray(appliedSummaryRowKeys) ? appliedSummaryRowKeys : []),
    [appliedSummaryRowKeys]
  );
  const hasAppliedSchedule = currentSummarySignature === appliedSummarySignature
    && currentLineSettingsSignature === appliedLineSettingsSignature
    && !holdMinutesTooSmall
    && !dwellMinutesTooSmall
    && pendingRemovedLineIds.length === 0;
  const summaryRows = useMemo(
    () => buildSummaryRowsWithConflicts(summaryEntries, t, appliedSummaryRowKeySet, minimumScheduleMinutes),
    [appliedSummaryRowKeySet, summaryEntries, t, minimumScheduleMinutes]
  );
  const lineRows = useMemo(
    () => buildLineRows(summaryRows, lastHydratedSnapshotRef.current, selectedLine.id, minimumScheduleMinutes),
    [summaryRows, catalogRevision, selectedLine.id, minimumScheduleMinutes]
  );
  const lineIssues = useMemo(() => {
    const issues = buildLineIssues(summaryRows, lastHydratedSnapshotRef.current, LINE_OPTIONS, minimumScheduleMinutes);
    issues.set(selectedLine.id, { ...issues.get(selectedLine.id), hold: holdMinutesTooSmall, dwell: dwellMinutesTooSmall });
    return issues;
  }, [summaryRows, catalogRevision, minimumScheduleMinutes, selectedLine.id, holdMinutesTooSmall, dwellMinutesTooSmall]);
  const lineSettingsPending = useMemo(() => {
    if (!appliedLineSettingsSignature) return false;
    const pick = (signature) => JSON.stringify(JSON.parse(signature).find((item) => item.lineId === selectedLine.id));
    return pick(currentLineSettingsSignature) !== pick(appliedLineSettingsSignature);
  }, [currentLineSettingsSignature, appliedLineSettingsSignature, selectedLine.id]);
  const visibleSummaryRows = useMemo(() => {
    if (summaryFilter === "current") {
      return summaryRows.filter((row) => row.serviceId === selectedLine.id);
    }

    if (summaryFilter === "local") {
      return summaryRows.filter((row) => row.kind === "local");
    }

    if (supportsExpress && summaryFilter === "express") {
      return summaryRows.filter((row) => row.kind === "express");
    }

    return summaryRows;
  }, [selectedLine.id, summaryFilter, summaryRows, supportsExpress]);
  const conflictCount = summaryRows.filter((row) => row.isConflict).length;
  const earliestStart = visibleSummaryRows[0]?.time || "--:--";
  const summaryStateLabel = hasAppliedSchedule ? t("nativeSchedule.summary.section.applied") : t("nativeSchedule.summary.section.pending");
  const summaryFooterNote = panelMessage?.scope === "summary" ? panelMessage : null;
  const autoFooterNote =
    autoSubMode === "custom" && panelMessage?.scope === "auto"
      ? panelMessage
      : currentAutoPlan.hasKindConflict
        && autoSubMode === "custom"
        ? { scope: "auto", tone: "error", text: t("nativeSchedule.message.auto.kindConflict") }
        : null;
  const quickFooterNote = panelMessage?.scope === "quick" ? panelMessage : null;
  const manualFooterNote = panelMessage?.scope === "manual" ? panelMessage : null;
  const copySourceOptions = useMemo(
    () => sortLineOptions(LINE_OPTIONS, t)
      .filter((line) => line?.id && line.id !== selectedLine.id && line.dispatchSupported !== false)
      .map((line) => ({
        value: line.id,
        label: getLocalizedLineName(line, t)
      })),
    [catalogRevision, selectedLine.id, t]
  );
  const copySourceLine = useMemo(
    () => copySourceOptions.find((option) => option.value === copySourceLineId) ?? null,
    [copySourceLineId, copySourceOptions]
  );
  const allCopySourceRows = useMemo(
    () => (copySourceLine && copyPreviewSnapshot?.sourceLineId === copySourceLine.value
      ? copyPreviewSnapshot.allSourceRows
      : []),
    [copyPreviewSnapshot, copySourceLine]
  );
  const copySourceRows = useMemo(
    () => (copySourceLine && copyPreviewSnapshot?.sourceLineId === copySourceLine.value
      ? copyPreviewSnapshot.sourceRows
      : []),
    [copyPreviewSnapshot, copySourceLine]
  );
  const copyPlan = useMemo(
    () => buildCopyPlan({
      minimumScheduleMinutes,
      sourceRows: copySourceRows,
      occupiedRows: copyPreviewSnapshot?.occupiedRows || [],
      targetOriginStationId: copyPreviewSnapshot?.targetOriginStationId || "",
      targetKind: copyPreviewSnapshot?.targetKind || "local"
    }),
    [copyPreviewSnapshot, copySourceRows, minimumScheduleMinutes]
  );
  const copyPreviewText = useMemo(() => {
    if (!copySourceLine) {
      return t("nativeSchedule.copy.preview.noSource");
    }

    if (copySourceRows.length === 0) {
      return t(allCopySourceRows.length === 0
        ? "nativeSchedule.copy.preview.sourceEmpty"
        : "nativeSchedule.copy.preview.noRows");
    }

    return t("nativeSchedule.copy.preview.summary", {
      sourceTotal: copySourceRows.length,
      copied: copyPlan.copiedCount,
      adjusted: copyPlan.adjustedCount,
      skipped: copyPlan.skippedCount
    });
  }, [allCopySourceRows.length, copyPlan, copySourceLine, copySourceRows.length, t]);
  const copyEmptyText = copySourceLine && copySourceRows.length === 0
    ? t(allCopySourceRows.length === 0
      ? "nativeSchedule.copy.preview.sourceEmpty"
      : "nativeSchedule.copy.preview.noRows")
    : "";
  const copyFooterNote = panelMessage?.scope === "copy" ? panelMessage : null;
  const copyImportDisabled = copyImportLocked
    || copySourceRows.length === 0
    || copyPlan.copiedCount <= 0;

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
    const { preservePendingRemovedLineIds = false, preserveQuickState = false, preserveLocalLinesExcept = "" } = options;
    const targetMode = normalizeScheduleMode(expectedMode);
    if (!shouldConsumeSchedulePayload(snapshot, targetMode)) {
      return false;
    }
    onSnapshot?.(targetMode, snapshot);

    const scopedMetadata = shouldConsumeSchedulePayload(metadataSnapshot, targetMode)
      ? metadataSnapshot
      : null;
    lastHydratedSnapshotRef.current = snapshot ?? null;
    setMinimumScheduleMinutes(Number(snapshot?.minimumScheduleMinutes) === 1 ? 1 : 5);
    const runtimeCatalog = buildRuntimeCatalog(
      snapshot,
      scopedMetadata,
      null,
      t
    );
    const savedSettingsSignature = JSON.stringify(serializeNativeLineSettings(runtimeCatalog.lineOptions, targetMode));
    if (preserveLocalLinesExcept) {
      runtimeCatalog.lineOptions = runtimeCatalog.lineOptions.map((line) => line.id === preserveLocalLinesExcept
        ? line
        : LINE_OPTIONS.find((current) => current.id === line.id) || line);
    }
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
      EMPTY_LINE_OPTION.id;
    const sourceLine =
      runtimeCatalog.lineOptions.find((line) => line?.id === sourceLineId) ??
      runtimeCatalog.lineOptions[0] ??
      EMPTY_LINE_OPTION;

    const restoredDraftRows = flattenSnapshotLineDraftRowsByLineId(snapshot?.lineDraftRowsByLineId, targetMode);
    const nextSummaryEntries = normalizeSummaryEntries(
      restoredDraftRows.length > 0
        ? restoredDraftRows
        : mapSnapshotSummaryRows(Array.isArray(snapshot?.appliedRows) ? snapshot.appliedRows : [], targetMode),
      t
    );
    const appliedSummaryEntries = normalizeSummaryEntries(
      mapSnapshotSummaryRows(Array.isArray(snapshot?.appliedRows) ? snapshot.appliedRows : [], targetMode),
      t
    );
    const nextSummarySignature = getSummaryRowsSignature(appliedSummaryEntries);
    const currentSummaryRowKeys = appliedSummaryEntries.map((row) => getSummaryRowKey(row));
    logBackendCleanup(snapshot?.cleanupInfo, "snapshot");

    setActiveRightTab((current) => (RIGHT_TAB_IDS.has(current) ? current : "auto"));
    if (!preserveQuickState) {
      setAutoSubMode("quick");
      setQuickConfigsByLine({});
    }
    setQuickImportRecordsByLine((current) => preserveLocalLinesExcept
      ? Object.fromEntries(Object.entries(current).filter(([lineId]) => lineId !== preserveLocalLinesExcept))
      : {});
    if (!preserveLocalLinesExcept) {
      setSelectedLineId(sourceLine.id);
      setSelectedLineType(supportsExpress ? sourceLine.kind : "local");
      setSelectedDepot(sourceLine.depotId);
      setOrigin(sourceLine.originId);
      setHoldMinutes(sourceLine.hold);
      setDwellMinutes(sourceLine.dwell);
    }
    setSummaryEntries((current) => preserveLocalLinesExcept
      ? [...current.filter((row) => getQuickLineRowId(row) !== preserveLocalLinesExcept), ...nextSummaryEntries.filter((row) => getQuickLineRowId(row) === preserveLocalLinesExcept)]
      : nextSummaryEntries);
    setAutoRules((current) => preserveLocalLinesExcept ? current.filter((row) => getQuickLineRowId(row) !== preserveLocalLinesExcept) : []);
    setManualDrafts((current) => preserveLocalLinesExcept ? current.filter((row) => getQuickLineRowId(row) !== preserveLocalLinesExcept) : []);
    if (preserveLocalLinesExcept) {
      setQuickConfigsByLine((current) => Object.fromEntries(Object.entries(current).filter(([lineId]) => lineId !== preserveLocalLinesExcept)));
    }
    if (!preservePendingRemovedLineIds) {
      setPendingRemovedLineIds([]);
    }
    if (!preserveLocalLinesExcept) {
      setManualInput("12:00");
      setEditorStart("08:00");
      setEditorEnd("10:00");
      setAutoFrequencyText("1");
      setAutoOffsetDirection("");
      setAutoOffsetMinutesText("");
    }
    setAppliedSummarySignature(nextSummarySignature);
    setAppliedLineSettingsSignature(savedSettingsSignature);
    setAppliedSummaryRowKeys(currentSummaryRowKeys);
    if (!preserveLocalLinesExcept) setSummaryFilter("all");
    setPanelMessage(null);
    hasHydratedRuntimeRef.current = true;
    return true;
  }

  function acceptAppliedLines(snapshot, lineIds) {
    lineIds.forEach((lineId) => applyHydratedState(snapshot, null, scheduleMode, {
      preservePendingRemovedLineIds: true,
      preserveQuickState: true,
      preserveLocalLinesExcept: lineId
    }));
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

    setMinimumScheduleMinutes(Number(metadata?.minimumScheduleMinutes) === 1 ? 1 : 5);

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
      setCopyPreviewSnapshot(null);
      setCopyImportLocked(false);
      setCopyExcludedSourceRowIds([]);
      return;
    }

    const selectedLineReplaced = !!previousSelectedLine
      && nextLine.id === selectedLineId
      && getLineRuntimeToken(nextLine) !== getLineRuntimeToken(previousSelectedLine);
    const changedLine = nextLine.id !== selectedLineId || selectedLineReplaced;
    if (changedLine) {
      setCopyExcludedSourceRowIds([]);
      setSelectedLineId(nextLine.id);
      setSelectedLineType(supportsExpress ? nextLine.kind : "local");
      setSelectedDepot(nextLine.depotId);
      setOrigin(nextLine.originId);
      setHoldMinutes(nextLine.hold);
      setDwellMinutes(nextLine.dwell);
    }
  }

  async function refreshNames() {
    if (namesRefreshRef.current) {
      return;
    }

    const modeAtRequest = scheduleMode;
    const generation = scheduleModeGenerationRef.current;
    namesRefreshRef.current = true;
    try {
      const metadata = await workbenchApi.refreshMetadata?.({
        mode: modeAtRequest,
        preferredLineId: selectedLineId,
        namesOnly: true
      });
      if (!isTrustedCatalogPayload(metadata, modeAtRequest)
        || !isCurrentModeRequest(modeAtRequest, generation)) {
        return;
      }

      const lineNames = new Map(
        (Array.isArray(metadata?.lines) ? metadata.lines : [])
          .filter((line) => line?.id)
          .map((line) => [line.id, line])
      );
      LINE_OPTIONS.forEach((line) => {
        const current = lineNames.get(line?.id);
        if (!current) {
          return;
        }

        patchRuntimeLineOption(line.id, {
          name: current.name || line.name,
          originStationName: current.originStationName || line.originStationName
        });
      });

      const originNames = new Map(
        (Array.isArray(metadata?.lines) ? metadata.lines : [])
          .filter((line) => line?.originStationId && line?.originStationName)
          .map((line) => [line.originStationId, line.originStationName])
      );
      for (let index = 0; index < ORIGIN_OPTIONS.length; index += 1) {
        const originOption = ORIGIN_OPTIONS[index];
        const nextName = originNames.get(originOption?.id);
        if (nextName) {
          ORIGIN_OPTIONS[index] = { ...originOption, label: nextName };
        }
      }

      const depotNames = new Map(
        (Array.isArray(metadata?.depots) ? metadata.depots : [])
          .filter((depot) => depot?.id)
          .map((depot) => [depot.id, depot.name || depot.id])
      );
      for (let index = 0; index < DEPOT_OPTIONS.length; index += 1) {
        const depot = DEPOT_OPTIONS[index];
        const nextName = depotNames.get(depot?.id);
        if (nextName) {
          DEPOT_OPTIONS[index] = { ...depot, label: nextName };
        }
      }
      bumpCatalogRevision();
    } catch {
    } finally {
      namesRefreshRef.current = false;
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
    setAutoSubMode("quick");
    setQuickConfigsByLine({});
    setQuickImportRecordsByLine({});
    setCopySourceLineId("");
    setCopyExcludedSourceRowIds([]);
    setCopyPreviewSnapshot(null);
    setCopyImportLocked(false);
    setPendingRemovedLineIds([]);
    setAppliedSummarySignature("");
    setAppliedLineSettingsSignature("");
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
      onSnapshot?.(modeAtRequest, null);
      cleanupInvalidatedLinesLocally(lineIds);
      refreshCatalog(modeAtRequest);
    });

    return () => {
      unsubscribe?.();
    };
  }, [scheduleMode, selectedLineId, t, workbenchApi]);

  useEffect(() => {
    const unsubscribe = workbenchApi.onCatalogChanged?.((event) => {
      if (event?.rulesOnly) {
        setMinimumScheduleMinutes(Number(event.minimumScheduleMinutes) === 1 ? 1 : 5);
        onSnapshot?.(scheduleMode, event);
        return;
      }
      const modeAtRequest = normalizeScheduleMode(event?.mode || scheduleMode);
      if (modeAtRequest !== scheduleMode) {
        return;
      }

      onSnapshot?.(modeAtRequest, null);
      refreshCatalog(modeAtRequest);
    }, true);

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

  async function saveNativeWorkbenchDraft({ applyDraft = false, candidateRows = summaryEntries, targetLineId = "" } = {}) {
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
      applyingSaveOperationRef.current = targetLineId || true;
      setIsApplyingSchedule(true);
      latestDraftSaveOperationRunIdRef.current += 1;
    }

    const requestedLineId = targetLineId || selectedLineId;
    const lineDraftRowsByLineId = serializeNativeLineDraftRowsByLineId(
      targetLineId ? candidateRows.filter((row) => getQuickLineRowId(row) === targetLineId) : candidateRows,
      scheduleMode
    );
    if (applyDraft && requestedLineId && !lineDraftRowsByLineId.some((block) => block?.lineId === requestedLineId)) {
      lineDraftRowsByLineId.push({ lineId: requestedLineId, lineDraftRows: [] });
    }
    const request = {
      mode: requestMode,
      selectedLineId: requestedLineId,
      selectedEditLine: requestedLineId,
      mergedView: createNativeMergedViewForSave(requestedLineId, lastHydratedSnapshotRef.current?.mergedView, scheduleMode),
      lineDraftRowsByLineId,
      lineSettings: serializeNativeLineSettings(LINE_OPTIONS, scheduleMode),
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
        if (isCurrentModeRequest(requestMode, requestGeneration) && (!targetLineId || result?.success === true)) {
          applyHydratedState({
            ...result.snapshot,
            featureSettings: lastHydratedSnapshotRef.current?.featureSettings || result.snapshot.featureSettings
          }, null, requestMode, {
            preservePendingRemovedLineIds: result?.success !== true,
            preserveQuickState: true,
            preserveLocalLinesExcept: targetLineId
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

  function isLineBeingSaved(lineId = selectedLine.id) {
    const applying = applyingSaveOperationRef.current;
    return Boolean(applying && (applying === true || applying === lineId));
  }

  function whenEditable(action) {
    return (...args) => { if (!isLineBeingSaved()) return action(...args); };
  }

  function markLocalDataDirty() {
    clearPanelMessage();
  }

  function getCopyTargetKind(line = selectedLine) {
    return supportsExpress ? normalizeKind(line?.kind || selectedLineType) : "local";
  }

  function buildCopyPreviewSnapshot({
    rows = summaryEntries,
    sourceId = copySourceLineId,
    targetLine = selectedLine,
    excludedRowIds = copyExcludedSourceRowIds,
    targetKind = getCopyTargetKind(targetLine)
  } = {}) {
    if (!sourceId || !targetLine?.id || sourceId === targetLine.id) {
      return null;
    }

    const allSourceRows = (Array.isArray(rows) ? rows : [])
      .filter((row) => row?.lineId === sourceId || row?.serviceId === sourceId)
      .map((row) => ({ ...row }));
    const excludedRowIdSet = new Set(Array.isArray(excludedRowIds) ? excludedRowIds : []);
    const sourceRows = allSourceRows.filter((row) => !excludedRowIdSet.has(row.id));
    const occupiedRows = (scheduleView === "line" ? withAppliedOccupancy(rows, targetLine.id) : rows)
      .filter((row) => row?.lineId !== targetLine.id && row?.serviceId !== targetLine.id)
      .map((row) => ({ ...row }));

    return {
      sourceLineId: sourceId,
      allSourceRows,
      sourceRows,
      occupiedRows,
      targetOriginStationId: targetLine.originStationId || "",
      targetKind
    };
  }

  function refreshCopyPreview({
    rows = summaryEntries,
    sourceId = copySourceLineId,
    targetLine = selectedLine,
    excludedRowIds = copyExcludedSourceRowIds,
    targetKind = getCopyTargetKind(targetLine)
  } = {}) {
    setCopyPreviewSnapshot(buildCopyPreviewSnapshot({
      rows,
      sourceId,
      targetLine,
      excludedRowIds,
      targetKind
    }));
    setCopyImportLocked(false);
  }

  function invalidateQuickImport(lineId = selectedLine.id) {
    if (!lineId) {
      return;
    }

    setQuickImportRecordsByLine((current) => {
      if (!current[lineId]) {
        return current;
      }
      const next = { ...current };
      delete next[lineId];
      return next;
    });
  }

  function applySelectedLine(nextLine) {
    if (!nextLine) {
      return;
    }

    if (nextLine.id !== selectedLineId) {
      setCopyExcludedSourceRowIds([]);
      setCopyPreviewSnapshot(null);
      setCopyImportLocked(false);
      if (copySourceLineId === nextLine.id) {
        setCopySourceLineId("");
      } else if (activeRightTab === "copy") {
        refreshCopyPreview({
          sourceId: copySourceLineId,
          targetLine: nextLine,
          targetKind: getCopyTargetKind(nextLine)
        });
      }
    }
    setSelectedLineId(nextLine.id);
    setSelectedLineType(supportsExpress ? nextLine.kind : "local");
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

    if (!supportsExpress && nextType !== "local") {
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
    invalidateQuickImport();
  }

  function handleSignalPrioritySelect(enabled) {
    if (!supportsSignalPriority) {
      return;
    }
    const nextEnabled = enabled === true;
    if (selectedLine.signalPriorityEnabled === nextEnabled) {
      clearPanelMessage();
      return;
    }
    markLocalDataDirty();
    updateRuntimeLineOption(selectedLine.id, { signalPriorityEnabled: nextEnabled });
  }

  function handleDepotChange(value) {
    markLocalDataDirty();
    setSelectedDepot(value);
    updateRuntimeLineOption(selectedLine.id, { depotId: value });
  }

  function handleHoldMinutesChange(value) {
    const numeric = Number(value);
    if (!Number.isFinite(numeric) || numeric < minimumScheduleMinutes) {
      setHoldMinutes(value);
      return;
    }
    markLocalDataDirty();
    setHoldMinutes(value);
    updateRuntimeLineOption(selectedLine.id, { hold: value });
  }

  function handleDwellMinutesChange(value) {
    const numeric = Number(value);
    if (!Number.isFinite(numeric) || numeric < minimumScheduleMinutes) {
      setDwellMinutes(value);
      return;
    }
    markLocalDataDirty();
    setDwellMinutes(value);
    updateRuntimeLineOption(selectedLine.id, { dwell: value });
  }

  function handleEditorStartChange(value) {
    clearPanelMessage();
    setEditorStart(value);
  }

  function handleEditorEndChange(value) {
    clearPanelMessage();
    setEditorEnd(value);
  }

  function handleAutoFrequencyChange(value) {
    clearPanelMessage();
    setAutoFrequencyText(normalizeFrequencyInput(value));
  }

  function handleManualInputChange(value) {
    clearPanelMessage();
    setManualInput(normalizeTimeInput(value));
  }

  function handleCopySourceChange(nextLineId) {
    clearPanelMessage();
    setCopyExcludedSourceRowIds([]);
    const nextSourceId = String(nextLineId || "");
    setCopySourceLineId(nextSourceId);
    refreshCopyPreview({ sourceId: nextSourceId, excludedRowIds: [] });
  }

  function handleRightTabChange(nextTab) {
    if (!RIGHT_TAB_IDS.has(nextTab)) {
      return;
    }

    clearPanelMessage();
    if (nextTab === "copy" && activeRightTab !== "copy") {
      refreshCopyPreview();
    }
    setActiveRightTab(nextTab);
  }

  function handleAutoSubModeChange(nextMode) {
    if (nextMode !== "quick" && nextMode !== "custom") {
      return;
    }
    clearPanelMessage();
    setAutoSubMode(nextMode);
  }

  function removeCopySourceRow(sourceRowId) {
    if (copyImportLocked) {
      return;
    }

    clearPanelMessage();
    const nextExcludedRowIds = copyExcludedSourceRowIds.includes(sourceRowId)
      ? copyExcludedSourceRowIds
      : [...copyExcludedSourceRowIds, sourceRowId];
    setCopyExcludedSourceRowIds(nextExcludedRowIds);
    refreshCopyPreview({ excludedRowIds: nextExcludedRowIds });
  }

  function isQuickBoundaryDisabled(segmentIndex, field, direction) {
    if (isLineBeingSaved()) return true;
    const segment = currentQuickSegments[segmentIndex];
    if (!segment) {
      return true;
    }

    const nextValue = Number(segment[field]) + direction * QUICK_ADD_STEP_MINUTES;
    if (field === "start") {
      if (nextValue < 0 || nextValue >= segment.end) {
        return true;
      }

      const previous = currentQuickSegments[segmentIndex - 1];
      return direction < 0
        && previous
        && nextValue < previous.end
        && nextValue - previous.start < QUICK_ADD_STEP_MINUTES;
    }

    if (nextValue > 1440 || nextValue <= segment.start) {
      return true;
    }

    const next = currentQuickSegments[segmentIndex + 1];
    return direction > 0
      && next
      && nextValue > next.start
      && next.end - nextValue < QUICK_ADD_STEP_MINUTES;
  }

  function updateQuickSegments(updater) {
    clearPanelMessage();
    const source = currentQuickSegments;
    const next = updater(source.map((segment) => ({ ...segment })))
      .map((segment, index) => ({
        ...segment,
        edited: segment.edited || segment.start !== source[index].start || segment.end !== source[index].end || segment.count !== source[index].count,
        count: Math.min(
          Math.max(0, Math.trunc(Number(segment.count) || 0)),
          getQuickAddSegmentCapacity(segment.start, segment.end, minimumScheduleMinutes)
        )
      }));
    setQuickConfigsByLine((current) => ({ ...current, [selectedLine.id]: next }));
    if (next.some((segment, index) => segment.start !== quickPeriods[index].start || segment.end !== quickPeriods[index].end)) {
      const periods = next.map(({ id, labelKey, start, end }) => ({ id, labelKey, start, end }));
      setQuickPeriods(periods);
      writeServicePeriods?.(scheduleMode, selectedLine.id, periods);
    }
  }

  function changeQuickBoundary(segmentIndex, field, direction) {
    if (isQuickBoundaryDisabled(segmentIndex, field, direction)) {
      return;
    }

    updateQuickSegments((segments) => moveServiceBoundary(segments, segmentIndex, field,
      Number(segments[segmentIndex][field]) + direction * QUICK_ADD_STEP_MINUTES));
  }

  function moveQuickBoundary(segmentIndex, field, minute) {
    updateQuickSegments((segments) => moveServiceBoundary(segments, segmentIndex, field, minute, true));
  }

  function changeQuickCount(segmentIndex, delta) {
    const segment = currentQuickSegments[segmentIndex];
    if (!segment) {
      return;
    }

    const fixedRows = rowsInSegment(lineFixedRows, segment);
    const total = segment.count + fixedRows.length;
    if (delta > 0 ? total >= getQuickAddSegmentCapacity(segment.start, segment.end, minimumScheduleMinutes) : total <= 0) {
      return;
    }

    if (delta > 0 || segment.count > 0) {
      updateQuickSegments((segments) => {
        segments[segmentIndex].count = segment.count + delta;
        return segments;
      });
      return;
    }

    const crowdedMinute = pickMostCrowdedMinute(
      fixedRows.map((row) => timeToMinutes(row.time)),
      quickLineCandidate.map((row) => timeToMinutes(row.time))
    );
    const target = fixedRows.find((row) => timeToMinutes(row.time) === crowdedMinute);
    updateQuickSegments((segments) => {
      segments[segmentIndex].edited = true;
      segments[segmentIndex].removedIds = [...(segments[segmentIndex].removedIds || []), target.id];
      return segments;
    });
  }

  function handleAutoOffsetDirectionChange(nextDirection) {
    clearPanelMessage();
    setAutoOffsetDirection(nextDirection);
  }

  function handleAutoOffsetMinutesChange(nextValue) {
    clearPanelMessage();
    setAutoOffsetMinutesText(nextValue);
  }

  function createEditorAutoRule() {
    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return null;
    }

    if (!isValidTimeValue(editorStart) || !isValidTimeValue(editorEnd)) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.auto.invalidWindow") });
      return null;
    }

    if ((!Array.isArray(liveAutoPreview.entries) || liveAutoPreview.entries.length === 0) && liveAutoPreview.meta) {
      setPanelMessage({ scope: "auto", tone: "warning", text: liveAutoPreview.meta });
      return null;
    }

    return {
      id: Date.now(),
      lineId: selectedLine.id,
      serviceId: selectedLine.id,
      kind: currentKind,
      enabled: true,
      start: editorStart,
      end: editorEnd,
      departuresPerHour: autoFrequencyPerHour,
      expressOffsetMode: supportsExpress ? offsetModeFromDirection(autoOffsetDirection) : "",
      expressOffsetMinutes: currentKind === "express" ? Math.abs(autoOffsetMinutes) : 0
    };
  }

  function addAutoRule() {
    const rule = createEditorAutoRule();
    if (!rule) {
      return;
    }

    markLocalDataDirty();
    setAutoRules((current) => sortAutoRuleRows([...current, rule]));
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
    const nextRows = summaryEntries.filter((row) => row.id !== rowId);
    setSummaryEntries(nextRows);
    if (activeRightTab === "copy") {
      refreshCopyPreview({ rows: nextRows });
    }
    invalidateQuickImport();
  }

  function clearSummaryTable() {
    markLocalDataDirty();
    const nextRows = summaryEntries.filter((row) => {
      if (summaryFilter === "current") {
        return row.lineId !== selectedLine.id && row.serviceId !== selectedLine.id;
      }

      if (summaryFilter === "local") {
        return row.kind !== "local" || (row.lineId !== selectedLine.id && row.serviceId !== selectedLine.id);
      }

      if (summaryFilter === "express") {
        return row.kind !== "express" || (row.lineId !== selectedLine.id && row.serviceId !== selectedLine.id);
      }

      return row.lineId !== selectedLine.id && row.serviceId !== selectedLine.id;
    });
    setSummaryEntries(nextRows);
    if (activeRightTab === "copy") {
      refreshCopyPreview({ rows: nextRows });
    }
    invalidateQuickImport();
  }

  async function importManualToSummary() {
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

    const occupiedRows = summaryEntries
      .map((row) => ({
        minute: timeToMinutes(row.time),
        originStationId: row.originStationId || ""
      }))
      .filter((row) => row.minute !== null);
    const { rows: importedRows, blockedIds } = placeManualDraftRows(
      validRows,
      occupiedRows,
      selectedLine.id,
      selectedLine.originStationId || "",
      minimumScheduleMinutes,
      t
    );
    const blockedRows = blockedIds.size;

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
    invalidateQuickImport();
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

  async function importCopyToSummary({ applyLine = false } = {}) {
    if (copyImportLocked) {
      return;
    }

    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "copy", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    if (!copySourceLine) {
      setPanelMessage({ scope: "copy", tone: "warning", text: t("nativeSchedule.message.copy.noSource") });
      return;
    }

    if (copySourceRows.length === 0) {
      setPanelMessage({
        scope: "copy",
        tone: "warning",
        text: t(allCopySourceRows.length === 0
          ? "nativeSchedule.message.copy.sourceEmpty"
          : "nativeSchedule.copy.preview.noRows")
      });
      return;
    }

    const importKind = supportsExpress ? normalizeKind(selectedLineType) : "local";
    const importedRows = copyPlan.rows
      .filter((row) => !row.skipped)
      .map((row) => createSummaryEntry({
        id: createShortRowId("c", selectedLine.id, copySourceLine.value, row.key, row.time.replace(":", "")),
        time: row.time,
        serviceId: selectedLine.id,
        kind: importKind,
        source: "manual",
        note: buildCombinedNote("direct", t)
      }, t));

    if (importedRows.length === 0) {
      setPanelMessage({
        scope: "copy",
        tone: "neutral",
        text: t("nativeSchedule.message.copy.blockedAll", { count: copyPlan.skippedCount })
      });
      return;
    }

    const nextRows = normalizeSummaryEntries([
      ...summaryEntries.filter((row) => row?.lineId !== selectedLine.id && row?.serviceId !== selectedLine.id),
      ...importedRows
    ], t);
    if (applyLine) return applyLineCandidate(nextRows, copyPlan.skippedCount);
    markLocalDataDirty();
    setSummaryEntries(nextRows);
    setCopyImportLocked(true);
    invalidateQuickImport();
    setPanelMessage({
      scope: "copy",
      tone: "neutral",
      text: copyPlan.skippedCount > 0
        ? t("nativeSchedule.message.copy.importedWithCounts", {
          count: importedRows.length,
          skipped: copyPlan.skippedCount
        })
        : t("nativeSchedule.message.copy.imported", { count: importedRows.length })
    });
  }

  function importQuickToSummary() {
    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "quick", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    if (currentQuickPlan.targetCount <= 0 || currentQuickPlan.successCount <= 0) {
      setPanelMessage({ scope: "quick", tone: "neutral", text: t("nativeSchedule.message.quick.noTrips") });
      return;
    }

    const importedRows = currentQuickPlan.plannedRows.map((row, index) => {
      const segment = currentQuickSegments.find((item) => item.id === row.segmentId);
      return createSummaryEntry({
        id: createShortRowId("q", selectedLine.id, row.segmentId, row.generatedIndex, index),
        time: minutesToTime(row.timeMinutes),
        serviceId: selectedLine.id,
        kind: row.kind,
        source: "auto",
        note: buildCombinedNote("generated", t, {
          start: quickMinutesToTime(segment?.start),
          end: quickMinutesToTime(segment?.end)
        })
      }, t);
    });
    const nextRows = normalizeSummaryEntries([
      ...summaryEntries.filter((row) => getQuickLineRowId(row) !== selectedLine.id),
      ...importedRows
    ], t);

    markLocalDataDirty();
    setSummaryEntries(nextRows);
    setQuickImportRecordsByLine((current) => ({
      ...current,
      [selectedLine.id]: {
        inputSignature: quickInputSignature,
        planSignature: quickPlanSignature,
        outputSignature: getQuickLineSignature(nextRows, selectedLine.id)
      }
    }));
    setPanelMessage({
      scope: "quick",
      tone: "neutral",
      text: currentQuickPlan.skippedCount > 0
        ? t("nativeSchedule.message.quick.importedWithSkipped", {
          count: importedRows.length,
          skipped: currentQuickPlan.skippedCount
        })
        : t("nativeSchedule.message.quick.imported", { count: importedRows.length })
    });
  }

  async function importAutoToSummary() {
    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    if (currentAutoRules.length === 0) {
      setPanelMessage({ scope: "auto", tone: "warning", text: t("nativeSchedule.message.auto.noRules") });
      return;
    }

    const plan = buildAutoStagedPlan({
      minimumScheduleMinutes,
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

    const importedRows = buildAutoImportedRows(plan.plannedRows, currentAutoRules, selectedLine.id, t);
    if (importedRows.length === 0) {
      const issuePreview = currentAutoRules
        .map((rule) => currentAutoPlan.previewsByRule[rule.id])
        .find((preview) => preview?.reason);
      setPanelMessage({
        scope: "auto",
        tone: "warning",
        text: issuePreview
          ? buildPreviewMetaText(issuePreview, false, t, { minimumScheduleMinutes })
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
    invalidateQuickImport();
    setPanelMessage({
      scope: "auto",
      tone: "neutral",
      text: plan.skippedCount > 0
        ? t("nativeSchedule.message.auto.importedWithSkipped", { count: importedRows.length, skipped: plan.skippedCount })
        : t("nativeSchedule.message.auto.imported", { count: importedRows.length })
    });
  }

  function formatApplyErrors(errors) {
    const mappedErrors = [];
    const dwellGroups = new Map();
    let dwellMinimum;
    (Array.isArray(errors) ? errors : []).forEach((err) => {
      if (typeof err === "string" && err.startsWith("timed-stop-minimum|")) {
        const [, minimum, lineId, time] = err.split("|");
        dwellMinimum = minimum;
        const times = dwellGroups.get(lineId) || [];
        times.push(time);
        dwellGroups.set(lineId, times);
        return;
      }
      if (typeof err === "string" && err.startsWith("line-limit-minimum|")) {
        const [, minimum, lineId, issue] = err.split("|");
        mappedErrors.push(t(`nativeSchedule.message.summary.${issue}TooSmall`, {
          line: getLocalizedLineName(LINE_OPTIONS.find((line) => line.id === lineId), t) || lineId,
          minutes: minimum
        }));
        return;
      }
      if (typeof err === "string" && err.startsWith("line-unsupported:")) {
        mappedErrors.push(t("nativeSchedule.message.lineUnsupported"));
      } else {
        mappedErrors.push(err);
      }
    });
    if (dwellGroups.size > 0) {
      const examples = (items) => {
        const labels = items.slice(0, 2).join(t("nativeSchedule.message.summary.listSeparator"));
        return items.length > 2 ? t("nativeSchedule.message.summary.examplesMore", { items: labels }) : labels;
      };
      const lines = Array.from(dwellGroups, ([lineId, times]) => t("nativeSchedule.message.summary.timedStopLine", {
        line: getLocalizedLineName(LINE_OPTIONS.find((line) => line.id === lineId), t) || lineId,
        times: examples(times)
      }));
      mappedErrors.push(t("nativeSchedule.message.summary.timedStopsTooShort", { lines: examples(lines), minutes: dwellMinimum }));
    }
    return mappedErrors.join("; ");
  }

  function formatLimitsError(line, holdTooSmall, dwellTooSmall) {
    const issue = holdTooSmall && dwellTooSmall ? "limitsTooSmall" : holdTooSmall ? "holdTooSmall" : "dwellTooSmall";
    return t(`nativeSchedule.message.summary.${issue}`, {
      line: getLocalizedLineName(line, t) || line.id,
      minutes: minimumScheduleMinutes
    });
  }

  async function applyLineCandidate(candidateRows, skipped = 0, doneKey = "nativeSchedule.line.applied") {
    if (applyingSaveOperationRef.current) return false;
    if (selectedLine.dispatchSupported === false || holdMinutesTooSmall || dwellMinutesTooSmall) {
      setPanelMessage({ scope: "line", tone: "error", text: selectedLine.dispatchSupported === false ? t("nativeSchedule.message.lineUnsupported") : formatLimitsError(selectedLine, holdMinutesTooSmall, dwellMinutesTooSmall) });
      return false;
    }
    const targetLineId = selectedLine.id;
    const candidate = normalizeSummaryEntries(candidateRows, t);
    const conflicts = buildSummaryRowsWithConflicts(withAppliedOccupancy(candidate, targetLineId), t, null, minimumScheduleMinutes).filter((row) => row.serviceId === targetLineId && row.isConflict);
    if (conflicts.length) {
      setPanelMessage({ scope: "line", tone: "error", text: t("nativeSchedule.line.conflict", { reason: conflicts[0].conflictReason || conflicts[0].conflictReasonLabel || t("nativeSchedule.summary.status.conflictUnknown") }) });
      return false;
    }
    const mode = scheduleMode;
    const generation = scheduleModeGenerationRef.current;
    setPanelMessage(null);
    try {
      const result = await saveNativeWorkbenchDraft({ applyDraft: true, candidateRows: candidate, targetLineId });
      if (!isCurrentModeRequest(mode, generation)) return false;
      if (!result?.success || result.superseded || !result.version) {
        throw new Error(formatApplyErrors(result?.errors) || "apply-operation-failed");
      }
      if (!result.snapshot) {
        setAppliedLineSettingsSignature(currentLineSettingsSignature);
        setSummaryEntries((current) => [...current.filter((row) => getQuickLineRowId(row) !== targetLineId), ...candidate.filter((row) => getQuickLineRowId(row) === targetLineId)]);
        setQuickConfigsByLine((current) => Object.fromEntries(Object.entries(current).filter(([lineId]) => lineId !== targetLineId)));
      }
      setPanelMessage({ scope: "line", tone: "neutral", text: t(doneKey, {
        line: getLocalizedLineName(selectedLine, t),
        count: candidate.filter((row) => getQuickLineRowId(row) === targetLineId).length,
        skipped
      }) });
      return true;
    } catch (error) {
      if (isCurrentModeRequest(mode, generation)) setPanelMessage({ scope: "line", tone: "error", text: t("nativeSchedule.message.summary.applyFailed", { message: error?.message || "unknown" }) });
      return false;
    }
  }

  function getQuickSkippedCount() {
    return currentQuickPlan.segments.reduce((sum, segment, index) => sum + (currentQuickSegments[index].edited ? segment.skippedCount : 0), 0);
  }

  function clearLineSchedule() {
    return applyLineCandidate(summaryEntries.filter((row) => getQuickLineRowId(row) !== selectedLine.id), 0, "nativeSchedule.line.cleared");
  }

  function applyQuickLine() {
    return applyLineCandidate([...summaryEntries.filter((row) => getQuickLineRowId(row) !== selectedLine.id), ...quickLineCandidate], getQuickSkippedCount());
  }

  function applySingleLine() {
    if (selectedLine.dispatchSupported === false) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.message.lineUnsupported") });
      return;
    }

    if (isAddManualDisabled) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.message.manual.invalidTime") });
      return;
    }

    const { rows } = placeManualDraftRows(
      [{ id: Date.now(), time: normalizedManualInput, serviceId: selectedLine.id, kind: currentKind }],
      lineDraftRows
        .map((row) => ({ minute: timeToMinutes(row.time), originStationId: row.originStationId || "" }))
        .filter((row) => row.minute !== null),
      selectedLine.id,
      selectedLine.originStationId || "",
      minimumScheduleMinutes,
      t
    );
    if (rows.length === 0) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.preview.reason.gap", { minutes: minimumScheduleMinutes }) });
      return;
    }

    return applyLineCandidate([
      ...summaryEntries.filter((row) => getQuickLineRowId(row) !== selectedLine.id),
      ...quickLineCandidate,
      ...rows
    ], getQuickSkippedCount());
  }

  async function applyAutoLine() {
    const rule = createEditorAutoRule();
    if (!rule) {
      return;
    }

    const plan = buildAutoStagedPlan({
      minimumScheduleMinutes,
      currentRows: lineDraftRows,
      rowsForLine: [rule],
      selectedEditLine: selectedLine.id,
      referenceLineIds: getReferenceLineIdsForLine(selectedLine, currentKind),
      lineOptions: planLineOptions,
      replaceExistingAutoRows: false
    });
    if (plan.hasKindConflict) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.line.kindConflict") });
      return;
    }

    if (plan.plannedRows.length === 0) {
      setPanelMessage({ scope: "auto", tone: "warning", text: t("nativeSchedule.line.batchBlocked", { count: plan.skippedCount }) });
      return;
    }

    return applyLineCandidate([
      ...summaryEntries.filter((row) => getQuickLineRowId(row) !== selectedLine.id),
      ...quickLineCandidate,
      ...buildAutoImportedRows(plan.plannedRows, [rule], selectedLine.id, t)
    ], getQuickSkippedCount() + plan.skippedCount);
  }

  async function handleApplySchedule() {
    const targetIds = new Set(summaryEntries.map((row) => row?.lineId || row?.serviceId));
    const invalidLine = [selectedLine, ...LINE_OPTIONS].find((line) => targetIds.has(line.id) && (lineIssues.get(line.id)?.hold || lineIssues.get(line.id)?.dwell));
    if (invalidLine) {
      const issue = lineIssues.get(invalidLine.id);
      setPanelMessage({ scope: "summary", tone: "error", text: formatLimitsError(invalidLine, issue.hold, issue.dwell) });
      return;
    }

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
        const message = formatApplyErrors(result?.errors) || t("nativeSchedule.message.summary.saveFailed", { message: "unknown" });
        setPanelMessage({ scope: "summary", tone: "error", text: t("nativeSchedule.message.summary.applyFailed", { message }) });
        return;
      }

      if (!result.version) {
        throw new Error("apply-operation-missing-version");
      }

      setPanelMessage(null);
      if (!result?.snapshot) {
        setAppliedSummarySignature(getSummaryRowsSignature(summaryEntries));
        setAppliedLineSettingsSignature(currentLineSettingsSignature);
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
      const firstConflictIndex = summaryRows.findIndex((row) => row.isConflict);
      if (!scrollContainer || firstConflictIndex < 0) {
        return;
      }

      const summaryTable = scrollContainer.querySelector(".dw-demo-summary-table");
      const rowHeight = Number(summaryTable?.getAttribute("data-row-height")) || 52;
      const nextScrollTop =
        (firstConflictIndex * rowHeight) - Math.max(0, Math.round((scrollContainer.clientHeight - rowHeight) / 2));
      scrollContainer.scrollTop = Math.max(0, nextScrollTop);
      scrollContainer.dispatchEvent(new Event("scroll"));
    }, 0);
  }

  return {
    line: {
      view: scheduleView,
      selectedSegment,
      manualMode,
      ready: hasHydratedRuntimeRef.current,
      catalogRevision,
      message: scheduleView === "line" ? panelMessage : null,
      isApplying: isApplyingSchedule,
      editDisabled: isLineBeingSaved(),
      quickConflict: quickLineConflict ? t("nativeSchedule.line.conflict", { reason: quickLineConflict.conflictReasonLabel || t("nativeSchedule.summary.status.conflictUnknown") }) : "",
      quickPending: quickPending || lineSettingsPending,
      issues: lineIssues,
      limitsError: scheduleView === "line" && (holdMinutesTooSmall || dwellMinutesTooSmall) ? formatLimitsError(selectedLine, holdMinutesTooSmall, dwellMinutesTooSmall) : "",
      day: quickDay,
      featureSettings: lastHydratedSnapshotRef.current?.featureSettings || null,
      rows: lineRows,
      draftRows: quickLineCandidate,
      removedTimedCount: lineEntries.filter((row) => removedTimedIds.has(row.id) && timedRowIds.has(row.id)).length
    },
    topbar: {
      editDisabled: isLineBeingSaved(),
      selectedLineId,
      selectedLine,
      selectedLineType,
      selectedDepot,
      origin,
      holdMinutes,
      dwellMinutes,
      holdMinutesTooSmall,
      minimumScheduleMinutes,
      dwellMinutesTooSmall,
      availableDepots,
      lineOptions: LINE_OPTIONS,
      supportsExpress,
      supportsSignalPriority,
      signalPriorityEnabled: selectedLine.signalPriorityEnabled === true
    },
    summary: {
      editingLineId: isApplyingSchedule ? applyingSaveOperationRef.current : null,
      summaryStateLabel,
      hasAppliedSchedule,
      rows: visibleSummaryRows,
      editableLineId: selectedLine.id,
      earliestStart,
      conflictCount,
      summaryFilter,
      supportsExpress,
      footerNote: summaryFooterNote,
      isApplyingSchedule
    },
    auto: {
      activeRightTab,
      autoSubMode,
      editorStart,
      editorEnd,
      autoFrequencyText,
      autoFrequencyPerHour,
      selectedLineType,
      supportsExpress,
      autoOffsetDirection,
      autoOffsetMinutesText,
      liveAutoPreview,
      autoRules: renderedAutoRules,
      footerNote: autoFooterNote,
      quickFooterNote,
      displaySegments: buildDepartureSegments(currentQuickPlan.segments, currentQuickSegments, quickLineCandidate, lineFixedRows),
      quickSegments: currentQuickPlan.segments.map((segment, index) => {
        const capacity = getQuickAddSegmentCapacity(segment.start, segment.end, minimumScheduleMinutes);
        const total = (currentQuickSegments[index]?.count || 0) + rowsInSegment(lineFixedRows, segment).length;
        return {
          ...segment,
          count: total,
          capacity,
          startMinusDisabled: isQuickBoundaryDisabled(index, "start", -1),
          startPlusDisabled: isQuickBoundaryDisabled(index, "start", 1),
          endMinusDisabled: isQuickBoundaryDisabled(index, "end", -1),
          endPlusDisabled: isQuickBoundaryDisabled(index, "end", 1),
          countMinusDisabled: isLineBeingSaved() || total <= 0,
          countPlusDisabled: isLineBeingSaved() || total >= capacity
        };
      }),
      quickImportDisabled: isQuickImportDisabled || isLineBeingSaved(),
      quickImported: isQuickImported
    },
    manual: {
      activeRightTab,
      manualInput,
      manualDrafts: validatedManualDrafts,
      manualInputError,
      isAddManualDisabled,
      footerNote: manualFooterNote
    },
    copy: {
      activeRightTab,
      copySourceLineId,
      copySourceLabel: copySourceLine?.label || t("nativeSchedule.copy.field.sourcePlaceholder"),
      copySourceOptions,
      copyPreviewText,
      copyEmptyText,
      copyRows: copyPlan.rows,
      copyImportDisabled,
      copyImported: copyImportLocked,
      footerNote: copyFooterNote
    },
    refs: {
      dropdownPortalHostRef,
      summaryScrollRef,
      manualInputRef,
      editorEndInputRef,
      frequencyInputRef
    },
    actions: {
      acceptFeatureSettings: (featureSettings) => {
        if (lastHydratedSnapshotRef.current) {
          lastHydratedSnapshotRef.current = { ...lastHydratedSnapshotRef.current, featureSettings };
        }
      },
      acceptAppliedLines,
      applyQuickLine,
      applySingleLine,
      applyAutoLine,
      clearLineSchedule,
      applyCopyLine: () => importCopyToSummary({ applyLine: true }),
      setScheduleView,
      selectSegment: (index) => setSelectedSegment((current) => current === index ? -1 : index),
      setManualMode,
      setActiveRightTab: handleRightTabChange,
      selectLine: handleSelectLine,
      refreshNames,
      selectLineType: whenEditable(handleLineTypeSelect),
      selectSignalPriority: whenEditable(handleSignalPrioritySelect),
      changeDepot: whenEditable(handleDepotChange),
      changeHoldMinutes: whenEditable(handleHoldMinutesChange),
      changeDwellMinutes: whenEditable(handleDwellMinutesChange),
      setHoldMinutes: whenEditable(setHoldMinutes),
      setDwellMinutes: whenEditable(setDwellMinutes),
      setSummaryFilter,
      removeSummaryRow: (rowId) => {
        const row = summaryEntries.find((entry) => entry.id === rowId);
        if (!isLineBeingSaved(getQuickLineRowId(row))) removeSummaryRow(rowId);
      },
      clearSummaryTable: whenEditable(clearSummaryTable),
      applySchedule: handleApplySchedule,
      locateConflict: handleLocateConflict,
      changeEditorStart: whenEditable(handleEditorStartChange),
      changeEditorEnd: whenEditable(handleEditorEndChange),
      setAutoSubMode: handleAutoSubModeChange,
      changeAutoFrequency: whenEditable(handleAutoFrequencyChange),
      changeAutoOffsetDirection: whenEditable(handleAutoOffsetDirectionChange),
      changeAutoOffsetMinutes: whenEditable(handleAutoOffsetMinutesChange),
      addAutoRule: whenEditable(addAutoRule),
      removeAutoRule: whenEditable(removeAutoRule),
      importAutoToSummary: whenEditable(importAutoToSummary),
      changeQuickBoundary: whenEditable(changeQuickBoundary),
      moveQuickBoundary: whenEditable(moveQuickBoundary),
      changeQuickCount: whenEditable(changeQuickCount),
      importQuickToSummary: whenEditable(importQuickToSummary),
      changeManualInput: whenEditable(handleManualInputChange),
      addManualDraft: whenEditable(addManualDraft),
      removeManualDraft: whenEditable(removeManualDraft),
      importManualToSummary: whenEditable(importManualToSummary),
      changeCopySource: whenEditable(handleCopySourceChange),
      removeCopySourceRow: whenEditable(removeCopySourceRow),
      importCopyToSummary: whenEditable(importCopyToSummary)
    }
  };
}
