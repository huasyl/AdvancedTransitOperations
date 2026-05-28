import { useEffect, useMemo, useRef, useState } from "react";
import {
  emptyAutoRules,
  emptyDepots,
  emptyFeatureSettings,
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

function normalizeFeatureSettings(value) {
  if (!value || typeof value !== "object") {
    return { ...emptyFeatureSettings };
  }

  return {
    dispatchEnabled: value.dispatchEnabled !== false,
    bypassEnabled: value.bypassEnabled !== false,
    broadcastEnabled: value.broadcastEnabled !== false,
    depotLockEnabled: value.depotLockEnabled !== false
  };
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
          ? Math.max(5, Math.min(120, Math.round(Number(line.originHoldLimitMinutes))))
          : 20,
      maxStationDwellMinutes:
        Number.isFinite(Number(line?.maxStationDwellMinutes)) && Number(line.maxStationDwellMinutes) > 0
          ? Math.max(5, Math.min(120, Math.round(Number(line.maxStationDwellMinutes))))
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
        ? Math.max(5, Math.min(120, Math.round(Number(line.originHoldLimitMinutes))))
        : 20,
    maxStationDwellMinutes:
      Number.isFinite(Number(line?.maxStationDwellMinutes)) && Number(line.maxStationDwellMinutes) > 0
        ? Math.max(5, Math.min(120, Math.round(Number(line.maxStationDwellMinutes))))
        : 10,
    allowedDepotId: line?.allowedDepotId || "",
    serviceKind: line?.kind === "express" ? "express" : "local"
  }));
}

function normalizeDepotOptions(depots) {
  return ensureArray(depots, emptyDepots).map((depot, index) => ({
    id: depot?.id || `depot-${index + 1}`,
    name: depot?.name || depot?.id || `Depot ${index + 1}`,
    transportType: depot?.transportType || ""
  }));
}

function mergeLineOptionsPreservingSettings(currentLines, nextLines) {
  const currentById = new Map(ensureArray(currentLines, emptyLines).map((line) => [line.id, line]));
  return ensureArray(nextLines, emptyLines).map((line) => {
    const current = currentById.get(line.id);
    if (!current) {
      return line;
    }

    return {
      ...line,
      originHoldLimitMinutes: current.originHoldLimitMinutes,
      maxStationDwellMinutes: current.maxStationDwellMinutes,
      allowedDepotId: current.allowedDepotId || "",
      kind: current.kind || line.kind
    };
  });
}
function areLineOptionsEquivalent(left, right) {
  const a = ensureArray(left, emptyLines);
  const b = ensureArray(right, emptyLines);
  if (a.length !== b.length) {
    return false;
  }

  for (let index = 0; index < a.length; index += 1) {
    const current = a[index];
    const next = b[index];
    if (
      current?.id !== next?.id ||
      current?.name !== next?.name ||
      current?.rawName !== next?.rawName ||
      current?.kind !== next?.kind ||
      current?.originStationId !== next?.originStationId ||
      current?.originStationName !== next?.originStationName ||
      current?.transportType !== next?.transportType ||
      current?.originHoldLimitMinutes !== next?.originHoldLimitMinutes ||
      current?.maxStationDwellMinutes !== next?.maxStationDwellMinutes ||
      current?.allowedDepotId !== next?.allowedDepotId
    ) {
      return false;
    }
  }

  return true;
}

function areDepotOptionsEquivalent(left, right) {
  const a = ensureArray(left, emptyDepots);
  const b = ensureArray(right, emptyDepots);
  if (a.length !== b.length) {
    return false;
  }

  for (let index = 0; index < a.length; index += 1) {
    const current = a[index];
    const next = b[index];
    if (
      current?.id !== next?.id ||
      current?.name !== next?.name ||
      current?.transportType !== next?.transportType
    ) {
      return false;
    }
  }

  return true;
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
  const seen = new Set();
  return ensureArray(rows, emptyStagedRows)
    .map((row, index) => ({
      id: row?.id || `staged-${index + 1}`,
      lineId: row?.lineId || "",
      time: row?.time || "",
      kind: row?.kind === "express" ? "express" : "local",
      source: row?.source || "manual",
      note: row?.note || ""
    }))
    .filter((row) => {
      const key = `${row.lineId}|${row.kind}|${row.time}`;
      if (seen.has(key)) {
        return false;
      }
      seen.add(key);
      return true;
    });
}

export function useWorkbenchController() {
  const { locale, t } = useI18n();
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const legacyWorkbenchReadOnly = true;
  const [activeTab, setActiveTab] = useState("overview");
  const [viewMode, setViewMode] = useState("merged");
  const [selectedLineId, setSelectedLineId] = useState("line3-local");
  const [selectedTripId, setSelectedTripId] = useState("L103");
  const [selectedEditLine, setSelectedEditLine] = useState("");
  const [mergedView, setMergedView] = useState(emptyMergedView);
  const [lineOptions, setLineOptions] = useState(() => normalizeLineOptions(emptyLines, t));
  const [depotOptions, setDepotOptions] = useState(() => normalizeDepotOptions(emptyDepots));
  const [stationOptions, setStationOptions] = useState(() => normalizeStationOptions(emptyStations, t));
  const [tripOptions, setTripOptions] = useState(emptyTrips);
  const [manualRows, setManualRows] = useState(emptyManualRows);
  const [autoRules, setAutoRules] = useState(emptyAutoRules);
  const [stagedRows, setStagedRows] = useState(emptyStagedRows);
  const [featureSettings, setFeatureSettings] = useState(() => ({ ...emptyFeatureSettings }));
  const [saveState, setSaveState] = useState({ status: "idle", message: "" });
  const hasLoadedSnapshotRef = useRef(false);
  const suppressNextSnapshotRef = useRef(false);

  const skipNextAutosaveRef = useRef(false);
  function reportLegacyReadonly() {
    setSaveState({
      status: "idle",
      message: t("message.workbenchReadonly")
    });
  }

  function setManualRowsGuarded(nextValue) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    setManualRows(nextValue);
  }

  function setAutoRulesGuarded(nextValue) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    setAutoRules(nextValue);
  }

  function setStagedRowsGuarded(nextValue) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    setStagedRows(nextValue);
  }

  useEffect(() => {
    if (!legacyWorkbenchReadOnly) {
      return;
    }

    setSaveState({
      status: "idle",
      message: t("message.workbenchReadonly")
    });
  }, [legacyWorkbenchReadOnly, t]);

  function applySnapshot(snapshot) {
    if (!snapshot) {
      return;
    }

    const nextLines = normalizeLineOptions(snapshot.lines, t);
    const nextDepots = normalizeDepotOptions(snapshot.depots);
    const nextStations = normalizeStationOptions(snapshot.stations, t);
    const nextTrips = ensureArray(snapshot.trips, emptyTrips);
    const fallbackLineId = snapshot.selectedEditLine || snapshot.selectedLineId || nextLines[0]?.id || "";
    const nextManualRows = normalizeManualRows(snapshot.manualRows, fallbackLineId);
    const nextAutoRules = normalizeAutoRules(snapshot.autoRules, fallbackLineId);
    const nextStagedRows = normalizeStagedRows(snapshot.lineDraftRows ?? snapshot.stagedRows);

    setLineOptions(nextLines);
    setDepotOptions(nextDepots);
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
    setFeatureSettings(normalizeFeatureSettings(snapshot.featureSettings));
    hasLoadedSnapshotRef.current = true;
  }

  function applySaveDraftResult(result, { reportFailure = true } = {}) {
    if (result?.snapshot) {
      applySnapshot(result.snapshot);
    }

    if (result?.success) {
      setSaveState({ status: "idle", message: "" });
      return true;
    }

    if (reportFailure) {
      const errorMessage = result?.errors?.length
        ? t("message.saveFailed", { message: result.errors[0] })
        : t("message.saveFailedGeneric");
      setSaveState({ status: "error", message: errorMessage });
    }
    return false;
  }

  async function refreshWorkbenchMetadata() {
    try {
      const metadata = await workbenchApi.refreshMetadata?.();
      if (!metadata) {
        return;
      }

      const nextLines = normalizeLineOptions(metadata.lines, t);
      setLineOptions((current) => {
        const mergedLines = mergeLineOptionsPreservingSettings(current, nextLines);
        return areLineOptionsEquivalent(current, mergedLines) ? current : mergedLines;
      });

      const nextDepots = normalizeDepotOptions(metadata.depots);
      setDepotOptions((current) => (
        areDepotOptionsEquivalent(current, nextDepots) ? current : nextDepots
      ));
    } catch {
      // Metadata refresh should stay silent; the page can keep current names.
    }
  }

  async function refreshWorkbenchSnapshot() {
    try {
      const snapshot = await workbenchApi.refreshSnapshot?.();
      if (!snapshot) {
        return null;
      }

      applySnapshot(snapshot);
      return snapshot;
    } catch (error) {
      setSaveState({
        status: "error",
        message: t("message.loadFailed", {
          message: error instanceof Error ? error.message : "unknown error"
        })
      });
      return null;
    }
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

    if (legacyWorkbenchReadOnly) {
      return undefined;
    }

    if (skipNextAutosaveRef.current) {
      skipNextAutosaveRef.current = false;
      return undefined;
    }

    const timeoutId = window.setTimeout(async () => {
      try {
        suppressNextSnapshotRef.current = true;
        const result = await workbenchApi.saveDraft({
          selectedLineId,
          selectedEditLine,
          mergedView: getPersistedMergedView(mergedView),
          manualRows,
          autoRules,
          lineDraftRows: stagedRows,
          lineSettings: lineSettingsForSave,
          featureSettings
        });
        if (!applySaveDraftResult(result, { reportFailure: true }) || !result?.snapshot) {
          suppressNextSnapshotRef.current = false;
        }
      } catch {
        suppressNextSnapshotRef.current = false;
      }
    }, 400);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [
    workbenchApi,
    selectedLineId,
    selectedEditLine,
    mergedView,
    lineSettingsForSave,
    manualRows,
    autoRules,
    stagedRows,
    featureSettings
  ]);

  function saveDraftImmediately(nextState) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    if (!hasLoadedSnapshotRef.current) {
      return;
    }
    skipNextAutosaveRef.current = true;

    suppressNextSnapshotRef.current = true;
    workbenchApi.saveDraft({
      selectedLineId: nextState.selectedLineId ?? selectedLineId,
      selectedEditLine: nextState.selectedEditLine ?? selectedEditLine,
      mergedView: getPersistedMergedView(nextState.mergedView ?? mergedView),
      manualRows: nextState.manualRows ?? manualRows,
      autoRules: nextState.autoRules ?? autoRules,
      lineDraftRows: nextState.stagedRows ?? stagedRows,
      lineSettings: nextState.lineSettings ?? lineSettingsForSave,
      featureSettings: nextState.featureSettings ?? featureSettings
    }).then((result) => {
      if (applySaveDraftResult(result, { reportFailure: true })) {
        return;
      }

      suppressNextSnapshotRef.current = false;
    }).catch(() => {
      suppressNextSnapshotRef.current = false;
    });
  }

  function handleOriginHoldLimitChange(lineId, nextValue) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    const normalizedValue =
      Number.isFinite(Number(nextValue)) && Number(nextValue) > 0
        ? Math.max(5, Math.min(120, Math.round(Number(nextValue))))
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
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    const normalizedKind = nextKind === "express" ? "express" : "local";
    if (!selectedEditLine) {
      return;
    }

    const base = ensureMergedView(mergedView);
    const nextLocalLineIds = base.localLineIds.filter((lineId) => lineId !== selectedEditLine);
    const nextExpressLineIds = base.expressLineIds.filter((lineId) => lineId !== selectedEditLine);

    if (normalizedKind === "express") {
      nextExpressLineIds.push(selectedEditLine);
    } else {
      nextLocalLineIds.push(selectedEditLine);
    }

    const nextMergedView = {
      ...base,
      localLineIds: nextLocalLineIds,
      localLineId: nextLocalLineIds[0] || "",
      expressLineIds: nextExpressLineIds,
      expressLineId: nextExpressLineIds[0] || ""
    };
    const nextManualRows = manualRows.map((row) =>
      row.lineId === selectedEditLine ? { ...row, kind: normalizedKind } : row
    );
    const nextAutoRules = autoRules.map((rule) =>
      rule.lineId === selectedEditLine ? { ...rule, kind: normalizedKind } : rule
    );
    const nextStagedRows = stagedRows.map((row) =>
      row.lineId === selectedEditLine ? { ...row, kind: normalizedKind } : row
    );

    const nextLineOptions = lineOptions.map((line) =>
      line.id === selectedEditLine ? { ...line, kind: normalizedKind } : line
    );
    setMergedView(nextMergedView);
    setManualRows(nextManualRows);
    setAutoRules(nextAutoRules);
    setStagedRows(nextStagedRows);
    setLineOptions(nextLineOptions);
    saveDraftImmediately({
      selectedLineId: selectedEditLine,
      mergedView: nextMergedView,
      manualRows: nextManualRows,
      autoRules: nextAutoRules,
      stagedRows: nextStagedRows,
      lineSettings: serializeLineSettingsForSave(nextLineOptions)
    });
  }

  function handleAllowedDepotChange(lineId, nextDepotId) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    const nextLineOptions = lineOptions.map((line) =>
      line.id === lineId
        ? { ...line, allowedDepotId: nextDepotId || "" }
        : line
    );
    setLineOptions(nextLineOptions);
    saveDraftImmediately({
      lineSettings: serializeLineSettingsForSave(nextLineOptions)
    });
  }

  function handleMaxStationDwellChange(lineId, nextValue) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    const normalizedValue =
      Number.isFinite(Number(nextValue)) && Number(nextValue) > 0
        ? Math.max(5, Math.min(120, Math.round(Number(nextValue))))
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
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    if (action === "sort-manual-rows") {
      setManualRows((current) => [...current].sort((left, right) => left.time.localeCompare(right.time)));
    }
  }

  function handleAddManualToStaged(rowsForLine) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

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
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

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
              ? `已跳�?${plan.skippedCount} 班自动车次：需同时满足偏移规则，以及同始发站最�?${MIN_DEPARTURE_INTERVAL_MINUTES} 分钟发车间隔。`
              : `Skipped ${plan.skippedCount} automatic departures because they violated the offset rule or the ${MIN_DEPARTURE_INTERVAL_MINUTES}-minute minimum headway for the same origin station.`
        });
      }

      return [...plan.retainedRows, ...nextRows];
    });
  }

  function handleClearStagedLine() {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    setStagedRows((current) => current.filter((row) => row.lineId !== selectedEditLine));
  }

  function handleRemoveStagedRow(rowId) {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return;
    }

    setStagedRows((current) => current.filter((row) => row.id !== rowId));
  }

  async function handleApplyDraft() {
    if (legacyWorkbenchReadOnly) {
      reportLegacyReadonly();
      return t("message.workbenchReadonly");
    }

    try {
      setSaveState({ status: "saving", message: t("message.savingDraft") });
      const result = await workbenchApi.saveDraft({
        selectedLineId,
        selectedEditLine,
        mergedView: getPersistedMergedView(mergedView),
        manualRows,
        autoRules,
        lineDraftRows: stagedRows,
        lineSettings: lineSettingsForSave,
        featureSettings,
        applyDraft: true
      });
      if (applySaveDraftResult(result, { reportFailure: true })) {
        return "";
      }

      return result?.errors?.length
        ? t("message.saveFailed", { message: result.errors[0] })
        : t("message.saveFailedGeneric");
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
    depotOptions,
    refreshWorkbenchMetadata,
    refreshWorkbenchSnapshot,
    isReadonly: legacyWorkbenchReadOnly,
    stationOptions,
    filteredTrips,
    selectedTrip,
    handleAllowedDepotChange,
    handleOriginHoldLimitChange,
    handleMaxStationDwellChange,
    manualRows,
    setManualRows: setManualRowsGuarded,
    autoRules,
    setAutoRules: setAutoRulesGuarded,
    stagedRows,
    setStagedRows: setStagedRowsGuarded,
    featureSettings,
    setFeatureSettings,
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
