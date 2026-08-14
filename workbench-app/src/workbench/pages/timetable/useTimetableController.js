import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { minutesToTime, timeToMinutes } from "./timetable-data";
import { isValidTimeValue } from "../schedule/schedule-normalize";

const EMPTY_SNAPSHOT = {
  lines: [],
  stations: [],
  lineDraftRowsByLineId: [],
  appliedRows: []
};
const MONITOR_DETAIL_BATCH = 32;

function isEditorRuntimeSource(source) {
  return source === "sliceHistoricalEstimate"
    || source === "theory"
    || source === "monitorAverage"
    || source === "busHistorical";
}

function createEditorId() {
  return `timetable-${Date.now()}-${Math.floor(Math.random() * 1000000)}`;
}

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function diagnosticNow() {
  return typeof performance !== "undefined" && typeof performance.now === "function"
    ? performance.now()
    : Date.now();
}

function buildRows(snapshot, lineId) {
  const draftBlock = asArray(snapshot.lineDraftRowsByLineId)
    .find((block) => block?.lineId === lineId);
  return draftBlock
    ? asArray(draftBlock.lineDraftRows)
    : asArray(snapshot.appliedRows).filter((row) => row?.lineId === lineId);
}

function buildAppliedRows(snapshot, lineId) {
  return asArray(snapshot.appliedRows).filter((row) => row?.lineId === lineId);
}

function normalizeSnapshot(snapshot) {
  const next = snapshot || EMPTY_SNAPSHOT;
  const blocks = asArray(next.lineDraftRowsByLineId).map((block) => ({
    ...block,
    lineDraftRows: asArray(block.lineDraftRows)
  }));
  const existing = new Set(blocks.map((block) => block.lineId));
  asArray(next.lines).forEach((line) => {
    if (!existing.has(line.id)) {
      blocks.push({
        lineId: line.id,
        lineDraftRows: asArray(next.appliedRows).filter((row) => row?.lineId === line.id)
      });
    }
  });
  return { ...next, lineDraftRowsByLineId: blocks };
}

function isSnapshotForMode(snapshot, mode) {
  if (!snapshot || typeof snapshot !== "object") {
    return false;
  }
  const snapshotMode = typeof snapshot.mode === "string" ? snapshot.mode : "train";
  return snapshotMode === mode;
}

function useAppliedRows(snapshot, lineIds) {
  const next = normalizeSnapshot(snapshot);
  const saved = new Set(asArray(lineIds));
  return {
    ...next,
    lineDraftRowsByLineId: asArray(next.lineDraftRowsByLineId).map((block) => saved.has(block?.lineId)
      ? {
          ...block,
          lineDraftRows: asArray(next.appliedRows).filter((row) => row?.lineId === block.lineId)
        }
      : block)
  };
}

function buildStopKeys(layout) {
  const layoutStops = asArray(layout?.stops).filter((stop) => stop?.stopKey);
  return layoutStops.map((stop) => stop.stopKey);
}

function hasRunTimeSegments(runtime, stopCount) {
  const segments = asArray(runtime?.segments);
  return stopCount < 2 || (segments.length >= stopCount - 1
    && segments.slice(0, stopCount - 1).every((segment) => Number.isFinite(segment?.segmentMinutes)));
}

function hasClosingSegment(runtime, stopCount) {
  const segments = asArray(runtime?.segments);
  return stopCount >= 2
    && segments.length >= stopCount
    && segments.slice(0, stopCount).every((segment) => Number.isFinite(segment?.segmentMinutes));
}

function isRetryableLayoutError(error) {
  return error === "timetable-line-layout-line-missing"
    || error === "timetable-line-layout-route-plan-unavailable"
    || error === "timetable-line-layout-invalid";
}

function buildTrain(row, layout, runtime, stationNames) {
  const slotMinute = timeToMinutes(row?.time);
  const stored = asArray(row?.timedStops);
  const layoutStops = asArray(layout?.stops).filter((stop) => stop?.stopKey);
  const stopKeys = buildStopKeys(layout);
  const segments = asArray(runtime?.segments);
  const storedClosing = stored.length === stopKeys.length + 1
    && stored[stored.length - 1]?.stopKey === stopKeys[0];
  const includeClosing = storedClosing || hasClosingSegment(runtime, stopKeys.length);
  const displayKeys = includeClosing ? [...stopKeys, stopKeys[0]] : stopKeys;
  let previousDeparture = slotMinute;
  let runtimeGap = false;
  const stops = displayKeys.map((stopKey, index) => {
    const storedStop = stored[index];
    const layoutStop = index === stopKeys.length ? layoutStops[0] : layoutStops[index];
    const segmentMinutes = segments[index - 1]?.segmentMinutes;
    const missingSlice = runtime?.source === "sliceHistoricalEstimate"
      && index > 0
      && (runtimeGap || !Number.isFinite(segmentMinutes));
    if (missingSlice) {
      runtimeGap = true;
    }
    const arrival = index === 0
      ? null
      : !missingSlice && previousDeparture != null && Number.isFinite(segmentMinutes)
        ? previousDeparture + segmentMinutes
        : missingSlice ? null : storedStop?.arrive ?? null;
    const departure = index === displayKeys.length - 1
      ? null
      : index === 0
        ? slotMinute
        : missingSlice ? null : storedStop?.depart ?? null;
    previousDeparture = departure;
    return {
      stationId: stopKey,
      stationName: layoutStop?.name || stationNames.get(stopKey) || stopKey,
      stopKey,
      waypointIndex: layoutStop?.waypointIndex,
      order: index,
      occurrence: index,
      arrivalMinute: arrival,
      departureMinute: departure,
      arrivalTime: arrival == null ? "--" : minutesToTime(arrival),
      departureTime: departure == null ? "--" : minutesToTime(departure)
    };
  });

  return {
    id: row?.id || `row-${slotMinute}`,
    name: row?.id || minutesToTime(slotMinute),
    kind: row?.kind || "local",
    source: row?.source || "manual",
    stopSig: row?.stopSig || "",
    scheduleType: stored.length > 0 ? "custom" : "default",
    slotMinute,
    canEdit: hasRunTimeSegments(runtime, stopKeys.length),
    stops
  };
}

function buildBatchTimedStops(row, layout, runtime, stationNames) {
  const train = buildTrain(row, layout, runtime, stationNames);
  if (train.stops.length !== buildStopKeys(layout).length + 1) {
    return [];
  }
  let previousDeparture = train.slotMinute;
  return train.stops.map((stop, index) => {
    const arrival = index === 0
      ? null
      : stop.arrivalMinute ?? previousDeparture + runtime.segments[index - 1].segmentMinutes;
    const departure = index === train.stops.length - 1
      ? null
      : index === 0
        ? train.slotMinute
        : stop.departureMinute ?? arrival + 5;
    previousDeparture = departure;
    return {
      stopKey: stop.stopKey,
      arrive: arrival,
      depart: departure
    };
  });
}

function buildSliceTrain(row, layout, runtime, stationNames) {
  const layoutStops = asArray(layout?.stops).filter((stop) => stop?.stopKey);
  const segments = asArray(runtime?.segments);
  const dwells = asArray(runtime?.dwells);
  const slotMinute = timeToMinutes(row?.time);
  if (layoutStops.length === 0 || runtime?.state !== "Completed") {
    return { id: row?.id || `row-${slotMinute}`, stops: [] };
  }

  const prefixStopCount = Number.isInteger(runtime?.prefixStopCount)
    ? Math.max(1, Math.min(layoutStops.length, runtime.prefixStopCount))
    : layoutStops.length;
  const stops = [];
  let departure = slotMinute;
  stops.push({
    stationId: layoutStops[0].stopKey,
    stationName: layoutStops[0].name || stationNames.get(layoutStops[0].stopKey) || layoutStops[0].stopKey,
    stopKey: layoutStops[0].stopKey,
    waypointIndex: layoutStops[0].waypointIndex,
    occurrence: 0,
    arrivalMinute: null,
    departureMinute: departure
  });
  for (let index = 1; index < prefixStopCount; index++) {
    const segment = segments[index - 1];
    if (!Number.isFinite(segment?.segmentMinutes)) {
      break;
    }
    const stop = layoutStops[index];
    const arrival = departure + segment.segmentMinutes;
    const next = {
      stationId: stop.stopKey,
      stationName: stop.name || stationNames.get(stop.stopKey) || stop.stopKey,
      stopKey: stop.stopKey,
      waypointIndex: stop.waypointIndex,
      occurrence: index,
      arrivalMinute: arrival,
      departureMinute: null
    };
    stops.push(next);
    if (index === layoutStops.length - 1) {
      break;
    }
    const dwell = dwells.find((item) => item?.stopKey === stop.stopKey
      && item?.waypointIndex === stop.waypointIndex);
    if (!dwell?.hasObservation || !Number.isFinite(dwell.averageMinutes)) {
      break;
    }
    departure = arrival + dwell.averageMinutes;
    next.departureMinute = departure;
  }

  return {
    id: row?.id || `row-${slotMinute}`,
    name: row?.id || minutesToTime(slotMinute),
    slotMinute,
    stops
  };
}

function continuousTimedStops(value) {
  const stops = asArray(value);
  if (stops.length < 2
    || !stops[0]?.stopKey
    || Number.isFinite(stops[0].arrive)
    || !Number.isFinite(stops[0].depart)) {
    return [];
  }

  const prefix = [stops[0]];
  for (let index = 1; index < stops.length; index++) {
    const stop = stops[index];
    const previous = prefix[prefix.length - 1];
    if (!stop?.stopKey
      || !Number.isFinite(previous?.depart)
      || !Number.isFinite(stop.arrive)) {
      break;
    }
    prefix.push(stop);
    if (!Number.isFinite(stop.depart)) {
      break;
    }
  }

  while (prefix.length > 0 && Number.isFinite(prefix[prefix.length - 1]?.depart)) {
    prefix.pop();
  }
  return prefix.length >= 2 ? prefix : [];
}

function rebuildTimedStops(row, layout, runtime) {
  const original = continuousTimedStops(row?.timedStops);
  const stopKeys = buildStopKeys(layout);
  const segments = asArray(runtime?.segments);
  if (original.length < 2 || stopKeys.length < 2) {
    return asArray(row?.timedStops);
  }

  const stops = [{
    stopKey: original[0].stopKey,
    arrive: null,
    depart: timeToMinutes(row?.time)
  }];
  for (let index = 1; index < original.length; index++) {
    const expectedKey = index === stopKeys.length ? stopKeys[0] : stopKeys[index];
    const minutes = segments[index - 1]?.segmentMinutes;
    const previous = stops[index - 1];
    if (!expectedKey
      || original[index]?.stopKey !== expectedKey
      || !Number.isFinite(previous?.depart)
      || !Number.isFinite(minutes)) {
      break;
    }
    const last = index === original.length - 1;
    const depart = last ? null : original[index]?.depart;
    stops.push({
      stopKey: expectedKey,
      arrive: previous.depart + minutes,
      depart: Number.isFinite(depart) ? depart : null
    });
    if (!last && !Number.isFinite(depart)) {
      break;
    }
  }
  return stops.length >= 2 ? stops : asArray(row?.timedStops);
}

function rebuildLineRows(snapshot, lineId, layout, runtime) {
  return {
    ...snapshot,
    lineDraftRowsByLineId: asArray(snapshot.lineDraftRowsByLineId).map((block) => block?.lineId !== lineId
      ? block
      : {
          ...block,
          lineDraftRows: asArray(block.lineDraftRows).map((row) => ({
            ...row,
            timedStops: rebuildTimedStops(row, layout, runtime)
          }))
        })
  };
}

function runtimeKey(lineId, source) {
  return `${lineId || ""}\u001f${source || "theory"}`;
}

function lineRuntime(runtimes, sources, lineId) {
  return runtimes[lineId]?.[sources[lineId] || "theory"] || null;
}

function validateDepartureValue(train, runtime, occurrence, value) {
  if (!isValidTimeValue(value)) {
    return { error: "format", minute: null };
  }
  const stopIndex = train.stops.findIndex((stop) => stop.occurrence === occurrence);
  const lastIndex = train.stops.length - 1;
  if (stopIndex <= 0 || stopIndex >= lastIndex) {
    return { error: "", minute: null };
  }
  const stop = train.stops[stopIndex];
  const anchor = stop.arrivalMinute ?? train.slotMinute;
  let minute = Math.floor(anchor / 1440) * 1440 + timeToMinutes(value);
  // A clock time more than half a day behind arrival is a midnight crossing.
  if (Number.isFinite(stop.arrivalMinute) && stop.arrivalMinute - minute > 720) {
    minute += 1440;
  }
  if (!Number.isFinite(stop.arrivalMinute) || minute - stop.arrivalMinute < 5) {
    return { error: "dwell", minute };
  }

  let previousDeparture = minute;
  let reachesThirdDay = minute >= 2880;
  for (let index = stopIndex + 1; index <= lastIndex; index += 1) {
    const segmentMinutes = runtime?.segments?.[index - 1]?.segmentMinutes;
    if (!Number.isFinite(previousDeparture) || !Number.isFinite(segmentMinutes)) {
      break;
    }
    const arrival = previousDeparture + segmentMinutes;
    reachesThirdDay = reachesThirdDay || arrival >= 2880;
    if (index === lastIndex) {
      break;
    }
    const departure = train.stops[index].departureMinute;
    if (!Number.isFinite(departure)) {
      break;
    }
    if (departure - arrival < 5) {
      return { error: "dwell", minute };
    }
    reachesThirdDay = reachesThirdDay || departure >= 2880;
    previousDeparture = departure;
  }

  return { error: reachesThirdDay ? "thirdDay" : "", minute };
}

function buildLines(snapshot, section, directory, runtimes, runtimeSources, layouts, timing) {
  const startedAt = timing ? diagnosticNow() : 0;
  const stationNames = new Map(asArray(directory).map((station) => [station.stationId, station.name]));
  const coverageIds = new Set(asArray(section?.coverages).map((coverage) => coverage.lineId));
  let targetRowCount = 0;
  let targetStopCount = 0;
  const result = asArray(snapshot.lines)
    .filter((line) => coverageIds.size === 0 || coverageIds.has(line.id))
    .map((line) => {
      const runtime = lineRuntime(runtimes, runtimeSources, line.id);
      const layout = layouts[line.id]?.value;
      const rows = buildRows(snapshot, line.id);
      const layoutStops = asArray(layout?.stops).filter((stop) => stop?.stopKey);
      const stopKeys = buildStopKeys(layout);
      if (timing?.lineId === line.id) {
        targetRowCount = rows.length;
        targetStopCount = layoutStops.length;
      }
      const stations = stopKeys.map((stopKey, index) => {
        const layoutStop = layoutStops[index];
        return {
          id: stopKey,
          name: layoutStop?.name || stationNames.get(stopKey) || stopKey,
          distance: index,
          stopKey,
          waypointIndex: layoutStop?.waypointIndex,
          order: layoutStop?.order ?? index,
          occurrence: layoutStop?.order ?? index
        };
      });
      return {
        ...line,
        runtime,
        stations,
        stopSig: layout?.stopSig || rows[0]?.stopSig || "",
        trains: rows.map((row) => buildTrain(row, layout, runtime, stationNames)),
        sliceTrains: rows.map((row) => buildSliceTrain(
          row,
          layout,
          runtimes[line.id]?.sliceHistoricalEstimate,
          stationNames)),
        plannedTrains: buildAppliedRows(snapshot, line.id)
          .map((row) => buildTrain(row, layout, null, stationNames))
      };
    });
  if (timing) {
    timing.buildLinesMs = diagnosticNow() - startedAt;
    timing.snapshotLineCount = asArray(snapshot.lines).length;
    timing.rowCount = asArray(snapshot.lines)
      .reduce((total, line) => total + buildRows(snapshot, line.id).length, 0);
    timing.targetRowCount = targetRowCount;
    timing.targetStopCount = targetStopCount;
    timing.buildMeasured = true;
  }
  return result;
}

export default function useTimetableController({ activeTransportMode, isActive, sharedSnapshot }) {
  const api = useMemo(() => getWorkbenchApi(), []);
  const editorIdRef = useRef(createEditorId());
  const directoryRetryRef = useRef(null);
  const directoryGenerationRef = useRef(0);
  const directoryRequestRef = useRef(0);
  const loadedModeRef = useRef("");
  const layoutGenerationRef = useRef(0);
  const layoutRequestRef = useRef(new Map());
  const layoutsRef = useRef({});
  const layoutRequestSequenceRef = useRef(0);
  const layoutTimingRef = useRef(null);
  const runtimeRequestRef = useRef(new Map());
  const runtimeRequestGenerationRef = useRef(new Map());
  const runtimeStartTailRef = useRef(Promise.resolve());
  const runtimesRef = useRef({});
  const runtimeSourcesRef = useRef({});
  const sourceTransactionsRef = useRef({});
  const monitorChartRequestRef = useRef(new Map());
  const actualTripsRef = useRef({});
  const averageWaitingLineRef = useRef("");
  const runtimeActiveRef = useRef(isActive);
  const reportedRunTimeErrorsRef = useRef(new Set());
  const pendingQueriesRef = useRef({});
  const pendingModeRef = useRef(activeTransportMode);
  const [snapshot, setSnapshot] = useState(EMPTY_SNAPSHOT);
  const [directory, setDirectory] = useState([]);
  const [indexVersion, setIndexVersion] = useState(0);
  const [startStationId, setStartStationId] = useState("");
  const [endStationId, setEndStationId] = useState("");
  const [sections, setSections] = useState([]);
  const [sectionId, setSectionId] = useState("");
  const [runtimes, setRuntimes] = useState({});
  const [runtimeSources, setRuntimeSources] = useState({});
  const [sourceTransactions, setSourceTransactions] = useState({});
  const [monitorAverageStates, setMonitorAverageStates] = useState({});
  const [actualTrips, setActualTrips] = useState({});
  const [layouts, setLayouts] = useState({});
  const [pendingQueries, setPendingQueries] = useState({});
  const [dirtyLineIds, setDirtyLineIds] = useState([]);
  const [loadError, setLoadError] = useState("");
  const [saveState, setSaveState] = useState("clean");
  const [saveError, setSaveError] = useState("");
  const [inputErrors, setInputErrors] = useState({});
  const snapshotReady = isSnapshotForMode(snapshot, activeTransportMode);
  const canSave = snapshotReady
    && dirtyLineIds.length > 0
    && saveState !== "saving"
    && Object.keys(sourceTransactions).length === 0
    && Object.keys(inputErrors).length === 0
    && dirtyLineIds.every((lineId) => {
      const source = runtimeSources[lineId]
        || (activeTransportMode === "bus" ? "busHistorical" : "theory");
      return Boolean(runtimes[lineId]?.[source]?.resultId);
    });

  const selectedSection = sections.find((section) => section.sectionId === sectionId) || sections[0] || null;
  const lines = useMemo(
    () => snapshotReady
      ? buildLines(snapshot, selectedSection, directory, runtimes, runtimeSources, layouts, layoutTimingRef.current)
      : [],
    [directory, layouts, runtimes, runtimeSources, selectedSection, snapshot, snapshotReady]
  );

  const setLineLayout = useCallback((lineId, layout) => {
    const next = { ...layoutsRef.current, [lineId]: layout };
    layoutsRef.current = next;
    setLayouts(next);
  }, []);

  const setInputError = useCallback((key, error) => {
    if (!key) {
      return;
    }
    setInputErrors((current) => {
      if (error) {
        return current[key] === error ? current : { ...current, [key]: error };
      }
      if (!current[key]) {
        return current;
      }
      const next = { ...current };
      delete next[key];
      return next;
    });
  }, []);

  const clearInputErrors = useCallback(() => {
    setInputErrors({});
  }, []);

  const validateDeparture = useCallback((lineId, trainId, occurrence, value) => {
    if (!isValidTimeValue(value)) {
      return { error: "format", minute: null };
    }
    const layout = layouts[lineId]?.value;
    const runtime = lineRuntime(runtimes, runtimeSources, lineId);
    const row = buildRows(snapshot, lineId).find((item) => item?.id === trainId);
    if (!row || !layout || !runtime) {
      return { error: "", minute: null };
    }
    const stationNames = new Map(directory.map((station) => [station.stationId, station.name]));
    return validateDepartureValue(buildTrain(row, layout, runtime, stationNames), runtime, occurrence, value);
  }, [directory, layouts, runtimeSources, runtimes, snapshot]);

  const resetLineLayouts = useCallback(() => {
    layoutGenerationRef.current += 1;
    layoutRequestRef.current.clear();
    layoutsRef.current = {};
    setLayouts({});
  }, []);

  const clearPendingQuery = useCallback((lineId, source, queryId = "") => {
    const key = runtimeKey(lineId, source);
    if (pendingQueriesRef.current[key]
      && (!queryId || pendingQueriesRef.current[key].queryId === queryId)) {
      const next = { ...pendingQueriesRef.current };
      delete next[key];
      pendingQueriesRef.current = next;
      setPendingQueries(next);
    }
  }, []);

  const setRuntimeSource = useCallback((lineId, source) => {
    if (!lineId || !isEditorRuntimeSource(source)) {
      return;
    }
    const next = { ...runtimeSourcesRef.current, [lineId]: source };
    runtimeSourcesRef.current = next;
    setRuntimeSources(next);
  }, []);

  const clearRuntimeSource = useCallback((lineId, source) => {
    setRuntimes((current) => {
      if (!current[lineId]?.[source]) {
        return current;
      }
      const line = { ...current[lineId] };
      delete line[source];
      const next = { ...current };
      if (Object.keys(line).length === 0) {
        delete next[lineId];
      } else {
        next[lineId] = line;
      }
      runtimesRef.current = next;
      return next;
    });
  }, []);

  const invalidateRuntimeSource = useCallback((lineId, source) => {
    if (!lineId || !isEditorRuntimeSource(source)) {
      return;
    }
    const key = runtimeKey(lineId, source);
    runtimeRequestGenerationRef.current.set(
      key,
      (runtimeRequestGenerationRef.current.get(key) || 0) + 1
    );
    runtimeRequestRef.current.delete(key);
    clearPendingQuery(lineId, source);
    clearRuntimeSource(lineId, source);
  }, [clearPendingQuery, clearRuntimeSource]);

  const reportRunTimeError = useCallback((operation, status) => {
    const detail = {
      operation,
      editorSessionId: status?.editorSessionId || editorIdRef.current,
      lineId: status?.lineId || "",
      source: status?.source || "",
      queryId: status?.queryId || "",
      state: status?.state || "Rejected",
      error: status?.error || "run-time-query-failed",
      detail: status?.detail || ""
    };
    if (detail.error === "run-time-theory-busy") {
      return;
    }
    const key = [
      detail.editorSessionId,
      detail.lineId,
      detail.source,
      detail.queryId,
      detail.state,
      detail.error,
      detail.detail
    ].join("|");
    if (reportedRunTimeErrorsRef.current.has(key)) {
      return;
    }
    if (reportedRunTimeErrorsRef.current.size >= 64) {
      reportedRunTimeErrorsRef.current.clear();
    }
    reportedRunTimeErrorsRef.current.add(key);
    if (typeof console !== "undefined" && typeof console.error === "function") {
      console.error(`[RT Workbench RunTime] ${JSON.stringify(detail)}`);
    }
  }, []);

  const acceptRuntime = useCallback((status, operation = "getRunTimeQueryStatus", context = null) => {
    if (!status || status.editorSessionId !== editorIdRef.current) {
      return;
    }
    const lineId = status.lineId || context?.lineId || "";
    const source = status.source || context?.source || "theory";
    if (status.state === "Completed" || status.state === "Failed" || status.state === "Cancelled") {
      setRuntimes((current) => {
        const next = {
          ...current,
          [lineId]: { ...current[lineId], [source]: status }
        };
        runtimesRef.current = next;
        return next;
      });
    }
    if (status.state === "Completed") {
      clearPendingQuery(lineId, source, status.queryId);
      if (sourceTransactionsRef.current[lineId] === source) {
        const layout = layoutsRef.current[lineId]?.value;
        if (layout) {
          setSnapshot((current) => rebuildLineRows(current, lineId, layout, status));
        }
        setRuntimeSource(lineId, source);
        const nextTransactions = { ...sourceTransactionsRef.current };
        delete nextTransactions[lineId];
        sourceTransactionsRef.current = nextTransactions;
        setSourceTransactions(nextTransactions);
        setDirtyLineIds((current) => current.includes(lineId) ? current : [...current, lineId]);
        setSaveError("");
        setSaveState("dirty");
      }
      setLoadError("");
    } else if (status.state === "Failed" || status.state === "Cancelled") {
      clearPendingQuery(lineId, source, status.queryId);
      if (sourceTransactionsRef.current[lineId] === source) {
        setRuntimeSource(lineId, source);
        const nextTransactions = { ...sourceTransactionsRef.current };
        delete nextTransactions[lineId];
        sourceTransactionsRef.current = nextTransactions;
        setSourceTransactions(nextTransactions);
      }
      setLoadError(status.error || "run-time-query-failed");
      const detail = {
        ...status,
        lineId,
        source,
        requestGeneration: context?.requestGeneration
      };
      reportRunTimeError(operation, detail);
    }
  }, [clearPendingQuery, reportRunTimeError, setRuntimeSource]);

  const requestRuntime = useCallback((lineId, source = "theory", refreshReady = false) => {
    if (!lineId) {
      return Promise.resolve(null);
    }
    const key = runtimeKey(lineId, source);
    const ready = runtimesRef.current[lineId]?.[source];
    const activeRequest = runtimeRequestRef.current.get(key);
    if (activeRequest) {
      return activeRequest.promise;
    }
    const pending = pendingQueriesRef.current[key];
    if (pending) {
      return Promise.resolve(pending);
    }
    if (!refreshReady && ready?.state === "Completed") {
        return Promise.resolve(ready);
    }

    const request = {
      editorSessionId: editorIdRef.current,
      lineId,
      source
    };
    const requestGeneration = (runtimeRequestGenerationRef.current.get(key) || 0) + 1;
    const mode = activeTransportMode;
    runtimeRequestGenerationRef.current.set(key, requestGeneration);
    if (refreshReady) {
      clearRuntimeSource(lineId, source);
    }
    clearPendingQuery(lineId, source);
    const isCurrent = () => runtimeRequestGenerationRef.current.get(key) === requestGeneration
      && pendingModeRef.current === mode;
    const start = async () => {
      if (!runtimeActiveRef.current || !isCurrent()) {
        return null;
      }
      const queuedReady = runtimesRef.current[lineId]?.[source];
      if (!refreshReady && queuedReady?.state === "Completed") {
        return queuedReady;
      }
      const queuedPending = pendingQueriesRef.current[key];
      if (queuedPending) {
        return queuedPending;
      }

      try {
        const status = source === "monitorAverage"
          ? await api.queryMonitorAverage(request)
          : await api.startRunTimeQuery(request);
        if (!isCurrent()) {
          return null;
        }
        if (status?.state === "Running") {
          const nextPending = { ...status, mode };
          const next = { ...pendingQueriesRef.current, [key]: nextPending };
          pendingQueriesRef.current = next;
          setPendingQueries(next);
        }
        acceptRuntime(status, "startRunTimeQuery", { ...request, requestGeneration });
        return status;
      } catch (error) {
        if (!isCurrent()) {
          return null;
        }
        const message = error instanceof Error ? error.message : String(error);
        setLoadError(message);
        const detail = {
          ...request,
          requestGeneration,
          state: "Rejected",
          error: message
        };
        reportRunTimeError("startRunTimeQuery", detail);
        return null;
      }
    };
    const promise = runtimeStartTailRef.current
      .then(start, start)
      .finally(() => {
        const current = runtimeRequestRef.current.get(key);
        if (current?.generation === requestGeneration) {
          runtimeRequestRef.current.delete(key);
        }
      });
    runtimeStartTailRef.current = promise.then(() => null, () => null);
    runtimeRequestRef.current.set(key, {
      generation: requestGeneration,
      promise
    });
    return promise;
  }, [acceptRuntime, activeTransportMode, api, clearPendingQuery, clearRuntimeSource, reportRunTimeError]);

  const ensureBusHistoricalRuntime = useCallback((lineId, forceRetry = false) => {
    if (!isActive || activeTransportMode !== "bus" || !lineId) {
      return Promise.resolve(null);
    }
    setRuntimeSource(lineId, "busHistorical");
    return requestRuntime(lineId, "busHistorical", forceRetry);
  }, [activeTransportMode, isActive, requestRuntime, setRuntimeSource]);

  const switchRuntimeSource = useCallback((lineId, source) => {
    if (!lineId || (source !== "theory" && source !== "monitorAverage")) {
      return Promise.resolve(null);
    }
    if (source === "monitorAverage" && !monitorAverageStates[lineId]?.ready) {
      return Promise.resolve(null);
    }
    const nextTransactions = { ...sourceTransactionsRef.current, [lineId]: source };
    sourceTransactionsRef.current = nextTransactions;
    setSourceTransactions(nextTransactions);
    const ready = runtimesRef.current[lineId]?.[source];
    if (source === "theory" && ready?.state === "Completed") {
        acceptRuntime(ready, "switchRuntimeSource");
        return Promise.resolve(ready);
    }
    return requestRuntime(lineId, source, source === "monitorAverage");
  }, [acceptRuntime, monitorAverageStates, requestRuntime]);

  const setActualLayer = useCallback((lineId, source, layer) => {
    const next = {
      ...actualTripsRef.current,
      [lineId]: { ...actualTripsRef.current[lineId], [source]: layer }
    };
    actualTripsRef.current = next;
    setActualTrips(next);
  }, []);

  const syncMonitorSubscription = useCallback(() => {
    api.setMonitorSubscription({
      averageWaitingLineId: averageWaitingLineRef.current
    }).catch(() => {});
  }, [api]);

  const loadMonitorAverageState = useCallback(async (lineId) => {
    if (!isActive || !lineId) {
      return null;
    }
    const response = await api.loadMonitorAverageState({
      lineId,
      stopSig: layoutsRef.current[lineId]?.value?.stopSig || ""
    });
    if (!response?.success) {
      return null;
    }
    setMonitorAverageStates((current) => ({ ...current, [lineId]: response }));
    averageWaitingLineRef.current = response.ready ? "" : lineId;
    syncMonitorSubscription();
    return response;
  }, [api, isActive, syncMonitorSubscription]);

  const loadActualTrips = useCallback(async (lineId, source, startMinute, endMinute, coverageFilter) => {
    if (!isActive || !lineId || (source !== "actualToday" && source !== "actualYesterday")) {
      return;
    }
    const key = runtimeKey(lineId, source);
    const generation = (monitorChartRequestRef.current.get(key) || 0) + 1;
    monitorChartRequestRef.current.set(key, generation);
    setActualLayer(lineId, source, {
      state: "loading",
      startMinute,
      endMinute,
      coverageFilter,
      headers: {},
      details: {}
    });
    try {
      const headers = await api.loadMonitorTripHeaders({
        dayOffset: source === "actualYesterday" ? -1 : 0,
        lineId,
        startMinute,
        endMinute,
        limit: 128,
        coverageFilter
      });
      if (monitorChartRequestRef.current.get(key) !== generation) {
        return;
      }
      if (!headers?.success || headers.truncated) {
        setActualLayer(lineId, source, {
          state: "unavailable",
          startMinute,
          endMinute,
          coverageFilter,
          hasLineTrips: headers?.hasLineTrips === true,
          dataComplete: headers?.dataComplete,
          persistenceHealthy: headers?.persistenceHealthy,
          headers: {},
          details: {}
        });
        setLoadError(headers?.error || "monitor-chart-range-too-large");
        return;
      }
      const tripKeys = asArray(headers.trips).map((trip) => trip?.tripKey).filter(Boolean);
      const layer = {
        state: "ready",
        serviceDateKey: headers.serviceDateKey,
        startMinute,
        endMinute,
        coverageFilter,
        hasLineTrips: headers.hasLineTrips === true,
        hasRangeTrips: tripKeys.length > 0,
        dataComplete: headers.dataComplete,
        persistenceHealthy: headers.persistenceHealthy,
        droppedTripCount: headers.droppedTripCount,
        lastIssueCode: headers.lastIssueCode,
        issueCount: headers.issueCount,
        headers: Object.fromEntries(asArray(headers.trips).map((trip) => [trip.tripKey, trip])),
        details: {}
      };
      setActualLayer(lineId, source, layer);
      for (let offset = 0; offset < tripKeys.length; offset += 32) {
        const response = await api.loadMonitorTripDetails({ tripKeys: tripKeys.slice(offset, offset + 32) });
        if (monitorChartRequestRef.current.get(key) !== generation) {
          return;
        }
        if (!response?.success) {
          setActualLayer(lineId, source, { ...layer, state: "unavailable" });
          setLoadError(response?.error || "monitor-chart-detail-failed");
          return;
        }
        asArray(response.details).filter((detail) => detail?.success).forEach((detail) => {
          if (detail.header?.tripKey) {
            layer.details[detail.header.tripKey] = detail;
          }
        });
        setActualLayer(lineId, source, {
          ...layer,
          details: { ...layer.details }
        });
      }
    } catch (error) {
      if (monitorChartRequestRef.current.get(key) !== generation) {
        return;
      }
      setActualLayer(lineId, source, {
        state: "unavailable",
        startMinute,
        endMinute,
        coverageFilter,
        headers: {},
        details: {}
      });
      const message = error instanceof Error ? error.message : String(error);
      setLoadError(message);
    }
  }, [api, isActive, setActualLayer]);

  const ensureTimetableLineLayout = useCallback((lineId, forceRetry = false) => {
    if (!isActive || !lineId) {
      return Promise.resolve(null);
    }

    const pending = layoutRequestRef.current.get(lineId);
    if (pending) {
      return pending.promise;
    }

    const existing = layoutsRef.current[lineId];
    if (existing?.state === "ready" && existing.value?.mode === activeTransportMode) {
      return Promise.resolve(existing.value);
    }
    if (existing?.state === "failed" && (!forceRetry || !existing.retryable)) {
      return Promise.resolve(null);
    }
    if (existing) {
      const next = { ...layoutsRef.current };
      delete next[lineId];
      layoutsRef.current = next;
      setLayouts(next);
    }

    const generation = layoutGenerationRef.current;
    const requestId = `${generation}:${lineId}:${++layoutRequestSequenceRef.current}`;
    const startedAt = forceRetry ? diagnosticNow() : 0;
    setLineLayout(lineId, { state: "loading", value: null, error: "", retryable: false });
    const promise = api.loadTimetableLineLayout({ lineId })
      .then((response) => {
        const current = layoutRequestRef.current.get(lineId);
        if (layoutGenerationRef.current !== generation || current?.requestId !== requestId) {
          return null;
        }

        const stops = asArray(response?.stops);
        let error = "";
        if (!response?.success) {
          error = response?.error || "timetable-line-layout-failed";
        } else if (response.lineId !== lineId) {
          error = "timetable-line-layout-line-id-mismatch";
        } else if (response.mode && response.mode !== activeTransportMode) {
          error = "timetable-line-layout-mode-mismatch";
        } else if (stops.length === 0 || stops.some((stop) => !stop?.stopKey)) {
          error = "timetable-line-layout-stops-invalid";
        }

        if (forceRetry) {
          layoutTimingRef.current = {
            requestId,
            lineId,
            engineCallMs: diagnosticNow() - startedAt,
            success: !error,
            error
          };
        }
        if (error) {
          setLineLayout(lineId, {
            state: "failed",
            value: null,
            error,
            retryable: isRetryableLayoutError(error)
          });
          setLoadError(error);
          return null;
        }

        const value = {
          lineId,
          mode: response.mode || "",
          stopSig: response.stopSig || "",
          stops
        };
        setLineLayout(lineId, { state: "ready", value, error: "" });
        setLoadError("");
        return value;
      })
      .catch((error) => {
        const current = layoutRequestRef.current.get(lineId);
        if (layoutGenerationRef.current !== generation || current?.requestId !== requestId) {
          return null;
        }

        const message = error instanceof Error ? error.message : String(error);
        if (forceRetry) {
          layoutTimingRef.current = {
            requestId,
            lineId,
            engineCallMs: diagnosticNow() - startedAt,
            success: false,
            error: message
          };
        }
        setLineLayout(lineId, { state: "failed", value: null, error: message, retryable: true });
        setLoadError(message);
        return null;
      })
      .finally(() => {
        const current = layoutRequestRef.current.get(lineId);
        if (current?.requestId === requestId) {
          layoutRequestRef.current.delete(lineId);
        }
      });
    layoutRequestRef.current.set(lineId, { requestId, promise });
    return promise;
  }, [activeTransportMode, api, isActive, setLineLayout]);

  const acceptDirectory = useCallback((stationResponse) => {
    if (!stationResponse?.success) {
      setLoadError(stationResponse?.error || "run-chart-station-directory-failed");
      return false;
    }
    const nextDirectory = asArray(stationResponse.stations);
    const selectable = nextDirectory.filter((station) => !station.passOnly && station.stationId);
    const selectableIds = new Set(selectable.map((station) => station.stationId));
    setDirectory(nextDirectory);
    setStartStationId((current) => selectableIds.has(current) ? current : "");
    setEndStationId((current) => selectableIds.has(current) ? current : "");
    setLoadError("");
    if (stationResponse.status === "warming" || stationResponse.status === "stale") {
      setIndexVersion(0);
      setSections([]);
      setSectionId("");
      return false;
    }
    setIndexVersion(stationResponse.publishedIndexVersion || 0);
    return true;
  }, []);

  const isCurrentDirectory = useCallback((mode, generation) => (
    isActive
      && activeTransportMode === mode
      && directoryGenerationRef.current === generation
  ), [activeTransportMode, isActive]);

  const isCurrentRequest = useCallback((mode, generation, request) => (
    isCurrentDirectory(mode, generation)
      && directoryRequestRef.current === request
  ), [isCurrentDirectory]);

  const loadDirectory = useCallback(async (mode, generation) => {
    if (!isCurrentDirectory(mode, generation)) {
      return;
    }
    const request = directoryRequestRef.current + 1;
    directoryRequestRef.current = request;
    if (directoryRetryRef.current != null) {
      window.clearTimeout(directoryRetryRef.current);
      directoryRetryRef.current = null;
    }
    let stationResponse;
    try {
      stationResponse = await api.loadRunChartStationDirectory({
        mode,
        expectedIndexVersion: 0
      });
    } catch (error) {
      if (isCurrentRequest(mode, generation, request)) {
        setLoadError(error instanceof Error ? error.message : String(error));
      }
      return;
    }
    if (!isCurrentRequest(mode, generation, request)) {
      return;
    }
    if (!acceptDirectory(stationResponse)
      && stationResponse?.success
      && (stationResponse.status === "warming" || stationResponse.status === "stale")) {
      if (!isCurrentRequest(mode, generation, request)) {
        return;
      }
      directoryRetryRef.current = window.setTimeout(() => {
        if (!isCurrentRequest(mode, generation, request)) {
          return;
        }
        loadDirectory(mode, generation);
      }, 500);
    }
  }, [acceptDirectory, api, isCurrentDirectory, isCurrentRequest]);

  const loadBase = useCallback(async (mode, generation) => {
    if (!isCurrentDirectory(mode, generation)) {
      return;
    }
    setLoadError("");
    const snapshotRequest = isSnapshotForMode(sharedSnapshot, mode)
      ? Promise.resolve(sharedSnapshot)
      : api.loadSnapshot({ mode });
    if (mode === "bus") {
      const nextSnapshot = await snapshotRequest;
      if (isCurrentDirectory(mode, generation)) {
        setSnapshot(normalizeSnapshot(nextSnapshot));
        setDirectory([]);
        setSections([]);
        setSectionId("");
        setIndexVersion(0);
      }
      return;
    }
    const directoryRequest = loadDirectory(mode, generation);
    const nextSnapshot = await snapshotRequest;
    if (!isCurrentDirectory(mode, generation)) {
      return;
    }
    setSnapshot(normalizeSnapshot(nextSnapshot));
    await directoryRequest;
  }, [api, isCurrentDirectory, loadDirectory, sharedSnapshot]);

  const reloadBase = useCallback(() => {
    resetLineLayouts();
    const generation = directoryGenerationRef.current + 1;
    directoryGenerationRef.current = generation;
    directoryRequestRef.current += 1;
    const mode = activeTransportMode;
    if (directoryRetryRef.current != null) {
      window.clearTimeout(directoryRetryRef.current);
      directoryRetryRef.current = null;
    }
    return loadBase(mode, generation).catch((error) => {
      if (isCurrentDirectory(mode, generation)) {
        setLoadError(error instanceof Error ? error.message : String(error));
      }
    });
  }, [activeTransportMode, isCurrentDirectory, loadBase, resetLineLayouts]);

  useEffect(() => {
    if (!isActive || loadedModeRef.current === activeTransportMode) {
      return undefined;
    }
    let cancelled = false;
    let innerFrame = 0;
    const mode = activeTransportMode;
    const outerFrame = window.requestAnimationFrame(() => {
      innerFrame = window.requestAnimationFrame(() => {
        reloadBase().then(() => {
          if (!cancelled) {
            loadedModeRef.current = mode;
          }
        });
      });
    });
    return () => {
      cancelled = true;
      window.cancelAnimationFrame(outerFrame);
      if (innerFrame) {
        window.cancelAnimationFrame(innerFrame);
      }
      directoryGenerationRef.current += 1;
      directoryRequestRef.current += 1;
      if (directoryRetryRef.current != null) {
        window.clearTimeout(directoryRetryRef.current);
        directoryRetryRef.current = null;
      }
    };
  }, [activeTransportMode, isActive, reloadBase]);

  useEffect(() => {
    if (!isActive || activeTransportMode === "bus" || !startStationId || !endStationId || !indexVersion) {
      return;
    }
    let cancelled = false;
    const generation = directoryGenerationRef.current;
    const mode = activeTransportMode;
    const isCurrent = () => !cancelled && isCurrentDirectory(mode, generation);
    api.queryRunChartSections({ mode, fromStationId: startStationId, toStationId: endStationId, expectedIndexVersion: indexVersion })
      .then((response) => {
        if (!isCurrent()) {
          return;
        }
        if (response?.status === "stale") {
          loadDirectory(mode, generation);
          return;
        }
        const nextSections = asArray(response?.sections);
        setSections(nextSections);
        setSectionId(nextSections[0]?.sectionId || "");
        if (!response?.success) {
          setLoadError(response?.error || "run-chart-section-query-failed");
        }
      })
      .catch((error) => {
        if (isCurrent()) {
          setLoadError(error instanceof Error ? error.message : String(error));
        }
      });
    return () => {
      cancelled = true;
    };
  }, [activeTransportMode, api, endStationId, indexVersion, isActive, isCurrentDirectory, loadDirectory, startStationId]);

  useEffect(() => api.onRunTimeQuery((status) => {
    const key = runtimeKey(status?.lineId, status?.source);
    if (pendingQueriesRef.current[key]?.queryId === status?.queryId) {
      acceptRuntime(status);
    }
  }), [acceptRuntime, api]);

  useEffect(() => api.onRunTimeInvalidated?.((event) => {
    if (event?.editorSessionId !== editorIdRef.current) {
      return;
    }
    invalidateRuntimeSource(event.lineId, event.source);
    if (event?.source !== "monitorAverage") {
      return;
    }
    setMonitorAverageStates((current) => {
      const next = { ...current };
      delete next[event.lineId];
      return next;
    });
    if (runtimeSourcesRef.current[event.lineId] !== "monitorAverage") {
      return;
    }
    setRuntimeSource(event.lineId, "theory");
    const nextTransactions = { ...sourceTransactionsRef.current };
    delete nextTransactions[event.lineId];
    sourceTransactionsRef.current = nextTransactions;
    setSourceTransactions(nextTransactions);
  }), [api, invalidateRuntimeSource, setRuntimeSource]);

  useEffect(() => api.onMonitorChanged?.((event) => {
    if (event?.monitorAverageBecameReady && event?.lineId) {
      averageWaitingLineRef.current = "";
      syncMonitorSubscription();
      loadMonitorAverageState(event.lineId).catch(() => {});
    }
  }), [api, loadMonitorAverageState, syncMonitorSubscription]);

  useEffect(() => {
    runtimeActiveRef.current = isActive;
    if (!isActive) {
      averageWaitingLineRef.current = "";
      monitorChartRequestRef.current.forEach((generation, key) => {
        monitorChartRequestRef.current.set(key, generation + 1);
      });
      syncMonitorSubscription();
      return;
    }
    if (pendingModeRef.current === activeTransportMode) {
      return;
    }
    pendingModeRef.current = activeTransportMode;
    runtimeRequestRef.current.clear();
    pendingQueriesRef.current = {};
    setPendingQueries({});
    runtimesRef.current = {};
    setRuntimes({});
    runtimeSourcesRef.current = {};
    setRuntimeSources({});
    sourceTransactionsRef.current = {};
    setSourceTransactions({});
    setMonitorAverageStates({});
    averageWaitingLineRef.current = "";
    monitorChartRequestRef.current.forEach((generation, key) => {
      monitorChartRequestRef.current.set(key, generation + 1);
    });
    actualTripsRef.current = {};
    setActualTrips({});
    syncMonitorSubscription();
  }, [activeTransportMode, isActive, syncMonitorSubscription]);

  useEffect(() => api.onLineInvalidated((event) => {
    loadedModeRef.current = "";
    const invalidIds = new Set(asArray(event?.lineIds));
    setRuntimes((current) => {
      const next = Object.fromEntries(Object.entries(current).filter(([lineId]) => !invalidIds.has(lineId)));
      runtimesRef.current = next;
      return next;
    });
    setRuntimeSources((current) => {
      const next = Object.fromEntries(Object.entries(current).filter(([lineId]) => !invalidIds.has(lineId)));
      runtimeSourcesRef.current = next;
      return next;
    });
    setMonitorAverageStates((current) => Object.fromEntries(
      Object.entries(current).filter(([lineId]) => !invalidIds.has(lineId))
    ));
    invalidIds.forEach((lineId) => ["actualToday", "actualYesterday"].forEach((source) => {
      const key = runtimeKey(lineId, source);
      monitorChartRequestRef.current.set(key, (monitorChartRequestRef.current.get(key) || 0) + 1);
    }));
    actualTripsRef.current = Object.fromEntries(Object.entries(actualTripsRef.current)
      .filter(([lineId]) => !invalidIds.has(lineId)));
    setActualTrips(actualTripsRef.current);
    const nextPending = Object.fromEntries(Object.entries(pendingQueriesRef.current)
      .filter(([, query]) => !invalidIds.has(query?.lineId)));
    pendingQueriesRef.current = nextPending;
    setPendingQueries(nextPending);
    reloadBase();
  }), [api, reloadBase]);

  useEffect(() => () => {
    runtimeActiveRef.current = false;
    averageWaitingLineRef.current = "";
    monitorChartRequestRef.current.forEach((generation, key) => {
      monitorChartRequestRef.current.set(key, generation + 1);
    });
    api.setMonitorSubscription({ averageWaitingLineId: "" }).catch(() => {});
    api.closeRunTimeEditorSession({ editorSessionId: editorIdRef.current }).catch(() => {});
  }, [api]);

  const updateDeparture = useCallback((lineId, trainId, occurrence, minute) => {
    const layout = layouts[lineId]?.value;
    const runtime = lineRuntime(runtimes, runtimeSources, lineId);
    const stopCount = asArray(layout?.stops).filter((stop) => stop?.stopKey).length;
    if (stopCount === 0) {
      setLoadError("timetable-line-layout-required");
      return;
    }
    if (!hasRunTimeSegments(runtime, stopCount)) {
      setLoadError("run-time-query-required");
      return;
    }
    if (!hasClosingSegment(runtime, stopCount)) {
      setLoadError("run-time-closing-segment-required");
      return;
    }
    setSnapshot((current) => {
      const blocks = asArray(current.lineDraftRowsByLineId).map((block) => ({
        ...block,
        lineDraftRows: asArray(block.lineDraftRows).map((row) => {
          if (block.lineId !== lineId || row.id !== trainId) {
            return row;
          }
          const train = buildTrain(row, layout, runtime, new Map(directory.map((station) => [station.stationId, station.name])));
          const stopIndex = train.stops.findIndex((stop) => stop.occurrence === occurrence);
          if (stopIndex < 0 || stopIndex === train.stops.length - 1) {
            return row;
          }
          train.stops[stopIndex].departureMinute = minute;
          for (let index = stopIndex + 1; index < train.stops.length; index++) {
            const previousDeparture = train.stops[index - 1].departureMinute;
            const arrival = previousDeparture == null
              ? null
              : previousDeparture + runtime.segments[index - 1].segmentMinutes;
            train.stops[index].arrivalMinute = arrival;
            if (index === train.stops.length - 1) {
              train.stops[index].departureMinute = null;
            }
          }
          return {
            ...row,
            source: row.source || "manual",
            timedStops: train.stops.map((stop, index) => ({
              stopKey: stop.stationId,
              arrive: index === 0 ? null : stop.arrivalMinute,
              depart: index === train.stops.length - 1 ? null : stop.departureMinute
            }))
          };
        })
      }));
      return { ...current, lineDraftRowsByLineId: blocks };
    });
    setDirtyLineIds((current) => current.includes(lineId) ? current : [...current, lineId]);
    setSaveError("");
    setSaveState("dirty");
  }, [directory, layouts, runtimeSources, runtimes]);

  const markLineCustom = useCallback((lineId) => {
    const layout = layouts[lineId]?.value;
    const runtime = lineRuntime(runtimes, runtimeSources, lineId);
    const stopCount = asArray(layout?.stops).filter((stop) => stop?.stopKey).length;
    if (stopCount === 0) {
      setLoadError("timetable-line-layout-required");
      return;
    }
    if (!hasRunTimeSegments(runtime, stopCount)) {
      setLoadError("run-time-query-required");
      return;
    }
    if (!hasClosingSegment(runtime, stopCount)) {
      setLoadError("run-time-closing-segment-required");
      return;
    }
    setSnapshot((current) => {
      const stationNames = new Map(directory.map((station) => [station.stationId, station.name]));
      const blocks = asArray(current.lineDraftRowsByLineId).map((block) => ({
        ...block,
        lineDraftRows: asArray(block.lineDraftRows).map((row) => {
          if (block.lineId !== lineId) {
            return row;
          }
          return {
            ...row,
            source: row.source || "manual",
            timedStops: buildBatchTimedStops(row, layout, runtime, stationNames)
          };
        })
      }));
      return { ...current, lineDraftRowsByLineId: blocks };
    });
    setDirtyLineIds((current) => current.includes(lineId) ? current : [...current, lineId]);
    setSaveError("");
    setSaveState("dirty");
  }, [directory, layouts, runtimeSources, runtimes]);

  const saveAll = useCallback(async () => {
    if (!canSave) {
      return;
    }
    setSaveState("saving");
    setSaveError("");
    const blocks = dirtyLineIds.map((lineId) => {
      const runtime = lineRuntime(runtimes, runtimeSources, lineId);
      const layout = layouts[lineId]?.value;
      const rows = buildRows(snapshot, lineId);
      return {
        lineId,
        stopSig: layout?.stopSig || rows[0]?.stopSig || "",
        runtimeResultId: runtime?.resultId || "",
        rows: rows.map((row) => {
          const timedStops = continuousTimedStops(row.timedStops);
          return {
            rowId: row.id,
            slotMinute: timeToMinutes(row.time),
            kind: row.kind || "local",
            source: row.source || "manual",
            timedStops,
            truncateFromStopIndex: -1
          };
        })
      };
    });
    try {
      const result = await api.saveScheduleBatch({ editorSessionId: editorIdRef.current, lines: blocks, returnSnapshot: true });
      if (!result?.success) {
        setSaveState("error");
        setSaveError(asArray(result?.errors).join("；") || "schedule-batch-save-failed");
        return;
      }
      setSnapshot(useAppliedRows(result.snapshot, dirtyLineIds));
      setDirtyLineIds([]);
      setSaveState("applied");
    } catch (error) {
      setSaveState("error");
      setSaveError(error instanceof Error ? error.message : String(error));
    }
  }, [api, canSave, dirtyLineIds, layouts, runtimeSources, runtimes, snapshot]);

  return {
    directory,
    lines,
    sections,
    sectionId,
    selectedSection,
    setSectionId,
    startStationId,
    setStartStationId,
    endStationId,
    setEndStationId,
    runtimes,
    runtimeSources,
    monitorAverageStates,
    actualTrips,
    sourceTransactions,
    pendingQueries,
    requestRuntime,
    setRuntimeSource,
    switchRuntimeSource,
    loadMonitorAverageState,
    loadActualTrips,
    ensureBusHistoricalRuntime,
    ensureTimetableLineLayout,
    layoutTimingRef,
    updateDeparture,
    validateDeparture,
    inputErrors,
    setInputError,
    clearInputErrors,
    markLineCustom,
    dirty: dirtyLineIds.length > 0,
    canSave,
    saveState,
    saveError,
    saveAll,
    loadError,
    reload: reloadBase
  };
}
