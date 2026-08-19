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

function buildBatchTimedStops(row, layout, runtime, stationNames, intervalMinutes) {
  const train = buildTrain(row, layout, runtime, stationNames);
  if (train.stops.length !== buildStopKeys(layout).length + 1) {
    return { error: "runtime", timedStops: [] };
  }
  const workingTrain = {
    ...train,
    stops: train.stops.map((stop, index) => ({
      ...stop,
      arrivalMinute: null,
      departureMinute: index === 0 ? train.slotMinute : null
    }))
  };
  let previousDeparture = train.slotMinute;
  const timedStops = [];
  for (let index = 0; index < workingTrain.stops.length; index += 1) {
    const stop = workingTrain.stops[index];
    if (index === 0) {
      timedStops.push({ stopKey: stop.stopKey, arrive: null, depart: train.slotMinute });
      continue;
    }
    const segmentMinutes = runtime?.segments?.[index - 1]?.segmentMinutes;
    if (!Number.isFinite(previousDeparture) || !Number.isFinite(segmentMinutes)) {
      return { error: "runtime", timedStops: [] };
    }
    const arrival = previousDeparture + segmentMinutes;
    stop.arrivalMinute = arrival;
    if (index === workingTrain.stops.length - 1) {
      timedStops.push({ stopKey: stop.stopKey, arrive: arrival, depart: null });
      continue;
    }
    const departure = Math.ceil((arrival + 5) / intervalMinutes) * intervalMinutes;
    const validation = validateDepartureValue(
      workingTrain,
      runtime,
      stop.occurrence,
      minutesToTime(departure)
    );
    if (validation.error || !Number.isFinite(validation.minute)) {
      return { error: validation.error || "format", timedStops: [] };
    }
    stop.departureMinute = validation.minute;
    previousDeparture = validation.minute;
    timedStops.push({
      stopKey: stop.stopKey,
      arrive: arrival,
      depart: validation.minute
    });
  }
  return { error: "", timedStops };
}

function buildSliceTrain(row, layout, runtime, stationNames) {
  const layoutStops = asArray(layout?.stops).filter((stop) => stop?.stopKey);
  const segments = asArray(runtime?.segments);
  const dwells = asArray(runtime?.dwells);
  const slotMinute = timeToMinutes(row?.time);
  if (layoutStops.length === 0 || runtime?.state !== "Completed") {
    return { id: row?.id || `row-${slotMinute}`, stops: [] };
  }

  const includeClosing = hasClosingSegment(runtime, layoutStops.length);
  const maxEventCount = includeClosing ? layoutStops.length + 1 : layoutStops.length;
  const prefixStopCount = Number.isInteger(runtime?.prefixStopCount)
    ? Math.max(1, Math.min(maxEventCount, runtime.prefixStopCount))
    : maxEventCount;
  const stops = [];
  let departure = slotMinute;
  stops.push({
    stationId: layoutStops[0].stopKey,
    stationName: layoutStops[0].name || stationNames.get(layoutStops[0].stopKey) || layoutStops[0].stopKey,
    stopKey: layoutStops[0].stopKey,
    waypointIndex: layoutStops[0].waypointIndex,
    occurrence: 0,
    order: 0,
    arrivalMinute: null,
    departureMinute: departure
  });
  for (let index = 1; index < prefixStopCount; index++) {
    const segment = segments[index - 1];
    if (!Number.isFinite(segment?.segmentMinutes)) {
      break;
    }
    const terminal = index === layoutStops.length;
    const stop = terminal ? layoutStops[0] : layoutStops[index];
    const arrival = departure + segment.segmentMinutes;
    const next = {
      stationId: stop.stopKey,
      stationName: stop.name || stationNames.get(stop.stopKey) || stop.stopKey,
      stopKey: stop.stopKey,
      waypointIndex: stop.waypointIndex,
      occurrence: index,
      order: index,
      arrivalMinute: arrival,
      departureMinute: null
    };
    stops.push(next);
    if (terminal || (index === layoutStops.length - 1 && !includeClosing)) {
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

function inputErrorKey(lineId, trainId, occurrence) {
  return `${lineId}\u001f${trainId}\u001f${occurrence}`;
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

function collectLineDwellErrors(snapshot, lineId, layout, runtime, directory) {
  if (!layout || !runtime) {
    return {};
  }
  const stationNames = new Map(asArray(directory).map((station) => [station.stationId, station.name]));
  const errors = {};
  buildRows(snapshot, lineId).forEach((row) => {
    const train = buildTrain(row, layout, runtime, stationNames);
    train.stops.slice(1, -1).forEach((stop) => {
      if (!Number.isFinite(stop.departureMinute)) {
        return;
      }
      const result = validateDepartureValue(
        train,
        runtime,
        stop.occurrence,
        minutesToTime(stop.departureMinute)
      );
      if (result.error === "dwell") {
        errors[inputErrorKey(lineId, train.id, stop.occurrence)] = "dwell";
      }
    });
  });
  return errors;
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
  const sharedSnapshotRef = useRef(sharedSnapshot);
  sharedSnapshotRef.current = sharedSnapshot;
  const editorIdRef = useRef(createEditorId());
  const directoryRetryRef = useRef(null);
  const directoryGenerationRef = useRef(0);
  const directoryRequestRef = useRef(0);
  const indexVersionRef = useRef(0);
  const startStationIdRef = useRef("");
  const endStationIdRef = useRef("");
  const lineNamesRequestRef = useRef(null);
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
  const snapshotRef = useRef(EMPTY_SNAPSHOT);
  snapshotRef.current = snapshot;
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
  const [layoutRevision, setLayoutRevision] = useState(0);
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
  const lineDirectory = snapshotReady ? asArray(snapshot.lines) : [];
  const lines = useMemo(
    () => snapshotReady
      ? buildLines(snapshot, selectedSection, directory, runtimes, runtimeSources, layouts, layoutTimingRef.current)
      : [],
    [directory, layouts, runtimes, runtimeSources, selectedSection, snapshot, snapshotReady]
  );
  const endStations = useMemo(() => {
    const startStation = directory.find((station) => station.stationId === startStationId);
    if (!startStation?.networkId) {
      return [];
    }
    return directory.filter((station) => station?.stationId
      && station.networkId === startStation.networkId);
  }, [directory, startStationId]);
  const hasSelectedNetwork = useMemo(
    () => endStations.some((station) => station.stationId === endStationId),
    [endStationId, endStations]
  );

  const setLineLayout = useCallback((lineId, layout) => {
    const next = { ...layoutsRef.current, [lineId]: layout };
    layoutsRef.current = next;
    setLayouts(next);
  }, []);

  const selectStartStation = useCallback((stationId) => {
    const nextStationId = stationId || "";
    if (startStationIdRef.current === nextStationId) {
      return;
    }
    startStationIdRef.current = nextStationId;
    endStationIdRef.current = "";
    setStartStationId(nextStationId);
    setEndStationId("");
    setSections([]);
    setSectionId("");
  }, []);

  const selectEndStation = useCallback((stationId) => {
    const nextStationId = stationId || "";
    if (endStationIdRef.current === nextStationId) {
      return;
    }
    endStationIdRef.current = nextStationId;
    setEndStationId(nextStationId);
    setSections([]);
    setSectionId("");
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

  const clearInputErrors = useCallback((lineId = "", trainId = "", preserveDwell = false) => {
    if (!lineId) {
      setInputErrors((current) => preserveDwell
        ? Object.fromEntries(Object.entries(current).filter(([, value]) => value === "dwell"))
        : {});
      return;
    }
    const prefix = trainId
      ? `${lineId}\u001f${trainId}\u001f`
      : `${lineId}\u001f`;
    setInputErrors((current) => Object.fromEntries(
      Object.entries(current).filter(([key, value]) => !key.startsWith(prefix)
        || (preserveDwell && value === "dwell"))
    ));
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
    setLayoutRevision((current) => current + 1);
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

  const markRuntimeQueued = useCallback((lineId, source) => {
    if (!lineId || source !== "theory") {
      return;
    }
    const key = runtimeKey(lineId, source);
    const current = runtimesRef.current[lineId]?.[source];
    const pending = pendingQueriesRef.current[key];
    const status = {
      ...(current || pending || {}),
      editorSessionId: editorIdRef.current,
      lineId,
      source,
      queryId: pending?.queryId || current?.queryId || "",
      state: "Queued",
      resultId: "",
      error: "",
      detail: "",
      complete: false,
      prefixStopCount: 0,
      missingKind: "none",
      segments: [],
      dwells: []
    };
    const nextRuntimes = {
      ...runtimesRef.current,
      [lineId]: { ...runtimesRef.current[lineId], [source]: status }
    };
    runtimesRef.current = nextRuntimes;
    setRuntimes(nextRuntimes);
    const nextPending = { ...pendingQueriesRef.current, [key]: status };
    pendingQueriesRef.current = nextPending;
    setPendingQueries(nextPending);
  }, []);

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
    if (!context) {
      const key = runtimeKey(lineId, source);
      const pending = pendingQueriesRef.current[key];
      const current = runtimesRef.current[lineId]?.[source];
      if (pending?.queryId !== status.queryId && current?.queryId !== status.queryId) {
        return;
      }
    }
    if (["Queued", "Running", "Completed", "Failed", "Cancelled", "Unavailable"].includes(status.state)) {
      setRuntimes((current) => {
        const next = {
          ...current,
          [lineId]: { ...current[lineId], [source]: status }
        };
        runtimesRef.current = next;
        return next;
      });
    }
    if (status.state === "Queued" || status.state === "Running") {
      const nextPending = { ...pendingQueriesRef.current, [runtimeKey(lineId, source)]: status };
      pendingQueriesRef.current = nextPending;
      setPendingQueries(nextPending);
    } else if (status.state === "Completed") {
      clearPendingQuery(lineId, source, status.queryId);
      const isTransaction = sourceTransactionsRef.current[lineId] === source;
      if (isTransaction) {
        const layout = layoutsRef.current[lineId]?.value;
        if (layout) {
          const rebuilt = rebuildLineRows(snapshotRef.current, lineId, layout, status);
          snapshotRef.current = rebuilt;
          setSnapshot(rebuilt);
          const lineErrors = collectLineDwellErrors(rebuilt, lineId, layout, status, directory);
          setInputErrors((current) => {
            const prefix = `${lineId}\u001f`;
            const retained = Object.fromEntries(
              Object.entries(current).filter(([key, value]) => !(key.startsWith(prefix) && value === "dwell"))
            );
            return { ...retained, ...lineErrors };
          });
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
      if (isTransaction || source !== "theory") {
        setLoadError(source === "busHistorical" && status.complete === false
          ? "bus-historical-missing"
          : "");
      }
    } else if (status.state === "Failed" || status.state === "Cancelled" || status.state === "Unavailable") {
      clearPendingQuery(lineId, source, status.queryId);
      const isTransaction = sourceTransactionsRef.current[lineId] === source;
      if (isTransaction) {
        setRuntimeSource(lineId, source);
        const nextTransactions = { ...sourceTransactionsRef.current };
        delete nextTransactions[lineId];
        sourceTransactionsRef.current = nextTransactions;
        setSourceTransactions(nextTransactions);
      }
      if (isTransaction || source !== "theory") {
        setLoadError(status.error || "run-time-query-failed");
      }
      const detail = {
        ...status,
        lineId,
        source,
        requestGeneration: context?.requestGeneration
      };
      if (isTransaction || source !== "theory") {
        reportRunTimeError(operation, detail);
      }
    }
  }, [clearPendingQuery, directory, reportRunTimeError, setRuntimeSource]);

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
        if (status?.state === "Queued" || status?.state === "Running") {
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

  const ensureBusHistoricalRuntime = useCallback((lineId) => {
    if (!isActive || activeTransportMode !== "bus" || !lineId) {
      return Promise.resolve(null);
    }
    setRuntimeSource(lineId, "busHistorical");
    return requestRuntime(lineId, "busHistorical");
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
    if (response.ready) {
      const cached = runtimesRef.current[lineId]?.monitorAverage;
      const unchanged = cached?.state === "Completed"
        && cached.stopSig === response.stopSig
        && cached.sourceRevision === response.revision;
      if (!unchanged) {
        requestRuntime(lineId, "monitorAverage", true).catch(() => {});
      }
    }
    return response;
  }, [api, isActive, requestRuntime, syncMonitorSubscription]);

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

  const ensureTimetableLineLayout = useCallback((lineId) => {
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
    if (existing?.state === "failed") {
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
    const startedAt = diagnosticNow();
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

        layoutTimingRef.current = {
          requestId,
          lineId,
          engineCallMs: diagnosticNow() - startedAt,
          success: !error,
          error
        };
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
        layoutTimingRef.current = {
            requestId,
            lineId,
            engineCallMs: diagnosticNow() - startedAt,
            success: false,
            error: message
        };
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
    const stationsById = new Map(nextDirectory.map((station) => [station.stationId, station]));
    const currentStartStationId = startStationIdRef.current;
    const currentEndStationId = endStationIdRef.current;
    const nextStartStationId = selectableIds.has(currentStartStationId)
      ? currentStartStationId
      : "";
    const startStation = stationsById.get(nextStartStationId);
    const endStation = stationsById.get(currentEndStationId);
    const nextEndStationId = selectableIds.has(currentEndStationId)
      && startStation?.networkId
      && startStation.networkId === endStation?.networkId
      ? currentEndStationId
      : "";
    setDirectory(nextDirectory);
    if (nextStartStationId !== currentStartStationId) {
      startStationIdRef.current = nextStartStationId;
      setStartStationId(nextStartStationId);
    }
    if (nextEndStationId !== currentEndStationId) {
      endStationIdRef.current = nextEndStationId;
      setEndStationId(nextEndStationId);
    }
    setLoadError("");
    if (stationResponse.status === "warming" || stationResponse.status === "stale") {
      indexVersionRef.current = 0;
      setIndexVersion(0);
      setSections([]);
      setSectionId("");
      return false;
    }
    const nextIndexVersion = stationResponse.publishedIndexVersion || 0;
    if (nextStartStationId !== currentStartStationId
      || nextEndStationId !== currentEndStationId
      || nextIndexVersion !== indexVersionRef.current) {
      setSections([]);
      setSectionId("");
    }
    indexVersionRef.current = nextIndexVersion;
    setIndexVersion(nextIndexVersion);
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

  const refreshStationNames = useCallback(() => {
    if (!isActive || activeTransportMode === "bus") {
      return Promise.resolve();
    }
    return loadDirectory(activeTransportMode, directoryGenerationRef.current);
  }, [activeTransportMode, isActive, loadDirectory]);

  const refreshLineNames = useCallback(() => {
    if (!isActive) {
      return Promise.resolve();
    }
    const mode = activeTransportMode;
    if (lineNamesRequestRef.current?.mode === mode) {
      return lineNamesRequestRef.current.promise;
    }
    const request = api.refreshMetadata({ mode, namesOnly: true })
      .then((metadata) => {
        if (!runtimeActiveRef.current || pendingModeRef.current !== mode || metadata?.mode !== mode) {
          return;
        }
        const names = new Map(asArray(metadata.lines)
          .filter((line) => line?.id && line?.name)
          .map((line) => [line.id, line.name]));
        if (names.size === 0) {
          return;
        }
        setSnapshot((current) => {
          if (!isSnapshotForMode(current, mode)) {
            return current;
          }
          let changed = false;
          const nextLines = asArray(current.lines).map((line) => {
            const name = names.get(line?.id);
            if (!name || name === line.name) {
              return line;
            }
            changed = true;
            return { ...line, name };
          });
          return changed ? { ...current, lines: nextLines } : current;
        });
      })
      .catch(() => {})
      .finally(() => {
        if (lineNamesRequestRef.current?.promise === request) {
          lineNamesRequestRef.current = null;
        }
      });
    lineNamesRequestRef.current = { mode, promise: request };
    return request;
  }, [activeTransportMode, api, isActive]);

  const loadBase = useCallback(async (mode, generation, forceRefresh = false) => {
    if (!isCurrentDirectory(mode, generation)) {
      return;
    }
    setLoadError("");
    const currentSnapshot = sharedSnapshotRef.current;
    const snapshotRequest = !forceRefresh && isSnapshotForMode(currentSnapshot, mode)
      ? Promise.resolve(currentSnapshot)
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
  }, [api, isCurrentDirectory, loadDirectory]);

  const reloadBase = useCallback((options = {}) => {
    const forceRefresh = options?.forceRefresh === true;
    resetLineLayouts();
    const generation = directoryGenerationRef.current + 1;
    directoryGenerationRef.current = generation;
    directoryRequestRef.current += 1;
    const mode = activeTransportMode;
    if (directoryRetryRef.current != null) {
      window.clearTimeout(directoryRetryRef.current);
      directoryRetryRef.current = null;
    }
    return loadBase(mode, generation, forceRefresh).catch((error) => {
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
    if (!isActive || activeTransportMode === "bus" || !startStationId || !endStationId || !indexVersion
      || !hasSelectedNetwork) {
      return;
    }
    let cancelled = false;
    const generation = directoryGenerationRef.current;
    const mode = activeTransportMode;
    const isCurrent = () => !cancelled
      && isCurrentDirectory(mode, generation)
      && startStationIdRef.current === startStationId
      && endStationIdRef.current === endStationId
      && indexVersionRef.current === indexVersion;
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
  }, [activeTransportMode, api, endStationId, hasSelectedNetwork, indexVersion, isActive, isCurrentDirectory, loadDirectory, startStationId]);

  useEffect(() => api.onRunTimeQuery((status) => {
    acceptRuntime(status);
  }), [acceptRuntime, api]);

  useEffect(() => api.onRunTimeInvalidated?.((event) => {
    if (event?.editorSessionId !== editorIdRef.current) {
      return;
    }
    if (event?.source === "theory") {
      markRuntimeQueued(event.lineId, event.source);
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
  }), [api, invalidateRuntimeSource, markRuntimeQueued, setRuntimeSource]);

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
      const next = Object.entries(current).reduce((result, [lineId, value]) => {
        if (!invalidIds.has(lineId)) {
          result[lineId] = value;
        } else if (value?.theory) {
          result[lineId] = { theory: value.theory };
        }
        return result;
      }, {});
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
      .filter(([key, query]) => !invalidIds.has(query?.lineId) || key.endsWith("\u001ftheory")));
    pendingQueriesRef.current = nextPending;
    setPendingQueries(nextPending);
    reloadBase();
  }), [api, reloadBase]);

  useEffect(() => api.onCatalogChanged((event) => {
    const mode = event?.mode || activeTransportMode;
    if (mode !== activeTransportMode) {
      return;
    }
    loadedModeRef.current = "";
    if (isActive) {
      reloadBase({ forceRefresh: true }).then(() => {
        loadedModeRef.current = activeTransportMode;
      });
    }
  }), [activeTransportMode, api, isActive, reloadBase]);

  useEffect(() => api.onSnapshotChanged((nextSnapshot) => {
    if (!isSnapshotForMode(nextSnapshot, activeTransportMode)) {
      return;
    }
    if (!isActive) {
      loadedModeRef.current = "";
      return;
    }
    loadedModeRef.current = activeTransportMode;
    setSnapshot(normalizeSnapshot(nextSnapshot));
    if (activeTransportMode !== "bus") {
      loadDirectory(activeTransportMode, directoryGenerationRef.current);
    }
  }), [activeTransportMode, api, isActive, loadDirectory]);

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
    const currentSnapshot = snapshotRef.current;
    const blocks = asArray(currentSnapshot.lineDraftRowsByLineId).map((block) => ({
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
    const nextSnapshot = { ...currentSnapshot, lineDraftRowsByLineId: blocks };
    snapshotRef.current = nextSnapshot;
    setSnapshot(nextSnapshot);
    const lineErrors = collectLineDwellErrors(nextSnapshot, lineId, layout, runtime, directory);
    setInputErrors((current) => {
      const prefix = `${lineId}\u001f`;
      const retained = Object.fromEntries(
        Object.entries(current).filter(([key, value]) => !(key.startsWith(prefix) && value === "dwell"))
      );
      return { ...retained, ...lineErrors };
    });
    setDirtyLineIds((current) => current.includes(lineId) ? current : [...current, lineId]);
    setSaveError("");
    setSaveState("dirty");
  }, [directory, layouts, runtimeSources, runtimes]);

  const clearDeparture = useCallback((lineId, trainId, occurrence) => {
    const currentSnapshot = snapshotRef.current;
    const row = buildRows(currentSnapshot, lineId).find((item) => item?.id === trainId);
    const timedStops = asArray(row?.timedStops);
    if (occurrence <= 0
      || occurrence >= timedStops.length
      || !Number.isFinite(timedStops[occurrence]?.depart)) {
      return false;
    }
    const nextSnapshot = {
      ...currentSnapshot,
      lineDraftRowsByLineId: asArray(currentSnapshot.lineDraftRowsByLineId).map((block) => ({
        ...block,
        lineDraftRows: block.lineId === lineId
          ? asArray(block.lineDraftRows).map((item) => item?.id === trainId
            ? {
                ...item,
                timedStops: asArray(item.timedStops).slice(0, occurrence + 1).map((stop, index) => index === occurrence
                  ? { ...stop, depart: null }
                  : stop)
              }
            : item)
          : asArray(block.lineDraftRows)
      }))
    };
    snapshotRef.current = nextSnapshot;
    setSnapshot(nextSnapshot);
    const layout = layouts[lineId]?.value;
    const runtime = lineRuntime(runtimes, runtimeSources, lineId);
    const lineErrors = collectLineDwellErrors(nextSnapshot, lineId, layout, runtime, directory);
    setInputErrors((current) => {
      const prefix = `${lineId}\u001f`;
      const retained = Object.fromEntries(
        Object.entries(current).filter(([key, value]) => !(key.startsWith(prefix) && value === "dwell"))
      );
      return { ...retained, ...lineErrors };
    });
    setDirtyLineIds((current) => current.includes(lineId) ? current : [...current, lineId]);
    setSaveError("");
    setSaveState("dirty");
    return true;
  }, [directory, layouts, runtimeSources, runtimes]);

  const clearTrainDetails = useCallback((lineId, trainId) => {
    const row = buildRows(snapshot, lineId).find((item) => item?.id === trainId);
    if (asArray(row?.timedStops).length === 0) {
      return false;
    }
    setSnapshot((current) => ({
      ...current,
      lineDraftRowsByLineId: asArray(current.lineDraftRowsByLineId).map((block) => ({
        ...block,
        lineDraftRows: block.lineId === lineId
          ? asArray(block.lineDraftRows).map((item) => item?.id === trainId
            ? { ...item, timedStops: [] }
            : item)
          : asArray(block.lineDraftRows)
      }))
    }));
    setInputErrors((current) => Object.fromEntries(
      Object.entries(current).filter(([key, value]) => !(key.startsWith(`${lineId}\u001f${trainId}\u001f`) && value === "dwell"))
    ));
    setDirtyLineIds((current) => current.includes(lineId) ? current : [...current, lineId]);
    setSaveError("");
    setSaveState("dirty");
    return true;
  }, [snapshot]);

  const clearLineDetails = useCallback((lineId) => {
    const rows = buildRows(snapshot, lineId);
    if (!rows.some((row) => asArray(row?.timedStops).length > 0)) {
      return false;
    }
    setSnapshot((current) => ({
      ...current,
      lineDraftRowsByLineId: asArray(current.lineDraftRowsByLineId).map((block) => ({
        ...block,
        lineDraftRows: block.lineId === lineId
          ? asArray(block.lineDraftRows).map((row) => ({ ...row, timedStops: [] }))
          : asArray(block.lineDraftRows)
      }))
    }));
    setInputErrors((current) => Object.fromEntries(
      Object.entries(current).filter(([key, value]) => !(key.startsWith(`${lineId}\u001f`) && value === "dwell"))
    ));
    setDirtyLineIds((current) => current.includes(lineId) ? current : [...current, lineId]);
    setSaveError("");
    setSaveState("dirty");
    return true;
  }, [snapshot]);

  const markLineCustom = useCallback((lineId, intervalMinutes, trainId = "") => {
    const layout = layouts[lineId]?.value;
    const runtime = lineRuntime(runtimes, runtimeSources, lineId);
    const stopCount = asArray(layout?.stops).filter((stop) => stop?.stopKey).length;
    if (![10, 15, 20, 30].includes(intervalMinutes)) {
      setLoadError("timetable-batch-interval-invalid");
      return false;
    }
    if (stopCount === 0) {
      setLoadError("timetable-line-layout-required");
      return false;
    }
    if (!hasRunTimeSegments(runtime, stopCount)) {
      setLoadError("run-time-query-required");
      return false;
    }
    if (!hasClosingSegment(runtime, stopCount)) {
      setLoadError("run-time-closing-segment-required");
      return false;
    }
    const rows = buildRows(snapshot, lineId);
    const targets = trainId ? rows.filter((row) => row?.id === trainId) : rows;
    if (targets.length === 0) {
      setLoadError("timetable-batch-train-required");
      return false;
    }
    const stationNames = new Map(directory.map((station) => [station.stationId, station.name]));
    const replacements = new Map();
    for (const row of targets) {
      const result = buildBatchTimedStops(row, layout, runtime, stationNames, intervalMinutes);
      if (result.error) {
        setLoadError(`timetable-batch-${result.error}`);
        return false;
      }
      replacements.set(row.id, {
        ...row,
        source: row.source || "manual",
        timedStops: result.timedStops
      });
    }
    setSnapshot((current) => {
      const blocks = asArray(current.lineDraftRowsByLineId).map((block) => ({
        ...block,
        lineDraftRows: block.lineId === lineId
          ? asArray(block.lineDraftRows).map((row) => replacements.get(row.id) || row)
          : asArray(block.lineDraftRows)
      }));
      return { ...current, lineDraftRowsByLineId: blocks };
    });
    setDirtyLineIds((current) => current.includes(lineId) ? current : [...current, lineId]);
    setLoadError("");
    setSaveError("");
    setSaveState("dirty");
    return true;
  }, [directory, layouts, runtimeSources, runtimes, snapshot]);

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
    endStations,
    lineDirectory,
    lineLayouts: layouts,
    layoutRevision,
    lines,
    sections,
    sectionId,
    selectedSection,
    setSectionId,
    startStationId,
    selectStartStation,
    endStationId,
    selectEndStation,
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
    refreshLineNames,
    refreshStationNames,
    loadActualTrips,
    ensureBusHistoricalRuntime,
    ensureTimetableLineLayout,
    layoutTimingRef,
    updateDeparture,
    clearDeparture,
    clearTrainDetails,
    clearLineDetails,
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
