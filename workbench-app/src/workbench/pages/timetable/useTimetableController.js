import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { minutesToTime, timeToMinutes } from "./timetable-data";

const EMPTY_SNAPSHOT = {
  lines: [],
  stations: [],
  lineDraftRowsByLineId: [],
  appliedRows: []
};

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
  const stops = displayKeys.map((stopKey, index) => {
    const storedStop = stored[index];
    const layoutStop = index === stopKeys.length ? layoutStops[0] : layoutStops[index];
    const segmentMinutes = segments[index - 1]?.segmentMinutes;
    const arrival = index === 0
      ? null
      : previousDeparture != null && Number.isFinite(segmentMinutes)
        ? previousDeparture + segmentMinutes
        : storedStop?.arrive ?? null;
    const departure = index === displayKeys.length - 1
      ? null
      : index === 0
        ? slotMinute
        : storedStop?.depart ?? null;
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
    source: row?.source || runtime?.source || "historical",
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

function runtimeKey(lineId, source) {
  return `${lineId || ""}\u001f${source || "historical"}`;
}

function lineRuntime(runtimes, sources, lineId) {
  return runtimes[lineId]?.[sources[lineId] || "historical"] || null;
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
        plannedTrains: rows.map((row) => buildTrain(row, layout, null, stationNames))
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
  const autoHistoricalLineRef = useRef("");
  const autoHistoricalAttemptRef = useRef(new Set());
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
  const [layouts, setLayouts] = useState({});
  const [pendingQueries, setPendingQueries] = useState({});
  const [dirtyLineIds, setDirtyLineIds] = useState([]);
  const [loadError, setLoadError] = useState("");
  const [saveState, setSaveState] = useState("clean");
  const [saveError, setSaveError] = useState("");

  const selectedSection = sections.find((section) => section.sectionId === sectionId) || sections[0] || null;
  const lines = useMemo(
    () => buildLines(snapshot, selectedSection, directory, runtimes, runtimeSources, layouts, layoutTimingRef.current),
    [directory, layouts, runtimes, runtimeSources, selectedSection, snapshot]
  );

  const setLineLayout = useCallback((lineId, layout) => {
    const next = { ...layoutsRef.current, [lineId]: layout };
    layoutsRef.current = next;
    setLayouts(next);
  }, []);

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
    if (!lineId || (source !== "historical" && source !== "theory")) {
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
    if (!lineId || (source !== "historical" && source !== "theory")) {
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
    const source = status.source || context?.source || "historical";
    if (status.state === "Completed") {
      setRuntimes((current) => {
        const next = {
          ...current,
          [lineId]: { ...current[lineId], [source]: status }
        };
        runtimesRef.current = next;
        return next;
      });
      clearPendingQuery(lineId, source, status.queryId);
      setLoadError("");
    } else if (status.state === "Failed" || status.state === "Cancelled") {
      clearPendingQuery(lineId, source, status.queryId);
      setLoadError(status.error || "run-time-query-failed");
      const detail = {
        ...status,
        lineId,
        source,
        requestGeneration: context?.requestGeneration
      };
      reportRunTimeError(operation, detail);
    }
  }, [clearPendingQuery, reportRunTimeError]);

  const requestRuntime = useCallback((lineId, source = "historical", automatic = false, refresh = false) => {
    if (!lineId) {
      return Promise.resolve(null);
    }
    const key = runtimeKey(lineId, source);
    const ready = runtimesRef.current[lineId]?.[source];
    if (!refresh && ready) {
      return Promise.resolve(ready);
    }
    const pending = pendingQueriesRef.current[key];
    if (!refresh && pending) {
      return Promise.resolve(pending);
    }
    const activeRequest = runtimeRequestRef.current.get(key);
    if (!refresh && activeRequest) {
      return activeRequest.promise;
    }

    const request = {
      editorSessionId: editorIdRef.current,
      lineId,
      source
    };
    const requestGeneration = (runtimeRequestGenerationRef.current.get(key) || 0) + 1;
    const mode = activeTransportMode;
    runtimeRequestGenerationRef.current.set(key, requestGeneration);
    if (refresh) {
      clearRuntimeSource(lineId, source);
    }
    clearPendingQuery(lineId, source);
    const isCurrent = () => runtimeRequestGenerationRef.current.get(key) === requestGeneration
      && pendingModeRef.current === mode;
    const start = async () => {
      if (!runtimeActiveRef.current || !isCurrent()) {
        return null;
      }
      if (automatic && (autoHistoricalLineRef.current !== lineId
        || runtimesRef.current[lineId]?.historical)) {
        return null;
      }
      const queuedReady = runtimesRef.current[lineId]?.[source];
      if (!refresh && queuedReady) {
        return queuedReady;
      }
      const queuedPending = pendingQueriesRef.current[key];
      if (!refresh && queuedPending) {
        return queuedPending;
      }

      try {
        const status = await api.startRunTimeQuery(request);
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
      automatic,
      generation: requestGeneration,
      promise
    });
    return promise;
  }, [acceptRuntime, activeTransportMode, api, clearPendingQuery, clearRuntimeSource, reportRunTimeError]);

  const ensureHistoricalRuntime = useCallback((lineId, forceRetry = false) => {
    autoHistoricalLineRef.current = lineId || "";
    if (!isActive || !lineId) {
      return Promise.resolve(null);
    }
    if (forceRetry) {
      autoHistoricalAttemptRef.current.delete(lineId);
    }
    if (runtimesRef.current[lineId]?.historical) {
      return Promise.resolve(runtimesRef.current[lineId].historical);
    }
    const activeRequest = runtimeRequestRef.current.get(runtimeKey(lineId, "historical"));
    if (activeRequest) {
      return activeRequest.promise;
    }
    if (autoHistoricalAttemptRef.current.has(lineId)) {
      return Promise.resolve(null);
    }
    autoHistoricalAttemptRef.current.add(lineId);
    return requestRuntime(lineId, "historical", true);
  }, [isActive, requestRuntime]);

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
    const firstStationId = selectable[0]?.stationId || "";
    const lastStationId = selectable[selectable.length - 1]?.stationId || "";
    setDirectory(nextDirectory);
    setStartStationId((current) => selectableIds.has(current) ? current : firstStationId);
    setEndStationId((current) => selectableIds.has(current) && current !== firstStationId
      ? current
      : lastStationId === firstStationId ? "" : lastStationId);
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
    autoHistoricalAttemptRef.current.clear();
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
    if (!isActive || !startStationId || !endStationId || !indexVersion) {
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
  }), [api, invalidateRuntimeSource]);

  useEffect(() => {
    runtimeActiveRef.current = isActive;
    if (pendingModeRef.current === activeTransportMode) {
      return;
    }
    pendingModeRef.current = activeTransportMode;
    runtimeRequestRef.current.clear();
    autoHistoricalLineRef.current = "";
    autoHistoricalAttemptRef.current.clear();
    pendingQueriesRef.current = {};
    setPendingQueries({});
    runtimesRef.current = {};
    setRuntimes({});
    runtimeSourcesRef.current = {};
    setRuntimeSources({});
  }, [activeTransportMode, isActive]);

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
    const nextPending = Object.fromEntries(Object.entries(pendingQueriesRef.current)
      .filter(([, query]) => !invalidIds.has(query?.lineId)));
    pendingQueriesRef.current = nextPending;
    setPendingQueries(nextPending);
    reloadBase();
  }), [api, reloadBase]);

  useEffect(() => () => {
    runtimeActiveRef.current = false;
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
            source: runtime?.source || row.source || "historical",
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
            source: runtime?.source || row.source || "historical",
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
    if (dirtyLineIds.length === 0 || saveState === "saving") {
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
            source: runtime?.source || row.source || "historical",
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
  }, [api, dirtyLineIds, layouts, runtimeSources, runtimes, saveState, snapshot]);

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
    pendingQueries,
    requestRuntime,
    setRuntimeSource,
    ensureHistoricalRuntime,
    ensureTimetableLineLayout,
    layoutTimingRef,
    updateDeparture,
    markLineCustom,
    dirty: dirtyLineIds.length > 0,
    saveState,
    saveError,
    saveAll,
    loadError,
    reload: reloadBase
  };
}
