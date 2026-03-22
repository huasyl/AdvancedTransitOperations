import { useEffect, useMemo, useRef, useState } from "react";
import {
  emptyAutoRules,
  emptyLines,
  emptyManualRows,
  emptyMergedView,
  emptyStagedRows,
  emptyStations,
  emptyTrips
} from "./workbench-defaults";
import { getWorkbenchApi } from "./workbench-api";
import {
  buildCombinedScheduleRows,
  buildOverviewSideContext,
  buildPreviewSummary,
  buildScheduleSideContext,
  getFilteredTrips,
  getSelectedTrip,
  isWindowValid,
  normalizeSelectedTripId
} from "./view-models";
import {
  buildAutoStagedPlan,
  getLineKinds,
  MIN_DEPARTURE_INTERVAL_MINUTES
} from "./auto-schedule";
import { useI18n } from "./i18n";
import { validateManualRows } from "./validation";

// Main UI state orchestrator.
// If a bug looks like "the page renders the wrong data", start here.
// If a bug looks like "EUIS host/layout/text path is wrong", do not start here.

function ensureArray(value, fallbackValue) {
  return Array.isArray(value) ? value : fallbackValue;
}

function ensureMergedView(value) {
  if (!value || typeof value !== "object") {
    return { ...emptyMergedView };
  }
  const merged = { ...emptyMergedView, ...value };
  const localLineIds = Array.isArray(merged.localLineIds)
    ? merged.localLineIds.filter((id) => typeof id === "string" && id.length > 0)
    : merged.localLineId
      ? [merged.localLineId]
      : [];
  const expressLineIds = Array.isArray(merged.expressLineIds)
    ? merged.expressLineIds.filter((id) => typeof id === "string" && id.length > 0)
    : merged.expressLineId
      ? [merged.expressLineId]
      : [];

  return {
    ...merged,
    localLineIds,
    expressLineIds,
    localLineId: localLineIds[0] || "",
    expressLineId: expressLineIds[0] || "",
    lineWidthScale:
      Number.isFinite(Number(merged.lineWidthScale)) && Number(merged.lineWidthScale) > 0
        ? Number(merged.lineWidthScale)
        : emptyMergedView.lineWidthScale,
    showStopAnchors:
      typeof merged.showStopAnchors === "boolean"
        ? merged.showStopAnchors
        : emptyMergedView.showStopAnchors
  };
}

function mergeLocalDisplayPrefs(nextMergedView, currentMergedView) {
  return {
    ...nextMergedView,
    lineWidthScale:
      Number.isFinite(Number(currentMergedView?.lineWidthScale))
        ? Number(currentMergedView.lineWidthScale)
        : nextMergedView.lineWidthScale,
    showStopAnchors:
      typeof currentMergedView?.showStopAnchors === "boolean"
        ? currentMergedView.showStopAnchors
        : nextMergedView.showStopAnchors
  };
}

function getPersistedMergedView(mergedView) {
  if (!mergedView || typeof mergedView !== "object") {
    return { ...emptyMergedView };
  }

  const { lineWidthScale, showStopAnchors, ...persistedMergedView } = mergedView;
  return persistedMergedView;
}

function normalizeLineOptions(lines, t) {
  return ensureArray(lines, emptyLines).map((line, index) => {
    const fallbackKey = line.sourceLineId || line.id || String(index + 1);
    const fallbackName =
      line.kind === "express"
        ? t("fallback.line.express", { key: fallbackKey })
        : t("fallback.line.local", { key: fallbackKey });
    return {
      ...line,
      originHoldLimitMinutes:
        Number.isFinite(Number(line?.originHoldLimitMinutes)) && Number(line.originHoldLimitMinutes) > 0
          ? Math.max(1, Math.min(120, Math.round(Number(line.originHoldLimitMinutes))))
          : 20,
      maxStationDwellMinutes:
        Number.isFinite(Number(line?.maxStationDwellMinutes)) && Number(line.maxStationDwellMinutes) > 0
          ? Math.max(1, Math.min(120, Math.round(Number(line.maxStationDwellMinutes))))
          : 10,
      rawName: line.name,
      name: line.name || fallbackName
    };
  });
}

function serializeLineSettingsForSave(lines) {
  return ensureArray(lines, emptyLines).map((line) => ({
    lineId: line?.id || "",
    originHoldLimitMinutes:
      Number.isFinite(Number(line?.originHoldLimitMinutes)) && Number(line.originHoldLimitMinutes) > 0
        ? Math.max(1, Math.min(120, Math.round(Number(line.originHoldLimitMinutes))))
        : 20,
    maxStationDwellMinutes:
      Number.isFinite(Number(line?.maxStationDwellMinutes)) && Number(line.maxStationDwellMinutes) > 0
        ? Math.max(1, Math.min(120, Math.round(Number(line.maxStationDwellMinutes))))
        : 10
  }));
}

function normalizeStationOptions(stations, t) {
  return ensureArray(stations, emptyStations).map((station, index) => {
    const fallbackName = t("fallback.station", {
      index: Number.isFinite(station.order) ? station.order + 1 : index + 1
    });
    return {
      ...station,
      rawName: station.name,
      name: station.name || fallbackName
    };
  });
}

function normalizeManualRows(rows, fallbackLineId) {
  return ensureArray(rows, emptyManualRows).map((row, index) => ({
    id: row?.id || `manual-${index + 1}`,
    lineId: row?.lineId || fallbackLineId || "",
    time: row?.time || "",
    kind: row?.kind === "express" ? "express" : "local",
    offsetMode: "none",
    offsetMinutes: ""
  }));
}

function normalizeAutoRules(rows, fallbackLineId) {
  return ensureArray(rows, emptyAutoRules).map((rule, index) => {
    const kind =
      rule?.kind ||
      ((Number(rule?.expressPerHour) || 0) > 0 && (Number(rule?.localPerHour) || 0) <= 0
        ? "express"
        : "local");
    const departuresPerHour =
      Number(rule?.departuresPerHour) > 0
        ? Number(rule.departuresPerHour)
        : kind === "express"
          ? Number(rule?.expressPerHour) || 0
          : Number(rule?.localPerHour) || 0;

    return {
      id: rule?.id || `rule-${index + 1}`,
      lineId: rule?.lineId || fallbackLineId || "",
      enabled: rule?.enabled !== false,
      start: rule?.start || "10:00",
      end: rule?.end || "11:00",
      kind,
      departuresPerHour,
      expressOffsetMode: rule?.expressOffsetMode || "after",
      expressOffsetMinutes: Number(rule?.expressOffsetMinutes) || 0,
      localPerHour: kind === "local" ? departuresPerHour : 0,
      expressPerHour: kind === "express" ? departuresPerHour : 0
    };
  });
}

function serializeAutoRulesForSave(rows) {
  return ensureArray(rows, emptyAutoRules).map((rule) => ({
    ...rule,
    departuresPerHour: Number(rule?.departuresPerHour) || 0,
    localPerHour: Number(rule?.localPerHour) || 0,
    expressPerHour: Number(rule?.expressPerHour) || 0,
    expressOffsetMinutes: Number(rule?.expressOffsetMinutes) || 0
  }));
}

function minutesToTime(totalMinutes) {
  const wrapped = (((totalMinutes % 1440) + 1440) % 1440);
  const hours = Math.floor(wrapped / 60).toString().padStart(2, "0");
  const minutes = (wrapped % 60).toString().padStart(2, "0");
  return `${hours}:${minutes}`;
}

function normalizeStagedRows(rows) {
  return ensureArray(rows, emptyStagedRows).map((row, index) => ({
    id: row?.id || `staged-${index + 1}`,
    lineId: row?.lineId || "",
    time: row?.time || "",
    kind: row?.kind === "express" ? "express" : "local",
    source: row?.source || "manual",
    note: row?.note || ""
  }));
}

export function useWorkbenchController() {
  const { locale, t } = useI18n();
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const [activeTab, setActiveTab] = useState("overview");
  const [viewMode, setViewMode] = useState("merged");
  const [selectedLineId, setSelectedLineId] = useState("line3-local");
  const [selectedTripId, setSelectedTripId] = useState("L103");
  const [selectedEditLine, setSelectedEditLine] = useState("");
  const [mergedView, setMergedView] = useState(emptyMergedView);
  const [lineOptions, setLineOptions] = useState(() => normalizeLineOptions(emptyLines, t));
  const [stationOptions, setStationOptions] = useState(() => normalizeStationOptions(emptyStations, t));
  const [tripOptions, setTripOptions] = useState(emptyTrips);
  const [manualRows, setManualRows] = useState(emptyManualRows);
  const [autoRules, setAutoRules] = useState(emptyAutoRules);
  const [stagedRows, setStagedRows] = useState(emptyStagedRows);
  const [saveState, setSaveState] = useState({ status: "idle", message: "" });
  const hasLoadedSnapshotRef = useRef(false);
  const suppressNextSnapshotRef = useRef(false);

  function applySnapshot(snapshot) {
    if (!snapshot) {
      return;
    }

    const nextLines = normalizeLineOptions(snapshot.lines, t);
    const nextStations = normalizeStationOptions(snapshot.stations, t);
    const nextTrips = ensureArray(snapshot.trips, emptyTrips);
    const fallbackLineId = snapshot.selectedEditLine || snapshot.selectedLineId || nextLines[0]?.id || "";
    const nextManualRows = normalizeManualRows(snapshot.manualRows, fallbackLineId);
    const nextAutoRules = normalizeAutoRules(snapshot.autoRules, fallbackLineId);
    const nextStagedRows = normalizeStagedRows(snapshot.stagedRows);

    setLineOptions(nextLines);
    setStationOptions(nextStations);
    setTripOptions(nextTrips);
    setSelectedLineId(snapshot.selectedLineId || nextLines[0]?.id || "line3-local");
    const nextSelectedEditLine =
      nextLines.some((line) => line.id === snapshot.selectedEditLine)
        ? snapshot.selectedEditLine
        : nextLines[0]?.id || snapshot.selectedEditLine || "";
    setSelectedEditLine(nextSelectedEditLine);
    setMergedView((current) => mergeLocalDisplayPrefs(ensureMergedView(snapshot.mergedView), current));
    setManualRows(nextManualRows);
    setAutoRules(nextAutoRules);
    setStagedRows(nextStagedRows);
    hasLoadedSnapshotRef.current = true;
  }

  const filteredTrips = useMemo(
    () => getFilteredTrips({ viewMode, selectedLineId, mergedView, trips: tripOptions }),
    [viewMode, selectedLineId, mergedView, tripOptions]
  );
  const currentLineManualRows = useMemo(
    () => manualRows.filter((row) => row.lineId === selectedEditLine),
    [manualRows, selectedEditLine]
  );
  const currentLineAutoRules = useMemo(
    () => autoRules.filter((rule) => rule.lineId === selectedEditLine),
    [autoRules, selectedEditLine]
  );
  const validatedRows = useMemo(() => validateManualRows(currentLineManualRows, t), [currentLineManualRows, t]);
  const selectedLine = useMemo(
    () => lineOptions.find((line) => line.id === selectedLineId) ?? lineOptions[0],
    [lineOptions, selectedLineId]
  );
  const autoReferenceLineIds = useMemo(() => {
    const hasExpressRule = currentLineAutoRules.some((rule) => rule.enabled && rule.kind === "express");
    if (!hasExpressRule) {
      return [];
    }

    const stagedLocalLineIds = [...new Set(
      stagedRows
        .filter((row) => row.lineId && row.lineId !== selectedEditLine && row.kind === "local")
        .map((row) => row.lineId)
    )];
    if (stagedLocalLineIds.length > 0) {
      return stagedLocalLineIds;
    }

    return mergedView.localLineIds.length > 0
      ? mergedView.localLineIds
      : mergedView.localLineId
        ? [mergedView.localLineId]
        : [];
  }, [currentLineAutoRules, stagedRows, mergedView, selectedEditLine]);
  const selectedTrip = useMemo(
    () => getSelectedTrip(filteredTrips, selectedTripId),
    [filteredTrips, selectedTripId]
  );
  const windowValid = useMemo(
    () => isWindowValid(mergedView.windowStart, mergedView.windowEnd),
    [mergedView.windowStart, mergedView.windowEnd]
  );
  const overviewSideContext = useMemo(
    () =>
      buildOverviewSideContext({
        viewMode,
        mergedView,
        selectedLine,
        selectedTrip,
        filteredTrips,
        stations: stationOptions,
        t
      }),
    [viewMode, mergedView, selectedLine, selectedTrip, filteredTrips, stationOptions, t]
  );
  const scheduleSideContext = useMemo(
    () =>
      buildScheduleSideContext({
        selectedEditLine,
        lineOptions,
        manualRows: currentLineManualRows,
        autoRules: currentLineAutoRules,
        validatedRows,
        t
      }),
    [selectedEditLine, lineOptions, currentLineManualRows, currentLineAutoRules, validatedRows, t]
  );
  const previewSummary = useMemo(
    () => buildPreviewSummary({ selectedEditLine, lineOptions, validatedRows, stagedRows, t }),
    [selectedEditLine, lineOptions, validatedRows, stagedRows, t]
  );
  const autoPreviewPlan = useMemo(
    () =>
      buildAutoStagedPlan({
        currentRows: stagedRows,
        rowsForLine: currentLineAutoRules,
        selectedEditLine,
        referenceLineIds: autoReferenceLineIds,
        lineOptions
      }),
    [stagedRows, currentLineAutoRules, selectedEditLine, autoReferenceLineIds, lineOptions]
  );
  const combinedRows = useMemo(
    () => buildCombinedScheduleRows({ stagedRows, lineOptions, selectedEditLine, t }),
    [stagedRows, lineOptions, selectedEditLine, t]
  );
  const lineSettingsForSave = useMemo(
    () => serializeLineSettingsForSave(lineOptions),
    [lineOptions]
  );

  useEffect(() => {
    const normalizedTripId = normalizeSelectedTripId(filteredTrips, selectedTripId);
    if (normalizedTripId !== selectedTripId) {
      setSelectedTripId(normalizedTripId);
    }
  }, [filteredTrips, selectedTripId]);

  useEffect(() => {
    let isDisposed = false;

    async function loadSnapshot() {
      try {
        const snapshot = await workbenchApi.loadSnapshot();
        if (!isDisposed) {
          applySnapshot(snapshot);
        }
      } catch (error) {
        if (!isDisposed) {
          setSaveState({
            status: "error",
            message: t("message.loadFailed", {
              message: error instanceof Error ? error.message : "unknown error"
            })
          });
        }
      }
    }

    loadSnapshot();
    const unsubscribe = workbenchApi.onSnapshotChanged?.((snapshot) => {
      if (suppressNextSnapshotRef.current) {
        suppressNextSnapshotRef.current = false;
        return;
      }
      if (!isDisposed) {
        applySnapshot(snapshot);
      }
    });

    return () => {
      isDisposed = true;
      unsubscribe?.();
    };
  }, [workbenchApi, t]);

  useEffect(() => {
    if (!hasLoadedSnapshotRef.current) {
      return undefined;
    }

    const timeoutId = window.setTimeout(async () => {
      try {
        suppressNextSnapshotRef.current = true;
        await workbenchApi.saveDraft({
          selectedEditLine,
          mergedView: getPersistedMergedView(mergedView),
          manualRows,
          autoRules,
          stagedRows,
          lineSettings: lineSettingsForSave
        });
      } catch {
        suppressNextSnapshotRef.current = false;
      }
    }, 400);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [
    workbenchApi,
    selectedEditLine,
    mergedView,
    lineSettingsForSave,
    manualRows,
    autoRules,
    stagedRows
  ]);

  function handleOriginHoldLimitChange(lineId, nextValue) {
    const normalizedValue =
      Number.isFinite(Number(nextValue)) && Number(nextValue) > 0
        ? Math.max(1, Math.min(120, Math.round(Number(nextValue))))
        : 20;

    setLineOptions((current) =>
      current.map((line) =>
        line.id === lineId
          ? { ...line, originHoldLimitMinutes: normalizedValue }
          : line
      )
    );
  }

  function handleSelectedLineKindChange(nextKind) {
    const normalizedKind = nextKind === "express" ? "express" : "local";
    if (!selectedEditLine) {
      return;
    }

    setMergedView((current) => {
      const base = ensureMergedView(current);
      const nextLocalLineIds = base.localLineIds.filter((lineId) => lineId !== selectedEditLine);
      const nextExpressLineIds = base.expressLineIds.filter((lineId) => lineId !== selectedEditLine);

      if (normalizedKind === "express") {
        nextExpressLineIds.push(selectedEditLine);
      } else {
        nextLocalLineIds.push(selectedEditLine);
      }

      return {
        ...base,
        localLineIds: nextLocalLineIds,
        localLineId: nextLocalLineIds[0] || "",
        expressLineIds: nextExpressLineIds,
        expressLineId: nextExpressLineIds[0] || ""
      };
    });
  }

  function handleMaxStationDwellChange(lineId, nextValue) {
    const normalizedValue =
      Number.isFinite(Number(nextValue)) && Number(nextValue) > 0
        ? Math.max(1, Math.min(120, Math.round(Number(nextValue))))
        : 10;

    setLineOptions((current) =>
      current.map((line) =>
        line.id === lineId
          ? { ...line, maxStationDwellMinutes: normalizedValue }
          : line
      )
    );
  }

  function handleOverviewContextAction(action) {
    if (action === "departure-control") {
      setActiveTab("schedule");
      return;
    }

    if (action === "single-line-view") {
      setViewMode("single");
      return;
    }

    if (action === "narrow-window") {
      setMergedView((current) => ({
        ...current,
        windowStart: "06:05",
        windowEnd: "06:20"
      }));
    }
  }

  function handleScheduleContextAction(action) {
    if (action === "sort-manual-rows") {
      setManualRows((current) => [...current].sort((left, right) => left.time.localeCompare(right.time)));
    }
  }

  function handleAddManualToStaged(rowsForLine) {
    const nextRows = validateManualRows(
      [...rowsForLine].sort((left, right) => (left.time || "").localeCompare(right.time || "")),
      t
    )
      .filter((row) => row.validation.status !== "error")
      .map((row) => ({
        id: `stage-manual-${row.lineId}-${row.id}`,
        lineId: row.lineId,
        time: row.time,
        kind: row.kind,
        source: "manual",
        note: t("combined.note.direct")
      }));

    setStagedRows((current) => {
      const nextKinds = new Set(nextRows.map((row) => row.kind));
      const existingKinds = getLineKinds(current, selectedEditLine);
      const hasConflict = [...nextKinds].some((kind) => existingKinds.size > 0 && !existingKinds.has(kind));
      if (hasConflict) {
        setSaveState({
          status: "error",
          message: "This line already has staged rows with a different line type."
        });
        return current;
      }

      return [
        ...current.filter((row) => !(row.lineId === selectedEditLine && row.source === "manual")),
        ...nextRows
      ];
    });
  }

  function handleAddAutoToStaged(rowsForLine) {
    setStagedRows((current) => {
      const plan = buildAutoStagedPlan({
        currentRows: current,
        rowsForLine,
        selectedEditLine,
        referenceLineIds: autoReferenceLineIds,
        lineOptions
      });
      if (plan.hasKindConflict) {
        setSaveState({
          status: "error",
          message: "This line already has staged rows with a different line type."
        });
        return current;
      }

      const nextRows = plan.plannedRows.map((row) => ({
        id: `stage-auto-${row.lineId}-${row.ruleId}-${row.generatedIndex}`,
        lineId: row.lineId,
        time: minutesToTime(row.timeMinutes),
        kind: row.kind,
        source: "auto",
        note:
          row.noteType === "before"
            ? t("combined.note.beforePaired", { minutes: row.offsetMinutes })
            : row.noteType === "after"
              ? t("combined.note.afterPaired", { minutes: row.offsetMinutes })
              : t("combined.note.generated", { start: row.start, end: row.end })
      }));

      if (plan.skippedCount > 0) {
        setSaveState({
          status: "idle",
          message:
            locale === "zh-CN"
              ? `已跳过 ${plan.skippedCount} 班自动车次：需同时满足偏移规则，以及同始发站最小 ${MIN_DEPARTURE_INTERVAL_MINUTES} 分钟发车间隔。`
              : `Skipped ${plan.skippedCount} automatic departures because they violated the offset rule or the ${MIN_DEPARTURE_INTERVAL_MINUTES}-minute minimum headway for the same origin station.`
        });
      }

      return [...plan.retainedRows, ...nextRows];
    });
  }

  function handleClearStagedLine() {
    setStagedRows((current) => current.filter((row) => row.lineId !== selectedEditLine));
  }

  function handleRemoveStagedRow(rowId) {
    setStagedRows((current) => current.filter((row) => row.id !== rowId));
  }

  async function handleApplyDraft() {
    try {
      setSaveState({ status: "saving", message: t("message.savingDraft") });
      const result = await workbenchApi.saveDraft({
        selectedLineId,
        selectedEditLine,
        mergedView: getPersistedMergedView(mergedView),
        manualRows,
        autoRules,
        stagedRows,
        lineSettings: lineSettingsForSave,
        applyDraft: true
      });

      if (result?.snapshot) {
        const snapshot = result.snapshot;
        setLineOptions(normalizeLineOptions(snapshot.lines, t));
        setStationOptions(normalizeStationOptions(snapshot.stations, t));
        setTripOptions(ensureArray(snapshot.trips, emptyTrips));
        setSelectedLineId(snapshot.selectedLineId || selectedLineId);
        setSelectedEditLine(snapshot.selectedEditLine || selectedEditLine);
        setMergedView((current) =>
          mergeLocalDisplayPrefs(ensureMergedView(snapshot.mergedView || mergedView), current)
        );
        setManualRows(normalizeManualRows(snapshot.manualRows, snapshot.selectedEditLine || selectedEditLine));
        setAutoRules(normalizeAutoRules(snapshot.autoRules, snapshot.selectedEditLine || selectedEditLine));
        setStagedRows(normalizeStagedRows(snapshot.stagedRows));
      }

      if (result?.success) {
        const successMessage = t("message.saveSuccessBackend", { version: result.version ?? "-" });
        setSaveState({ status: "success", message: successMessage });
        return successMessage;
      }

      const errorMessage = result?.errors?.length
        ? t("message.saveFailed", { message: result.errors[0] })
        : t("message.saveFailedGeneric");
      setSaveState({ status: "error", message: errorMessage });
      return errorMessage;
    } catch (error) {
      const errorMessage = t("message.saveFailed", {
        message: error instanceof Error ? error.message : "unknown error"
      });
      setSaveState({ status: "error", message: errorMessage });
      return errorMessage;
    }
  }

  return {
    activeTab,
    setActiveTab,
    viewMode,
    setViewMode,
    selectedLineId,
    setSelectedLineId,
    selectedTripId,
    setSelectedTripId,
    selectedEditLine,
    setSelectedEditLine,
    mergedView,
    setMergedView,
    lineOptions,
    stationOptions,
    filteredTrips,
    selectedTrip,
    handleOriginHoldLimitChange,
    handleMaxStationDwellChange,
    manualRows,
    setManualRows,
    autoRules,
    setAutoRules,
    stagedRows,
    setStagedRows,
    validatedRows,
    overviewSideContext,
    scheduleSideContext,
    previewSummary,
    autoPreviewPlan,
    combinedRows,
    saveState,
    windowValid,
    handleOverviewContextAction,
    handleScheduleContextAction,
    handleSelectedLineKindChange,
    handleApplyDraft,
    handleAddManualToStaged,
    handleAddAutoToStaged,
    handleClearStagedLine,
    handleRemoveStagedRow
  };
}
