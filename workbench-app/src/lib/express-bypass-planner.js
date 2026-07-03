import { minutesToTime, timeToMinutes } from "./time.js";

const DEFAULT_OBJECTIVE = "balanced";
const DEFAULT_OFFSET_STEP_MINUTES = 2;
const DEFAULT_MAX_OFFSET_MINUTES = 10;
const DEFAULT_MIN_DEPARTURE_GAP_MINUTES = 5;
const DEFAULT_LOCAL_RETIME_STEP_MINUTES = 2;
const DEFAULT_REGION_LINK_PADDING_MINUTES = 12;
const DEFAULT_MAX_REGION_SPAN_MINUTES = 90;
const DEFAULT_MAX_REBUILD_WINDOW_MINUTES = 36;
const DEFAULT_MAX_REBUILD_TRIPS = 6;
const DEFAULT_MAX_SHIFT_WINDOW_MINUTES = 24;
const DEFAULT_MAX_SHIFT_TRIPS = 5;
const DEFAULT_MAX_COUPLED_REBUILD_TRIPS = 8;
const DEFAULT_SCHEDULE_BEAM_WIDTH = 2;
const DEFAULT_SCHEDULE_SEARCH_ITERATIONS = 2;
const DEFAULT_MAX_SCHEDULE_ACTIONS = 6;
const DEFAULT_MAX_REGION_RETIME_ACTIONS = 3;
const DEFAULT_STOP_START_LOSS_MINUTES_PER_SKIPPED_STOP = 3;
const DEFAULT_OBSERVED_STATION_RUNTIME_MIN_SAMPLES = 2;
const DEFAULT_MAX_OBSERVED_STATION_RUNTIME_MINUTES = 180;
const DEFAULT_OBSERVED_STATION_RUNTIME_MAX_PROFILE_RATIO = 1.8;
const DEFAULT_OBSERVED_STATION_RUNTIME_MAX_PROFILE_EXTRA_MINUTES = 12;
const DEFAULT_PURSUIT_TRUNK_MERGE_GAP_ATOMS = 12;
const DEFAULT_PURSUIT_CURVE_SAMPLE_STEP_MINUTES = 2;
const PREPARED_CONTEXT_KIND = "express-bypass-planner-context-v1";
const pursuitTrunkCorridorCache = new WeakMap();

const OBJECTIVE_WEIGHTS = {
  balanced: {
    expressBenefit: 1.0,
    resolvedHoldCost: 0.7,
    unresolvedRisk: 1.4,
    robustnessRisk: 0.9,
    activeBypassCost: 0.2,
    departureConflict: 1.1,
    retimedTripCost: 2.0,
    retimedMinuteCost: 0.45,
    rebuildSpanCost: 0.12
  },
  fastestExpress: {
    expressBenefit: 1.4,
    resolvedHoldCost: 0.45,
    unresolvedRisk: 1.2,
    robustnessRisk: 0.65,
    activeBypassCost: 0.05,
    departureConflict: 1.0,
    retimedTripCost: 1.2,
    retimedMinuteCost: 0.25,
    rebuildSpanCost: 0.06
  },
  minBypassStations: {
    expressBenefit: 0.9,
    resolvedHoldCost: 0.9,
    unresolvedRisk: 1.5,
    robustnessRisk: 1.0,
    activeBypassCost: 0.8,
    departureConflict: 1.1,
    retimedTripCost: 1.8,
    retimedMinuteCost: 0.4,
    rebuildSpanCost: 0.1
  },
  maxSystemEfficiency: {
    expressBenefit: 1.1,
    resolvedHoldCost: 1.0,
    unresolvedRisk: 1.5,
    robustnessRisk: 1.1,
    activeBypassCost: 0.35,
    departureConflict: 1.2,
    retimedTripCost: 2.2,
    retimedMinuteCost: 0.5,
    rebuildSpanCost: 0.14
  }
};

function clampNumber(value, fallbackValue = 0) {
  const numberValue = Number(value);
  return Number.isFinite(numberValue) ? numberValue : fallbackValue;
}

function resolveStopStartLossMinutesPerSkippedStop(options = {}) {
  return Math.max(
    0,
    clampNumber(
      options.stopStartLossMinutesPerSkippedStop,
      DEFAULT_STOP_START_LOSS_MINUTES_PER_SKIPPED_STOP
    )
  );
}

function resolveObservedStationRuntimeMinSamples(options = {}) {
  return Math.max(
    1,
    Math.round(clampNumber(
      options.observedStationRuntimeMinSamples,
      DEFAULT_OBSERVED_STATION_RUNTIME_MIN_SAMPLES
    ))
  );
}

function resolveObservedStationRuntimeMaxProfileRatio(options = {}) {
  return Math.max(
    1,
    clampNumber(
      options.observedStationRuntimeMaxProfileRatio,
      DEFAULT_OBSERVED_STATION_RUNTIME_MAX_PROFILE_RATIO
    )
  );
}

function resolveObservedStationRuntimeMaxProfileExtraMinutes(options = {}) {
  return Math.max(
    0,
    clampNumber(
      options.observedStationRuntimeMaxProfileExtraMinutes,
      DEFAULT_OBSERVED_STATION_RUNTIME_MAX_PROFILE_EXTRA_MINUTES
    )
  );
}

function computeForwardMinuteDelta(startTime, endTime) {
  const startMinute = timeToMinutes(startTime || "");
  const endMinute = timeToMinutes(endTime || "");
  if (startMinute === null || endMinute === null) {
    return null;
  }

  let delta = endMinute - startMinute;
  if (delta < -720) {
    delta += 1440;
  }
  return delta >= 0 ? delta : null;
}

function summarizeRuntimeSamples(samples) {
  const ordered = asArray(samples)
    .filter((value) => Number.isFinite(value) && value >= 0)
    .sort((left, right) => left - right);
  if (ordered.length === 0) {
    return null;
  }

  const lowerIndex = Math.floor((ordered.length - 1) * 0.25);
  const median = ordered[Math.floor(ordered.length / 2)];
  const upperIndex = Math.floor((ordered.length - 1) * 0.75);
  const fastMinutes = ordered[lowerIndex];
  const upperMinutes = ordered[upperIndex];
  const average = ordered.reduce((sum, value) => sum + value, 0) / ordered.length;
  const confidence = Math.min(0.9, 0.48 + ordered.length * 0.06);
  const coreSpreadMinutes = Math.max(0, upperMinutes - fastMinutes);
  const medianGapMinutes = Math.max(0, median - fastMinutes);
  const variabilityMinutes = Math.max(
    1,
    Math.min(fastMinutes * 0.3, (coreSpreadMinutes * 0.5) + (medianGapMinutes * 0.25))
  );
  return {
    minutes: Number(fastMinutes.toFixed(2)),
    medianMinutes: Number(median.toFixed(2)),
    sampleCount: ordered.length,
    minMinutes: Number(ordered[0].toFixed(2)),
    maxMinutes: Number(ordered[ordered.length - 1].toFixed(2)),
    averageMinutes: Number(average.toFixed(2)),
    confidence: Number(confidence.toFixed(2)),
    variabilityMinutes: Number(variabilityMinutes.toFixed(2)),
    baselinePolicy: "fastObservedQuartile",
    source: "tripObserved"
  };
}

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function dedupeLineIds(values) {
  return [...new Set(asArray(values).filter(Boolean))];
}

function toLineStationKey(lineId, order) {
  return `${lineId || ""}:${order}`;
}

function toStationRuntimeKey(lineId, fromStationId, toStationId) {
  return `${lineId || ""}|${fromStationId || ""}->${toStationId || ""}`;
}

function toOrderedLinePairKey(lineId, otherLineId) {
  return `${lineId || ""}|${otherLineId || ""}`;
}

function toProtectedIntervalKey(interval) {
  if (!interval) {
    return "";
  }

  const fromBuilding = Number.isFinite(interval.fromBuildingEntityIndex)
    ? interval.fromBuildingEntityIndex
    : -1;
  const toBuilding = Number.isFinite(interval.toBuildingEntityIndex)
    ? interval.toBuildingEntityIndex
    : -1;
  if (fromBuilding >= 0 && toBuilding >= 0) {
    return `${fromBuilding}->${toBuilding}`;
  }

  return `${interval.fromStationId || ""}->${interval.toStationId || ""}`;
}

function isMinuteInsideWindow(minute, windowStartMinute, windowEndMinute) {
  if (!Number.isFinite(minute)) {
    return false;
  }
  if (!Number.isFinite(windowStartMinute) || !Number.isFinite(windowEndMinute)) {
    return true;
  }

  return minute >= windowStartMinute && minute < windowEndMinute;
}

function quantizeOffsetVariants(stepMinutes, maxOffsetMinutes) {
  const step = Math.max(1, Math.round(stepMinutes || DEFAULT_OFFSET_STEP_MINUTES));
  const maxOffset = Math.max(0, Math.round(maxOffsetMinutes || DEFAULT_MAX_OFFSET_MINUTES));
  const result = [];
  for (let delta = -maxOffset; delta <= maxOffset; delta += step) {
    result.push(delta);
  }
  if (!result.includes(0)) {
    result.push(0);
    result.sort((left, right) => left - right);
  }
  return result;
}

function sortRowsByMinute(rows) {
  return [...rows].sort((left, right) => {
    const leftMinute = clampNumber(left.minute, timeToMinutes(left.time) ?? 0);
    const rightMinute = clampNumber(right.minute, timeToMinutes(right.time) ?? 0);
    if (leftMinute !== rightMinute) {
      return leftMinute - rightMinute;
    }
    if (left.lineId !== right.lineId) {
      return left.lineId.localeCompare(right.lineId);
    }
    return String(left.id || "").localeCompare(String(right.id || ""));
  });
}

function rowsToStagedRows(rows) {
  return sortRowsByMinute(rows).map((row) => ({
    id: row.id,
    lineId: row.lineId,
    time: minutesToTime(clampNumber(row.minute, timeToMinutes(row.time) ?? 0)),
    kind: row.kind === "express" ? "express" : "local",
    source: row.source || "planner",
    note: row.note || ""
  }));
}

function quantizeMinuteToStep(minute, stepMinutes = DEFAULT_LOCAL_RETIME_STEP_MINUTES) {
  const step = Math.max(1, Math.round(stepMinutes || 1));
  return Math.round(clampNumber(minute, 0) / step) * step;
}

function circularMinuteGap(leftMinute, rightMinute) {
  const dayMinutes = 24 * 60;
  const left = ((Math.round(leftMinute) % dayMinutes) + dayMinutes) % dayMinutes;
  const right = ((Math.round(rightMinute) % dayMinutes) + dayMinutes) % dayMinutes;
  const directGap = Math.abs(right - left);
  return Math.min(directGap, dayMinutes - directGap);
}

function hasSameOriginDepartureGap(rows, minGapMinutes = DEFAULT_MIN_DEPARTURE_GAP_MINUTES) {
  if (!Array.isArray(rows) || rows.length < 2) {
    return true;
  }

  const ordered = [...rows].sort((left, right) => left.minute - right.minute);
  for (let index = 1; index < ordered.length; index += 1) {
    if (ordered[index].minute - ordered[index - 1].minute < minGapMinutes) {
      return false;
    }
  }

  return circularMinuteGap(ordered[0].minute, ordered[ordered.length - 1].minute) >= minGapMinutes;
}

function enumerateSymmetricStepDeltas(maxMinutes, stepMinutes = DEFAULT_LOCAL_RETIME_STEP_MINUTES) {
  const deltas = [];
  const step = Math.max(1, Math.round(stepMinutes || 1));
  const maxShiftMinutes = Math.max(0, clampNumber(maxMinutes, 0));
  for (let delta = step; delta <= maxShiftMinutes; delta += step) {
    deltas.push(-delta, delta);
  }
  return deltas;
}

function estimateVariabilityMinutes(baseMinutes, confidence, sampleCount = 0, fastMinutes = 0) {
  const safeBaseMinutes = Math.max(0, clampNumber(baseMinutes, 0));
  if (!(safeBaseMinutes > 0)) {
    return 0;
  }

  let variabilityMinutes = 0;
  if (fastMinutes > 0 && safeBaseMinutes > 0) {
    variabilityMinutes = Math.max(variabilityMinutes, Math.abs(safeBaseMinutes - fastMinutes));
  }

  const confidenceGap = Math.max(0, 1 - clampNumber(confidence, 0.2));
  const samplePenalty = sampleCount > 0
    ? Math.min(1, 3 / Math.max(1, sampleCount))
    : 1;
  variabilityMinutes = Math.max(
    variabilityMinutes,
    safeBaseMinutes * (0.12 + (confidenceGap * 0.28 * samplePenalty))
  );
  return Number(variabilityMinutes.toFixed(4));
}

function buildScenarioRowsFromWorkingRows(rows) {
  return sortRowsByMinute(rows).map((row) => ({
    id: row.id,
    lineId: row.lineId,
    time: minutesToTime(clampNumber(row.minute, timeToMinutes(row.time) ?? 0)),
    minute: clampNumber(row.minute, timeToMinutes(row.time) ?? 0),
    kind: row.kind === "express" ? "express" : "local",
    source: row.source || "planner",
    note: row.note || ""
  }));
}

function normalizeManualRow(row = {}) {
  return {
    id: row.id || "",
    lineId: row.lineId || "",
    time: row.time || "",
    kind: row.kind === "express" ? "express" : "local",
    offsetMode: row.offsetMode || "none",
    offsetMinutes: row.offsetMinutes ?? ""
  };
}

function normalizeAutoRule(rule = {}) {
  return {
    id: rule.id || "",
    lineId: rule.lineId || "",
    enabled: rule.enabled === true,
    start: rule.start || "",
    end: rule.end || "",
    kind: rule.kind === "express" ? "express" : rule.kind === "local" ? "local" : "",
    departuresPerHour: clampNumber(rule.departuresPerHour, 0),
    localPerHour: clampNumber(rule.localPerHour, 0),
    expressPerHour: clampNumber(rule.expressPerHour, 0),
    expressOffsetMode: rule.expressOffsetMode === "before" ? "before" : "after",
    expressOffsetMinutes: clampNumber(rule.expressOffsetMinutes, 0)
  };
}

function normalizeTripStop(stop = {}) {
  return {
    stationId: stop.stationId || "",
    time: stop.time || "",
    arrivalTime: stop.arrivalTime || "",
    departureTime: stop.departureTime || "",
    stopType: stop.stopType || "normal",
    waitMinutes: stop.waitMinutes ?? null
  };
}

function normalizeTrip(trip = {}) {
  return {
    id: trip.id || "",
    lineId: trip.lineId || "",
    kind: trip.kind === "express" ? "express" : "local",
    depart: trip.depart || "",
    realtimeSegment: clampNumber(trip.realtimeSegment, 0),
    realtimeProgress: clampNumber(trip.realtimeProgress, 0),
    realtimeFromStationId: trip.realtimeFromStationId || "",
    realtimeToStationId: trip.realtimeToStationId || "",
    realtimeTime: trip.realtimeTime || "",
    stops: asArray(trip.stops)
      .map(normalizeTripStop)
      .filter((stop) => stop.stationId)
  };
}

function buildObservedStationRuntimeByLinePair(drafts) {
  const samplesByPair = new Map();
  const seenTrips = new Set();

  asArray(drafts).forEach((draft) => {
    asArray(draft.trips).forEach((trip) => {
      const stopSignature = asArray(trip.stops)
        .map((stop) => `${stop.stationId}|${stop.arrivalTime || ""}|${stop.departureTime || ""}`)
        .join(";");
      const tripSignature = `${trip.lineId}|${trip.id}|${stopSignature}`;
      if (seenTrips.has(tripSignature)) {
        return;
      }
      seenTrips.add(tripSignature);

      const stops = asArray(trip.stops);
      for (let index = 0; index < stops.length - 1; index += 1) {
        const fromStop = stops[index];
        const toStop = stops[index + 1];
        if (!fromStop.stationId
          || !toStop.stationId
          || fromStop.stationId === toStop.stationId
          || !fromStop.departureTime
          || !toStop.arrivalTime) {
          continue;
        }

        const runtimeMinutes = computeForwardMinuteDelta(fromStop.departureTime, toStop.arrivalTime);
        if (runtimeMinutes === null
          || runtimeMinutes <= 0
          || runtimeMinutes > DEFAULT_MAX_OBSERVED_STATION_RUNTIME_MINUTES) {
          continue;
        }

        const key = toStationRuntimeKey(trip.lineId, fromStop.stationId, toStop.stationId);
        if (!samplesByPair.has(key)) {
          samplesByPair.set(key, []);
        }
        samplesByPair.get(key).push(runtimeMinutes);
      }
    });
  });

  const runtimeByPair = new Map();
  samplesByPair.forEach((samples, key) => {
    const summary = summarizeRuntimeSamples(samples);
    if (summary) {
      runtimeByPair.set(key, summary);
    }
  });

  return runtimeByPair;
}

function buildGeneratedRowsFromDraftIntent(draft = {}) {
  const generatedRows = [];

  asArray(draft.manualRows).forEach((row) => {
    if (!row.id || !row.lineId || !row.time) {
      return;
    }
    generatedRows.push({
      id: row.id,
      lineId: row.lineId,
      time: row.time,
      kind: row.kind === "express" ? "express" : "local",
      source: "manual",
      note: ""
    });
  });

  asArray(draft.autoRules).forEach((rule) => {
    if (!rule.enabled) {
      return;
    }

    const startMinutes = timeToMinutes(rule.start);
    const endMinutes = timeToMinutes(rule.end);
    if (startMinutes === null || endMinutes === null || endMinutes <= startMinutes) {
      return;
    }

    const ruleKind = rule.kind || (rule.expressPerHour > 0 && rule.localPerHour <= 0 ? "express" : "local");
    const departuresPerHour = rule.departuresPerHour > 0
      ? rule.departuresPerHour
      : ruleKind === "express"
        ? rule.expressPerHour
        : rule.localPerHour;
    if (!(departuresPerHour > 0)) {
      return;
    }

    const intervalMinutes = 60 / departuresPerHour;
    for (let minute = startMinutes; minute < endMinutes; minute += intervalMinutes) {
      if (ruleKind === "local") {
        generatedRows.push({
          id: `auto-local-${rule.id}-${minute.toFixed(0)}`,
          lineId: rule.lineId,
          time: minutesToTime(Math.round(minute)),
          kind: "local",
          source: "autoRule",
          note: rule.id ? `rule:${rule.id}` : ""
        });
        continue;
      }

      if (ruleKind === "express") {
        const adjustedMinute = Math.round(minute)
          + (rule.expressOffsetMode === "before" ? -rule.expressOffsetMinutes : rule.expressOffsetMinutes);
        generatedRows.push({
          id: `auto-express-${rule.id}-${minute.toFixed(0)}`,
          lineId: rule.lineId,
          time: minutesToTime(adjustedMinute),
          kind: "express",
          source: "autoRule",
          note: rule.id ? `rule:${rule.id}` : ""
        });
      }
    }
  });

  return sortRowsByMinute(generatedRows);
}

function deriveExpressStopStationIdsFromTrips(draft, scenario) {
  const expressLineIdSet = new Set(asArray(scenario?.expressLineIds).filter(Boolean));
  if (expressLineIdSet.size === 0) {
    return [];
  }

  const stopStationIds = new Set();
  asArray(draft?.trips).forEach((trip) => {
    if (trip.kind !== "express" || !expressLineIdSet.has(trip.lineId)) {
      return;
    }
    asArray(trip.stops).forEach((stop) => {
      if (!stop.stationId || stop.stopType === "pass") {
        return;
      }
      stopStationIds.add(stop.stationId);
    });
  });

  return [...stopStationIds];
}

function normalizeJointPlannerRequest(options = {}, draft = null, scenario = null) {
  const expressTripsPerHourValue = options.expressTripsPerHour;
  const expressTripsPerHourCandidates = [...new Set(
    asArray(options.expressTripsPerHourCandidates)
      .map((value) => clampNumber(value, null))
      .filter((value) => value !== null && value > 0)
  )].sort((left, right) => left - right);
  const expressOffsetCandidates = [...new Set(
    asArray(options.expressOffsetCandidates)
      .map((value) => clampNumber(value, null))
      .filter((value) => value !== null)
      .map((value) => quantizeMinuteToStep(value, DEFAULT_OFFSET_STEP_MINUTES))
  )].sort((left, right) => left - right);
  const explicitExpressStopStationIds = asArray(options.expressStopStationIds).filter(Boolean);
  const derivedExpressStopStationIds = explicitExpressStopStationIds.length > 0
    ? []
    : deriveExpressStopStationIdsFromTrips(draft, scenario);
  const expressStopStationIds = explicitExpressStopStationIds.length > 0
    ? explicitExpressStopStationIds
    : derivedExpressStopStationIds;
  const adjustableLineIds = dedupeLineIds(
    asArray(options.adjustableLineIds).filter(Boolean).length > 0
      ? asArray(options.adjustableLineIds)
      : asArray(scenario?.adjustableLineIds)
  );
  return {
    objective: options.objective || DEFAULT_OBJECTIVE,
    expressTripsPerHour: expressTripsPerHourValue === undefined || expressTripsPerHourValue === null
      ? null
      : clampNumber(expressTripsPerHourValue, null),
    expressTripsPerHourCandidates,
    expressWindowStart: options.expressWindowStart || options.windowStart || "00:00",
    expressWindowEnd: options.expressWindowEnd || options.windowEnd || "23:59",
    expressOffsetMinutes: options.expressOffsetMinutes === undefined || options.expressOffsetMinutes === null
      ? null
      : clampNumber(options.expressOffsetMinutes, 0),
    expressOffsetCandidates,
    maxAdditionalBypassStations: clampNumber(options.maxAdditionalBypassStations, 0),
    forcedBypassStationId: options.forcedBypassStationId || "",
    maxLocalHoldMinutes: options.maxLocalHoldMinutes === undefined || options.maxLocalHoldMinutes === null
      ? null
      : Math.max(0, clampNumber(options.maxLocalHoldMinutes, 0)),
    maxLocalRetimeMinutes: Math.max(0, clampNumber(options.maxLocalRetimeMinutes, 0)),
    adjustableLineIds,
    adjustableLineIdSet: new Set(adjustableLineIds),
    lockedLocalTripIds: new Set(asArray(options.lockedLocalTripIds).filter(Boolean)),
    lockedExpressTripIds: new Set(asArray(options.lockedExpressTripIds).filter(Boolean)),
    stopStartLossMinutesPerSkippedStop: resolveStopStartLossMinutesPerSkippedStop(options),
    scheduleBeamWidth: Math.max(1, Math.min(8, Math.round(clampNumber(options.scheduleBeamWidth, DEFAULT_SCHEDULE_BEAM_WIDTH)))),
    scheduleSearchIterations: Math.max(1, Math.min(5, Math.round(clampNumber(options.scheduleSearchIterations, DEFAULT_SCHEDULE_SEARCH_ITERATIONS)))),
    maxScheduleActions: Math.max(4, Math.min(48, Math.round(clampNumber(options.maxScheduleActions, DEFAULT_MAX_SCHEDULE_ACTIONS)))),
    turnbackStationId: options.turnbackStationId || draft?.mergedView?.turnbackStationId || "",
    expressStopStationIds,
    expressStopStationIdSet: new Set(expressStopStationIds),
    stopPatternSpecified: expressStopStationIds.length > 0,
    stopPatternSource: explicitExpressStopStationIds.length > 0
      ? "options"
      : derivedExpressStopStationIds.length > 0
        ? "draftTrips"
        : "none"
  };
}

function normalizeLine(line = {}) {
  return {
    id: line.id || "",
    name: line.name || line.id || "",
    kind: line.kind === "express" ? "express" : "local",
    configuredKind: line.configuredKind === "express" ? "express" : "local",
    transportType: line.transportType || "",
    routeNumber: clampNumber(line.routeNumber, -1),
    stationCount: clampNumber(line.stationCount, 0),
    color: line.color || "",
    hasTimeProfile: line.hasTimeProfile === true,
    estimatedLoopMinutes: clampNumber(line.estimatedLoopMinutes, 0),
    originStationId: line.originStationId || "",
    originStationName: line.originStationName || "",
    originHoldLimitMinutes: clampNumber(line.originHoldLimitMinutes, 20),
    maxStationDwellMinutes: clampNumber(line.maxStationDwellMinutes, 10),
    allowedDepotId: line.allowedDepotId || ""
  };
}

function normalizeStation(station = {}) {
  return {
    ...station,
    id: station.id || "",
    lineId: station.lineId || "",
    name: station.name || station.id || "",
    order: clampNumber(station.order, 0),
    waypointIndex: clampNumber(station.waypointIndex, -1),
    trackAtomIndex: clampNumber(station.trackAtomIndex, -1),
    canConfigureBypass: station.canConfigureBypass === true,
    isConfiguredBypass: station.isConfiguredBypass === true,
    profileDwellMinutes: clampNumber(station.profileDwellMinutes, 0),
    observedDwellMinutes: clampNumber(station.observedDwellMinutes, 0),
    observedDwellSampleCount: clampNumber(station.observedDwellSampleCount, 0),
    confidence: clampNumber(station.confidence, 0.3)
  };
}

function normalizeSegment(segment = {}) {
  return {
    ...segment,
    id: segment.id || "",
    lineId: segment.lineId || "",
    fromStationId: segment.fromStationId || "",
    toStationId: segment.toStationId || "",
    fromOrder: clampNumber(segment.fromOrder, 0),
    toOrder: clampNumber(segment.toOrder, 0),
    distanceMeters: clampNumber(segment.distanceMeters, 0),
    profileMinutes: clampNumber(segment.profileMinutes, 0),
    estimatedMinutes: clampNumber(segment.estimatedMinutes, 0),
    confidence: clampNumber(segment.confidence, 0.2)
  };
}

function normalizeBypassStation(station = {}) {
  return {
    ...station,
    stationId: station.stationId || "",
    lineId: station.lineId || "",
    name: station.name || station.stationId || "",
    order: clampNumber(station.order, 0),
    isConfigured: station.isConfigured === true,
    isVirtualCandidate: station.isVirtualCandidate === true
  };
}

function normalizeObservationStop(stop = {}) {
  return {
    ...stop,
    stationId: stop.stationId || "",
    lineId: stop.lineId || "",
    waypointIndex: clampNumber(stop.waypointIndex, -1),
    averageMinutes: clampNumber(stop.averageMinutes, 0),
    sampleCount: clampNumber(stop.sampleCount, 0),
    confidence: clampNumber(stop.confidence, 0.2)
  };
}

function normalizeTraversalSlice(slice = {}) {
  return {
    ...slice,
    id: slice.id || "",
    lineId: slice.lineId || "",
    sliceIndex: clampNumber(slice.sliceIndex, -1),
    startAtomIndex: clampNumber(slice.startAtomIndex, -1),
    endAtomIndexExclusive: clampNumber(slice.endAtomIndexExclusive, -1),
    startEventKind: slice.startEventKind || "unknown",
    endEventKind: slice.endEventKind || "unknown",
    startWaypointIndex: clampNumber(slice.startWaypointIndex, -1),
    endWaypointIndex: clampNumber(slice.endWaypointIndex, -1),
    stationTraversalKind: slice.stationTraversalKind || "none",
    stationWaypointIndex: clampNumber(slice.stationWaypointIndex, -1),
    stationStopMinutes: clampNumber(slice.stationStopMinutes, 0),
    observedIncludesStationStop: slice.observedIncludesStationStop === true,
    modelRunMinutes: clampNumber(slice.modelRunMinutes, 0),
    observedAverageMinutes: clampNumber(slice.observedAverageMinutes, 0),
    observedFastMinutes: clampNumber(slice.observedFastMinutes, 0),
    observedSampleCount: clampNumber(slice.observedSampleCount, 0),
    confidence: clampNumber(slice.confidence, 0.2),
    source: slice.source || "model"
  };
}

function normalizeSharedCorridor(corridor = {}) {
  return {
    ...corridor,
    id: corridor.id || "",
    lineId: corridor.lineId || "",
    otherLineId: corridor.otherLineId || "",
    lineStartAtomIndex: clampNumber(corridor.lineStartAtomIndex, -1),
    lineEndAtomIndexExclusive: clampNumber(corridor.lineEndAtomIndexExclusive, -1),
    otherStartAtomIndex: clampNumber(corridor.otherStartAtomIndex, -1),
    otherEndAtomIndexExclusive: clampNumber(corridor.otherEndAtomIndexExclusive, -1),
    lineStartStationId: corridor.lineStartStationId || "",
    lineEndStationId: corridor.lineEndStationId || "",
    otherStartStationId: corridor.otherStartStationId || "",
    otherEndStationId: corridor.otherEndStationId || "",
    lineSharedSliceCount: clampNumber(corridor.lineSharedSliceCount, 0),
    otherSharedSliceCount: clampNumber(corridor.otherSharedSliceCount, 0),
    lineBridgedGapAtoms: clampNumber(corridor.lineBridgedGapAtoms, 0),
    otherBridgedGapAtoms: clampNumber(corridor.otherBridgedGapAtoms, 0),
    physicalOverlap: clampNumber(corridor.physicalOverlap, 0),
    orderedRun: clampNumber(corridor.orderedRun, 0),
    hasMirroredContext: corridor.hasMirroredContext === true,
    maxSharedLineCount: clampNumber(corridor.maxSharedLineCount, 0),
    traversalRelation: corridor.traversalRelation || "Unknown",
    hasCanonicalDirection: corridor.hasCanonicalDirection === true,
    lineAlongCanonical: corridor.lineAlongCanonical === true,
    otherAlongCanonical: corridor.otherAlongCanonical === true,
    confidence: clampNumber(corridor.confidence, 0.3)
  };
}

function normalizeDraft(draft = {}) {
  return {
    lineKey: draft.lineKey || "",
    selectedLineId: draft.selectedLineId || "",
    selectedEditLine: draft.selectedEditLine || "",
    mergedView: {
      direction: draft?.mergedView?.direction || "up",
      localLineIds: asArray(draft?.mergedView?.localLineIds).filter(Boolean),
      expressLineIds: asArray(draft?.mergedView?.expressLineIds).filter(Boolean),
      localLineId: draft?.mergedView?.localLineId || "",
      expressLineId: draft?.mergedView?.expressLineId || "",
      windowStart: draft?.mergedView?.windowStart || "00:00",
      windowEnd: draft?.mergedView?.windowEnd || "23:59",
      isLoop: draft?.mergedView?.isLoop === true,
      turnbackStationId: draft?.mergedView?.turnbackStationId || ""
    },
    manualRows: asArray(draft.manualRows)
      .map(normalizeManualRow)
      .filter((row) => row.id && row.lineId && row.time),
    stagedRows: asArray(draft.lineDraftRows ?? draft.stagedRows)
      .map((row) => ({
        id: row?.id || "",
        lineId: row?.lineId || "",
        time: row?.time || "",
        kind: row?.kind === "express" ? "express" : "local",
        source: row?.source || "manual",
        note: row?.note || ""
      }))
      .filter((row) => row.id && row.lineId && row.time),
    autoRules: asArray(draft.autoRules)
      .map(normalizeAutoRule)
      .filter((rule) => rule.id && rule.lineId),
    trips: asArray(draft.trips)
      .map(normalizeTrip)
      .filter((trip) => trip.id && trip.lineId)
  };
}

export function normalizePlannerInput(rawInput = {}) {
  const lines = asArray(rawInput.lines).map(normalizeLine).filter((line) => line.id);
  const lineById = new Map(lines.map((line) => [line.id, line]));

  const stations = asArray(rawInput.stations)
    .map(normalizeStation)
    .filter((station) => station.id && station.lineId && lineById.has(station.lineId))
    .sort((left, right) => {
      if (left.lineId !== right.lineId) {
        return left.lineId.localeCompare(right.lineId);
      }
      return left.order - right.order;
    });
  const stationsByLineId = new Map();
  const stationById = new Map();
  stations.forEach((station) => {
    stationById.set(station.id, station);
    if (!stationsByLineId.has(station.lineId)) {
      stationsByLineId.set(station.lineId, []);
    }
    stationsByLineId.get(station.lineId).push(station);
  });

  const segments = asArray(rawInput.segments)
    .map(normalizeSegment)
    .filter((segment) => segment.lineId && lineById.has(segment.lineId))
    .sort((left, right) => {
      if (left.lineId !== right.lineId) {
        return left.lineId.localeCompare(right.lineId);
      }
      return left.fromOrder - right.fromOrder;
    });
  const segmentsByLineId = new Map();
  segments.forEach((segment) => {
    if (!segmentsByLineId.has(segment.lineId)) {
      segmentsByLineId.set(segment.lineId, []);
    }
    segmentsByLineId.get(segment.lineId).push(segment);
  });

  const configuredBypassStations = asArray(rawInput.configuredBypassStations)
    .map(normalizeBypassStation)
    .filter((station) => station.stationId && lineById.has(station.lineId));
  const candidateBypassStations = asArray(rawInput.candidateBypassStations)
    .map(normalizeBypassStation)
    .filter((station) => station.stationId && lineById.has(station.lineId));

  const configuredBypassByLineId = new Map();
  const candidateBypassByLineId = new Map();
  configuredBypassStations.forEach((station) => {
    if (!configuredBypassByLineId.has(station.lineId)) {
      configuredBypassByLineId.set(station.lineId, []);
    }
    configuredBypassByLineId.get(station.lineId).push(station);
  });
  candidateBypassStations.forEach((station) => {
    if (!candidateBypassByLineId.has(station.lineId)) {
      candidateBypassByLineId.set(station.lineId, []);
    }
    candidateBypassByLineId.get(station.lineId).push(station);
  });

  const lineTracks = asArray(rawInput?.currentTrackScenario?.lines).map((lineTrack) => ({
    ...lineTrack,
    lineId: lineTrack.lineId || "",
    available: lineTrack.available !== false,
    executionMode: lineTrack.executionMode || "Unknown",
    confidence: clampNumber(lineTrack.confidence, clampNumber(rawInput?.currentTrackScenario?.confidence, 0.4)),
    protectedIntervals: asArray(lineTrack.protectedIntervals).map((interval) => ({
      ...interval,
      intervalIndex: clampNumber(interval.intervalIndex, -1),
      fromStationId: interval.fromStationId || "",
      toStationId: interval.toStationId || "",
      fromBuildingEntityIndex: clampNumber(interval.fromBuildingEntityIndex, -1),
      toBuildingEntityIndex: clampNumber(interval.toBuildingEntityIndex, -1),
      baseMinutes: clampNumber(interval.baseMinutes, 0),
      minEntryOffsetMinutes: clampNumber(interval.minEntryOffsetMinutes, 0),
      maxClearOffsetMinutes: clampNumber(interval.maxClearOffsetMinutes, 0),
      confidence: clampNumber(interval.confidence, 0.3)
    })),
    traversalSlices: asArray(lineTrack.traversalSlices).map(normalizeTraversalSlice)
  })).filter((lineTrack) => lineTrack.lineId && lineById.has(lineTrack.lineId));
  const lineTrackById = new Map(lineTracks.map((lineTrack) => [lineTrack.lineId, lineTrack]));
  const sharedCorridors = asArray(rawInput?.currentTrackScenario?.sharedCorridors)
    .map(normalizeSharedCorridor)
    .filter((corridor) =>
      corridor.id
      && lineById.has(corridor.lineId)
      && lineById.has(corridor.otherLineId)
    );
  const sharedCorridorsByLinePair = new Map();
  sharedCorridors.forEach((corridor) => {
    const key = toOrderedLinePairKey(corridor.lineId, corridor.otherLineId);
    if (!sharedCorridorsByLinePair.has(key)) {
      sharedCorridorsByLinePair.set(key, []);
    }
    sharedCorridorsByLinePair.get(key).push(corridor);
  });

  const stopDwell = asArray(rawInput?.observations?.stopDwell).map(normalizeObservationStop);
  const stopDwellByStationId = new Map(stopDwell.map((entry) => [entry.stationId, entry]));
  const traversalSliceObservations = asArray(rawInput?.observations?.traversalSlices).map(normalizeTraversalSlice);
  const traversalById = new Map(traversalSliceObservations.map((slice) => [slice.id, slice]));

  const drafts = asArray(rawInput.drafts)
    .map(normalizeDraft)
    .map((draft) => {
      const localLineIds = draft.mergedView.localLineIds.filter((lineId) => lineById.has(lineId));
      const expressLineIds = draft.mergedView.expressLineIds.filter((lineId) => lineById.has(lineId));
      const localLineId = lineById.has(draft.mergedView.localLineId) ? draft.mergedView.localLineId : "";
      const expressLineId = lineById.has(draft.mergedView.expressLineId) ? draft.mergedView.expressLineId : "";
      const manualRows = draft.manualRows.filter((row) => lineById.has(row.lineId));
      const stagedRows = draft.stagedRows.filter((row) => lineById.has(row.lineId));
      const autoRules = draft.autoRules.filter((rule) => lineById.has(rule.lineId));
      const trips = draft.trips.filter((trip) => lineById.has(trip.lineId));
      const generatedRows = buildGeneratedRowsFromDraftIntent({
        ...draft,
        manualRows,
        autoRules
      }).filter((row) => lineById.has(row.lineId));
      return {
        ...draft,
        mergedView: {
          ...draft.mergedView,
          localLineIds,
          expressLineIds,
          localLineId,
          expressLineId
        },
        manualRows,
        stagedRows,
        autoRules,
        trips,
        generatedRows
      };
    })
    .filter((draft) => {
      const knownSelection = lineById.has(draft.selectedLineId) || lineById.has(draft.selectedEditLine);
      const knownMergedLine = draft.mergedView.localLineIds.length > 0 || draft.mergedView.expressLineIds.length > 0;
      const knownRow = draft.stagedRows.length > 0 || draft.generatedRows.length > 0;
      const knownTrip = draft.trips.length > 0;
      return knownSelection || knownMergedLine || knownRow || knownTrip;
    });
  const stationRuntimeByLinePair = buildObservedStationRuntimeByLinePair(drafts);

  return {
    rawInput,
    generatedAtFrame: clampNumber(rawInput.generatedAtFrame, 0),
    version: rawInput.version || "",
    runtimeParams: {
      simFramesPerMinute: clampNumber(rawInput?.runtimeParams?.simFramesPerMinute, 182.044),
      trackModelEntryClearSafetyGapMinutes: clampNumber(
        rawInput?.runtimeParams?.trackModelEntryClearSafetyGapMinutes,
        1
      )
    },
    lines,
    lineById,
    stations,
    stationById,
    stationsByLineId,
    segments,
    segmentsByLineId,
    configuredBypassStations,
    configuredBypassByLineId,
    candidateBypassStations,
    candidateBypassByLineId,
    lineTracks,
    lineTrackById,
    sharedCorridors,
    sharedCorridorsByLinePair,
    observations: {
      stopDwell,
      stopDwellByStationId,
      traversalSlices: traversalSliceObservations,
      traversalById,
      stationRuntimeByLinePair
    },
    drafts
  };
}

export function pickPlannerDraft(normalizedInput, options = {}) {
  const preferredKey = options.draftKey || "";
  const preferredLineId = options.selectedLineId || "";
  if (preferredKey) {
    const exactKey = normalizedInput.drafts.find((draft) => draft.lineKey === preferredKey);
    if (exactKey) {
      return exactKey;
    }
  }

  if (preferredLineId) {
    const exactLine = normalizedInput.drafts.find((draft) => draft.selectedLineId === preferredLineId);
    if (exactLine) {
      return exactLine;
    }
  }

  const draftScore = (draft) => {
    const localCount = draft.mergedView.localLineIds.length;
    const expressCount = draft.mergedView.expressLineIds.length;
    const stagedRowCount = draft.stagedRows.length;
    const generatedRowCount = asArray(draft.generatedRows).length;
    const tripCount = asArray(draft.trips).length;
    const stagedKinds = new Set(draft.stagedRows.map((row) => row.kind));
    let score = 0;
    if (localCount > 0 && expressCount > 0) {
      score += 10000;
    }
    if (stagedKinds.has("local") && stagedKinds.has("express")) {
      score += 5000;
    }
    if (stagedRowCount > 0) {
      score += 1000;
    }
    score += stagedRowCount;
    score += generatedRowCount;
    score += tripCount * 0.25;
    return score;
  };

  return [...normalizedInput.drafts].sort((left, right) => draftScore(right) - draftScore(left))[0] || null;
}

function uniqueLineIds(rows, kind) {
  return [...new Set(
    asArray(rows)
      .filter((row) => row.kind === kind && row.lineId)
      .map((row) => row.lineId)
  )];
}

export function buildAnalysisScenario(normalizedInput, draft, options = {}) {
  const mergedView = draft?.mergedView || {};
  const draftRowsForSelection = asArray(draft?.stagedRows).length > 0
    ? asArray(draft?.stagedRows)
    : asArray(draft?.generatedRows);
  const stagedLocalLineIds = uniqueLineIds(draftRowsForSelection, "local").filter((lineId) => normalizedInput.lineById.has(lineId));
  const stagedExpressLineIds = uniqueLineIds(draftRowsForSelection, "express").filter((lineId) => normalizedInput.lineById.has(lineId));
  const stagedSelectedLineIds = dedupeLineIds([...stagedLocalLineIds, ...stagedExpressLineIds]);
  const explicitLocalLineIds = asArray(options.localLineIds).filter((lineId) => normalizedInput.lineById.has(lineId));
  const explicitExpressLineIds = asArray(options.expressLineIds).filter((lineId) => normalizedInput.lineById.has(lineId));
  const explicitSelectedLineIds = asArray(options.selectedLineIds).filter((lineId) => normalizedInput.lineById.has(lineId));
  const explicitAdjustableLineIds = asArray(options.adjustableLineIds).filter((lineId) => normalizedInput.lineById.has(lineId));
  const mergedLocalLineIds = asArray(mergedView.localLineIds).filter((lineId) => normalizedInput.lineById.has(lineId));
  const mergedExpressLineIds = asArray(mergedView.expressLineIds).filter((lineId) => normalizedInput.lineById.has(lineId));
  const mergedSelectedLineIds = dedupeLineIds([...mergedLocalLineIds, ...mergedExpressLineIds]);
  const preferMergedViewLineIds = options.preferMergedViewLineIds === true;

  const localLineIds = explicitLocalLineIds.length > 0
    ? explicitLocalLineIds
    : !preferMergedViewLineIds && stagedLocalLineIds.length > 0
      ? stagedLocalLineIds
      : mergedLocalLineIds.length > 0
        ? mergedLocalLineIds
        : stagedLocalLineIds;
  const expressLineIds = explicitExpressLineIds.length > 0
    ? explicitExpressLineIds
    : !preferMergedViewLineIds && stagedExpressLineIds.length > 0
      ? stagedExpressLineIds
      : mergedExpressLineIds.length > 0
        ? mergedExpressLineIds
        : stagedExpressLineIds;
  const selectedLineIds = explicitSelectedLineIds.length > 0
    ? dedupeLineIds(explicitSelectedLineIds)
    : !preferMergedViewLineIds && stagedSelectedLineIds.length > 0
      ? stagedSelectedLineIds
      : mergedSelectedLineIds.length > 0
        ? mergedSelectedLineIds
        : dedupeLineIds([...localLineIds, ...expressLineIds]);
  const adjustableLineIds = explicitAdjustableLineIds.length > 0
    ? dedupeLineIds(explicitAdjustableLineIds.filter((lineId) => selectedLineIds.includes(lineId)))
    : dedupeLineIds(localLineIds.filter((lineId) => selectedLineIds.includes(lineId)));

  const windowStart = options.windowStart || mergedView.windowStart || "00:00";
  const windowEnd = options.windowEnd || mergedView.windowEnd || "23:59";
  const windowStartMinute = timeToMinutes(windowStart);
  const windowEndMinute = timeToMinutes(windowEnd);

  const scenarioLineIds = new Set(selectedLineIds);
  const stagedRowSource = asArray(options.stagedRowsOverride).length > 0
    ? asArray(options.stagedRowsOverride)
    : asArray(draft?.stagedRows).length > 0
      ? asArray(draft?.stagedRows)
      : asArray(draft?.generatedRows);
  const stagedRows = stagedRowSource
    .map((row) => ({
      ...row,
      minute: timeToMinutes(row.time)
    }))
    .filter((row) =>
      row.minute !== null
      && scenarioLineIds.has(row.lineId)
      && isMinuteInsideWindow(row.minute, windowStartMinute, windowEndMinute)
    )
    .sort((left, right) => {
      if (left.minute !== right.minute) {
        return left.minute - right.minute;
      }
      return left.lineId.localeCompare(right.lineId);
    });

  return {
    draft,
    draftKey: draft?.lineKey || "",
    selectedLineId: draft?.selectedLineId || "",
    turnbackStationId: mergedView.turnbackStationId || "",
    selectedLineIds,
    selectedLineIdSet: new Set(selectedLineIds),
    selectedLocalLineIds: localLineIds,
    adjustableLineIds,
    adjustableLineIdSet: new Set(adjustableLineIds),
    fixedLineIds: selectedLineIds.filter((lineId) => !adjustableLineIds.includes(lineId)),
    localLineIds: adjustableLineIds,
    expressLineIds,
    windowStart,
    windowEnd,
    windowStartMinute,
    windowEndMinute,
    stagedRows
  };
}

function resolveStationDwellMinutes(normalizedInput, station) {
  const observed = normalizedInput.observations.stopDwellByStationId.get(station.id);
  if (observed && observed.sampleCount > 0 && observed.averageMinutes > 0) {
    return {
      minutes: observed.averageMinutes,
      sampleCount: observed.sampleCount,
      source: "observed",
      confidence: observed.confidence,
      variabilityMinutes: estimateVariabilityMinutes(
        observed.averageMinutes,
        observed.confidence,
        observed.sampleCount
      )
    };
  }

  if (station.profileDwellMinutes > 0) {
    return {
      minutes: station.profileDwellMinutes,
      sampleCount: 0,
      source: "profile",
      confidence: station.confidence || 0.4,
      variabilityMinutes: estimateVariabilityMinutes(
        station.profileDwellMinutes,
        station.confidence || 0.4,
        0
      )
    };
  }

  return {
    minutes: 0,
    sampleCount: 0,
    source: "fallback",
    confidence: 0.2,
    variabilityMinutes: 0
  };
}

function resolveSegmentRuntimeMinutes(segment, observedRuntime = null) {
  if (observedRuntime
    && observedRuntime.sampleCount > 0
    && observedRuntime.minutes > 0) {
    return {
      minutes: observedRuntime.minutes,
      medianMinutes: observedRuntime.medianMinutes,
      averageMinutes: observedRuntime.averageMinutes,
      minMinutes: observedRuntime.minMinutes,
      maxMinutes: observedRuntime.maxMinutes,
      baselinePolicy: observedRuntime.baselinePolicy,
      sampleCount: observedRuntime.sampleCount,
      source: "tripObserved",
      confidence: observedRuntime.confidence || 0.65,
      variabilityMinutes: observedRuntime.variabilityMinutes || estimateVariabilityMinutes(
        observedRuntime.minutes,
        observedRuntime.confidence || 0.65,
        observedRuntime.sampleCount
      )
    };
  }

  const baseMinutes = segment.profileMinutes > 0
    ? segment.profileMinutes
    : Math.max(segment.estimatedMinutes, 0);
  const confidence = segment.profileMinutes > 0
    ? segment.confidence || 0.5
    : segment.confidence || 0.3;
  if (segment.profileMinutes > 0) {
    return {
      minutes: baseMinutes,
      source: "profile",
      confidence,
      variabilityMinutes: estimateVariabilityMinutes(baseMinutes, confidence, 0)
    };
  }

  return {
    minutes: baseMinutes,
    source: segment.estimatedMinutes > 0 ? "estimated" : "fallback",
    confidence,
    variabilityMinutes: estimateVariabilityMinutes(baseMinutes, confidence, 0)
  };
}

function resolveObservedStationRuntime(
  normalizedInput,
  lineId,
  fromStation,
  toStation,
  fallbackRuntime,
  options = {}
) {
  const minSamples = resolveObservedStationRuntimeMinSamples(options);
  const runtimeByPair = normalizedInput.observations.stationRuntimeByLinePair;
  if (!(runtimeByPair instanceof Map)) {
    return null;
  }
  const runtime = runtimeByPair.get(
    toStationRuntimeKey(lineId, fromStation?.workbenchStationId, toStation?.workbenchStationId)
  );
  if (!runtime || runtime.sampleCount < minSamples || !(runtime.minutes > 0)) {
    return null;
  }

  const fallbackMinutes = clampNumber(fallbackRuntime?.minutes, 0);
  if (fallbackMinutes > 0) {
    const maxRatio = resolveObservedStationRuntimeMaxProfileRatio(options);
    const maxExtraMinutes = resolveObservedStationRuntimeMaxProfileExtraMinutes(options);
    const maxAllowedMinutes = Math.max(fallbackMinutes * maxRatio, fallbackMinutes + maxExtraMinutes);
    if (runtime.minutes > maxAllowedMinutes) {
      return null;
    }
  }

  return runtime;
}

function resolveTraversalSliceRuntimeMinutes(slice) {
  const observedMinutes = slice.observedAverageMinutes > 0
    ? slice.observedAverageMinutes
    : Math.max(slice.modelRunMinutes, 0);
  const confidence = slice.confidence || (slice.observedSampleCount > 0 ? 0.5 : 0.3);
  if (slice.observedSampleCount > 0 && slice.observedAverageMinutes > 0) {
    return {
      minutes: observedMinutes,
      source: "observed",
      confidence,
      variabilityMinutes: estimateVariabilityMinutes(
        observedMinutes,
        confidence,
        slice.observedSampleCount,
        slice.observedFastMinutes
      )
    };
  }

  return {
    minutes: observedMinutes,
    source: slice.modelRunMinutes > 0 ? "model" : "fallback",
    confidence,
    variabilityMinutes: estimateVariabilityMinutes(
      observedMinutes,
      confidence,
      0,
      slice.observedFastMinutes
    )
  };
}

function findStationOffsetForTraversalSlice(stations, stationOffsetsById, slice) {
  if (slice.stationTraversalKind !== "stop" || slice.stationWaypointIndex < 0) {
    return null;
  }

  const station = asArray(stations).find((candidate) =>
    candidate.waypointIndex === slice.stationWaypointIndex
    && clampNumber(candidate.trackAtomIndex, -1) >= clampNumber(slice.startAtomIndex, -1)
    && clampNumber(candidate.trackAtomIndex, -1) <= clampNumber(slice.endAtomIndexExclusive, -1)
  );
  if (!station) {
    return null;
  }

  return stationOffsetsById.get(station.id) || null;
}

function resolveTraversalSliceRuntimeForStopPattern(slice, stations, stationOffsetsById, dwellIncludedWaypointIndices = null) {
  const stationOffset = findStationOffsetForTraversalSlice(stations, stationOffsetsById, slice);
  if (stationOffset && stationOffset.shouldStop === false) {
    const modelMinutes = Math.max(slice.modelRunMinutes, 0);
    return {
      minutes: modelMinutes,
      source: modelMinutes > 0 ? "modelSkipStop" : "fallback",
      confidence: Math.min(clampNumber(slice.confidence, 0.2), 0.55),
      variabilityMinutes: estimateVariabilityMinutes(modelMinutes, 0.45, 0)
    };
  }

  const runtime = resolveTraversalSliceRuntimeMinutes(slice);
  if (stationOffset
    && stationOffset.shouldStop !== false
    && slice.observedIncludesStationStop
    && slice.observedSampleCount > 0
    && slice.observedAverageMinutes > 0
    && dwellIncludedWaypointIndices instanceof Set) {
    dwellIncludedWaypointIndices.add(stationOffset.stationId);
  }

  return runtime;
}

function fillMissingRunMinutesByAtom(stations, segmentRuntimeByStationPair, runMinutesByAtom, coveredBySlice) {
  asArray(stations).forEach((station, stationIndex) => {
    if (stationIndex >= stations.length - 1) {
      return;
    }

    const nextStation = stations[stationIndex + 1];
    const runtime = segmentRuntimeByStationPair?.get(`${station.id}->${nextStation.id}`);
    if (!runtime || !(runtime.minutes > 0)) {
      return;
    }

    const startAtomIndex = Math.max(0, clampNumber(station.trackAtomIndex, -1));
    const endAtomIndexExclusive = Math.min(runMinutesByAtom.length, clampNumber(nextStation.trackAtomIndex, -1));
    if (endAtomIndexExclusive <= startAtomIndex) {
      return;
    }

    const perAtomMinutes = runtime.minutes / Math.max(1, endAtomIndexExclusive - startAtomIndex);
    for (let atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex += 1) {
      if (!coveredBySlice[atomIndex]) {
        runMinutesByAtom[atomIndex] = perAtomMinutes;
      }
    }
  });
}

function fillMissingVariabilitySquareByAtom(stations, segmentRuntimeByStationPair, variabilitySquareByAtom, coveredBySlice) {
  asArray(stations).forEach((station, stationIndex) => {
    if (stationIndex >= stations.length - 1) {
      return;
    }

    const nextStation = stations[stationIndex + 1];
    const runtime = segmentRuntimeByStationPair?.get(`${station.id}->${nextStation.id}`);
    if (!runtime || !(runtime.variabilityMinutes > 0)) {
      return;
    }

    const startAtomIndex = Math.max(0, clampNumber(station.trackAtomIndex, -1));
    const endAtomIndexExclusive = Math.min(variabilitySquareByAtom.length, clampNumber(nextStation.trackAtomIndex, -1));
    if (endAtomIndexExclusive <= startAtomIndex) {
      return;
    }

    const perAtomVariabilityMinutes = runtime.variabilityMinutes / Math.max(1, endAtomIndexExclusive - startAtomIndex);
    const perAtomVariabilitySquare = perAtomVariabilityMinutes * perAtomVariabilityMinutes;
    for (let atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex += 1) {
      if (!coveredBySlice[atomIndex]) {
        variabilitySquareByAtom[atomIndex] = perAtomVariabilitySquare;
      }
    }
  });
}

function buildAtomBoundaryMinuteOffsets(stations, stationOffsetsById, lineTrack, segmentRuntimeByStationPair = null) {
  const trackAtomCount = clampNumber(lineTrack?.trackAtomCount, 0);
  if (trackAtomCount <= 0) {
    return [0];
  }

  const runMinutesByAtom = new Array(trackAtomCount).fill(0);
  const coveredBySlice = new Array(trackAtomCount).fill(false);
  const dwellIncludedWaypointIndices = new Set();
  asArray(lineTrack?.traversalSlices).forEach((slice) => {
    const startAtomIndex = Math.max(0, clampNumber(slice.startAtomIndex, -1));
    const endAtomIndexExclusive = Math.min(trackAtomCount, clampNumber(slice.endAtomIndexExclusive, -1));
    if (endAtomIndexExclusive <= startAtomIndex) {
      return;
    }

    const runtime = resolveTraversalSliceRuntimeForStopPattern(
      slice,
      stations,
      stationOffsetsById,
      dwellIncludedWaypointIndices
    );
    const atomCount = Math.max(1, endAtomIndexExclusive - startAtomIndex);
    const perAtomMinutes = runtime.minutes / atomCount;
    for (let atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex += 1) {
      runMinutesByAtom[atomIndex] += perAtomMinutes;
      coveredBySlice[atomIndex] = true;
    }
  });
  fillMissingRunMinutesByAtom(stations, segmentRuntimeByStationPair, runMinutesByAtom, coveredBySlice);

  const dwellMinutesByBoundary = new Array(trackAtomCount + 1).fill(0);
  asArray(stations).forEach((station, stationIndex) => {
    const stationOffset = stationOffsetsById.get(station.id);
    const boundaryIndex = clampNumber(station.trackAtomIndex, -1);
    if (!stationOffset || boundaryIndex < 0 || boundaryIndex > trackAtomCount) {
      return;
    }
    if (stationIndex === 0) {
      return;
    }
    if (dwellIncludedWaypointIndices.has(station.id)) {
      return;
    }

    dwellMinutesByBoundary[boundaryIndex] += stationOffset.dwellMinutes
      - clampNumber(stationOffset.skippedStopStartLossMinutes, 0);
  });

  const atomBoundaryMinuteOffsets = new Array(trackAtomCount + 1).fill(0);
  let cumulativeMinutes = 0;
  for (let boundaryIndex = 0; boundaryIndex <= trackAtomCount; boundaryIndex += 1) {
    cumulativeMinutes += dwellMinutesByBoundary[boundaryIndex];
    atomBoundaryMinuteOffsets[boundaryIndex] = Number(cumulativeMinutes.toFixed(4));
    if (boundaryIndex < trackAtomCount) {
      cumulativeMinutes += runMinutesByAtom[boundaryIndex];
    }
  }

  return atomBoundaryMinuteOffsets;
}

function buildAtomBoundaryVariabilityOffsets(stations, stationOffsetsById, lineTrack, segmentRuntimeByStationPair = null) {
  const trackAtomCount = clampNumber(lineTrack?.trackAtomCount, 0);
  if (trackAtomCount <= 0) {
    return [0];
  }

  const variabilitySquareByAtom = new Array(trackAtomCount).fill(0);
  const coveredBySlice = new Array(trackAtomCount).fill(false);
  const dwellIncludedWaypointIndices = new Set();
  asArray(lineTrack?.traversalSlices).forEach((slice) => {
    const startAtomIndex = Math.max(0, clampNumber(slice.startAtomIndex, -1));
    const endAtomIndexExclusive = Math.min(trackAtomCount, clampNumber(slice.endAtomIndexExclusive, -1));
    if (endAtomIndexExclusive <= startAtomIndex) {
      return;
    }

    const runtime = resolveTraversalSliceRuntimeForStopPattern(
      slice,
      stations,
      stationOffsetsById,
      dwellIncludedWaypointIndices
    );
    const atomCount = Math.max(1, endAtomIndexExclusive - startAtomIndex);
    const perAtomVariabilityMinutes = runtime.variabilityMinutes / atomCount;
    for (let atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex += 1) {
      variabilitySquareByAtom[atomIndex] += perAtomVariabilityMinutes * perAtomVariabilityMinutes;
      coveredBySlice[atomIndex] = true;
    }
  });
  fillMissingVariabilitySquareByAtom(stations, segmentRuntimeByStationPair, variabilitySquareByAtom, coveredBySlice);

  const dwellVariabilitySquareByBoundary = new Array(trackAtomCount + 1).fill(0);
  asArray(stations).forEach((station, stationIndex) => {
    const stationOffset = stationOffsetsById.get(station.id);
    const boundaryIndex = clampNumber(station.trackAtomIndex, -1);
    if (!stationOffset || boundaryIndex < 0 || boundaryIndex > trackAtomCount || stationIndex === 0) {
      return;
    }
    if (dwellIncludedWaypointIndices.has(station.id)) {
      return;
    }

    const dwellVariabilityMinutes = clampNumber(stationOffset.variabilityMinutes, 0);
    dwellVariabilitySquareByBoundary[boundaryIndex] += dwellVariabilityMinutes * dwellVariabilityMinutes;
  });

  const atomBoundaryVariabilityOffsets = new Array(trackAtomCount + 1).fill(0);
  let cumulativeVariabilitySquare = 0;
  for (let boundaryIndex = 0; boundaryIndex <= trackAtomCount; boundaryIndex += 1) {
    cumulativeVariabilitySquare += dwellVariabilitySquareByBoundary[boundaryIndex];
    atomBoundaryVariabilityOffsets[boundaryIndex] = Number(Math.sqrt(cumulativeVariabilitySquare).toFixed(4));
    if (boundaryIndex < trackAtomCount) {
      cumulativeVariabilitySquare += variabilitySquareByAtom[boundaryIndex];
    }
  }

  return atomBoundaryVariabilityOffsets;
}

function getAtomBoundaryMinuteOffset(lineRuntimeModel, atomIndex) {
  const offsets = lineRuntimeModel?.atomBoundaryMinuteOffsets;
  if (!Array.isArray(offsets) || offsets.length === 0) {
    return 0;
  }

  const clampedIndex = Math.max(0, Math.min(offsets.length - 1, Math.round(atomIndex)));
  return offsets[clampedIndex];
}

function getAtomBoundaryVariabilityOffset(lineRuntimeModel, atomIndex) {
  const offsets = lineRuntimeModel?.atomBoundaryVariabilityOffsets;
  if (!Array.isArray(offsets) || offsets.length === 0) {
    return 0;
  }

  const clampedIndex = Math.max(0, Math.min(offsets.length - 1, Math.round(atomIndex)));
  return offsets[clampedIndex];
}

function hasUsableAtomRuntime(lineTrack) {
  return clampNumber(lineTrack?.trackAtomCount, 0) > 0
    && asArray(lineTrack?.traversalSlices).some((slice) =>
      clampNumber(slice.endAtomIndexExclusive, -1) > clampNumber(slice.startAtomIndex, -1)
    );
}

function applyStationTimelineFromAtomOffsets(stations, stationOffsets, atomBoundaryMinuteOffsets) {
  const offsetModel = { atomBoundaryMinuteOffsets };
  asArray(stations).forEach((station, stationIndex) => {
    const stationOffset = stationOffsets[stationIndex];
    if (!stationOffset) {
      return;
    }

    const departureMinute = stationIndex === 0
      ? 0
      : getAtomBoundaryMinuteOffset(offsetModel, station.trackAtomIndex);
    const arrivalMinute = stationOffset.shouldStop
      ? Math.max(0, departureMinute - clampNumber(stationOffset.dwellMinutes, 0))
      : departureMinute;
    stationOffset.arrivalMinute = Number(arrivalMinute.toFixed(4));
    stationOffset.departureMinute = Number(departureMinute.toFixed(4));
  });
}

function rebuildSegmentRuntimeOffsetsFromAtomTimeline(
  stations,
  stationOffsets,
  segmentRuntimeOffsets,
  segmentRuntimeByStationPair,
  atomBoundaryVariabilityOffsets
) {
  segmentRuntimeOffsets.length = 0;
  segmentRuntimeByStationPair.clear();
  const variabilityModel = { atomBoundaryMinuteOffsets: atomBoundaryVariabilityOffsets };
  for (let index = 0; index < stations.length - 1; index += 1) {
    const station = stations[index];
    const nextStation = stations[index + 1];
    const stationOffset = stationOffsets[index];
    const nextStationOffset = stationOffsets[index + 1];
    if (!station || !nextStation || !stationOffset || !nextStationOffset) {
      continue;
    }

    const minutes = Math.max(0, nextStationOffset.arrivalMinute - stationOffset.departureMinute);
    const variabilityMinutes = Math.max(
      0,
      getAtomBoundaryMinuteOffset(variabilityModel, nextStation.trackAtomIndex)
        - getAtomBoundaryMinuteOffset(variabilityModel, station.trackAtomIndex)
    );
    const segmentRuntime = {
      fromStationId: station.id,
      toStationId: nextStation.id,
      fromOrder: station.order,
      toOrder: nextStation.order,
      minutes: Number(minutes.toFixed(4)),
      averageMinutes: Number(minutes.toFixed(4)),
      source: "atomSlice",
      sampleCount: 0,
      confidence: 0.65,
      variabilityMinutes: Number(variabilityMinutes.toFixed(4))
    };
    segmentRuntimeOffsets.push(segmentRuntime);
    segmentRuntimeByStationPair.set(`${station.id}->${nextStation.id}`, segmentRuntime);
  }
}

function shouldExpressStopAtStation(line, station, stopStationIdSet, stationIndex, stationCount) {
  if (line?.kind !== "express") {
    return true;
  }
  if (!(stopStationIdSet instanceof Set) || stopStationIdSet.size === 0) {
    return true;
  }
  if (stationIndex === 0 || stationIndex === stationCount - 1) {
    return true;
  }
  return stopStationIdSet.has(station.id);
}

function buildLineRuntimeModel(normalizedInput, lineId, options = {}) {
  const line = normalizedInput.lineById.get(lineId);
  if (!line) {
    return null;
  }

  const stations = normalizedInput.stationsByLineId.get(lineId) || [];
  const segments = normalizedInput.segmentsByLineId.get(lineId) || [];
  const lineTrack = normalizedInput.lineTrackById.get(lineId) || null;
  const stationOffsets = [];
  const stationOffsetsById = new Map();
  const segmentRuntimeOffsets = [];
  const segmentRuntimeByStationPair = new Map();
  let cursorMinute = 0;

  const expressStopStationIdSet = options.expressStopStationIdSet instanceof Set
    ? options.expressStopStationIdSet
    : asArray(options.expressStopStationIds).filter(Boolean).length > 0
      ? new Set(asArray(options.expressStopStationIds).filter(Boolean))
      : null;
  const stopStartLossMinutesPerSkippedStop = resolveStopStartLossMinutesPerSkippedStop(options);

  for (let index = 0; index < stations.length; index += 1) {
    const station = stations[index];
    const dwell = resolveStationDwellMinutes(normalizedInput, station);
    const shouldStop = shouldExpressStopAtStation(line, station, expressStopStationIdSet, index, stations.length);
    const skippedStopStartLossMinutes = shouldStop
      || index === 0
      || index === stations.length - 1
      ? 0
      : stopStartLossMinutesPerSkippedStop;
    const effectiveDwellMinutes = shouldStop ? dwell.minutes : 0;
    const rawArrivalMinute = index === 0 ? 0 : cursorMinute;
    const arrivalMinute = shouldStop
      ? rawArrivalMinute
      : Math.max(0, rawArrivalMinute - skippedStopStartLossMinutes);
    const departureMinute = index === 0
      ? 0
      : Math.max(0, arrivalMinute + effectiveDwellMinutes);
    const stationOffset = {
      stationId: station.id,
      order: station.order,
      name: station.name,
      arrivalMinute,
      departureMinute,
      dwellMinutes: effectiveDwellMinutes,
      skippedStopStartLossMinutes,
      variabilityMinutes: dwell.variabilityMinutes,
      dwellSource: shouldStop ? dwell.source : "skipPattern",
      shouldStop,
      confidence: dwell.confidence
    };
    stationOffsets.push(stationOffset);
    stationOffsetsById.set(station.id, stationOffset);

    const nextSegment = segments[index];
    if (nextSegment) {
      const nextStation = stations[index + 1] || null;
      const fallbackRuntime = resolveSegmentRuntimeMinutes(nextSegment, null);
      const observedRuntime = nextStation
        ? resolveObservedStationRuntime(
          normalizedInput,
          lineId,
          station,
          nextStation,
          fallbackRuntime,
          options
        )
        : null;
      const runtime = resolveSegmentRuntimeMinutes(nextSegment, observedRuntime);
      const segmentRuntime = {
        fromStationId: station.id,
        toStationId: nextStation?.id || nextSegment.toStationId || "",
        fromOrder: station.order,
        toOrder: nextStation?.order ?? nextSegment.toOrder,
        minutes: runtime.minutes,
        medianMinutes: runtime.medianMinutes,
        averageMinutes: runtime.averageMinutes,
        minMinutes: runtime.minMinutes,
        maxMinutes: runtime.maxMinutes,
        baselinePolicy: runtime.baselinePolicy,
        source: runtime.source,
        sampleCount: runtime.sampleCount || 0,
        confidence: runtime.confidence,
        variabilityMinutes: runtime.variabilityMinutes
      };
      segmentRuntimeOffsets.push(segmentRuntime);
      if (nextStation) {
        segmentRuntimeByStationPair.set(`${station.id}->${nextStation.id}`, segmentRuntime);
      }
      cursorMinute = departureMinute + runtime.minutes;
    }
  }

  const atomBoundaryMinuteOffsets = buildAtomBoundaryMinuteOffsets(
    stations,
    stationOffsetsById,
    lineTrack,
    segmentRuntimeByStationPair
  );
  const atomBoundaryVariabilityOffsets = buildAtomBoundaryVariabilityOffsets(
    stations,
    stationOffsetsById,
    lineTrack,
    segmentRuntimeByStationPair
  );
  if (hasUsableAtomRuntime(lineTrack)) {
    applyStationTimelineFromAtomOffsets(stations, stationOffsets, atomBoundaryMinuteOffsets);
    rebuildSegmentRuntimeOffsetsFromAtomTimeline(
      stations,
      stationOffsets,
      segmentRuntimeOffsets,
      segmentRuntimeByStationPair,
      atomBoundaryVariabilityOffsets
    );
  }

  return {
    line,
    lineTrack,
    stations,
    segments,
    segmentRuntimeOffsets,
    segmentRuntimeByStationPair,
    stationOffsets,
    stationOffsetsById,
    atomBoundaryMinuteOffsets,
    atomBoundaryVariabilityOffsets,
    totalMinuteSpan:
      stationOffsets.length > 0
        ? stationOffsets[stationOffsets.length - 1].departureMinute
        : 0
  };
}

export function buildLineRuntimeModels(normalizedInput, scenario, options = {}) {
  const lineRuntimeModels = new Map();
  dedupeLineIds(scenario.selectedLineIds).forEach((lineId) => {
    const runtimeModel = buildLineRuntimeModel(normalizedInput, lineId, options);
    if (runtimeModel) {
      lineRuntimeModels.set(lineId, runtimeModel);
    }
  });
  return lineRuntimeModels;
}

function estimateIntervalRuntimeMinutes(lineTrack, interval) {
  if (!lineTrack || !interval) {
    return 0;
  }

  const overlappingSlices = asArray(lineTrack.traversalSlices).filter((slice) =>
    slice.endAtomIndexExclusive > interval.startAtomIndex
    && slice.startAtomIndex < interval.endAtomIndexExclusive
  );

  if (overlappingSlices.length === 0) {
    return Math.max(interval.baseMinutes, 0);
  }

  let totalMinutes = 0;
  let observedSliceCount = 0;
  overlappingSlices.forEach((slice) => {
    if (slice.observedSampleCount > 0 && slice.observedAverageMinutes > 0) {
      totalMinutes += slice.observedAverageMinutes;
      observedSliceCount += 1;
      return;
    }
    totalMinutes += Math.max(slice.modelRunMinutes, 0);
  });

  if (totalMinutes > 0) {
    return totalMinutes;
  }

  return Math.max(interval.baseMinutes, 0);
}

function orientSharedCorridorForScenario(normalizedInput, scenario, sharedCorridor, lineRuntimeModels) {
  const primaryLineId = sharedCorridor.lineId;
  const secondaryLineId = sharedCorridor.otherLineId;
  const primaryModel = lineRuntimeModels.get(primaryLineId);
  const secondaryModel = lineRuntimeModels.get(secondaryLineId);
  if (!primaryModel || !secondaryModel) {
    return null;
  }

  const primaryEntryOffsetMinutes = getAtomBoundaryMinuteOffset(primaryModel, sharedCorridor.lineStartAtomIndex);
  const primaryExitOffsetMinutes = getAtomBoundaryMinuteOffset(primaryModel, sharedCorridor.lineEndAtomIndexExclusive);
  const secondaryEntryOffsetMinutes = getAtomBoundaryMinuteOffset(secondaryModel, sharedCorridor.otherStartAtomIndex);
  const secondaryExitOffsetMinutes = getAtomBoundaryMinuteOffset(secondaryModel, sharedCorridor.otherEndAtomIndexExclusive);
  const primaryRuntimeMinutes = Number(Math.max(0, primaryExitOffsetMinutes - primaryEntryOffsetMinutes).toFixed(2));
  const secondaryRuntimeMinutes = Number(Math.max(0, secondaryExitOffsetMinutes - secondaryEntryOffsetMinutes).toFixed(2));
  const primaryAdjustable = scenario.adjustableLineIdSet?.has(primaryLineId) === true;
  const secondaryAdjustable = scenario.adjustableLineIdSet?.has(secondaryLineId) === true;

  let usePrimaryAsLocal = false;
  if (primaryAdjustable !== secondaryAdjustable) {
    usePrimaryAsLocal = primaryAdjustable;
  } else if (primaryRuntimeMinutes !== secondaryRuntimeMinutes) {
    usePrimaryAsLocal = primaryRuntimeMinutes >= secondaryRuntimeMinutes;
  } else {
    const primaryKind = normalizedInput.lineById.get(primaryLineId)?.kind || "local";
    const secondaryKind = normalizedInput.lineById.get(secondaryLineId)?.kind || "local";
    if (primaryKind !== secondaryKind) {
      usePrimaryAsLocal = primaryKind !== "express";
    } else {
      usePrimaryAsLocal = String(primaryLineId).localeCompare(String(secondaryLineId)) <= 0;
    }
  }

  if (usePrimaryAsLocal) {
    return {
      corridorId: sharedCorridor.id,
      sharedKey: sharedCorridor.id,
      localLineId: primaryLineId,
      expressLineId: secondaryLineId,
      yieldingLineId: primaryLineId,
      priorityLineId: secondaryLineId,
      localStartAtomIndex: sharedCorridor.lineStartAtomIndex,
      localEndAtomIndexExclusive: sharedCorridor.lineEndAtomIndexExclusive,
      expressStartAtomIndex: sharedCorridor.otherStartAtomIndex,
      expressEndAtomIndexExclusive: sharedCorridor.otherEndAtomIndexExclusive,
      localStartStationId: sharedCorridor.lineStartStationId,
      localEndStationId: sharedCorridor.lineEndStationId,
      expressStartStationId: sharedCorridor.otherStartStationId,
      expressEndStationId: sharedCorridor.otherEndStationId,
      localEntryOffsetMinutes: primaryEntryOffsetMinutes,
      localExitOffsetMinutes: primaryExitOffsetMinutes,
      expressEntryOffsetMinutes: secondaryEntryOffsetMinutes,
      expressExitOffsetMinutes: secondaryExitOffsetMinutes,
      localRuntimeMinutes: primaryRuntimeMinutes,
      expressRuntimeMinutes: secondaryRuntimeMinutes,
      orderedRun: sharedCorridor.orderedRun,
      physicalOverlap: sharedCorridor.physicalOverlap,
      confidence: clampNumber(sharedCorridor.confidence, 0.3),
      localAdjustable: primaryAdjustable
    };
  }

  return {
    corridorId: sharedCorridor.id,
    sharedKey: sharedCorridor.id,
    localLineId: secondaryLineId,
    expressLineId: primaryLineId,
    yieldingLineId: secondaryLineId,
    priorityLineId: primaryLineId,
    localStartAtomIndex: sharedCorridor.otherStartAtomIndex,
    localEndAtomIndexExclusive: sharedCorridor.otherEndAtomIndexExclusive,
    expressStartAtomIndex: sharedCorridor.lineStartAtomIndex,
    expressEndAtomIndexExclusive: sharedCorridor.lineEndAtomIndexExclusive,
    localStartStationId: sharedCorridor.otherStartStationId,
    localEndStationId: sharedCorridor.otherEndStationId,
    expressStartStationId: sharedCorridor.lineStartStationId,
    expressEndStationId: sharedCorridor.lineEndStationId,
    localEntryOffsetMinutes: secondaryEntryOffsetMinutes,
    localExitOffsetMinutes: secondaryExitOffsetMinutes,
    expressEntryOffsetMinutes: primaryEntryOffsetMinutes,
    expressExitOffsetMinutes: primaryExitOffsetMinutes,
    localRuntimeMinutes: secondaryRuntimeMinutes,
    expressRuntimeMinutes: primaryRuntimeMinutes,
    orderedRun: sharedCorridor.orderedRun,
    physicalOverlap: sharedCorridor.physicalOverlap,
    confidence: clampNumber(sharedCorridor.confidence, 0.3),
    localAdjustable: secondaryAdjustable
  };
}

function buildSharedCorridors(normalizedInput, scenario, lineRuntimeModels) {
  const corridors = [];
  const selectedLineIdSet = scenario.selectedLineIdSet || new Set(asArray(scenario.selectedLineIds).filter(Boolean));
  asArray(normalizedInput.sharedCorridors).forEach((sharedCorridor) => {
    if (!selectedLineIdSet.has(sharedCorridor.lineId) || !selectedLineIdSet.has(sharedCorridor.otherLineId)) {
      return;
    }
    if (sharedCorridor.traversalRelation !== "SameDirection") {
      return;
    }
    if (sharedCorridor.hasMirroredContext) {
      return;
    }
    if (sharedCorridor.orderedRun <= 0 || sharedCorridor.physicalOverlap <= 0) {
      return;
    }

    const orientedCorridor = orientSharedCorridorForScenario(
      normalizedInput,
      scenario,
      sharedCorridor,
      lineRuntimeModels
    );
    if (!orientedCorridor || orientedCorridor.localLineId === orientedCorridor.expressLineId) {
      return;
    }
    corridors.push(orientedCorridor);
  });

  return corridors;
}

export function buildPlanningContext(rawInput, options = {}) {
  const normalizedInput = rawInput?.lineById ? rawInput : normalizePlannerInput(rawInput);
  const draft = pickPlannerDraft(normalizedInput, options);
  const scenario = buildAnalysisScenario(normalizedInput, draft, options);
  const lineRuntimeModels = buildLineRuntimeModels(normalizedInput, scenario, options);
  const corridors = buildSharedCorridors(normalizedInput, scenario, lineRuntimeModels);
  const baseRows = buildWorkingRowsFromScenario(scenario);

  return {
    preparedKind: PREPARED_CONTEXT_KIND,
    normalizedInput,
    draft,
    scenario,
    lineRuntimeModels,
    corridors,
    baseRows
  };
}

export function prepareExpressBypassPlannerContext(rawInput, options = {}) {
  return buildPlanningContext(rawInput, options);
}

function isPreparedPlanningContext(value) {
  return value?.preparedKind === PREPARED_CONTEXT_KIND;
}

function resolvePlanningContext(input, options = {}) {
  return isPreparedPlanningContext(input)
    ? input
    : buildPlanningContext(input, options);
}

function buildTripForDepartureRow(lineRuntimeModel, row, offsetDeltaMinutes = 0) {
  if (!lineRuntimeModel) {
    return {
      tripId: row.id,
      lineId: row.lineId,
      kind: row.kind === "express" ? "express" : "local",
      departureMinute: row.minute + offsetDeltaMinutes,
      departureTime: minutesToTime(row.minute + offsetDeltaMinutes),
      source: row.source,
      note: row.note,
      stationEvents: [],
      intervalWindows: new Map(),
      atomBoundaryMinuteOffsets: [],
      atomBoundaryVariabilityOffsets: []
    };
  }

  const tripMinute = row.minute + offsetDeltaMinutes;
  const stationEvents = lineRuntimeModel.stationOffsets.map((offset) => ({
    stationId: offset.stationId,
    order: offset.order,
    name: offset.name,
    arrivalMinute: tripMinute + offset.arrivalMinute,
    departureMinute: tripMinute + offset.departureMinute,
    dwellMinutes: offset.dwellMinutes,
    skippedStopStartLossMinutes: clampNumber(offset.skippedStopStartLossMinutes, 0)
  }));

  const intervalWindows = new Map();
  asArray(lineRuntimeModel.lineTrack?.protectedIntervals).forEach((interval) => {
    const stationOffset = lineRuntimeModel.stationOffsetsById.get(interval.fromStationId);
    const fallbackEntryOffset = stationOffset ? stationOffset.departureMinute : clampNumber(interval.minEntryOffsetMinutes, 0);
    const runtimeMinutes = estimateIntervalRuntimeMinutes(lineRuntimeModel.lineTrack, interval);
    const entryMinute = tripMinute + fallbackEntryOffset;
    intervalWindows.set(interval.intervalIndex, {
      intervalIndex: interval.intervalIndex,
      sharedKey: toProtectedIntervalKey(interval),
      entryMinute,
      exitMinute: entryMinute + runtimeMinutes,
      runtimeMinutes,
      fromStationId: interval.fromStationId || "",
      toStationId: interval.toStationId || ""
    });
  });

  return {
    tripId: row.id,
    lineId: row.lineId,
    kind: row.kind === "express" ? "express" : "local",
    departureMinute: tripMinute,
    departureTime: minutesToTime(tripMinute),
    source: row.source,
    note: row.note,
    stationEvents,
    intervalWindows,
    atomBoundaryMinuteOffsets: lineRuntimeModel.atomBoundaryMinuteOffsets,
    atomBoundaryVariabilityOffsets: lineRuntimeModel.atomBoundaryVariabilityOffsets
  };
}

export function buildScenarioTrips(normalizedInput, scenario, lineRuntimeModels, offsetDeltaMinutes = 0) {
  return scenario.stagedRows.map((row) => {
    const lineRuntimeModel = lineRuntimeModels.get(row.lineId);
    const appliedOffset = scenario.expressLineIds.includes(row.lineId) ? offsetDeltaMinutes : 0;
    return buildTripForDepartureRow(lineRuntimeModel, row, appliedOffset);
  });
}

function buildAllowedBypassStationIdSet(options = {}) {
  const stationIds = new Set();
  asArray(options.baseBypassStationIds).forEach((stationId) => {
    if (stationId) {
      stationIds.add(stationId);
    }
  });  
  asArray(options.virtualBypassStationIds).forEach((stationId) => {
    if (stationId) {
      stationIds.add(stationId);
    }
  });
  if (options.forcedBypassStationId) {
    stationIds.add(options.forcedBypassStationId);
  }
  asArray(options.allowedBypassStationIds).forEach((stationId) => {
    if (stationId) {
      stationIds.add(stationId);
    }
  });
  return stationIds.size > 0 ? stationIds : null;
}

function collectBypassStationsForCorridor(normalizedInput, lineId, fromStationId, toStationId, allowedStationIdSet = null, options = {}) {
  const stations = normalizedInput.stationsByLineId.get(lineId) || [];
  const fromStation = normalizedInput.stationById.get(fromStationId);
  const toStation = normalizedInput.stationById.get(toStationId);
  const useCandidatePool = options.useCandidateBypassPool === true;
  const stationPool = useCandidatePool
    ? normalizedInput.candidateBypassByLineId.get(lineId) || []
    : normalizedInput.configuredBypassByLineId.get(lineId) || [];
  if (!fromStation || !toStation || stationPool.length === 0) {
    return [];
  }

  const corridorStations = fromStation.order <= toStation.order
    ? stationPool.filter((station) => station.order >= fromStation.order && station.order <= toStation.order)
    : stationPool.filter((station) =>
      station.order >= fromStation.order || station.order <= toStation.order || station.order >= stations.length
    );

  if (!allowedStationIdSet) {
    return corridorStations;
  }

  return corridorStations.filter((station) => allowedStationIdSet.has(station.stationId));
}

function computeDepartureGapPenalty(normalizedInput, trips, localLineIds, expressLineIds, minGapMinutes) {
  const groupedByOriginStation = new Map();
  trips.forEach((trip) => {
    if (!localLineIds.includes(trip.lineId) && !expressLineIds.includes(trip.lineId)) {
      return;
    }

    const line = normalizedInput.lineById.get(trip.lineId);
    const groupKey = line?.originStationId || `line:${trip.lineId}`;
    if (!groupedByOriginStation.has(groupKey)) {
      groupedByOriginStation.set(groupKey, []);
    }
    groupedByOriginStation.get(groupKey).push(trip.departureMinute);
  });

  let penaltyMinutes = 0;
  groupedByOriginStation.forEach((minutes) => {
    minutes.sort((left, right) => left - right);
    for (let i = 1; i < minutes.length; i += 1) {
      const gap = minutes[i] - minutes[i - 1];
      if (gap < minGapMinutes) {
        penaltyMinutes += minGapMinutes - gap;
      }
    }
    if (minutes.length > 1 && minutes[0] !== minutes[minutes.length - 1]) {
      const wrapGap = circularMinuteGap(minutes[0], minutes[minutes.length - 1]);
      if (wrapGap < minGapMinutes) {
        penaltyMinutes += minGapMinutes - wrapGap;
      }
    }
  });

  return penaltyMinutes;
}

function buildWorkingRowsFromScenario(scenario) {
  return sortRowsByMinute(scenario.stagedRows.map((row) => ({
    id: row.id,
    lineId: row.lineId,
    kind: row.kind === "express" ? "express" : "local",
    source: row.source || "planner",
    note: row.note || "",
    minute: row.minute
  })));
}

function generatePeriodicMinutes(windowStartMinute, windowEndMinute, tripsPerHour, anchorMinute = null) {
  if (!Number.isFinite(windowStartMinute) || !Number.isFinite(windowEndMinute) || windowEndMinute <= windowStartMinute) {
    return [];
  }
  if (!(tripsPerHour > 0)) {
    return [];
  }

  const intervalMinutes = 60 / tripsPerHour;
  const minutes = [];
  let firstMinute = windowStartMinute;
  if (Number.isFinite(anchorMinute)) {
    const intervalsFromAnchor = Math.ceil((windowStartMinute - anchorMinute) / intervalMinutes);
    firstMinute = anchorMinute + (Math.max(0, intervalsFromAnchor) * intervalMinutes);
    while (firstMinute - intervalMinutes >= windowStartMinute) {
      firstMinute -= intervalMinutes;
    }
  }
  for (let minute = firstMinute; minute < windowEndMinute; minute += intervalMinutes) {
    if (minute < windowStartMinute) {
      continue;
    }
    minutes.push(Math.round(minute));
  }
  return minutes;
}

function groupLineIdsByOrigin(normalizedInput, lineIds) {
  const groups = new Map();
  asArray(lineIds).forEach((lineId) => {
    const line = normalizedInput.lineById.get(lineId);
    const key = line?.originStationId || `line:${lineId}`;
    if (!groups.has(key)) {
      groups.set(key, []);
    }
    groups.get(key).push(lineId);
  });
  return groups;
}

function ensureMinuteGap(candidateMinute, originStationId, occupiedRows, minGapMinutes) {
  return occupiedRows.every((row) => {
    if ((row.originStationId || "") !== originStationId) {
      return true;
    }
    return circularMinuteGap(row.minute, candidateMinute) >= minGapMinutes;
  });
}

function buildExpressCandidateRows(normalizedInput, scenario, rows, request) {
  if (!(request.expressTripsPerHour > 0)) {
    return rows;
  }

  const windowStartMinute = timeToMinutes(request.expressWindowStart);
  const windowEndMinute = timeToMinutes(request.expressWindowEnd);
  const baseRows = rows.filter((row) => row.kind !== "express" || request.lockedExpressTripIds.has(row.id));
  const occupiedRows = baseRows.map((row) => {
    const line = normalizedInput.lineById.get(row.lineId);
    return {
      minute: row.minute,
      originStationId: line?.originStationId || ""
    };
  });

  const originGroups = groupLineIdsByOrigin(normalizedInput, scenario.expressLineIds);
  let generatedRows = [...baseRows];

  originGroups.forEach((lineIds) => {
    lineIds.forEach((lineId, lineIndex) => {
      const line = normalizedInput.lineById.get(lineId);
      const originStationId = line?.originStationId || "";
      const existingExpressRows = rows
        .filter((row) => row.lineId === lineId && row.kind === "express")
        .sort((left, right) => left.minute - right.minute);
      const existingAnchorMinute = existingExpressRows.length > 0
        ? existingExpressRows[0].minute
        : windowStartMinute;
      const perLineShift = lineIds.length > 1
        ? Math.round((lineIndex * 60) / (request.expressTripsPerHour * lineIds.length))
        : 0;
      const baseMinutes = generatePeriodicMinutes(
        windowStartMinute,
        windowEndMinute,
        request.expressTripsPerHour,
        existingAnchorMinute + perLineShift
      );
      baseMinutes.forEach((baseMinute, generatedIndex) => {
        const candidateMinute = baseMinute;
        if (!ensureMinuteGap(candidateMinute, originStationId, occupiedRows, DEFAULT_MIN_DEPARTURE_GAP_MINUTES)) {
          return;
        }

        const row = {
          id: `joint-express-${lineId}-${generatedIndex}`,
          lineId,
          kind: "express",
          source: "planner",
          note: `planner-tph-${request.expressTripsPerHour}`,
          minute: candidateMinute
        };
        generatedRows.push(row);
        occupiedRows.push({
          minute: candidateMinute,
          originStationId
        });
      });
    });
  });

  return sortRowsByMinute(generatedRows);
}

function buildCorridorWindowForTrip(trip, corridor, role) {
  if (!trip || !corridor) {
    return null;
  }

  const startAtomIndex = role === "local"
    ? corridor.localStartAtomIndex
    : corridor.expressStartAtomIndex;
  const endAtomIndexExclusive = role === "local"
    ? corridor.localEndAtomIndexExclusive
    : corridor.expressEndAtomIndexExclusive;
  if (startAtomIndex < 0 || endAtomIndexExclusive <= startAtomIndex) {
    return null;
  }

  const entryOffsetMinutes = getAtomBoundaryMinuteOffset({ atomBoundaryMinuteOffsets: trip.atomBoundaryMinuteOffsets }, startAtomIndex);
  const exitOffsetMinutes = getAtomBoundaryMinuteOffset({ atomBoundaryMinuteOffsets: trip.atomBoundaryMinuteOffsets }, endAtomIndexExclusive);
  return {
    entryMinute: trip.departureMinute + entryOffsetMinutes,
    exitMinute: trip.departureMinute + exitOffsetMinutes,
    runtimeMinutes: Number(Math.max(0, exitOffsetMinutes - entryOffsetMinutes).toFixed(2))
  };
}

function shouldEvaluateTripPairForCorridor(localWindow, expressWindow, minSharedGapMinutes) {
  if (!localWindow || !expressWindow) {
    return false;
  }

  const entryGapMinutes = expressWindow.entryMinute - localWindow.entryMinute;
  if (!(entryGapMinutes > 0)) {
    return false;
  }
  if (expressWindow.entryMinute >= localWindow.exitMinute) {
    return false;
  }

  const closingCapacityMinutes = Math.max(0, localWindow.runtimeMinutes - expressWindow.runtimeMinutes);
  const safetyMarginMinutes = minSharedGapMinutes + DEFAULT_LOCAL_RETIME_STEP_MINUTES + 6;
  return entryGapMinutes <= closingCapacityMinutes + safetyMarginMinutes;
}

function getCorridorRoleAtomBounds(corridor, role) {
  return role === "local"
    ? {
      startAtomIndex: corridor.localStartAtomIndex,
      endAtomIndexExclusive: corridor.localEndAtomIndexExclusive
    }
    : {
      startAtomIndex: corridor.expressStartAtomIndex,
      endAtomIndexExclusive: corridor.expressEndAtomIndexExclusive
    };
}

function getCorridorRoleStationBounds(corridor, role) {
  return role === "local"
    ? {
      startStationId: corridor.localStartStationId,
      endStationId: corridor.localEndStationId
    }
    : {
      startStationId: corridor.expressStartStationId,
      endStationId: corridor.expressEndStationId
    };
}

function mapCorridorAxisToAtomIndex(corridor, role, axisIndex, axisSampleCount) {
  const bounds = getCorridorRoleAtomBounds(corridor, role);
  const lengthAtoms = Math.max(1, bounds.endAtomIndexExclusive - bounds.startAtomIndex);
  const ratio = axisSampleCount <= 0 ? 0 : axisIndex / axisSampleCount;
  const atomOffset = Math.round(lengthAtoms * ratio);
  return Math.max(
    bounds.startAtomIndex,
    Math.min(bounds.endAtomIndexExclusive, bounds.startAtomIndex + atomOffset)
  );
}

function getCorridorAtomSampleCount(corridor) {
  const localLength = Math.max(1, corridor.localEndAtomIndexExclusive - corridor.localStartAtomIndex);
  const expressLength = Math.max(1, corridor.expressEndAtomIndexExclusive - corridor.expressStartAtomIndex);
  const overlap = Math.max(1, Math.round(corridor.physicalOverlap || 0));
  return Math.max(1, Math.min(Math.max(localLength, expressLength), overlap));
}

function estimatePursuitCurveSampleCount(corridor) {
  const atomSampleCount = getCorridorAtomSampleCount(corridor);
  const runtimeMinutes = Math.max(
    clampNumber(corridor.localRuntimeMinutes, 0),
    clampNumber(corridor.expressRuntimeMinutes, 0)
  );
  if (!(runtimeMinutes > 0)) {
    return atomSampleCount;
  }

  const runtimeSampleCount = Math.ceil(runtimeMinutes / DEFAULT_PURSUIT_CURVE_SAMPLE_STEP_MINUTES);
  return Math.max(2, Math.min(atomSampleCount, runtimeSampleCount));
}

function getCorridorAxisSampleCount(corridor) {
  const configuredSampleCount = Math.round(clampNumber(corridor?.axisSampleCount, 0));
  return configuredSampleCount > 0
    ? configuredSampleCount
    : getCorridorAtomSampleCount(corridor);
}

function buildTripCorridorCurve(trip, corridor, role) {
  if (!trip || !corridor) {
    return null;
  }

  const axisSampleCount = getCorridorAxisSampleCount(corridor);
  const samples = [];
  for (let axisIndex = 0; axisIndex <= axisSampleCount; axisIndex += 1) {
    const atomIndex = mapCorridorAxisToAtomIndex(corridor, role, axisIndex, axisSampleCount);
    const minuteOffset = getAtomBoundaryMinuteOffset(
      { atomBoundaryMinuteOffsets: trip.atomBoundaryMinuteOffsets },
      atomIndex
    );
    const variabilityMinutes = getAtomBoundaryVariabilityOffset(
      { atomBoundaryVariabilityOffsets: trip.atomBoundaryVariabilityOffsets },
      atomIndex
    );
    samples.push({
      axisIndex,
      atomIndex,
      minute: Number((trip.departureMinute + minuteOffset).toFixed(4)),
      variabilityMinutes: Number(variabilityMinutes.toFixed(4))
    });
  }

  return {
    tripId: trip.tripId,
    corridorId: corridor.corridorId,
    role,
    axisSampleCount,
    samples
  };
}

function computeCorridorGapProfile(localCurve, expressCurve) {
  if (!localCurve || !expressCurve) {
    return null;
  }

  const sampleCount = Math.min(localCurve.samples.length, expressCurve.samples.length);
  if (sampleCount <= 0) {
    return null;
  }

  const samples = [];
  let minGapMinutes = Number.POSITIVE_INFINITY;
  let minGapAxisIndex = -1;
  let minGapMinute = 0;
  let minGapUncertaintyMinutes = 0;
  for (let sampleIndex = 0; sampleIndex < sampleCount; sampleIndex += 1) {
    const localSample = localCurve.samples[sampleIndex];
    const expressSample = expressCurve.samples[sampleIndex];
    const gapMinutes = Number((expressSample.minute - localSample.minute).toFixed(4));
    const minute = Number(((localSample.minute + expressSample.minute) * 0.5).toFixed(4));
    const uncertaintyMinutes = Number((clampNumber(localSample.variabilityMinutes, 0) + clampNumber(expressSample.variabilityMinutes, 0)).toFixed(4));
    samples.push({
      axisIndex: sampleIndex,
      minute,
      localMinute: localSample.minute,
      expressMinute: expressSample.minute,
      gapMinutes,
      uncertaintyMinutes
    });

    if (gapMinutes < minGapMinutes) {
      minGapMinutes = gapMinutes;
      minGapAxisIndex = sampleIndex;
      minGapMinute = minute;
      minGapUncertaintyMinutes = uncertaintyMinutes;
    }
  }

  return {
    samples,
    entryGapMinutes: Number(samples[0].gapMinutes.toFixed(2)),
    exitGapMinutes: Number(samples[samples.length - 1].gapMinutes.toFixed(2)),
    minGapMinutes: Number(minGapMinutes.toFixed(2)),
    minGapUncertaintyMinutes: Number(minGapUncertaintyMinutes.toFixed(2)),
    minGapAxisIndex,
    minGapMinute: Number(minGapMinute.toFixed(2))
  };
}

function findCatchupPoint(gapProfile, minSharedGapMinutes) {
  if (!gapProfile || !gapProfile.samples.length) {
    return null;
  }

  if (gapProfile.entryGapMinutes <= 0) {
    return null;
  }

  const severityMinutes = Math.max(0, minSharedGapMinutes - gapProfile.minGapMinutes);
  const effectiveUncertaintyMinutes = clampNumber(gapProfile.minGapUncertaintyMinutes, 0) * 0.5;
  const worstCaseGapMinutes = Number((gapProfile.minGapMinutes - effectiveUncertaintyMinutes).toFixed(2));
  const robustnessRiskMinutes = Number(Math.max(0, minSharedGapMinutes - worstCaseGapMinutes).toFixed(2));
  const didCatchUp = gapProfile.minGapMinutes <= 0;
  const didThreaten = severityMinutes > 0;
  const didRobustnessThreaten = robustnessRiskMinutes > 0
    && gapProfile.minGapMinutes <= minSharedGapMinutes + DEFAULT_LOCAL_RETIME_STEP_MINUTES;
  if (!didCatchUp && !didThreaten && !didRobustnessThreaten) {
    return null;
  }

  return {
    catchupAxisIndex: gapProfile.minGapAxisIndex,
    catchupMinute: gapProfile.minGapMinute,
    minGapMinutes: gapProfile.minGapMinutes,
    minGapUncertaintyMinutes: gapProfile.minGapUncertaintyMinutes,
    entryGapMinutes: gapProfile.entryGapMinutes,
    exitGapMinutes: gapProfile.exitGapMinutes,
    closingMinutes: Number((gapProfile.entryGapMinutes - gapProfile.minGapMinutes).toFixed(2)),
    severityMinutes: Number(severityMinutes.toFixed(2)),
    worstCaseGapMinutes,
    robustnessRiskMinutes,
    didCatchUp
  };
}

function buildStationEventById(trip) {
  const stationEventById = new Map();
  asArray(trip?.stationEvents).forEach((stationEvent) => {
    stationEventById.set(stationEvent.stationId, stationEvent);
  });
  return stationEventById;
}

function findAxisIndexForLocalStation(normalizedInput, corridor, stationId) {
  const station = normalizedInput.stationById.get(stationId);
  if (!station) {
    return -1;
  }

  const axisSampleCount = getCorridorAxisSampleCount(corridor);
  const localLength = Math.max(1, corridor.localEndAtomIndexExclusive - corridor.localStartAtomIndex);
  const stationOffset = station.trackAtomIndex - corridor.localStartAtomIndex;
  if (station.stationId === corridor.localStartStationId || station.id === corridor.localStartStationId) {
    return 0;
  }
  if (stationOffset < 0 || station.trackAtomIndex > corridor.localEndAtomIndexExclusive) {
    return -1;
  }

  const ratio = stationOffset / localLength;
  return Math.max(0, Math.min(axisSampleCount, Math.round(axisSampleCount * ratio)));
}

function evaluateBypassStationForCatchup(normalizedInput, catchupPoint, corridor, gapProfile, localTrip, expressTrip, station) {
  if (!station || !catchupPoint || !gapProfile) {
    return null;
  }

  const axisIndex = findAxisIndexForLocalStation(normalizedInput, corridor, station.stationId);
  if (axisIndex < 0 || axisIndex >= gapProfile.samples.length) {
    return null;
  }

  if (axisIndex > catchupPoint.catchupAxisIndex) {
    return null;
  }

  const gapAtStationMinutes = gapProfile.samples[axisIndex].gapMinutes;
  const holdNeededMinutes = Number(Math.max(0, catchupPoint.severityMinutes - Math.max(0, gapAtStationMinutes - catchupPoint.minGapMinutes)).toFixed(2));
  const localStationEvent = buildStationEventById(localTrip).get(station.stationId) || null;
  const expressMinute = gapProfile.samples[axisIndex].expressMinute;
  const localMinute = gapProfile.samples[axisIndex].localMinute;
  return {
    stationId: station.stationId,
    lineId: station.lineId,
    name: station.name,
    order: station.order,
    axisIndex,
    gapAtStationMinutes: Number(gapAtStationMinutes.toFixed(2)),
    holdNeededMinutes,
    localStationMinute: Number(localMinute.toFixed(2)),
    expressStationMinute: Number(expressMinute.toFixed(2)),
    stationDepartureMinute: localStationEvent ? Number(localStationEvent.departureMinute.toFixed(2)) : Number(localMinute.toFixed(2))
  };
}

function pickBestBypassStationEvaluation(evaluations, holdBudgetMinutes) {
  const usable = evaluations
    .filter(Boolean)
    .sort((left, right) => {
      if (left.holdNeededMinutes !== right.holdNeededMinutes) {
        return left.holdNeededMinutes - right.holdNeededMinutes;
      }
      return left.axisIndex - right.axisIndex;
    });
  if (usable.length === 0) {
    return null;
  }

  const best = usable[0];
  return {
    ...best,
    holdBudgetMinutes: Number(holdBudgetMinutes.toFixed(2)),
    withinHoldBudget: best.holdNeededMinutes <= holdBudgetMinutes
  };
}

function mergeCatchupEvents(events) {
  const mergedByKey = new Map();
  events.forEach((event) => {
    const key = `${event.localTripId}|${event.expressTripId}|${event.localLineId}|${event.expressLineId}`;
    if (!mergedByKey.has(key)) {
      mergedByKey.set(key, {
        ...event,
        corridorIds: asArray(event.sourceCorridorIds).length > 0
          ? [...event.sourceCorridorIds]
          : [event.corridorId],
        corridorFromStationIds: [event.corridorFromStationId].filter(Boolean),
        corridorToStationIds: [event.corridorToStationId].filter(Boolean),
        configuredBypassStations: [...event.configuredBypassStations]
      });
      return;
    }

    const current = mergedByKey.get(key);
    current.corridorIds = [...new Set([
      ...current.corridorIds,
      ...(asArray(event.sourceCorridorIds).length > 0 ? event.sourceCorridorIds : [event.corridorId])
    ])];
    current.corridorFromStationIds = [...new Set([...current.corridorFromStationIds, event.corridorFromStationId].filter(Boolean))];
    current.corridorToStationIds = [...new Set([...current.corridorToStationIds, event.corridorToStationId].filter(Boolean))];

    const stationMap = new Map(current.configuredBypassStations.map((station) => [station.stationId, station]));
    event.configuredBypassStations.forEach((station) => {
      stationMap.set(station.stationId, station);
    });
    current.configuredBypassStations = [...stationMap.values()];

    if (event.severityMinutes > current.severityMinutes) {
      current.corridorId = event.corridorId;
      current.sharedKey = event.sharedKey;
      current.corridorFromStationId = event.corridorFromStationId;
      current.corridorToStationId = event.corridorToStationId;
      current.localEntryMinute = event.localEntryMinute;
      current.expressEntryMinute = event.expressEntryMinute;
      current.localExitMinute = event.localExitMinute;
      current.expressExitMinute = event.expressExitMinute;
      current.gapAtEntryMinutes = event.gapAtEntryMinutes;
      current.gapAtExitMinutes = event.gapAtExitMinutes;
      current.closingMinutes = event.closingMinutes;
      current.expectedGapMinutes = event.expectedGapMinutes;
      current.severityMinutes = event.severityMinutes;
      current.requiredHoldMinutes = event.requiredHoldMinutes;
      current.holdBudgetMinutes = event.holdBudgetMinutes;
      current.withinHoldBudget = event.withinHoldBudget;
      current.resolvedHoldMinutes = event.resolvedHoldMinutes;
      current.unresolvedRiskMinutes = event.unresolvedRiskMinutes;
      current.expressSavedMinutes = event.expressSavedMinutes;
      current.catchupAxisIndex = event.catchupAxisIndex;
      current.catchupMinute = event.catchupMinute;
      current.minGapMinutes = event.minGapMinutes;
      current.worstCaseGapMinutes = event.worstCaseGapMinutes;
      current.minGapUncertaintyMinutes = event.minGapUncertaintyMinutes;
      current.didCatchUp = event.didCatchUp;
      current.robustnessRiskMinutes = event.robustnessRiskMinutes;
      current.selectedBypassStation = event.selectedBypassStation;
      current.confidence = Math.max(current.confidence, event.confidence);
    } else {
      current.expressSavedMinutes = Number((current.expressSavedMinutes + event.expressSavedMinutes).toFixed(2));
      current.unresolvedRiskMinutes = Number(Math.max(current.unresolvedRiskMinutes, event.unresolvedRiskMinutes).toFixed(2));
      current.resolvedHoldMinutes = Number(Math.max(current.resolvedHoldMinutes, event.resolvedHoldMinutes).toFixed(2));
      current.robustnessRiskMinutes = Number(Math.max(
        clampNumber(current.robustnessRiskMinutes, 0),
        clampNumber(event.robustnessRiskMinutes, 0)
      ).toFixed(2));
    }
  });

  return [...mergedByKey.values()].sort((left, right) => {
    if (right.severityMinutes !== left.severityMinutes) {
      return right.severityMinutes - left.severityMinutes;
    }
    return left.expressEntryMinute - right.expressEntryMinute;
  });
}

function buildTrunkGroups(corridors) {
  const groupedByLinePair = new Map();
  corridors.forEach((corridor) => {
    const key = toOrderedLinePairKey(corridor.localLineId, corridor.expressLineId);
    if (!groupedByLinePair.has(key)) {
      groupedByLinePair.set(key, []);
    }
    groupedByLinePair.get(key).push(corridor);
  });

  const trunkGroups = [];
  const trunkGroupByCorridorId = new Map();
  groupedByLinePair.forEach((pairCorridors, pairKey) => {
    const sorted = [...pairCorridors].sort((left, right) => {
      if (left.localStartAtomIndex !== right.localStartAtomIndex) {
        return left.localStartAtomIndex - right.localStartAtomIndex;
      }
      return left.expressStartAtomIndex - right.expressStartAtomIndex;
    });

    let currentGroup = null;
    sorted.forEach((corridor, corridorIndex) => {
      const startsNewGroup = !currentGroup
        || corridor.localStartAtomIndex > currentGroup.localEndAtomIndexExclusive + DEFAULT_PURSUIT_TRUNK_MERGE_GAP_ATOMS
        || corridor.expressStartAtomIndex > currentGroup.expressEndAtomIndexExclusive + DEFAULT_PURSUIT_TRUNK_MERGE_GAP_ATOMS;
      if (startsNewGroup) {
        currentGroup = {
          trunkKey: `${pairKey}|trunk-group-${trunkGroups.filter((group) => toOrderedLinePairKey(group.localLineId, group.expressLineId) === pairKey).length}`,
          localLineId: corridor.localLineId,
          expressLineId: corridor.expressLineId,
          yieldingLineId: corridor.localLineId,
          priorityLineId: corridor.expressLineId,
          corridorIds: [],
          localStartAtomIndex: corridor.localStartAtomIndex,
          localEndAtomIndexExclusive: corridor.localEndAtomIndexExclusive,
          expressStartAtomIndex: corridor.expressStartAtomIndex,
          expressEndAtomIndexExclusive: corridor.expressEndAtomIndexExclusive,
          corridorFromStationIds: [],
          corridorToStationIds: []
        };
        trunkGroups.push(currentGroup);
      }

      currentGroup.corridorIds.push(corridor.corridorId);
      currentGroup.localStartAtomIndex = Math.min(currentGroup.localStartAtomIndex, corridor.localStartAtomIndex);
      currentGroup.localEndAtomIndexExclusive = Math.max(currentGroup.localEndAtomIndexExclusive, corridor.localEndAtomIndexExclusive);
      currentGroup.expressStartAtomIndex = Math.min(currentGroup.expressStartAtomIndex, corridor.expressStartAtomIndex);
      currentGroup.expressEndAtomIndexExclusive = Math.max(currentGroup.expressEndAtomIndexExclusive, corridor.expressEndAtomIndexExclusive);
      if (corridor.localStartStationId) {
        currentGroup.corridorFromStationIds.push(corridor.localStartStationId);
      }
      if (corridor.localEndStationId) {
        currentGroup.corridorToStationIds.push(corridor.localEndStationId);
      }
      trunkGroupByCorridorId.set(corridor.corridorId, currentGroup);
    });
  });

  trunkGroups.forEach((group) => {
    group.corridorFromStationIds = [...new Set(group.corridorFromStationIds)];
    group.corridorToStationIds = [...new Set(group.corridorToStationIds)];
    trunkGroupByCorridorId.set(group.trunkKey, group);
  });

  return {
    trunkGroups,
    trunkGroupByCorridorId
  };
}

function buildPursuitTrunkCorridors(corridors) {
  if (Array.isArray(corridors) && pursuitTrunkCorridorCache.has(corridors)) {
    return pursuitTrunkCorridorCache.get(corridors);
  }

  const trunkGrouping = buildTrunkGroups(corridors);
  const corridorById = new Map(asArray(corridors).map((corridor) => [corridor.corridorId, corridor]));
  const pursuitCorridors = [];

  trunkGrouping.trunkGroups.forEach((group) => {
    const groupCorridors = group.corridorIds
      .map((corridorId) => corridorById.get(corridorId))
      .filter(Boolean)
      .sort((left, right) => {
        if (left.localStartAtomIndex !== right.localStartAtomIndex) {
          return left.localStartAtomIndex - right.localStartAtomIndex;
        }
        return left.expressStartAtomIndex - right.expressStartAtomIndex;
    });
    if (groupCorridors.length === 0) {
      return;
    }
    if (groupCorridors.length === 1) {
      const singleCorridor = {
        ...groupCorridors[0],
        sourceCorridorIds: [groupCorridors[0].corridorId]
      };
      singleCorridor.axisSampleCount = estimatePursuitCurveSampleCount(singleCorridor);
      pursuitCorridors.push(singleCorridor);
      return;
    }

    const localStartCorridor = groupCorridors.reduce((best, corridor) =>
      corridor.localStartAtomIndex < best.localStartAtomIndex ? corridor : best
    );
    const localEndCorridor = groupCorridors.reduce((best, corridor) =>
      corridor.localEndAtomIndexExclusive > best.localEndAtomIndexExclusive ? corridor : best
    );
    const expressStartCorridor = groupCorridors.reduce((best, corridor) =>
      corridor.expressStartAtomIndex < best.expressStartAtomIndex ? corridor : best
    );
    const expressEndCorridor = groupCorridors.reduce((best, corridor) =>
      corridor.expressEndAtomIndexExclusive > best.expressEndAtomIndexExclusive ? corridor : best
    );
    const localRuntimeMinutes = Math.max(
      0,
      localEndCorridor.localExitOffsetMinutes - localStartCorridor.localEntryOffsetMinutes
    );
    const expressRuntimeMinutes = Math.max(
      0,
      expressEndCorridor.expressExitOffsetMinutes - expressStartCorridor.expressEntryOffsetMinutes
    );
    const localSpanAtoms = Math.max(1, group.localEndAtomIndexExclusive - group.localStartAtomIndex);
    const expressSpanAtoms = Math.max(1, group.expressEndAtomIndexExclusive - group.expressStartAtomIndex);
    const confidence = groupCorridors.reduce(
      (sum, corridor) => sum + clampNumber(corridor.confidence, 0.3),
      0
    ) / groupCorridors.length;

    const pursuitCorridor = {
      corridorId: group.trunkKey,
      sharedKey: group.trunkKey,
      sourceCorridorIds: [...group.corridorIds],
      localLineId: group.localLineId,
      expressLineId: group.expressLineId,
      yieldingLineId: group.localLineId,
      priorityLineId: group.expressLineId,
      localStartAtomIndex: group.localStartAtomIndex,
      localEndAtomIndexExclusive: group.localEndAtomIndexExclusive,
      expressStartAtomIndex: group.expressStartAtomIndex,
      expressEndAtomIndexExclusive: group.expressEndAtomIndexExclusive,
      localStartStationId: localStartCorridor.localStartStationId,
      localEndStationId: localEndCorridor.localEndStationId,
      expressStartStationId: expressStartCorridor.expressStartStationId,
      expressEndStationId: expressEndCorridor.expressEndStationId,
      localEntryOffsetMinutes: localStartCorridor.localEntryOffsetMinutes,
      localExitOffsetMinutes: localEndCorridor.localExitOffsetMinutes,
      expressEntryOffsetMinutes: expressStartCorridor.expressEntryOffsetMinutes,
      expressExitOffsetMinutes: expressEndCorridor.expressExitOffsetMinutes,
      localRuntimeMinutes: Number(localRuntimeMinutes.toFixed(2)),
      expressRuntimeMinutes: Number(expressRuntimeMinutes.toFixed(2)),
      orderedRun: Math.max(localSpanAtoms, expressSpanAtoms),
      physicalOverlap: Math.min(localSpanAtoms, expressSpanAtoms),
      confidence: Number(confidence.toFixed(2))
    };
    pursuitCorridor.axisSampleCount = estimatePursuitCurveSampleCount(pursuitCorridor);
    pursuitCorridors.push(pursuitCorridor);
  });

  if (Array.isArray(corridors)) {
    pursuitTrunkCorridorCache.set(corridors, pursuitCorridors);
  }
  return pursuitCorridors;
}

function buildTrunkProblemClusters(catchupEvents, trunkGrouping) {
  const clustersByKey = new Map();
  catchupEvents.forEach((event) => {
    const trunkGroup = trunkGrouping.trunkGroupByCorridorId.get(event.corridorId);
    const key = trunkGroup?.trunkKey || `${event.localLineId}|${event.expressLineId}|fallback|${event.corridorId}`;
    if (!clustersByKey.has(key)) {
      clustersByKey.set(key, {
        clusterId: key,
        trunkKey: key,
        localLineId: event.localLineId,
        expressLineId: event.expressLineId,
        yieldingLineId: event.localLineId,
        priorityLineId: event.expressLineId,
        corridorIds: [...new Set(trunkGroup?.corridorIds || [event.corridorId])],
        localStartAtomIndex: trunkGroup?.localStartAtomIndex ?? -1,
        localEndAtomIndexExclusive: trunkGroup?.localEndAtomIndexExclusive ?? -1,
        expressStartAtomIndex: trunkGroup?.expressStartAtomIndex ?? -1,
        expressEndAtomIndexExclusive: trunkGroup?.expressEndAtomIndexExclusive ?? -1,
        corridorFromStationIds: [...new Set(trunkGroup?.corridorFromStationIds || [event.corridorFromStationId].filter(Boolean))],
        corridorToStationIds: [...new Set(trunkGroup?.corridorToStationIds || [event.corridorToStationId].filter(Boolean))],
        catchupIds: [...new Set([event.catchupId])],
        localTripIds: [...new Set([event.localTripId])],
        expressTripIds: [...new Set([event.expressTripId])],
        occurrenceCount: 1,
        firstCatchupMinute: event.catchupMinute,
        lastCatchupMinute: event.catchupMinute,
        dominantCatchupMinute: event.catchupMinute,
        dominantSeverityMinutes: event.severityMinutes,
        totalExpressSavedMinutes: event.expressSavedMinutes,
        totalLocalExtraWaitMinutes: event.resolvedHoldMinutes,
        totalUnresolvedRiskMinutes: event.unresolvedRiskMinutes,
        totalRobustnessRiskMinutes: clampNumber(event.robustnessRiskMinutes, 0),
        recommendedBypassStation: event.selectedBypassStation || null,
        candidateBypassStations: [...event.usableBypassStations]
      });
      return;
    }

    const cluster = clustersByKey.get(key);
    cluster.catchupIds = [...new Set([...cluster.catchupIds, event.catchupId])];
    cluster.localTripIds = [...new Set([...cluster.localTripIds, event.localTripId])];
    cluster.expressTripIds = [...new Set([...cluster.expressTripIds, event.expressTripId])];
    cluster.occurrenceCount += 1;
    cluster.firstCatchupMinute = Math.min(cluster.firstCatchupMinute, event.catchupMinute);
    cluster.lastCatchupMinute = Math.max(cluster.lastCatchupMinute, event.catchupMinute);
    cluster.totalExpressSavedMinutes = Number((cluster.totalExpressSavedMinutes + event.expressSavedMinutes).toFixed(2));
    cluster.totalLocalExtraWaitMinutes = Number((cluster.totalLocalExtraWaitMinutes + event.resolvedHoldMinutes).toFixed(2));
    cluster.totalUnresolvedRiskMinutes = Number((cluster.totalUnresolvedRiskMinutes + event.unresolvedRiskMinutes).toFixed(2));
    cluster.totalRobustnessRiskMinutes = Number((cluster.totalRobustnessRiskMinutes + clampNumber(event.robustnessRiskMinutes, 0)).toFixed(2));

    const stationMap = new Map(cluster.candidateBypassStations.map((station) => [station.stationId, station]));
    event.usableBypassStations.forEach((station) => {
      stationMap.set(station.stationId, station);
    });
    cluster.candidateBypassStations = [...stationMap.values()];

    if (event.severityMinutes > cluster.dominantSeverityMinutes) {
      cluster.dominantSeverityMinutes = event.severityMinutes;
      cluster.dominantCatchupMinute = event.catchupMinute;
      cluster.recommendedBypassStation = event.selectedBypassStation || cluster.recommendedBypassStation;
    }
  });

  return [...clustersByKey.values()].sort((left, right) => {
    if (right.totalUnresolvedRiskMinutes !== left.totalUnresolvedRiskMinutes) {
      return right.totalUnresolvedRiskMinutes - left.totalUnresolvedRiskMinutes;
    }
    if (right.dominantSeverityMinutes !== left.dominantSeverityMinutes) {
      return right.dominantSeverityMinutes - left.dominantSeverityMinutes;
    }
    return left.firstCatchupMinute - right.firstCatchupMinute;
  });
}

function buildStringSet(values) {
  return new Set(asArray(values).filter(Boolean));
}

function setsIntersect(leftValues, rightValues) {
  const left = leftValues instanceof Set ? leftValues : buildStringSet(leftValues);
  const right = rightValues instanceof Set ? rightValues : buildStringSet(rightValues);
  if (left.size === 0 || right.size === 0) {
    return false;
  }
  for (const value of left) {
    if (right.has(value)) {
      return true;
    }
  }
  return false;
}

function clusterWindowsOverlap(left, right, paddingMinutes = DEFAULT_REGION_LINK_PADDING_MINUTES) {
  return clampNumber(left.firstCatchupMinute, 0) <= clampNumber(right.lastCatchupMinute, 0) + paddingMinutes
    && clampNumber(right.firstCatchupMinute, 0) <= clampNumber(left.lastCatchupMinute, 0) + paddingMinutes;
}

function clustersBelongToSameOptimizationRegion(left, right) {
  if (!left || !right || left.clusterId === right.clusterId) {
    return false;
  }

  const leftLocalTripIds = buildStringSet(left.localTripIds);
  const rightLocalTripIds = buildStringSet(right.localTripIds);
  const leftExpressTripIds = buildStringSet(left.expressTripIds);
  const rightExpressTripIds = buildStringSet(right.expressTripIds);
  const leftCandidateStationIds = buildStringSet(asArray(left.candidateBypassStations).map((station) => station.stationId));
  const rightCandidateStationIds = buildStringSet(asArray(right.candidateBypassStations).map((station) => station.stationId));
  const leftBoundaryStationIds = buildStringSet([
    ...asArray(left.corridorFromStationIds),
    ...asArray(left.corridorToStationIds)
  ]);
  const rightBoundaryStationIds = buildStringSet([
    ...asArray(right.corridorFromStationIds),
    ...asArray(right.corridorToStationIds)
  ]);
  const overlappingWindow = clusterWindowsOverlap(left, right);
  const closeInTime = Math.abs(clampNumber(left.dominantCatchupMinute, 0) - clampNumber(right.dominantCatchupMinute, 0))
    <= DEFAULT_MAX_REGION_SPAN_MINUTES;

  return setsIntersect(leftExpressTripIds, rightExpressTripIds)
    || setsIntersect(leftLocalTripIds, rightLocalTripIds)
    || (left.localLineId === right.localLineId && overlappingWindow && closeInTime)
    || (left.expressLineId === right.expressLineId && overlappingWindow && closeInTime)
    || setsIntersect(leftCandidateStationIds, rightCandidateStationIds)
    || (overlappingWindow && setsIntersect(leftBoundaryStationIds, rightBoundaryStationIds));
}

function createOptimizationRegion(seedCluster, index) {
  const halfSpanMinutes = DEFAULT_MAX_REGION_SPAN_MINUTES / 2;
  const dominantCatchupMinute = clampNumber(seedCluster.dominantCatchupMinute, 0);
  return {
    regionId: `region-${index}`,
    clusterIds: [seedCluster.clusterId],
    localLineIds: [...new Set([seedCluster.localLineId].filter(Boolean))],
    expressLineIds: [...new Set([seedCluster.expressLineId].filter(Boolean))],
    localTripIds: [...new Set(asArray(seedCluster.localTripIds).filter(Boolean))],
    expressTripIds: [...new Set(asArray(seedCluster.expressTripIds).filter(Boolean))],
    candidateBypassStationIds: [...new Set(asArray(seedCluster.candidateBypassStations).map((station) => station.stationId).filter(Boolean))],
    corridorFromStationIds: [...new Set(asArray(seedCluster.corridorFromStationIds).filter(Boolean))],
    corridorToStationIds: [...new Set(asArray(seedCluster.corridorToStationIds).filter(Boolean))],
    eventWindowStartMinute: clampNumber(seedCluster.firstCatchupMinute, 0),
    eventWindowEndMinute: clampNumber(seedCluster.lastCatchupMinute, 0),
    windowStartMinute: Math.max(0, Number((dominantCatchupMinute - halfSpanMinutes).toFixed(2))),
    windowEndMinute: Number((dominantCatchupMinute + halfSpanMinutes).toFixed(2)),
    dominantSeverityMinutes: clampNumber(seedCluster.dominantSeverityMinutes, 0),
    totalUnresolvedRiskMinutes: clampNumber(seedCluster.totalUnresolvedRiskMinutes, 0)
  };
}

function absorbClusterIntoOptimizationRegion(region, cluster) {
  region.clusterIds = [...new Set([...region.clusterIds, cluster.clusterId])];
  region.localLineIds = [...new Set([...region.localLineIds, cluster.localLineId].filter(Boolean))];
  region.expressLineIds = [...new Set([...region.expressLineIds, cluster.expressLineId].filter(Boolean))];
  region.localTripIds = [...new Set([...region.localTripIds, ...asArray(cluster.localTripIds).filter(Boolean)])];
  region.expressTripIds = [...new Set([...region.expressTripIds, ...asArray(cluster.expressTripIds).filter(Boolean)])];
  region.candidateBypassStationIds = [...new Set([
    ...region.candidateBypassStationIds,
    ...asArray(cluster.candidateBypassStations).map((station) => station.stationId).filter(Boolean)
  ])];
  region.corridorFromStationIds = [...new Set([...region.corridorFromStationIds, ...asArray(cluster.corridorFromStationIds).filter(Boolean)])];
  region.corridorToStationIds = [...new Set([...region.corridorToStationIds, ...asArray(cluster.corridorToStationIds).filter(Boolean)])];
  region.eventWindowStartMinute = Math.min(
    region.eventWindowStartMinute,
    clampNumber(cluster.firstCatchupMinute, region.eventWindowStartMinute)
  );
  region.eventWindowEndMinute = Math.max(
    region.eventWindowEndMinute,
    clampNumber(cluster.lastCatchupMinute, region.eventWindowEndMinute)
  );
  const halfSpanMinutes = DEFAULT_MAX_REGION_SPAN_MINUTES / 2;
  const clusterWindowStartMinute = Math.max(0, clampNumber(cluster.dominantCatchupMinute, 0) - halfSpanMinutes);
  const clusterWindowEndMinute = clampNumber(cluster.dominantCatchupMinute, 0) + halfSpanMinutes;
  region.windowStartMinute = Math.min(region.windowStartMinute, Number(clusterWindowStartMinute.toFixed(2)));
  region.windowEndMinute = Math.max(region.windowEndMinute, Number(clusterWindowEndMinute.toFixed(2)));
  region.dominantSeverityMinutes = Math.max(region.dominantSeverityMinutes, clampNumber(cluster.dominantSeverityMinutes, 0));
  region.totalUnresolvedRiskMinutes = Number((region.totalUnresolvedRiskMinutes + clampNumber(cluster.totalUnresolvedRiskMinutes, 0)).toFixed(2));
}

function splitOptimizationRegionByTime(region, clusterById) {
  const regionClusters = asArray(region.clusterIds)
    .map((clusterId) => clusterById.get(clusterId))
    .filter(Boolean)
    .sort((left, right) => {
      if (left.firstCatchupMinute !== right.firstCatchupMinute) {
        return left.firstCatchupMinute - right.firstCatchupMinute;
      }
      return left.clusterId.localeCompare(right.clusterId);
    });
  if (regionClusters.length <= 1) {
    return [region];
  }

  const splitRegions = [];
  let currentRegion = createOptimizationRegion(regionClusters[0], 0);

  for (let index = 1; index < regionClusters.length; index += 1) {
    const cluster = regionClusters[index];
    const nextSpanMinutes = Math.max(
      currentRegion.windowEndMinute,
      clampNumber(cluster.lastCatchupMinute, currentRegion.windowEndMinute)
    ) - Math.min(
      currentRegion.windowStartMinute,
      clampNumber(cluster.firstCatchupMinute, currentRegion.windowStartMinute)
    );
    const closeEnoughInTime = clusterWindowsOverlap(
      {
        firstCatchupMinute: currentRegion.windowStartMinute,
        lastCatchupMinute: currentRegion.windowEndMinute
      },
      cluster,
      DEFAULT_REGION_LINK_PADDING_MINUTES
    );
    if (nextSpanMinutes > DEFAULT_MAX_REGION_SPAN_MINUTES || !closeEnoughInTime) {
      splitRegions.push(currentRegion);
      currentRegion = createOptimizationRegion(cluster, 0);
      continue;
    }
    absorbClusterIntoOptimizationRegion(currentRegion, cluster);
  }

  splitRegions.push(currentRegion);
  return splitRegions;
}

function buildOptimizationRegions(trunkProblemClusters) {
  const clusters = asArray(trunkProblemClusters);
  const adjacency = new Map();
  clusters.forEach((cluster) => {
    adjacency.set(cluster.clusterId, new Set());
  });

  for (let leftIndex = 0; leftIndex < clusters.length; leftIndex += 1) {
    for (let rightIndex = leftIndex + 1; rightIndex < clusters.length; rightIndex += 1) {
      const left = clusters[leftIndex];
      const right = clusters[rightIndex];
      if (!clustersBelongToSameOptimizationRegion(left, right)) {
        continue;
      }
      adjacency.get(left.clusterId).add(right.clusterId);
      adjacency.get(right.clusterId).add(left.clusterId);
    }
  }

  const clusterById = new Map(clusters.map((cluster) => [cluster.clusterId, cluster]));
  const visited = new Set();
  const componentRegions = [];

  clusters.forEach((cluster) => {
    if (visited.has(cluster.clusterId)) {
      return;
    }

    const queue = [cluster.clusterId];
    visited.add(cluster.clusterId);
    const region = createOptimizationRegion(cluster, componentRegions.length);

    while (queue.length > 0) {
      const currentClusterId = queue.shift();
      const currentCluster = clusterById.get(currentClusterId);
      if (!currentCluster) {
        continue;
      }
      if (currentClusterId !== cluster.clusterId) {
        absorbClusterIntoOptimizationRegion(region, currentCluster);
      }

      const neighbors = adjacency.get(currentClusterId) || new Set();
      neighbors.forEach((neighborClusterId) => {
        if (visited.has(neighborClusterId)) {
          return;
        }
        visited.add(neighborClusterId);
        queue.push(neighborClusterId);
      });
    }

    componentRegions.push(region);
  });

  const regions = componentRegions
    .flatMap((region) => splitOptimizationRegionByTime(region, clusterById));
  const regionByClusterId = new Map();

  regions.sort((left, right) => {
    if (right.totalUnresolvedRiskMinutes !== left.totalUnresolvedRiskMinutes) {
      return right.totalUnresolvedRiskMinutes - left.totalUnresolvedRiskMinutes;
    }
    if (right.dominantSeverityMinutes !== left.dominantSeverityMinutes) {
      return right.dominantSeverityMinutes - left.dominantSeverityMinutes;
    }
    return left.windowStartMinute - right.windowStartMinute;
  });

  regions.forEach((region, index) => {
    region.regionId = `region-${index}`;
    region.clusterCount = region.clusterIds.length;
  });
  regionByClusterId.clear();
  regions.forEach((region) => {
    region.clusterIds.forEach((clusterId) => {
      regionByClusterId.set(clusterId, region);
    });
  });

  return {
    regions,
    regionByClusterId
  };
}

function stationIsWithinTrunkProblemCluster(station, cluster) {
  if (!station || station.lineId !== cluster.localLineId) {
    return false;
  }

  const stationId = station.stationId || station.id || "";
  if (stationId && (
    cluster.corridorFromStationIds.includes(stationId)
    || cluster.corridorToStationIds.includes(stationId)
  )) {
    return true;
  }

  if (station.trackAtomIndex < 0) {
    return false;
  }

  return station.trackAtomIndex >= cluster.localStartAtomIndex
    && station.trackAtomIndex <= cluster.localEndAtomIndexExclusive;
}

function rankVirtualBypassCandidates(normalizedInput, scenario, trunkProblemClusters) {
  const configuredIds = new Set(
    scenario.adjustableLineIds.flatMap((lineId) =>
      asArray(normalizedInput.configuredBypassByLineId.get(lineId)).map((station) => station.stationId)
    )
  );
  const candidates = [];
  scenario.adjustableLineIds.forEach((lineId) => {
    asArray(normalizedInput.candidateBypassByLineId.get(lineId)).forEach((station) => {
      if (!configuredIds.has(station.stationId)) {
        candidates.push(station);
      }
    });
  });

  return candidates
    .map((station) => {
      let score = 0;
      trunkProblemClusters.forEach((cluster) => {
        if (stationIsWithinTrunkProblemCluster(station, cluster)) {
          score += cluster.totalUnresolvedRiskMinutes
            + (cluster.dominantSeverityMinutes * 0.5)
            + (cluster.occurrenceCount * 0.1);
        }
      });
      return {
        ...station,
        candidateScore: Number(score.toFixed(2))
      };
    })
    .filter((station) => station.candidateScore > 0)
    .sort((left, right) => right.candidateScore - left.candidateScore);
}

function enumerateVirtualBypassStationSets(rankedCandidates, options = {}) {
  const forcedBypassStationId = options.forcedBypassStationId || "";
  if (forcedBypassStationId) {
    return isConfiguredBypassStationId(options.normalizedInput, forcedBypassStationId)
      ? [[]]
      : [[forcedBypassStationId]];
  }

  const maxAdditionalStations = Math.max(1, clampNumber(options.maxAdditionalBypassStations, 1));
  const candidateLimit = Math.max(1, clampNumber(options.virtualCandidateLimit, 8));
  const candidateIds = rankedCandidates.slice(0, candidateLimit).map((station) => station.stationId);
  const result = [];

  for (let i = 0; i < candidateIds.length; i += 1) {
    result.push([candidateIds[i]]);
  }
  if (maxAdditionalStations >= 2) {
    for (let i = 0; i < candidateIds.length; i += 1) {
      for (let j = i + 1; j < candidateIds.length; j += 1) {
        result.push([candidateIds[i], candidateIds[j]]);
      }
    }
  }
  if (maxAdditionalStations >= 3) {
    for (let i = 0; i < candidateIds.length; i += 1) {
      for (let j = i + 1; j < candidateIds.length; j += 1) {
        for (let k = j + 1; k < candidateIds.length; k += 1) {
          result.push([candidateIds[i], candidateIds[j], candidateIds[k]]);
        }
      }
    }
  }

  return result;
}

function getConfiguredBypassStationIdsForLines(normalizedInput, lineIds) {
  const stationIds = [];
  asArray(lineIds).forEach((lineId) => {
    asArray(normalizedInput.configuredBypassByLineId.get(lineId)).forEach((station) => {
      if (station.stationId) {
        stationIds.push(station.stationId);
      }
    });
  });
  return [...new Set(stationIds)];
}

function isConfiguredBypassStationId(normalizedInput, stationId) {
  if (!stationId) {
    return false;
  }

  return asArray(normalizedInput?.configuredBypassStations).some(
    (station) => station?.stationId === stationId
  );
}

export function buildLocalObservedModel(rawInput, options = {}) {
  const normalizedInput = rawInput?.lineById ? rawInput : normalizePlannerInput(rawInput);
  const draft = options.draft || pickPlannerDraft(normalizedInput, options);
  const scenario = options.scenario || buildAnalysisScenario(normalizedInput, draft, options);
  const localLineIds = asArray(options.localLineIds).filter(Boolean).length > 0
    ? asArray(options.localLineIds).filter(Boolean)
    : scenario.adjustableLineIds;

  const lineRuntimeModels = new Map();
  const summaries = [];
  let confidenceSum = 0;

  localLineIds.forEach((lineId) => {
    const runtimeModel = buildLineRuntimeModel(normalizedInput, lineId, options);
    if (!runtimeModel) {
      return;
    }

    lineRuntimeModels.set(lineId, runtimeModel);
    const observedStationCount = runtimeModel.stationOffsets.filter((stationOffset) => stationOffset.dwellSource === "observed").length;
    const observedRuntimeSegmentCount = runtimeModel.segmentRuntimeOffsets.filter((segmentRuntime) =>
      segmentRuntime.source === "tripObserved" || segmentRuntime.source === "atomSlice"
    ).length;
    const totalStationCount = runtimeModel.stationOffsets.length;
    const observedConfidence = totalStationCount > 0
      ? observedStationCount / totalStationCount
      : 0;
    const modelConfidence = runtimeModel.stationOffsets.length > 0
      ? runtimeModel.stationOffsets.reduce((sum, stationOffset) => sum + clampNumber(stationOffset.confidence, 0.2), 0)
        / runtimeModel.stationOffsets.length
      : 0.2;
    const confidence = Number(((observedConfidence * 0.5) + (modelConfidence * 0.5)).toFixed(2));
    confidenceSum += confidence;

    summaries.push({
      lineId,
      stationCount: runtimeModel.stationOffsets.length,
      segmentCount: runtimeModel.segments.length,
      observedRuntimeSegmentCount,
      totalMinuteSpan: Number(runtimeModel.totalMinuteSpan.toFixed(2)),
      observedStationCount,
      confidence,
      stationOffsets: runtimeModel.stationOffsets.map((stationOffset) => ({
        stationId: stationOffset.stationId,
        order: stationOffset.order,
        name: stationOffset.name,
        arrivalMinute: Number(stationOffset.arrivalMinute.toFixed(2)),
        departureMinute: Number(stationOffset.departureMinute.toFixed(2)),
        dwellMinutes: Number(stationOffset.dwellMinutes.toFixed(2)),
        skippedStopStartLossMinutes: Number(clampNumber(stationOffset.skippedStopStartLossMinutes, 0).toFixed(2)),
        dwellSource: stationOffset.dwellSource,
        confidence: Number(clampNumber(stationOffset.confidence, 0.2).toFixed(2))
      }))
    });
  });

  return {
    localLineIds,
    lineRuntimeModels,
    summaries,
    confidence: summaries.length > 0 ? Number((confidenceSum / summaries.length).toFixed(2)) : 0.2
  };
}

export function inferExpressRuntimeFromLocal(rawInput, options = {}) {
  const normalizedInput = rawInput?.lineById ? rawInput : normalizePlannerInput(rawInput);
  const localLineId = options.localLineId || "";
  const localModel = buildLocalObservedModel(normalizedInput, { localLineIds: localLineId ? [localLineId] : [] });
  const runtimeModel = localLineId ? localModel.lineRuntimeModels.get(localLineId) : localModel.lineRuntimeModels.values().next().value;
  if (!runtimeModel) {
    return null;
  }

  const stopStationIdSet = new Set(asArray(options.stopStationIds).filter(Boolean));
  const stopStartLossMinutesPerSkippedStop = resolveStopStartLossMinutesPerSkippedStop(options);
  const savedStationDwells = [];
  let savedDwellMinutes = 0;
  let savedStopStartLossMinutes = 0;

  runtimeModel.stationOffsets.forEach((stationOffset, index) => {
    const isOrigin = index === 0;
    const isTerminal = index === runtimeModel.stationOffsets.length - 1;
    if (isOrigin || isTerminal) {
      return;
    }
    if (stopStationIdSet.size > 0 && stopStationIdSet.has(stationOffset.stationId)) {
      return;
    }

    savedDwellMinutes += stationOffset.dwellMinutes;
    savedStopStartLossMinutes += stopStartLossMinutesPerSkippedStop;
    savedStationDwells.push({
      stationId: stationOffset.stationId,
      order: stationOffset.order,
      name: stationOffset.name,
      savedDwellMinutes: Number(stationOffset.dwellMinutes.toFixed(2)),
      savedStopStartLossMinutes: Number(stopStartLossMinutesPerSkippedStop.toFixed(2)),
      savedTotalMinutes: Number((stationOffset.dwellMinutes + stopStartLossMinutesPerSkippedStop).toFixed(2))
    });
  });

  const totalSavedMinutes = savedDwellMinutes + savedStopStartLossMinutes;
  const estimatedRuntimeMinutes = Math.max(0, runtimeModel.totalMinuteSpan - totalSavedMinutes);
  return {
    localLineId: runtimeModel.line.id,
    localLineName: runtimeModel.line.name,
    estimatedRuntimeMinutes: Number(estimatedRuntimeMinutes.toFixed(2)),
    baselineLocalRuntimeMinutes: Number(runtimeModel.totalMinuteSpan.toFixed(2)),
    savedDwellMinutes: Number(savedDwellMinutes.toFixed(2)),
    savedStopStartLossMinutes: Number(savedStopStartLossMinutes.toFixed(2)),
    totalSavedMinutes: Number(totalSavedMinutes.toFixed(2)),
    stopStartLossMinutesPerSkippedStop: Number(stopStartLossMinutesPerSkippedStop.toFixed(2)),
    skippedStationCount: savedStationDwells.length,
    savedStationDwells,
    confidence: Number(Math.max(0.15, localModel.confidence - 0.1).toFixed(2)),
    assumptions: [
      "skip saving is observed/profile dwell plus a fixed stop-start loss per skipped stop",
      "stop-start loss defaults to 3 minutes until enough comparable observed skip samples exist"
    ]
  };
}

export function computeCatchupWindow(localCurve, expressCurve, minSharedGapMinutes) {
  const gapProfile = computeCorridorGapProfile(localCurve, expressCurve);
  if (!gapProfile) {
    return null;
  }

  const catchupPoint = findCatchupPoint(gapProfile, minSharedGapMinutes);
  if (!catchupPoint) {
    return null;
  }

  return {
    ...catchupPoint,
    expectedGapMinutes: catchupPoint.minGapMinutes,
    gapProfile
  };
}

export function findCatchupEvents(normalizedInput, scenario, trips, corridors, options = {}) {
  const tripByLineId = new Map();
  trips.forEach((trip) => {
    if (!tripByLineId.has(trip.lineId)) {
      tripByLineId.set(trip.lineId, []);
    }
    tripByLineId.get(trip.lineId).push(trip);
  });

  const minSharedGapMinutes = Math.max(
    0.1,
    clampNumber(
      options.minSharedGapMinutes,
      normalizedInput.runtimeParams.trackModelEntryClearSafetyGapMinutes
    )
  );
  const allowedStationIdSet = buildAllowedBypassStationIdSet(options);
  const curveCache = new Map();
  const getCurve = (trip, corridor, role) => {
    const key = `${trip.tripId}|${corridor.corridorId}|${role}`;
    if (!curveCache.has(key)) {
      curveCache.set(key, buildTripCorridorCurve(trip, corridor, role));
    }
    return curveCache.get(key);
  };

  const events = [];
  const pursuitCorridors = buildPursuitTrunkCorridors(corridors);
  pursuitCorridors.forEach((corridor) => {
    const localTrips = tripByLineId.get(corridor.localLineId) || [];
    const expressTrips = tripByLineId.get(corridor.expressLineId) || [];
    localTrips.forEach((localTrip) => {
      expressTrips.forEach((expressTrip) => {
        const localWindow = buildCorridorWindowForTrip(localTrip, corridor, "local");
        const expressWindow = buildCorridorWindowForTrip(expressTrip, corridor, "express");
        if (!shouldEvaluateTripPairForCorridor(localWindow, expressWindow, minSharedGapMinutes)) {
          return;
        }

        const localCurve = getCurve(localTrip, corridor, "local");
        const expressCurve = getCurve(expressTrip, corridor, "express");
        const catchupWindow = computeCatchupWindow(localCurve, expressCurve, minSharedGapMinutes);
        if (!catchupWindow) {
          return;
        }

        const corridorStations = collectBypassStationsForCorridor(
          normalizedInput,
          corridor.localLineId,
          corridor.localStartStationId,
          corridor.localEndStationId,
          allowedStationIdSet,
          options
        );
        const localLine = normalizedInput.lineById.get(corridor.localLineId);
        const lineHoldBudgetMinutes = clampNumber(localLine?.maxStationDwellMinutes, 0);
        const holdBudgetMinutes = options.maxLocalHoldMinutes === null || options.maxLocalHoldMinutes === undefined
          ? lineHoldBudgetMinutes
          : lineHoldBudgetMinutes > 0
            ? Math.min(lineHoldBudgetMinutes, options.maxLocalHoldMinutes)
            : options.maxLocalHoldMinutes;
        const stationEvaluations = corridorStations
          .map((station) =>
            evaluateBypassStationForCatchup(
              normalizedInput,
              catchupWindow,
              corridor,
              catchupWindow.gapProfile,
              localTrip,
              expressTrip,
              station
            ))
          .filter(Boolean);
        const selectedBypassStation = pickBestBypassStationEvaluation(stationEvaluations, holdBudgetMinutes);
        const canUseConfiguredBypass = selectedBypassStation !== null;
        const requiredHoldMinutes = canUseConfiguredBypass
          ? selectedBypassStation.holdNeededMinutes
          : catchupWindow.severityMinutes;
        const effectiveResolvedHoldMinutes = canUseConfiguredBypass
          ? holdBudgetMinutes > 0
            ? Math.min(requiredHoldMinutes, holdBudgetMinutes)
            : requiredHoldMinutes
          : 0;
        const unresolvedRiskMinutes = canUseConfiguredBypass
          ? Math.max(0, requiredHoldMinutes - effectiveResolvedHoldMinutes)
          : Math.max(requiredHoldMinutes, catchupWindow.closingMinutes * 0.5);
        const robustnessRiskMinutes = Math.max(
          0,
          catchupWindow.robustnessRiskMinutes - effectiveResolvedHoldMinutes
        );

        events.push({
          catchupId: `${expressTrip.tripId}|${localTrip.tripId}|${corridor.sharedKey}`,
          corridorId: corridor.corridorId,
          sourceCorridorIds: asArray(corridor.sourceCorridorIds).length > 0
            ? [...corridor.sourceCorridorIds]
            : [corridor.corridorId],
          sharedKey: corridor.sharedKey,
          localTripId: localTrip.tripId,
          expressTripId: expressTrip.tripId,
          localLineId: corridor.localLineId,
          expressLineId: corridor.expressLineId,
          yieldingTripId: localTrip.tripId,
          priorityTripId: expressTrip.tripId,
          yieldingLineId: corridor.localLineId,
          priorityLineId: corridor.expressLineId,
          localDepartTime: localTrip.departureTime,
          expressDepartTime: expressTrip.departureTime,
          corridorFromStationId: corridor.localStartStationId,
          corridorToStationId: corridor.localEndStationId,
          localEntryMinute: localWindow.entryMinute,
          expressEntryMinute: expressWindow.entryMinute,
          localExitMinute: localWindow.exitMinute,
          expressExitMinute: expressWindow.exitMinute,
          gapAtEntryMinutes: catchupWindow.entryGapMinutes,
          gapAtExitMinutes: catchupWindow.exitGapMinutes,
          closingMinutes: catchupWindow.closingMinutes,
          minSharedGapMinutes: Number(minSharedGapMinutes.toFixed(2)),
          expectedGapMinutes: catchupWindow.expectedGapMinutes,
          severityMinutes: catchupWindow.severityMinutes,
          worstCaseGapMinutes: catchupWindow.worstCaseGapMinutes,
          minGapUncertaintyMinutes: catchupWindow.minGapUncertaintyMinutes,
          catchupAxisIndex: catchupWindow.catchupAxisIndex,
          catchupMinute: catchupWindow.catchupMinute,
          minGapMinutes: catchupWindow.minGapMinutes,
          didCatchUp: catchupWindow.didCatchUp,
          requiredHoldMinutes: Number(requiredHoldMinutes.toFixed(2)),
          holdBudgetMinutes: Number(holdBudgetMinutes.toFixed(2)),
          withinHoldBudget: effectiveResolvedHoldMinutes >= requiredHoldMinutes,
          expressSavedMinutes: Number(Math.max(0, corridor.localRuntimeMinutes - corridor.expressRuntimeMinutes).toFixed(2)),
          resolvedHoldMinutes: Number(effectiveResolvedHoldMinutes.toFixed(2)),
          unresolvedRiskMinutes: Number(unresolvedRiskMinutes.toFixed(2)),
          robustnessRiskMinutes: Number(robustnessRiskMinutes.toFixed(2)),
          selectedBypassStation,
          usableBypassStations: corridorStations.map((station) => ({
            stationId: station.stationId,
            lineId: station.lineId,
            name: station.name,
            order: station.order,
            isConfigured: station.isConfigured === true,
            isVirtualCandidate: station.isVirtualCandidate === true
          })),
          configuredBypassStations: corridorStations.map((station) => ({
            stationId: station.stationId,
            lineId: station.lineId,
            name: station.name,
            order: station.order
          })),
          confidence: Number(corridor.confidence.toFixed(2))
        });
      });
    });
  });

  return mergeCatchupEvents(events);
}

export const findDeterministicCatchupEvents = findCatchupEvents;

function getReservationListForStation(reservationsByStationId, stationId) {
  if (!reservationsByStationId.has(stationId)) {
    reservationsByStationId.set(stationId, []);
  }
  return reservationsByStationId.get(stationId);
}

function reservationOverlaps(existingReservation, startMinute, endMinute) {
  return startMinute < existingReservation.endMinute && endMinute > existingReservation.startMinute;
}

function tryReserveBypassWindow(reservationsByStationId, stationId, reservation) {
  if (!stationId) {
    return true;
  }
  const reservations = getReservationListForStation(reservationsByStationId, stationId);
  if (reservations.some((existing) => reservationOverlaps(existing, reservation.startMinute, reservation.endMinute))) {
    return false;
  }
  reservations.push(reservation);
  reservations.sort((left, right) => left.startMinute - right.startMinute);
  return true;
}

function simulateCatchupEventInRegion(event, localDelayByTripId, expressDelayByTripId, reservationsByStationId) {
  const localDelayMinutes = clampNumber(localDelayByTripId.get(event.localTripId), 0);
  const expressDelayMinutes = clampNumber(expressDelayByTripId.get(event.expressTripId), 0);
  const effectiveMinGapMinutes = Number((event.minGapMinutes + expressDelayMinutes - localDelayMinutes).toFixed(2));
  const effectiveSeverityMinutes = Number(Math.max(0, event.minSharedGapMinutes - effectiveMinGapMinutes).toFixed(2));
  const effectiveWorstCaseGapMinutes = Number((effectiveMinGapMinutes - clampNumber(event.minGapUncertaintyMinutes, 0)).toFixed(2));
  const effectiveRobustnessRiskMinutes = Number(Math.max(0, event.minSharedGapMinutes - effectiveWorstCaseGapMinutes).toFixed(2));
  const severityDeltaMinutes = Number((effectiveSeverityMinutes - event.severityMinutes).toFixed(2));
  const effectiveRequiredHoldMinutes = Number(Math.max(0, event.requiredHoldMinutes + severityDeltaMinutes).toFixed(2));
  const effectiveHoldBudgetMinutes = clampNumber(event.holdBudgetMinutes, 0);
  let effectiveResolvedHoldMinutes = 0;
  let effectiveUnresolvedRiskMinutes = 0;
  let remainingRobustnessRiskMinutes = effectiveRobustnessRiskMinutes;
  let reservationConflict = false;
  let blockedBypassStation = null;
  let selectedBypassStation = event.selectedBypassStation
    ? {
      ...event.selectedBypassStation,
      stationDepartureMinute: Number((clampNumber(event.selectedBypassStation.stationDepartureMinute, event.localEntryMinute) + localDelayMinutes).toFixed(2))
    }
    : null;

  if (selectedBypassStation) {
    effectiveResolvedHoldMinutes = Number(Math.min(effectiveRequiredHoldMinutes, effectiveHoldBudgetMinutes).toFixed(2));
    effectiveUnresolvedRiskMinutes = Number(Math.max(0, effectiveRequiredHoldMinutes - effectiveResolvedHoldMinutes).toFixed(2));
    remainingRobustnessRiskMinutes = Number(Math.max(0, effectiveRobustnessRiskMinutes - effectiveResolvedHoldMinutes).toFixed(2));
    const reservationStartMinute = selectedBypassStation.stationDepartureMinute;
    const reservationEndMinute = Number((reservationStartMinute + effectiveResolvedHoldMinutes).toFixed(2));
    if (effectiveResolvedHoldMinutes > 0 && !tryReserveBypassWindow(reservationsByStationId, selectedBypassStation.stationId, {
      stationId: selectedBypassStation.stationId,
      localTripId: event.localTripId,
      expressTripId: event.expressTripId,
      startMinute: reservationStartMinute,
      endMinute: reservationEndMinute
    })) {
      reservationConflict = true;
      effectiveResolvedHoldMinutes = 0;
      effectiveUnresolvedRiskMinutes = Number(Math.max(
        effectiveRequiredHoldMinutes,
        event.unresolvedRiskMinutes,
        effectiveSeverityMinutes
      ).toFixed(2));
      remainingRobustnessRiskMinutes = Number(Math.max(
        effectiveRobustnessRiskMinutes,
        clampNumber(event.robustnessRiskMinutes, 0)
      ).toFixed(2));
      blockedBypassStation = {
        ...selectedBypassStation,
        reservationConflict: true
      };
      selectedBypassStation = null;
    }
  } else {
    effectiveUnresolvedRiskMinutes = Number(Math.max(
      effectiveRequiredHoldMinutes,
      event.unresolvedRiskMinutes + Math.max(0, severityDeltaMinutes),
      effectiveSeverityMinutes
    ).toFixed(2));
    remainingRobustnessRiskMinutes = Number(Math.max(
      effectiveRobustnessRiskMinutes,
      clampNumber(event.robustnessRiskMinutes, 0) + Math.max(0, severityDeltaMinutes)
    ).toFixed(2));
  }

  if (effectiveResolvedHoldMinutes > 0) {
    localDelayByTripId.set(
      event.localTripId,
      Number((localDelayMinutes + effectiveResolvedHoldMinutes).toFixed(2))
    );
  }
  if (effectiveUnresolvedRiskMinutes > 0) {
    expressDelayByTripId.set(
      event.expressTripId,
      Number((expressDelayMinutes + effectiveUnresolvedRiskMinutes).toFixed(2))
    );
  }

  return {
    ...event,
    localEntryMinute: Number((event.localEntryMinute + localDelayMinutes).toFixed(2)),
    expressEntryMinute: Number((event.expressEntryMinute + expressDelayMinutes).toFixed(2)),
    localExitMinute: Number((event.localExitMinute + localDelayMinutes).toFixed(2)),
    expressExitMinute: Number((event.expressExitMinute + expressDelayMinutes).toFixed(2)),
    catchupMinute: Number((event.catchupMinute + expressDelayMinutes).toFixed(2)),
    minGapMinutes: effectiveMinGapMinutes,
    expectedGapMinutes: effectiveMinGapMinutes,
    worstCaseGapMinutes: effectiveWorstCaseGapMinutes,
    severityMinutes: effectiveSeverityMinutes,
    requiredHoldMinutes: effectiveRequiredHoldMinutes,
    resolvedHoldMinutes: effectiveResolvedHoldMinutes,
    unresolvedRiskMinutes: effectiveUnresolvedRiskMinutes,
    robustnessRiskMinutes: remainingRobustnessRiskMinutes,
    withinHoldBudget: effectiveResolvedHoldMinutes >= effectiveRequiredHoldMinutes,
    localDelayBeforeCatchupMinutes: localDelayMinutes,
    expressDelayBeforeCatchupMinutes: expressDelayMinutes,
    reservationConflict,
    blockedBypassStation,
    selectedBypassStation
  };
}

function simulateRegionCatchupChain(region, regionCatchupEvents) {
  const localDelayByTripId = new Map();
  const expressDelayByTripId = new Map();
  const reservationsByStationId = new Map();
  const events = [...asArray(regionCatchupEvents)]
    .sort((left, right) => {
      if (left.catchupMinute !== right.catchupMinute) {
        return left.catchupMinute - right.catchupMinute;
      }
      if (left.expressTripId !== right.expressTripId) {
        return left.expressTripId.localeCompare(right.expressTripId);
      }
      return left.localTripId.localeCompare(right.localTripId);
    })
    .map((event) => simulateCatchupEventInRegion(event, localDelayByTripId, expressDelayByTripId, reservationsByStationId))
    .sort((left, right) => {
      if (right.severityMinutes !== left.severityMinutes) {
        return right.severityMinutes - left.severityMinutes;
      }
      return left.expressEntryMinute - right.expressEntryMinute;
    });

  const reservationCount = [...reservationsByStationId.values()].reduce((sum, reservations) => sum + reservations.length, 0);
  const reservationConflictCount = events.filter((event) => event.reservationConflict).length;
  const regionLocalDelayMaxMinutes = [...localDelayByTripId.values()].reduce((max, value) => Math.max(max, value), 0);
  const regionExpressDelayMaxMinutes = [...expressDelayByTripId.values()].reduce((max, value) => Math.max(max, value), 0);

  return {
    regionId: region.regionId,
    clusterIds: region.clusterIds,
    events,
    reservationsByStationId,
    localDelayByTripId,
    expressDelayByTripId,
    reservationCount,
    reservationConflictCount,
    regionLocalDelayMaxMinutes: Number(regionLocalDelayMaxMinutes.toFixed(2)),
    regionExpressDelayMaxMinutes: Number(regionExpressDelayMaxMinutes.toFixed(2))
  };
}

function simulateRegionPlanState(rawCatchupEvents, rawTrunkProblemClusters, optimizationRegioning) {
  const regionByCatchupId = new Map();
  asArray(rawTrunkProblemClusters).forEach((cluster) => {
    const region = optimizationRegioning.regionByClusterId.get(cluster.clusterId);
    if (!region) {
      return;
    }
    asArray(cluster.catchupIds).forEach((catchupId) => {
      if (catchupId) {
        regionByCatchupId.set(catchupId, region);
      }
    });
  });

  const catchupEventsByRegionId = new Map();
  const passthroughEvents = [];
  asArray(rawCatchupEvents).forEach((event) => {
    const region = regionByCatchupId.get(event.catchupId);
    if (!region) {
      passthroughEvents.push(event);
      return;
    }
    if (!catchupEventsByRegionId.has(region.regionId)) {
      catchupEventsByRegionId.set(region.regionId, []);
    }
    catchupEventsByRegionId.get(region.regionId).push(event);
  });

  const regionResults = asArray(optimizationRegioning.regions).map((region) => {
    const regionCatchupEvents = catchupEventsByRegionId.get(region.regionId) || [];
    const result = simulateRegionCatchupChain(region, regionCatchupEvents);
    return {
      regionId: region.regionId,
      clusterIds: region.clusterIds,
      localLineIds: region.localLineIds,
      expressLineIds: region.expressLineIds,
      windowStartMinute: region.windowStartMinute,
      windowEndMinute: region.windowEndMinute,
      eventCount: result.events.length,
      reservationCount: result.reservationCount,
      reservationConflictCount: result.reservationConflictCount,
      regionLocalDelayMaxMinutes: result.regionLocalDelayMaxMinutes,
      regionExpressDelayMaxMinutes: result.regionExpressDelayMaxMinutes,
      totalUnresolvedRiskMinutes: Number(result.events.reduce((sum, event) => sum + clampNumber(event.unresolvedRiskMinutes, 0), 0).toFixed(2)),
      events: result.events
    };
  });

  const simulatedEvents = [
    ...passthroughEvents,
    ...regionResults.flatMap((region) => region.events)
  ].sort((left, right) => {
    if (right.severityMinutes !== left.severityMinutes) {
      return right.severityMinutes - left.severityMinutes;
    }
    return left.expressEntryMinute - right.expressEntryMinute;
  });

  return {
    events: simulatedEvents,
    regionResults
  };
}

function buildPlanMetrics(catchupEvents) {
  const activeBypassStationIds = new Set();
  let totalExpressSavedMinutes = 0;
  let totalLocalExtraWaitMinutes = 0;
  let totalUnresolvedRiskMinutes = 0;
  let totalRobustnessRiskMinutes = 0;

  catchupEvents.forEach((event) => {
    totalExpressSavedMinutes += event.expressSavedMinutes;
    totalLocalExtraWaitMinutes += event.resolvedHoldMinutes;
    totalUnresolvedRiskMinutes += event.unresolvedRiskMinutes;
    totalRobustnessRiskMinutes += clampNumber(event.robustnessRiskMinutes, 0);
    if (event.selectedBypassStation?.stationId) {
      activeBypassStationIds.add(event.selectedBypassStation.stationId);
    }
  });

  return {
    totalExpressSavedMinutes: Number(totalExpressSavedMinutes.toFixed(2)),
    totalLocalExtraWaitMinutes: Number(totalLocalExtraWaitMinutes.toFixed(2)),
    totalUnresolvedRiskMinutes: Number(totalUnresolvedRiskMinutes.toFixed(2)),
    totalRobustnessRiskMinutes: Number(totalRobustnessRiskMinutes.toFixed(2)),
    totalNetBenefitMinutes: Number((totalExpressSavedMinutes - totalLocalExtraWaitMinutes - totalUnresolvedRiskMinutes - totalRobustnessRiskMinutes).toFixed(2)),
    activeBypassStationCount: activeBypassStationIds.size,
    retimedTripCount: 0,
    totalRetimedMinutes: 0,
    rebuildSpanMinutes: 0
  };
}

function buildScenarioLineRoleSummary(scenario) {
  const selectedLineIds = dedupeLineIds(scenario?.selectedLineIds);
  const adjustableLineIds = dedupeLineIds(scenario?.adjustableLineIds);
  const fixedLineIds = selectedLineIds.filter((lineId) => !adjustableLineIds.includes(lineId));
  const targetLineIds = dedupeLineIds(
    asArray(scenario?.expressLineIds).length > 0
      ? scenario.expressLineIds
      : [scenario?.selectedLineId].filter(Boolean)
  ).filter((lineId) => selectedLineIds.includes(lineId));
  return {
    selectedLineIds,
    adjustableLineIds,
    fixedLineIds,
    targetLineIds,
    roles: selectedLineIds.map((lineId) => ({
      lineId,
      participates: true,
      adjustable: adjustableLineIds.includes(lineId),
      fixed: fixedLineIds.includes(lineId),
      target: targetLineIds.includes(lineId)
    }))
  };
}

function collectPlanAdjustedLineIds(plan) {
  const adjustedLineIds = new Set();
  asArray(plan?.scheduleActions).forEach((action) => {
    asArray(action.affectedLineIds).forEach((lineId) => adjustedLineIds.add(lineId));
    if (action.affectedLineId) {
      adjustedLineIds.add(action.affectedLineId);
    }
  });
  asArray(plan?.addedVirtualBypassStations).forEach((station) => {
    if (station?.lineId) {
      adjustedLineIds.add(station.lineId);
    }
  });
  return [...adjustedLineIds];
}

function buildPlanProblemIssues(plan, scenario) {
  const issues = [];
  const roleSummary = buildScenarioLineRoleSummary(scenario);
  const fixedLineIdSet = new Set(roleSummary.fixedLineIds);

  asArray(plan?.trunkProblemClusters)
    .filter((cluster) => clampNumber(cluster.totalUnresolvedRiskMinutes, 0) > 0)
    .slice(0, 8)
    .forEach((cluster) => {
      issues.push({
        type: "unresolvedConflict",
        severity: "high",
        clusterId: cluster.clusterId,
        yieldingLineId: cluster.yieldingLineId || cluster.localLineId,
        priorityLineId: cluster.priorityLineId || cluster.expressLineId,
        severityMinutes: Number(clampNumber(cluster.totalUnresolvedRiskMinutes, 0).toFixed(2)),
        recommendedBypassStationId: cluster.recommendedBypassStation?.stationId || ""
      });
    });

  asArray(plan?.catchupEvents)
    .filter((event) => clampNumber(event.requiredHoldMinutes, 0) > clampNumber(event.holdBudgetMinutes, 0))
    .slice(0, 8)
    .forEach((event) => {
      issues.push({
        type: "waitLimitExceeded",
        severity: "medium",
        catchupId: event.catchupId,
        yieldingLineId: event.yieldingLineId || event.localLineId,
        priorityLineId: event.priorityLineId || event.expressLineId,
        yieldingTripId: event.yieldingTripId || event.localTripId,
        priorityTripId: event.priorityTripId || event.expressTripId,
        requiredHoldMinutes: event.requiredHoldMinutes,
        holdBudgetMinutes: event.holdBudgetMinutes
      });
    });

  if (clampNumber(plan?.metrics?.totalRobustnessRiskMinutes, 0) > 0) {
    issues.push({
      type: "robustnessWeak",
      severity: clampNumber(plan.metrics.totalRobustnessRiskMinutes, 0) > 10 ? "medium" : "low",
      riskMinutes: plan.metrics.totalRobustnessRiskMinutes
    });
  }

  const fixedAffectedLineIds = collectPlanAdjustedLineIds(plan).filter((lineId) => fixedLineIdSet.has(lineId));
  if (fixedAffectedLineIds.length > 0) {
    issues.push({
      type: "fixedLineAffected",
      severity: "high",
      lineIds: fixedAffectedLineIds
    });
  }

  return issues;
}

function attachPlannerFrontendSummary(plan, scenario) {
  const lineRoleSummary = buildScenarioLineRoleSummary(scenario);
  const problemIssues = buildPlanProblemIssues(plan, scenario);
  const actuallyAdjustedLineIds = collectPlanAdjustedLineIds(plan);
  plan.lineRoleSummary = lineRoleSummary;
  plan.problemIssues = problemIssues;
  plan.frontendSummary = {
    selectedLineIds: lineRoleSummary.selectedLineIds,
    adjustableLineIds: lineRoleSummary.adjustableLineIds,
    fixedLineIds: lineRoleSummary.fixedLineIds,
    targetLineIds: lineRoleSummary.targetLineIds,
    actuallyAdjustedLineIds,
    issueCountsByType: problemIssues.reduce((counts, issue) => {
      counts[issue.type] = clampNumber(counts[issue.type], 0) + 1;
      return counts;
    }, {}),
    actionCount: asArray(plan.scheduleActions).length,
    catchupClusterCount: asArray(plan.trunkProblemClusters).length,
    unresolvedRiskMinutes: clampNumber(plan.metrics?.totalUnresolvedRiskMinutes, 0),
    robustnessRiskMinutes: clampNumber(plan.metrics?.totalRobustnessRiskMinutes, 0)
  };
  return plan;
}

function scoreCatchupScenario(objective, metrics, departureGapPenalty) {
  const weights = OBJECTIVE_WEIGHTS[objective] || OBJECTIVE_WEIGHTS[DEFAULT_OBJECTIVE];
  const score =
    metrics.totalExpressSavedMinutes * weights.expressBenefit
    - metrics.totalLocalExtraWaitMinutes * weights.resolvedHoldCost
    - metrics.totalUnresolvedRiskMinutes * weights.unresolvedRisk
    - clampNumber(metrics.totalRobustnessRiskMinutes, 0) * weights.robustnessRisk
    - metrics.activeBypassStationCount * weights.activeBypassCost
    - clampNumber(metrics.retimedTripCount, 0) * weights.retimedTripCost
    - clampNumber(metrics.totalRetimedMinutes, 0) * weights.retimedMinuteCost
    - clampNumber(metrics.rebuildSpanMinutes, 0) * weights.rebuildSpanCost
    - departureGapPenalty * weights.departureConflict;
  return Number(score.toFixed(2));
}

function buildPlanExplanation(plan, scenario) {
  const notes = [];
  notes.push(
    `window ${scenario.windowStart}-${scenario.windowEnd}, local=${scenario.localLineIds.join(",") || "-"}, express=${scenario.expressLineIds.join(",") || "-"}`
  );
  if (plan.catchupEvents.length === 0) {
    notes.push("no predicted catchup in analysis window");
  }

  if (plan.metrics.totalExpressSavedMinutes > 0) {
    notes.push(`express saving ${plan.metrics.totalExpressSavedMinutes.toFixed(2)} min`);
  }
  if (plan.metrics.totalLocalExtraWaitMinutes > 0) {
    notes.push(`local extra wait ${plan.metrics.totalLocalExtraWaitMinutes.toFixed(2)} min`);
  }
  if (plan.metrics.totalUnresolvedRiskMinutes > 0) {
    notes.push(`unresolved risk ${plan.metrics.totalUnresolvedRiskMinutes.toFixed(2)} min`);
  }
  if (clampNumber(plan.metrics.totalRobustnessRiskMinutes, 0) > 0) {
    notes.push(`robustness risk ${plan.metrics.totalRobustnessRiskMinutes.toFixed(2)} min`);
  }
  if (plan.recommendedExpressOffsetDeltaMinutes !== 0) {
    const direction = plan.recommendedExpressOffsetDeltaMinutes > 0 ? "later" : "earlier";
    notes.push(`shift express ${Math.abs(plan.recommendedExpressOffsetDeltaMinutes)} min ${direction}`);
  }

  return notes;
}

function comparePlanQuality(left, right) {
  if (!left && right) {
    return 1;
  }
  if (!right && left) {
    return -1;
  }
  if (!left && !right) {
    return 0;
  }
  if (left.metrics.totalUnresolvedRiskMinutes !== right.metrics.totalUnresolvedRiskMinutes) {
    return left.metrics.totalUnresolvedRiskMinutes - right.metrics.totalUnresolvedRiskMinutes;
  }
  if (clampNumber(left.metrics.totalRobustnessRiskMinutes, 0) !== clampNumber(right.metrics.totalRobustnessRiskMinutes, 0)) {
    return clampNumber(left.metrics.totalRobustnessRiskMinutes, 0) - clampNumber(right.metrics.totalRobustnessRiskMinutes, 0);
  }
  const leftClusterCount = asArray(left.trunkProblemClusters).filter((cluster) => cluster.totalUnresolvedRiskMinutes > 0).length;
  const rightClusterCount = asArray(right.trunkProblemClusters).filter((cluster) => cluster.totalUnresolvedRiskMinutes > 0).length;
  if (leftClusterCount !== rightClusterCount) {
    return leftClusterCount - rightClusterCount;
  }
  if (right.score !== left.score) {
    return right.score - left.score;
  }
  return left.metrics.totalLocalExtraWaitMinutes - right.metrics.totalLocalExtraWaitMinutes;
}

function scorePlanForObjective(plan, objective) {
  return scoreCatchupScenario(
    objective,
    plan.metrics,
    clampNumber(plan.metrics?.departureGapPenalty, 0)
  );
}

function comparePlansForObjective(left, right, objective) {
  if (left.metrics.totalUnresolvedRiskMinutes !== right.metrics.totalUnresolvedRiskMinutes) {
    return left.metrics.totalUnresolvedRiskMinutes - right.metrics.totalUnresolvedRiskMinutes;
  }
  const leftScore = scorePlanForObjective(left, objective);
  const rightScore = scorePlanForObjective(right, objective);
  if (rightScore !== leftScore) {
    return rightScore - leftScore;
  }
  if (clampNumber(left.metrics.totalRobustnessRiskMinutes, 0) !== clampNumber(right.metrics.totalRobustnessRiskMinutes, 0)) {
    return clampNumber(left.metrics.totalRobustnessRiskMinutes, 0) - clampNumber(right.metrics.totalRobustnessRiskMinutes, 0);
  }
  if (left.metrics.totalLocalExtraWaitMinutes !== right.metrics.totalLocalExtraWaitMinutes) {
    return left.metrics.totalLocalExtraWaitMinutes - right.metrics.totalLocalExtraWaitMinutes;
  }
  return clampNumber(left.metrics.activeBypassStationCount, 0) - clampNumber(right.metrics.activeBypassStationCount, 0);
}

function rankPlansByPreset(plans) {
  return Object.keys(OBJECTIVE_WEIGHTS).reduce((result, objective) => {
    const plan = asArray(plans).slice().sort((left, right) => comparePlansForObjective(left, right, objective))[0] || null;
    result[objective] = plan
      ? {
        planId: plan.planId,
        score: scorePlanForObjective(plan, objective),
        offsetMinutes: plan.recommendedExpressOffsetDeltaMinutes,
        requestedExpressTripsPerHour: plan.requestedExpressTripsPerHour ?? null,
        addedVirtualBypassStationIds: asArray(plan.addedVirtualBypassStations).map((station) => station.stationId),
        totalUnresolvedRiskMinutes: plan.metrics.totalUnresolvedRiskMinutes,
        totalRobustnessRiskMinutes: plan.metrics.totalRobustnessRiskMinutes,
        totalLocalExtraWaitMinutes: plan.metrics.totalLocalExtraWaitMinutes,
        activeBypassStationCount: plan.metrics.activeBypassStationCount,
        retimedTripCount: plan.metrics.retimedTripCount
      }
      : null;
    return result;
  }, {});
}

function evaluateScenarioRows(normalizedInput, draft, baseOptions, workingRows, offsetDeltaMinutes) {
  const scenario = buildAnalysisScenario(normalizedInput, draft, {
    ...baseOptions,
    stagedRowsOverride: rowsToStagedRows(workingRows)
  });
  const lineRuntimeModels = buildLineRuntimeModels(normalizedInput, scenario, baseOptions);
  const corridors = buildSharedCorridors(normalizedInput, scenario, lineRuntimeModels);
  return evaluateExistingOnlyVariant(
    normalizedInput,
    scenario,
    lineRuntimeModels,
    corridors,
    {
      ...baseOptions,
      objective: baseOptions.objective || DEFAULT_OBJECTIVE
    },
    offsetDeltaMinutes
  );
}

function evaluatePreparedScenarioRows(preparedContext, baseOptions, workingRows, offsetDeltaMinutes) {
  const scenario = {
    ...preparedContext.scenario,
    stagedRows: buildScenarioRowsFromWorkingRows(workingRows)
  };
  return evaluateExistingOnlyVariant(
    preparedContext.normalizedInput,
    scenario,
    preparedContext.lineRuntimeModels,
    preparedContext.corridors,
    {
      ...baseOptions,
      objective: baseOptions.objective || DEFAULT_OBJECTIVE
    },
    offsetDeltaMinutes
  );
}

function buildWorkingRowsSignature(workingRows) {
  return sortRowsByMinute(workingRows)
    .map((row) => `${row.id}@${row.minute}`)
    .join("|");
}

function sortTripIdsByMinute(tripIds, rowsById) {
  return [...new Set(asArray(tripIds).filter(Boolean))]
    .filter((tripId) => rowsById.has(tripId))
    .sort((left, right) => {
      const leftMinute = rowsById.get(left)?.minute ?? 0;
      const rightMinute = rowsById.get(right)?.minute ?? 0;
      if (leftMinute !== rightMinute) {
        return leftMinute - rightMinute;
      }
      return left.localeCompare(right);
    });
}

function pickClosestRowToMinute(rows, targetMinute) {
  let bestRow = null;
  let bestDistance = Infinity;
  asArray(rows).forEach((row) => {
    const distance = Math.abs(clampNumber(row?.minute, 0) - clampNumber(targetMinute, 0));
    if (distance < bestDistance) {
      bestDistance = distance;
      bestRow = row;
    }
  });
  return bestRow;
}

function collectClusterShiftCandidateRows(lineRows, cluster, request) {
  if (!Array.isArray(lineRows) || lineRows.length === 0) {
    return [];
  }

  const dominantMinute = clampNumber(
    cluster?.dominantCatchupMinute,
    clampNumber(cluster?.firstCatchupMinute, lineRows[0]?.minute ?? 0)
  );
  const windowHalfSpan = Math.min(
    DEFAULT_MAX_SHIFT_WINDOW_MINUTES / 2,
    Math.max(8, clampNumber(request?.maxLocalRetimeMinutes, 0) * 4)
  );
  const clusterTripIdSet = new Set(
    asArray(cluster?.localTripIds).filter((tripId) => !request.lockedLocalTripIds.has(tripId))
  );
  const rowsWithinWindow = lineRows.filter((row) =>
    row.minute >= dominantMinute - windowHalfSpan
    && row.minute <= dominantMinute + windowHalfSpan
  );
  const preferredRows = rowsWithinWindow.filter((row) => clusterTripIdSet.has(row.id));
  const anchorRow = pickClosestRowToMinute(
    preferredRows.length > 0 ? preferredRows : rowsWithinWindow.length > 0 ? rowsWithinWindow : lineRows,
    dominantMinute
  );
  if (!anchorRow) {
    return [];
  }

  const anchorIndex = lineRows.findIndex((row) => row.id === anchorRow.id);
  if (anchorIndex < 0) {
    return [anchorRow];
  }

  const selectedRows = [lineRows[anchorIndex]];
  let leftIndex = anchorIndex - 1;
  let rightIndex = anchorIndex + 1;
  while (selectedRows.length < DEFAULT_MAX_SHIFT_TRIPS && (leftIndex >= 0 || rightIndex < lineRows.length)) {
    const leftRow = leftIndex >= 0 ? lineRows[leftIndex] : null;
    const rightRow = rightIndex < lineRows.length ? lineRows[rightIndex] : null;
    const leftAllowed = leftRow && leftRow.minute >= dominantMinute - windowHalfSpan;
    const rightAllowed = rightRow && rightRow.minute <= dominantMinute + windowHalfSpan;

    if (!leftAllowed && !rightAllowed) {
      break;
    }

    if (leftAllowed && rightAllowed) {
      const leftDistance = Math.abs(leftRow.minute - dominantMinute);
      const rightDistance = Math.abs(rightRow.minute - dominantMinute);
      if (leftDistance <= rightDistance) {
        selectedRows.unshift(leftRow);
        leftIndex -= 1;
      } else {
        selectedRows.push(rightRow);
        rightIndex += 1;
      }
      continue;
    }

    if (leftAllowed) {
      selectedRows.unshift(leftRow);
      leftIndex -= 1;
      continue;
    }

    selectedRows.push(rightRow);
    rightIndex += 1;
  }

  return selectedRows;
}

function buildClusterRetimeGroups(plan, workingRows, normalizedInput, request) {
  const rowsById = new Map(workingRows.map((row) => [row.id, row]));
  const localRowsByLineId = new Map();
  workingRows.forEach((row) => {
    if (!isAdjustableRow(row, request)) {
      return;
    }
    if (!localRowsByLineId.has(row.lineId)) {
      localRowsByLineId.set(row.lineId, []);
    }
    localRowsByLineId.get(row.lineId).push(row);
  });
  localRowsByLineId.forEach((rows) => {
    rows.sort((left, right) => left.minute - right.minute);
  });

  return asArray(plan.trunkProblemClusters)
    .filter((cluster) =>
      cluster.totalUnresolvedRiskMinutes > 0
      || clampNumber(cluster.totalRobustnessRiskMinutes, 0) > 0
    )
    .slice(0, 3)
    .map((cluster) => {
      const lineRows = localRowsByLineId.get(cluster.localLineId) || [];
      const candidateRows = collectClusterShiftCandidateRows(lineRows, cluster, request);
      if (candidateRows.length === 0) {
        return null;
      }

      const clusterTripIdSet = new Set(
        asArray(cluster.localTripIds).filter((tripId) => !request.lockedLocalTripIds.has(tripId))
      );
      const primaryRow = pickClosestRowToMinute(
        candidateRows.filter((row) => clusterTripIdSet.has(row.id)),
        cluster.dominantCatchupMinute
      ) || pickClosestRowToMinute(candidateRows, cluster.dominantCatchupMinute);
      if (!primaryRow?.id) {
        return null;
      }

      const primaryTripIds = [primaryRow.id];
      const linkedTripIds = candidateRows.map((row) => row.id).filter(Boolean);

      return {
        clusterId: cluster.clusterId,
        localLineId: cluster.localLineId,
        expressLineId: cluster.expressLineId,
        totalUnresolvedRiskMinutes: cluster.totalUnresolvedRiskMinutes,
        dominantSeverityMinutes: cluster.dominantSeverityMinutes,
        dominantCatchupMinute: cluster.dominantCatchupMinute,
        firstCatchupMinute: cluster.firstCatchupMinute,
        lastCatchupMinute: cluster.lastCatchupMinute,
        primaryTripIds,
        linkedTripIds,
        linkedTrips: candidateRows.map((row) => ({
          tripId: row.id,
          minute: row.minute
        })),
        anchorTripId: primaryRow.id
      };
    })
    .filter(Boolean);
}

function enumerateClusterShiftPlans(group, request) {
  const maxShiftMinutes = Math.max(0, clampNumber(request.maxLocalRetimeMinutes, 0));
  if (maxShiftMinutes <= 0) {
    return [];
  }

  const linkedTripIds = [...new Set(asArray(group.linkedTripIds).filter(Boolean))];
  const primaryTripIds = [...new Set(asArray(group.primaryTripIds).filter(Boolean))];
  const anchorIndex = Math.max(0, linkedTripIds.indexOf(group.anchorTripId));
  const buildUniformShifts = (tripIds, deltaMinutes) =>
    tripIds.map((tripId) => ({
      tripId,
      deltaMinutes
    }));
  const buildCenteredTaperShifts = (tripIds, deltaMinutes) => {
    const sign = deltaMinutes < 0 ? -1 : 1;
    const amplitude = Math.abs(deltaMinutes);
    return tripIds
      .map((tripId, index) => {
        const distance = Math.abs(index - anchorIndex);
        const shiftAmplitude = Math.max(0, amplitude - distance);
        if (!(shiftAmplitude > 0)) {
          return null;
        }
        return {
          tripId,
          deltaMinutes: sign * shiftAmplitude
        };
      })
      .filter(Boolean);
  };
  const buildDirectionalTaperShifts = (tripIds, deltaMinutes, direction) => {
    const sign = deltaMinutes < 0 ? -1 : 1;
    const amplitude = Math.abs(deltaMinutes);
    return tripIds
      .map((tripId, index) => {
        const distance = direction < 0
          ? anchorIndex - index
          : index - anchorIndex;
        if (distance < 0) {
          return null;
        }
        const shiftAmplitude = Math.max(0, amplitude - distance);
        if (!(shiftAmplitude > 0)) {
          return null;
        }
        return {
          tripId,
          deltaMinutes: sign * shiftAmplitude
        };
      })
      .filter(Boolean);
  };
  const buildFanoutShifts = (linkedTrips, amplitude) =>
    asArray(linkedTrips)
      .map((trip, index) => {
        if (!trip?.tripId || index === anchorIndex) {
          return null;
        }
        const distance = Math.abs(index - anchorIndex);
        const shiftAmplitude = Math.max(0, amplitude - (distance - 1));
        if (!(shiftAmplitude > 0)) {
          return null;
        }
        return {
          tripId: trip.tripId,
          deltaMinutes: index < anchorIndex ? -shiftAmplitude : shiftAmplitude
        };
      })
      .filter(Boolean);
  const plans = [];
  const seenPlanSignatures = new Set();
  const addPlan = (mode, shape, shifts) => {
    const normalizedShifts = asArray(shifts)
      .filter((shift) => shift?.tripId && shift.deltaMinutes)
      .map((shift) => ({
        tripId: shift.tripId,
        deltaMinutes: shift.deltaMinutes
      }));
    if (normalizedShifts.length === 0) {
      return;
    }

    const signature = normalizedShifts
      .map((shift) => `${shift.tripId}:${shift.deltaMinutes}`)
      .join("|");
    if (seenPlanSignatures.has(signature)) {
      return;
    }
    seenPlanSignatures.add(signature);
    plans.push({
      clusterId: group.clusterId,
      localLineId: group.localLineId,
      mode,
      shape,
      totalUnresolvedRiskMinutes: group.totalUnresolvedRiskMinutes,
      dominantSeverityMinutes: group.dominantSeverityMinutes,
      dominantCatchupMinute: group.dominantCatchupMinute,
      shifts: normalizedShifts
    });
  };

  const deltas = enumerateSymmetricStepDeltas(maxShiftMinutes, DEFAULT_LOCAL_RETIME_STEP_MINUTES);

  deltas.forEach((deltaMinutes) => {
    addPlan("primary", "single", buildUniformShifts(primaryTripIds, deltaMinutes));
    if (linkedTripIds.length > primaryTripIds.length) {
      addPlan("linked", "uniform", buildUniformShifts(linkedTripIds, deltaMinutes));
      addPlan("linked", "centeredTaper", buildCenteredTaperShifts(linkedTripIds, deltaMinutes));
      addPlan("linked", "backwardTaper", buildDirectionalTaperShifts(linkedTripIds, deltaMinutes, -1));
      addPlan("linked", "forwardTaper", buildDirectionalTaperShifts(linkedTripIds, deltaMinutes, 1));
    }
  });
  for (let amplitude = 1; amplitude <= maxShiftMinutes; amplitude += 1) {
    addPlan("linked", "fanout", buildFanoutShifts(group.linkedTrips, amplitude));
  }

  return plans;
}

function applyShiftPlan(workingRows, shiftPlan) {
  const shiftByTripId = new Map(
    asArray(shiftPlan?.shifts).map((shift) => [shift.tripId, shift.deltaMinutes])
  );
  return sortRowsByMinute(workingRows.map((row) => {
    if (!shiftByTripId.has(row.id)) {
      return row;
    }
    const deltaMinutes = shiftByTripId.get(row.id);
    return {
      ...row,
      minute: row.minute + deltaMinutes,
      note: [row.note, `cluster-retime:${shiftPlan.clusterId}:${shiftPlan.shape || shiftPlan.mode}:${deltaMinutes}`].filter(Boolean).join("|")
    };
  }));
}

function validateShiftPlan(normalizedInput, scenario, workingRows, shiftPlan) {
  const shiftedTripIdSet = new Set(asArray(shiftPlan?.shifts).map((shift) => shift.tripId));
  if (shiftedTripIdSet.size === 0) {
    return false;
  }

  for (const row of workingRows) {
    if (shiftedTripIdSet.has(row.id)
      && (row.minute < scenario.windowStartMinute || row.minute >= scenario.windowEndMinute)) {
      return false;
    }
  }

  const rowsByOrigin = new Map();
  workingRows.forEach((row) => {
    const line = normalizedInput.lineById.get(row.lineId);
    const originStationId = line?.originStationId || `line:${row.lineId}`;
    if (!rowsByOrigin.has(originStationId)) {
      rowsByOrigin.set(originStationId, []);
    }
    rowsByOrigin.get(originStationId).push(row);
  });

  for (const rows of rowsByOrigin.values()) {
    if (!hasSameOriginDepartureGap(rows)) {
      return false;
    }
  }

  return true;
}

function compareRetimedCandidateQuality(left, right) {
  return comparePlanQuality(left.plan, right.plan);
}

function dedupeRetimedCandidates(candidates) {
  const bestBySignature = new Map();
  candidates.forEach((candidate) => {
    const current = bestBySignature.get(candidate.rowSignature);
    if (!current || compareRetimedCandidateQuality(candidate, current) < 0) {
      bestBySignature.set(candidate.rowSignature, candidate);
    }
  });
  return [...bestBySignature.values()];
}

function buildPlanStateSignature(rows, virtualBypassStationIds, offsetDeltaMinutes = 0) {
  return `${buildWorkingRowsSignature(rows)}|offset:${quantizeMinuteToStep(offsetDeltaMinutes)}|virtual:${[...new Set(asArray(virtualBypassStationIds).filter(Boolean))].sort().join(",")}`;
}

function isAdjustableRow(row, request) {
  if (!row?.id || !row?.lineId) {
    return false;
  }
  if (request?.lockedLocalTripIds?.has(row.id)) {
    return false;
  }
  const adjustableLineIdSet = request?.adjustableLineIdSet;
  if (adjustableLineIdSet instanceof Set && adjustableLineIdSet.size > 0) {
    return adjustableLineIdSet.has(row.lineId);
  }
  return row.kind !== "express";
}

function buildStatePenaltyMetrics(rows, baselineRowById, actionLog, request = null) {
  const changedAdjustableRows = rows.filter((row) => {
    if (!isAdjustableRow(row, request)) {
      return false;
    }
    const baselineRow = baselineRowById.get(row.id);
    return baselineRow && baselineRow.minute !== row.minute;
  });
  const totalRetimedMinutes = changedAdjustableRows.reduce((sum, row) => {
    const baselineRow = baselineRowById.get(row.id);
    return sum + Math.abs((baselineRow?.minute ?? row.minute) - row.minute);
  }, 0);
  const rebuildSpanMinutes = asArray(actionLog)
    .filter((action) => action.type === "localWindowRebuild")
    .reduce((sum, action) => sum + Math.max(0, clampNumber(action.windowEndMinute, 0) - clampNumber(action.windowStartMinute, 0)), 0);

  return {
    retimedTripCount: changedAdjustableRows.length,
    totalRetimedMinutes: Number(totalRetimedMinutes.toFixed(2)),
    rebuildSpanMinutes: Number(rebuildSpanMinutes.toFixed(2))
  };
}

function buildPlanState(preparedContext, baseOptions, rows, offsetDeltaMinutes, virtualBypassStationIds, baselineRowById, actionLog = [], basePlanByStateSignature = null) {
  const configuredBypassStationIds = getConfiguredBypassStationIdsForLines(
    preparedContext.normalizedInput,
    preparedContext.scenario.adjustableLineIds
  );
  const uniqueVirtualBypassStationIds = [...new Set(asArray(virtualBypassStationIds).filter(Boolean))];
  const stateSignature = buildPlanStateSignature(rows, uniqueVirtualBypassStationIds, offsetDeltaMinutes);
  let cachedBasePlan = basePlanByStateSignature?.get(stateSignature) || null;
  if (!cachedBasePlan) {
    cachedBasePlan = evaluatePreparedScenarioRows(
      preparedContext,
      {
        ...baseOptions,
        virtualBypassStationIds: uniqueVirtualBypassStationIds,
        baseBypassStationIds: configuredBypassStationIds,
        useCandidateBypassPool: uniqueVirtualBypassStationIds.length > 0
      },
      rows,
      offsetDeltaMinutes
    );
    if (basePlanByStateSignature) {
      basePlanByStateSignature.set(stateSignature, cachedBasePlan);
    }
  }
  const penaltyMetrics = buildStatePenaltyMetrics(rows, baselineRowById, actionLog, {
    adjustableLineIdSet: preparedContext.scenario.adjustableLineIdSet
  });
  const plan = {
    ...cachedBasePlan,
    metrics: {
      ...cachedBasePlan.metrics,
      ...penaltyMetrics
    }
  };
  plan.score = scoreCatchupScenario(
    plan.objective || DEFAULT_OBJECTIVE,
    plan.metrics,
    clampNumber(plan.metrics.departureGapPenalty, 0)
  );
  return {
    rows,
    virtualBypassStationIds: uniqueVirtualBypassStationIds,
    offsetDeltaMinutes,
    plan,
    actionLog,
    stateSignature
  };
}

function comparePlanStateQuality(left, right) {
  return comparePlanQuality(left.plan, right.plan);
}

function dedupePlanStates(states) {
  const bestBySignature = new Map();
  states.forEach((state) => {
    const current = bestBySignature.get(state.stateSignature);
    if (!current || comparePlanStateQuality(state, current) < 0) {
      bestBySignature.set(state.stateSignature, state);
    }
  });
  return [...bestBySignature.values()];
}

function buildPlanStateStyleSignature(state) {
  const actionShapes = asArray(state?.actionLog)
    .map((action) => {
      if (action.type === "retimeVector") {
        return `${action.type}:${action.shape || "vector"}`;
      }
      if (action.type === "bypassSet") {
        return `${action.type}:${asArray(action.stationIds).length}`;
      }
      if (action.type === "expressOffset") {
        return `${action.type}:${Math.sign(clampNumber(action.deltaOffsetMinutes, 0))}`;
      }
      return action.type || "unknown";
    })
    .slice(-3)
    .join(",");
  return [
    `v${asArray(state?.virtualBypassStationIds).length}`,
    `r${clampNumber(state?.plan?.metrics?.retimedTripCount, 0)}`,
    actionShapes || "base"
  ].join("|");
}

function selectDiversePlanStates(states, beamWidth) {
  const sortedStates = dedupePlanStates(states).sort(comparePlanStateQuality);
  const selectedStates = [];
  const selectedSignatures = new Set();
  const selectedStyles = new Set();

  sortedStates.forEach((state) => {
    if (selectedStates.length >= beamWidth) {
      return;
    }
    const styleSignature = buildPlanStateStyleSignature(state);
    if (selectedStyles.has(styleSignature)) {
      return;
    }
    selectedStates.push(state);
    selectedSignatures.add(state.stateSignature);
    selectedStyles.add(styleSignature);
  });

  sortedStates.forEach((state) => {
    if (selectedStates.length >= beamWidth || selectedSignatures.has(state.stateSignature)) {
      return;
    }
    selectedStates.push(state);
    selectedSignatures.add(state.stateSignature);
  });

  return selectedStates;
}

function enumerateJointTripsPerHourVariants(request) {
  if (asArray(request.expressTripsPerHourCandidates).length > 0) {
    return request.expressTripsPerHourCandidates;
  }
  if (request.expressTripsPerHour !== null && request.expressTripsPerHour !== undefined) {
    return [request.expressTripsPerHour];
  }
  return [null];
}

function enumerateJointOffsetVariants(request, options = {}) {
  if (asArray(request.expressOffsetCandidates).length > 0) {
    return request.expressOffsetCandidates;
  }
  if (request.expressOffsetMinutes !== null) {
    return [request.expressOffsetMinutes];
  }
  if (request.expressTripsPerHour !== null) {
    return [0, 2, -2, 4, -4];
  }
  return quantizeOffsetVariants(options.offsetStepMinutes, options.maxOffsetMinutes);
}

function validateWorkingRows(normalizedInput, scenario, workingRows) {
  for (const row of workingRows) {
    if (row.minute < scenario.windowStartMinute || row.minute >= scenario.windowEndMinute) {
      return false;
    }
  }

  const rowsByOrigin = new Map();
  workingRows.forEach((row) => {
    const line = normalizedInput.lineById.get(row.lineId);
    const originStationId = line?.originStationId || `line:${row.lineId}`;
    if (!rowsByOrigin.has(originStationId)) {
      rowsByOrigin.set(originStationId, []);
    }
    rowsByOrigin.get(originStationId).push(row);
  });

  for (const rows of rowsByOrigin.values()) {
    if (!hasSameOriginDepartureGap(rows)) {
      return false;
    }
  }

  return true;
}

function retimeLocalTripsAroundClusters(normalizedInput, draft, baseOptions, initialRows, offsetDeltaMinutes, request) {
  const scenarioForValidation = buildAnalysisScenario(normalizedInput, draft, {
    ...baseOptions,
    stagedRowsOverride: rowsToStagedRows(initialRows)
  });
  if (!(request.maxLocalRetimeMinutes > 0)) {
    return {
      rows: initialRows,
      plan: evaluateScenarioRows(normalizedInput, draft, baseOptions, initialRows, offsetDeltaMinutes),
      iterations: 0,
      appliedShiftPlans: []
    };
  }

  const basePlan = evaluateScenarioRows(normalizedInput, draft, baseOptions, initialRows, offsetDeltaMinutes);
  const beamWidth = 3;
  const maxIterations = 2;
  let frontier = [{
    rows: initialRows,
    plan: basePlan,
    rowSignature: buildWorkingRowsSignature(initialRows),
    appliedShiftPlans: []
  }];
  let bestCandidate = frontier[0];
  let iterations = 0;

  while (iterations < maxIterations) {
    let expanded = [...frontier];
    let improved = false;

    frontier.forEach((candidate) => {
      const groups = buildClusterRetimeGroups(candidate.plan, candidate.rows, normalizedInput, request);
      groups.forEach((group) => {
        const shiftPlans = enumerateClusterShiftPlans(group, request);
        shiftPlans.forEach((shiftPlan) => {
          const shiftedRows = applyShiftPlan(candidate.rows, shiftPlan);
          if (!validateShiftPlan(normalizedInput, scenarioForValidation, shiftedRows, shiftPlan)) {
            return;
          }

          const shiftedPlan = evaluateScenarioRows(normalizedInput, draft, baseOptions, shiftedRows, offsetDeltaMinutes);
          improved = true;
          expanded.push({
            rows: shiftedRows,
            plan: shiftedPlan,
            rowSignature: buildWorkingRowsSignature(shiftedRows),
            appliedShiftPlans: [...candidate.appliedShiftPlans, shiftPlan]
          });
        });
      });
    });

    if (!improved) {
      break;
    }

    frontier = dedupeRetimedCandidates(expanded)
      .sort(compareRetimedCandidateQuality)
      .slice(0, beamWidth);
    if (frontier.length > 0 && compareRetimedCandidateQuality(frontier[0], bestCandidate) < 0) {
      bestCandidate = frontier[0];
    }
    iterations += 1;
  }

  return {
    rows: bestCandidate.rows,
    plan: bestCandidate.plan,
    iterations: bestCandidate.appliedShiftPlans.length,
    appliedShiftPlans: bestCandidate.appliedShiftPlans
  };
}

function buildRegionClusterMap(plan) {
  return new Map(asArray(plan.trunkProblemClusters).map((cluster) => [cluster.clusterId, cluster]));
}

function summarizeOptimizationRegions(plan) {
  const clusterById = buildRegionClusterMap(plan);
  return asArray(plan.optimizationRegions)
    .map((region) => {
      const regionClusters = asArray(region.clusterIds).map((clusterId) => clusterById.get(clusterId)).filter(Boolean);
      return {
        ...region,
        totalUnresolvedRiskMinutes: Number(regionClusters.reduce((sum, cluster) => sum + clampNumber(cluster.totalUnresolvedRiskMinutes, 0), 0).toFixed(2)),
        totalRobustnessRiskMinutes: Number(regionClusters.reduce((sum, cluster) => sum + clampNumber(cluster.totalRobustnessRiskMinutes, 0), 0).toFixed(2)),
        dominantSeverityMinutes: Math.max(...regionClusters.map((cluster) => clampNumber(cluster.dominantSeverityMinutes, 0)), 0),
        regionClusters
      };
    })
    .filter((region) => region.totalUnresolvedRiskMinutes > 0 || region.totalRobustnessRiskMinutes > 0)
    .sort((left, right) => {
      if (right.totalUnresolvedRiskMinutes !== left.totalUnresolvedRiskMinutes) {
        return right.totalUnresolvedRiskMinutes - left.totalUnresolvedRiskMinutes;
      }
      if (right.totalRobustnessRiskMinutes !== left.totalRobustnessRiskMinutes) {
        return right.totalRobustnessRiskMinutes - left.totalRobustnessRiskMinutes;
      }
      if (right.dominantSeverityMinutes !== left.dominantSeverityMinutes) {
        return right.dominantSeverityMinutes - left.dominantSeverityMinutes;
      }
      return left.windowStartMinute - right.windowStartMinute;
    });
}

function computeScheduleRegionRisk(region) {
  return Number((
    clampNumber(region?.totalUnresolvedRiskMinutes, 0)
    + clampNumber(region?.totalRobustnessRiskMinutes, 0) * 0.75
    + clampNumber(region?.dominantSeverityMinutes, 0) * 0.35
  ).toFixed(2));
}

function buildTripImpactSummary(regions) {
  const impactByTripId = new Map();
  asArray(regions).forEach((region) => {
    const regionRisk = computeScheduleRegionRisk(region);
    asArray(region.regionClusters).forEach((cluster) => {
      asArray(cluster.localTripIds).forEach((tripId) => {
        if (!tripId) {
          return;
        }
        const current = impactByTripId.get(tripId) || {
          tripId,
          regionIds: new Set(),
          clusterIds: new Set(),
          riskMinutes: 0
        };
        current.regionIds.add(region.regionId);
        current.clusterIds.add(cluster.clusterId);
        current.riskMinutes += regionRisk;
        impactByTripId.set(tripId, current);
      });
    });
  });

  impactByTripId.forEach((impact) => {
    impact.regionIds = [...impact.regionIds];
    impact.clusterIds = [...impact.clusterIds];
    impact.riskMinutes = Number(impact.riskMinutes.toFixed(2));
  });
  return impactByTripId;
}

export function buildScheduleProblem(planState, normalizedInput, scenario, request) {
  const rowsById = new Map(asArray(planState?.rows).map((row) => [row.id, row]));
  const localRowsByLineId = new Map();
  asArray(planState?.rows).forEach((row) => {
    if (!isAdjustableRow(row, request)) {
      return;
    }
    if (!localRowsByLineId.has(row.lineId)) {
      localRowsByLineId.set(row.lineId, []);
    }
    localRowsByLineId.get(row.lineId).push(row);
  });
  localRowsByLineId.forEach((rows) => {
    rows.sort((left, right) => left.minute - right.minute);
  });

  const regions = summarizeOptimizationRegions(planState.plan)
    .map((region) => ({
      ...region,
      riskScore: computeScheduleRegionRisk(region),
      regionClusters: asArray(region.regionClusters).map((cluster) => ({
        ...cluster,
        sourceRegionId: region.regionId
      }))
    }));
  const activeVirtualBypassStationIds = new Set(asArray(planState?.virtualBypassStationIds).filter(Boolean));
  const virtualBypassCandidatesByRegionId = new Map();
  regions.slice(0, 4).forEach((region) => {
    const candidates = rankVirtualBypassCandidates(normalizedInput, scenario, region.regionClusters)
      .filter((station) => station?.stationId && !activeVirtualBypassStationIds.has(station.stationId))
      .slice(0, 4);
    virtualBypassCandidatesByRegionId.set(region.regionId, candidates);
  });

  const totalUnresolvedRiskMinutes = Number(regions.reduce(
    (sum, region) => sum + clampNumber(region.totalUnresolvedRiskMinutes, 0),
    0
  ).toFixed(2));
  const totalRobustnessRiskMinutes = Number(regions.reduce(
    (sum, region) => sum + clampNumber(region.totalRobustnessRiskMinutes, 0),
    0
  ).toFixed(2));
  const tripImpacts = buildTripImpactSummary(regions);
  const bottlenecks = [];
  if (totalUnresolvedRiskMinutes > 0 && request.maxAdditionalBypassStations <= activeVirtualBypassStationIds.size) {
    bottlenecks.push("noBypassCapacity");
  }
  if (totalRobustnessRiskMinutes > totalUnresolvedRiskMinutes) {
    bottlenecks.push("robustness");
  }
  if (tripImpacts.size > 0) {
    bottlenecks.push("localRetime");
  }

  return {
    kind: "scheduleProblem",
    rowsById,
    localRowsByLineId,
    regions,
    tripImpacts,
    virtualBypassCandidatesByRegionId,
    activeVirtualBypassStationIds,
    remainingVirtualBypassCapacity: Math.max(0, request.maxAdditionalBypassStations - activeVirtualBypassStationIds.size),
    totalUnresolvedRiskMinutes,
    totalRobustnessRiskMinutes,
    bottlenecks
  };
}

function normalizeTripDeltas(tripDeltas) {
  const deltaByTripId = new Map();
  asArray(tripDeltas).forEach((entry) => {
    if (!entry?.tripId) {
      return;
    }
    const deltaMinutes = quantizeMinuteToStep(entry.deltaMinutes, DEFAULT_LOCAL_RETIME_STEP_MINUTES);
    if (!deltaMinutes) {
      return;
    }
    deltaByTripId.set(entry.tripId, clampNumber(deltaByTripId.get(entry.tripId), 0) + deltaMinutes);
  });
  return [...deltaByTripId.entries()]
    .map(([tripId, deltaMinutes]) => ({
      tripId,
      deltaMinutes: quantizeMinuteToStep(deltaMinutes, DEFAULT_LOCAL_RETIME_STEP_MINUTES)
    }))
    .filter((entry) => entry.deltaMinutes !== 0)
    .sort((left, right) => left.tripId.localeCompare(right.tripId));
}

function buildScheduleActionSignature(action) {
  if (action.type === "retimeVector") {
    return `${action.type}:${action.shape}:${asArray(action.tripDeltas).map((entry) => `${entry.tripId}@${entry.deltaMinutes}`).join("|")}`;
  }
  if (action.type === "bypassSet") {
    return `${action.type}:${asArray(action.stationIds).slice().sort().join("|")}`;
  }
  if (action.type === "expressOffset") {
    return `${action.type}:${action.deltaOffsetMinutes}`;
  }
  return JSON.stringify(action);
}

function addScheduleAction(actions, seenSignatures, action) {
  const normalizedAction = action.type === "retimeVector"
    ? {
      ...action,
      tripDeltas: normalizeTripDeltas(action.tripDeltas)
    }
    : action;
  if (normalizedAction.type === "retimeVector" && normalizedAction.tripDeltas.length === 0) {
    return;
  }
  if (normalizedAction.type === "bypassSet" && asArray(normalizedAction.stationIds).length === 0) {
    return;
  }

  const signature = buildScheduleActionSignature(normalizedAction);
  if (seenSignatures.has(signature)) {
    return;
  }
  seenSignatures.add(signature);
  actions.push({
    ...normalizedAction,
    actionSignature: signature,
    totalDeltaMinutes: normalizedAction.type === "retimeVector"
      ? Number(asArray(normalizedAction.tripDeltas).reduce((sum, entry) => sum + Math.abs(entry.deltaMinutes), 0).toFixed(2))
      : 0
  });
}

function buildRetimeActionFromRows(problem, cluster, rows, tripDeltaFactory, shape, reason, addAction) {
  const targetRegionIds = [...new Set(asArray([cluster.sourceRegionId]).filter(Boolean))];
  const tripDeltas = asArray(rows)
    .map((row, index) => ({
      tripId: row.id,
      deltaMinutes: tripDeltaFactory(row, index)
    }))
    .filter((entry) => entry.tripId && entry.deltaMinutes);
  addAction({
    type: "retimeVector",
    shape,
    reason,
    localLineId: cluster.localLineId,
    targetRegionIds,
    clusterIds: [cluster.clusterId],
    riskScore: targetRegionIds.reduce((sum, regionId) => {
      const region = problem.regions.find((candidate) => candidate.regionId === regionId);
      return sum + computeScheduleRegionRisk(region);
    }, 0),
    tripDeltas
  });
}

function enumerateClusterScheduleRetimeActions(problem, cluster, request, addAction) {
  const maxShiftMinutes = Math.max(0, clampNumber(request.maxLocalRetimeMinutes, 0));
  if (!(maxShiftMinutes > 0)) {
    return;
  }
  const lineRows = problem.localRowsByLineId.get(cluster.localLineId) || [];
  const candidateRows = collectClusterShiftCandidateRows(lineRows, cluster, request);
  if (candidateRows.length === 0) {
    return;
  }

  const clusterTripIdSet = new Set(asArray(cluster.localTripIds).filter((tripId) => !request.lockedLocalTripIds.has(tripId)));
  const anchorRow = pickClosestRowToMinute(
    candidateRows.filter((row) => clusterTripIdSet.has(row.id)),
    cluster.dominantCatchupMinute
  ) || pickClosestRowToMinute(candidateRows, cluster.dominantCatchupMinute);
  if (!anchorRow?.id) {
    return;
  }
  const anchorIndex = Math.max(0, candidateRows.findIndex((row) => row.id === anchorRow.id));
  const deltas = enumerateSymmetricStepDeltas(maxShiftMinutes, DEFAULT_LOCAL_RETIME_STEP_MINUTES);
  let emitted = 0;
  const emit = (rows, factory, shape, reason) => {
    if (emitted >= DEFAULT_MAX_REGION_RETIME_ACTIONS) {
      return;
    }
    buildRetimeActionFromRows(problem, cluster, rows, factory, shape, reason, addAction);
    emitted += 1;
  };

  deltas.forEach((deltaMinutes) => {
    emit([anchorRow], () => deltaMinutes, "singleAnchor", "reduceCatchup");
  });

  deltas.forEach((deltaMinutes) => {
    if (candidateRows.length >= 2) {
      emit(candidateRows, () => deltaMinutes, "windowUniform", "reduceCatchup");
      const sign = deltaMinutes < 0 ? -1 : 1;
      const amplitude = Math.abs(deltaMinutes);
      emit(candidateRows, (_row, index) => {
        const distance = Math.abs(index - anchorIndex);
        const shift = Math.max(0, amplitude - distance * DEFAULT_LOCAL_RETIME_STEP_MINUTES);
        return sign * shift;
      }, "windowTaper", "improveRobustness");
    }
  });

  enumerateSymmetricStepDeltas(maxShiftMinutes, DEFAULT_LOCAL_RETIME_STEP_MINUTES)
    .filter((deltaMinutes) => deltaMinutes > 0)
    .forEach((deltaMinutes) => {
      if (candidateRows.length >= 2) {
        emit(candidateRows, (_row, index) => index <= anchorIndex ? -deltaMinutes : deltaMinutes, "openGap", "moveExpressBetweenLocalTrips");
        emit(candidateRows, (_row, index) => index <= anchorIndex ? deltaMinutes : -deltaMinutes, "closeGap", "protectHeadway");
      }

      const cascadeRows = candidateRows.slice(anchorIndex, Math.min(candidateRows.length, anchorIndex + 3));
      if (cascadeRows.length >= 2) {
        emit(cascadeRows, (_row, index) => Math.max(0, deltaMinutes - index * DEFAULT_LOCAL_RETIME_STEP_MINUTES), "cascade", "reduceCatchupChain");
      }
      const backwardCascadeRows = candidateRows.slice(Math.max(0, anchorIndex - 2), anchorIndex + 1);
      if (backwardCascadeRows.length >= 2) {
        emit(backwardCascadeRows, (_row, index) => -Math.max(0, deltaMinutes - (backwardCascadeRows.length - 1 - index) * DEFAULT_LOCAL_RETIME_STEP_MINUTES), "cascade", "reduceCatchupChain");
      }
    });
}

function enumerateInterRegionRetimeActions(problem, request, addAction) {
  const maxShiftMinutes = Math.max(0, clampNumber(request.maxLocalRetimeMinutes, 0));
  if (!(maxShiftMinutes > 0)) {
    return;
  }

  const clustersByLineId = new Map();
  problem.regions.slice(0, 4).forEach((region) => {
    asArray(region.regionClusters).forEach((cluster) => {
      if (!cluster?.localLineId) {
        return;
      }
      if (!clustersByLineId.has(cluster.localLineId)) {
        clustersByLineId.set(cluster.localLineId, []);
      }
      clustersByLineId.get(cluster.localLineId).push(cluster);
    });
  });

  clustersByLineId.forEach((clusters) => {
    const orderedClusters = [...clusters].sort((left, right) =>
      clampNumber(left.dominantCatchupMinute, 0) - clampNumber(right.dominantCatchupMinute, 0)
    );
    for (let index = 0; index + 1 < orderedClusters.length; index += 1) {
      const leftCluster = orderedClusters[index];
      const rightCluster = orderedClusters[index + 1];
      if (Math.abs(
        clampNumber(rightCluster.dominantCatchupMinute, 0) - clampNumber(leftCluster.dominantCatchupMinute, 0)
      ) > DEFAULT_MAX_REGION_SPAN_MINUTES) {
        continue;
      }

      const lineRows = problem.localRowsByLineId.get(leftCluster.localLineId) || [];
      const leftRows = collectClusterShiftCandidateRows(lineRows, leftCluster, request);
      const rightRows = collectClusterShiftCandidateRows(lineRows, rightCluster, request);
      const leftAnchor = pickClosestRowToMinute(leftRows, leftCluster.dominantCatchupMinute);
      const rightAnchor = pickClosestRowToMinute(rightRows, rightCluster.dominantCatchupMinute);
      if (!leftAnchor?.id || !rightAnchor?.id || leftAnchor.id === rightAnchor.id) {
        continue;
      }

      enumerateSymmetricStepDeltas(maxShiftMinutes, DEFAULT_LOCAL_RETIME_STEP_MINUTES)
        .filter((deltaMinutes) => deltaMinutes > 0)
        .forEach((deltaMinutes) => {
          const targetRegionIds = [...new Set([leftCluster.sourceRegionId, rightCluster.sourceRegionId].filter(Boolean))];
          const riskScore = targetRegionIds.reduce((sum, regionId) => {
            const region = problem.regions.find((candidate) => candidate.regionId === regionId);
            return sum + computeScheduleRegionRisk(region);
          }, 0);
          addAction({
            type: "retimeVector",
            shape: "openGap",
            reason: "moveExpressBetweenLocalTrips",
            localLineId: leftCluster.localLineId,
            targetRegionIds,
            clusterIds: [leftCluster.clusterId, rightCluster.clusterId],
            riskScore,
            tripDeltas: [
              { tripId: leftAnchor.id, deltaMinutes: -deltaMinutes },
              { tripId: rightAnchor.id, deltaMinutes }
            ]
          });
          addAction({
            type: "retimeVector",
            shape: "closeGap",
            reason: "protectHeadway",
            localLineId: leftCluster.localLineId,
            targetRegionIds,
            clusterIds: [leftCluster.clusterId, rightCluster.clusterId],
            riskScore,
            tripDeltas: [
              { tripId: leftAnchor.id, deltaMinutes },
              { tripId: rightAnchor.id, deltaMinutes: -deltaMinutes }
            ]
          });
        });
      break;
    }
  });
}

function enumerateBypassSetScheduleActions(problem, request, addAction) {
  if (!(problem.remainingVirtualBypassCapacity > 0)) {
    return;
  }

  const firstChoices = [];
  problem.regions.slice(0, 4).forEach((region) => {
    const candidates = asArray(problem.virtualBypassCandidatesByRegionId.get(region.regionId)).slice(0, 3);
    candidates.forEach((station) => {
      firstChoices.push({
        region,
        station
      });
      addAction({
        type: "bypassSet",
        shape: "singleStation",
        reason: "resolveWithoutRetime",
        targetRegionIds: [region.regionId],
        stationIds: [station.stationId],
        riskScore: computeScheduleRegionRisk(region)
      });
    });
  });

  if (problem.remainingVirtualBypassCapacity >= 2 && firstChoices.length >= 2) {
    for (let leftIndex = 0; leftIndex < firstChoices.length; leftIndex += 1) {
      for (let rightIndex = leftIndex + 1; rightIndex < firstChoices.length; rightIndex += 1) {
        const left = firstChoices[leftIndex];
        const right = firstChoices[rightIndex];
        if (left.station.stationId === right.station.stationId || left.region.regionId === right.region.regionId) {
          continue;
        }
        addAction({
          type: "bypassSet",
          shape: "pairedStations",
          reason: "resolveWithoutRetime",
          targetRegionIds: [left.region.regionId, right.region.regionId],
          stationIds: [left.station.stationId, right.station.stationId],
          riskScore: computeScheduleRegionRisk(left.region) + computeScheduleRegionRisk(right.region)
        });
        return;
      }
    }
  }
}

function enumerateExpressOffsetScheduleActions(planState, request, addAction) {
  if (request.expressOffsetMinutes !== null || asArray(request.expressOffsetCandidates).length > 0) {
    return;
  }

  [-4, -2, 2, 4].forEach((deltaOffsetMinutes) => {
    addAction({
      type: "expressOffset",
      shape: "offsetStep",
      reason: "moveExpressBetweenLocalTrips",
      deltaOffsetMinutes,
      targetRegionIds: [],
      riskScore: clampNumber(planState?.plan?.metrics?.totalUnresolvedRiskMinutes, 0)
        + clampNumber(planState?.plan?.metrics?.totalRobustnessRiskMinutes, 0) * 0.75
    });
  });
}

function compareScheduleActions(left, right) {
  if (clampNumber(right.riskScore, 0) !== clampNumber(left.riskScore, 0)) {
    return clampNumber(right.riskScore, 0) - clampNumber(left.riskScore, 0);
  }
  const typeRank = {
    bypassSet: 0,
    retimeVector: 1,
    expressOffset: 2
  };
  const leftTypeRank = typeRank[left.type] ?? 9;
  const rightTypeRank = typeRank[right.type] ?? 9;
  if (leftTypeRank !== rightTypeRank) {
    return leftTypeRank - rightTypeRank;
  }
  if (clampNumber(left.totalDeltaMinutes, 0) !== clampNumber(right.totalDeltaMinutes, 0)) {
    return clampNumber(left.totalDeltaMinutes, 0) - clampNumber(right.totalDeltaMinutes, 0);
  }
  return buildScheduleActionSignature(left).localeCompare(buildScheduleActionSignature(right));
}

export function enumerateUnifiedScheduleActions(problem, planState, normalizedInput, scenario, request) {
  const actions = [];
  const seenSignatures = new Set();
  const addAction = (action) => addScheduleAction(actions, seenSignatures, action);

  problem.regions.slice(0, 2).forEach((region) => {
    asArray(region.regionClusters)
      .slice()
      .sort((left, right) => {
        const leftRisk = clampNumber(left.totalUnresolvedRiskMinutes, 0) + clampNumber(left.totalRobustnessRiskMinutes, 0) * 0.75;
        const rightRisk = clampNumber(right.totalUnresolvedRiskMinutes, 0) + clampNumber(right.totalRobustnessRiskMinutes, 0) * 0.75;
        return rightRisk - leftRisk;
      })
      .slice(0, 1)
      .forEach((cluster) => {
        enumerateClusterScheduleRetimeActions(problem, cluster, request, addAction);
      });
  });

  enumerateInterRegionRetimeActions(problem, request, addAction);
  enumerateBypassSetScheduleActions(problem, request, addAction);
  enumerateExpressOffsetScheduleActions(planState, request, addAction);

  return actions
    .sort(compareScheduleActions)
    .slice(0, request.maxScheduleActions || DEFAULT_MAX_SCHEDULE_ACTIONS);
}

function summarizeScheduleProblem(problem) {
  return {
    totalUnresolvedRiskMinutes: problem.totalUnresolvedRiskMinutes,
    totalRobustnessRiskMinutes: problem.totalRobustnessRiskMinutes,
    bottlenecks: problem.bottlenecks,
    regionCount: problem.regions.length,
    affectedTripCount: problem.tripImpacts.size,
    regions: problem.regions.slice(0, 5).map((region) => ({
      regionId: region.regionId,
      riskScore: region.riskScore,
      totalUnresolvedRiskMinutes: region.totalUnresolvedRiskMinutes,
      totalRobustnessRiskMinutes: region.totalRobustnessRiskMinutes,
      clusterCount: asArray(region.regionClusters).length
    }))
  };
}

function buildStructuredScheduleActions(actionLog, rows, normalizedInput = null) {
  const rowById = new Map(asArray(rows).map((row) => [row.id, row]));
  return asArray(actionLog).map((action) => {
    const affectedTripIds = action.type === "retimeVector"
      ? asArray(action.tripDeltas).map((entry) => entry.tripId)
      : asArray(action.shiftPlan?.shifts).map((entry) => entry.tripId);
    const stationLineIds = asArray(action.stationIds)
      .map((stationId) =>
        normalizedInput?.stationById?.get(stationId)?.lineId
        || normalizedInput?.candidateBypassStations?.find((station) => station.stationId === stationId)?.lineId
        || normalizedInput?.configuredBypassStations?.find((station) => station.stationId === stationId)?.lineId
        || ""
      );
    const affectedLineIds = [...new Set([
      action.lineId,
      action.localLineId,
      action.shiftPlan?.localLineId,
      ...stationLineIds,
      ...affectedTripIds.map((tripId) => rowById.get(tripId)?.lineId)
    ].filter(Boolean))];
    const deltaPattern = action.type === "retimeVector"
      ? asArray(action.tripDeltas).map((entry) => entry.deltaMinutes)
      : asArray(action.shiftPlan?.shifts).map((entry) => entry.deltaMinutes);
    return {
      actionType: action.type,
      type: action.type,
      shape: action.shape || action.shiftPlan?.shape || "",
      reason: action.reason || "",
      reasonRegionIds: asArray(action.targetRegionIds),
      targetRegionIds: asArray(action.targetRegionIds),
      reasonClusterIds: asArray(action.clusterIds),
      clusterIds: asArray(action.clusterIds),
      stationIds: asArray(action.stationIds),
      affectedLineIds,
      affectedLineId: affectedLineIds[0] || "",
      affectedTripIds,
      tripIds: affectedTripIds,
      deltaPattern,
      deltaMinutes: deltaPattern.reduce((sum, value) => sum + Math.abs(clampNumber(value, 0)), 0),
      deltaOffsetMinutes: action.deltaOffsetMinutes || 0,
      riskScore: clampNumber(action.riskScore, 0)
    };
  });
}

function collectWindowRebuildCandidateRows(planState, cluster, request) {
  const localLineId = cluster.localLineId;
  const windowHalfSpan = DEFAULT_MAX_REBUILD_WINDOW_MINUTES / 2;
  const windowStartMinute = Math.max(0, clampNumber(cluster.dominantCatchupMinute, 0) - windowHalfSpan);
  const windowEndMinute = clampNumber(cluster.dominantCatchupMinute, 0) + windowHalfSpan;
  const lineRows = sortRowsByMinute(
    planState.rows.filter((row) =>
      row.lineId === localLineId
      && isAdjustableRow(row, request)
    )
  );
  const rowIndexById = new Map(lineRows.map((row, index) => [row.id, index]));
  const candidateIndexSet = new Set(
    lineRows
      .map((row, index) => ({ row, index }))
      .filter(({ row }) => row.minute >= windowStartMinute && row.minute <= windowEndMinute)
      .map(({ index }) => index)
  );

  asArray(cluster.localTripIds).forEach((tripId) => {
    if (rowIndexById.has(tripId)) {
      candidateIndexSet.add(rowIndexById.get(tripId));
    }
  });

  if (candidateIndexSet.size === 0 && lineRows.length > 0) {
    let bestIndex = 0;
    let bestDistance = Infinity;
    lineRows.forEach((row, index) => {
      const distance = Math.abs(row.minute - clampNumber(cluster.dominantCatchupMinute, row.minute));
      if (distance < bestDistance) {
        bestDistance = distance;
        bestIndex = index;
      }
    });
    candidateIndexSet.add(bestIndex);
  }

  const orderedIndexes = [...candidateIndexSet].sort((left, right) => left - right);
  let leftIndex = orderedIndexes.length > 0 ? orderedIndexes[0] : 0;
  let rightIndex = orderedIndexes.length > 0 ? orderedIndexes[orderedIndexes.length - 1] : -1;
  while (orderedIndexes.length < DEFAULT_MAX_REBUILD_TRIPS && (leftIndex > 0 || rightIndex + 1 < lineRows.length)) {
    if (leftIndex > 0) {
      leftIndex -= 1;
      if (!orderedIndexes.includes(leftIndex)) {
        orderedIndexes.unshift(leftIndex);
      }
    }
    if (orderedIndexes.length >= DEFAULT_MAX_REBUILD_TRIPS) {
      break;
    }
    if (rightIndex + 1 < lineRows.length) {
      rightIndex += 1;
      if (!orderedIndexes.includes(rightIndex)) {
        orderedIndexes.push(rightIndex);
      }
    }
  }

  return orderedIndexes
    .slice(0, DEFAULT_MAX_REBUILD_TRIPS)
    .map((index) => lineRows[index])
    .filter(Boolean);
}

function collectCoupledWindowRebuildCandidateRows(planState, clusters, request) {
  const rowById = new Map();
  asArray(clusters).forEach((cluster) => {
    collectWindowRebuildCandidateRows(planState, cluster, request).forEach((row) => {
      if (row?.id) {
        rowById.set(row.id, row);
      }
    });
  });

  const rows = sortRowsByMinute([...rowById.values()]);
  if (rows.length <= DEFAULT_MAX_COUPLED_REBUILD_TRIPS) {
    return rows;
  }

  const dominantMinute = asArray(clusters).length > 0
    ? asArray(clusters).reduce((sum, cluster) => sum + clampNumber(cluster.dominantCatchupMinute, 0), 0) / clusters.length
    : rows[Math.floor(rows.length / 2)]?.minute ?? 0;
  return rows
    .map((row) => ({
      row,
      distance: Math.abs(row.minute - dominantMinute)
    }))
    .sort((left, right) => {
      if (left.distance !== right.distance) {
        return left.distance - right.distance;
      }
      return left.row.minute - right.row.minute;
    })
    .slice(0, DEFAULT_MAX_COUPLED_REBUILD_TRIPS)
    .map((entry) => entry.row)
    .sort((left, right) => left.minute - right.minute);
}

function buildRegionShiftActions(planState, normalizedInput, request) {
  const actions = [];
  const summarizedRegions = summarizeOptimizationRegions(planState.plan);
  summarizedRegions
    .slice(0, 2)
    .forEach((region) => {
      const groups = asArray(region.regionClusters).flatMap((cluster) =>
        buildClusterRetimeGroups(
          { trunkProblemClusters: [cluster] },
          planState.rows,
          normalizedInput,
          request
        )
      );
      groups.forEach((group) => {
        enumerateClusterShiftPlans(group, request).forEach((shiftPlan) => {
          actions.push({
            type: "clusterShift",
            regionId: region.regionId,
            shiftPlan
          });
        });
      });
    });

  const globalGroups = summarizedRegions
    .slice(0, 4)
    .flatMap((region) =>
      asArray(region.regionClusters).flatMap((cluster) =>
        buildClusterRetimeGroups(
          { trunkProblemClusters: [cluster] },
          planState.rows,
          normalizedInput,
          request
        ).map((group) => ({
          ...group,
          sourceRegionId: region.regionId
        }))
      )
    );
  const groupsByLineId = new Map();
  globalGroups.forEach((group) => {
    if (!group?.localLineId) {
      return;
    }
    if (!groupsByLineId.has(group.localLineId)) {
      groupsByLineId.set(group.localLineId, []);
    }
    groupsByLineId.get(group.localLineId).push(group);
  });
  groupsByLineId.forEach((groups) => {
    const orderedGroups = [...groups]
      .sort((left, right) => clampNumber(left.dominantCatchupMinute, 0) - clampNumber(right.dominantCatchupMinute, 0))
      .slice(0, 4);
    for (let index = 0; index + 1 < orderedGroups.length; index += 1) {
      const leftGroup = orderedGroups[index];
      const rightGroup = orderedGroups[index + 1];
      const leftTripId = leftGroup.primaryTripIds[0] || "";
      const rightTripId = rightGroup.primaryTripIds[0] || "";
      if (!leftTripId || !rightTripId || leftTripId === rightTripId) {
        continue;
      }
      if (Math.abs(
        clampNumber(rightGroup.dominantCatchupMinute, 0) - clampNumber(leftGroup.dominantCatchupMinute, 0)
      ) > DEFAULT_MAX_REGION_SPAN_MINUTES) {
        continue;
      }

      enumerateSymmetricStepDeltas(request.maxLocalRetimeMinutes, DEFAULT_LOCAL_RETIME_STEP_MINUTES)
        .filter((deltaMinutes) => deltaMinutes > 0)
        .forEach((deltaMinutes) => {
          actions.push({
            type: "clusterShift",
            regionId: `${leftGroup.sourceRegionId}+${rightGroup.sourceRegionId}`,
            shiftPlan: {
              clusterId: `${leftGroup.clusterId}+${rightGroup.clusterId}`,
              localLineId: leftGroup.localLineId,
              mode: "paired",
              shape: "pairedPushApart",
              totalUnresolvedRiskMinutes: Number((
                clampNumber(leftGroup.totalUnresolvedRiskMinutes, 0)
                + clampNumber(rightGroup.totalUnresolvedRiskMinutes, 0)
              ).toFixed(2)),
              dominantSeverityMinutes: Math.max(
                clampNumber(leftGroup.dominantSeverityMinutes, 0),
                clampNumber(rightGroup.dominantSeverityMinutes, 0)
              ),
              shifts: [
                {
                  tripId: leftTripId,
                  deltaMinutes: -deltaMinutes
                },
                {
                  tripId: rightTripId,
                  deltaMinutes
                }
              ]
            }
          });
          actions.push({
            type: "clusterShift",
            regionId: `${leftGroup.sourceRegionId}+${rightGroup.sourceRegionId}`,
            shiftPlan: {
              clusterId: `${leftGroup.clusterId}+${rightGroup.clusterId}`,
              localLineId: leftGroup.localLineId,
              mode: "paired",
              shape: "pairedPullTogether",
              totalUnresolvedRiskMinutes: Number((
                clampNumber(leftGroup.totalUnresolvedRiskMinutes, 0)
                + clampNumber(rightGroup.totalUnresolvedRiskMinutes, 0)
              ).toFixed(2)),
              dominantSeverityMinutes: Math.max(
                clampNumber(leftGroup.dominantSeverityMinutes, 0),
                clampNumber(rightGroup.dominantSeverityMinutes, 0)
              ),
              shifts: [
                {
                  tripId: leftTripId,
                  deltaMinutes
                },
                {
                  tripId: rightTripId,
                  deltaMinutes: -deltaMinutes
                }
              ]
            }
          });
        });
      break;
    }
  });
  return actions;
}

function enumerateLocalWindowRebuildActions(planState, normalizedInput, request) {
  const actions = [];
  summarizeOptimizationRegions(planState.plan)
    .slice(0, 2)
    .forEach((region) => {
      region.regionClusters
        .slice()
        .sort((left, right) => {
          if (right.totalUnresolvedRiskMinutes !== left.totalUnresolvedRiskMinutes) {
            return right.totalUnresolvedRiskMinutes - left.totalUnresolvedRiskMinutes;
          }
          return right.dominantSeverityMinutes - left.dominantSeverityMinutes;
        })
        .slice(0, 1)
        .forEach((cluster) => {
          const candidateRows = collectWindowRebuildCandidateRows(planState, cluster, request);
          if (candidateRows.length < 2) {
            return;
          }

          actions.push({
            type: "localWindowRebuild",
            regionId: region.regionId,
            clusterId: cluster.clusterId,
            lineId: cluster.localLineId,
            tripIds: candidateRows.map((row) => row.id),
            windowStartMinute: candidateRows[0].minute,
            windowEndMinute: candidateRows[candidateRows.length - 1].minute,
            anchorMode: "keep-center"
          });
        });
    });
  return actions;
}

function enumerateCoupledWindowRebuildActions(planState, normalizedInput, request) {
  const actions = [];
  const clusterById = buildRegionClusterMap(planState.plan);
  const regionClusters = asArray(planState.plan.optimizationRegions)
    .flatMap((region) =>
      asArray(region.clusterIds)
        .map((clusterId) => clusterById.get(clusterId))
        .filter((cluster) =>
          cluster
          && (
            clampNumber(cluster.totalUnresolvedRiskMinutes, 0) > 0
            || clampNumber(cluster.totalRobustnessRiskMinutes, 0) > 0
          )
        )
        .map((cluster) => ({
          ...cluster,
          sourceRegionId: region.regionId
        }))
    );
  const clustersByLineId = new Map();
  regionClusters.forEach((cluster) => {
    if (!cluster?.localLineId) {
      return;
    }
    if (!clustersByLineId.has(cluster.localLineId)) {
      clustersByLineId.set(cluster.localLineId, []);
    }
    clustersByLineId.get(cluster.localLineId).push(cluster);
  });

  clustersByLineId.forEach((clusters, lineId) => {
    const orderedClusters = [...clusters]
      .sort((left, right) => left.firstCatchupMinute - right.firstCatchupMinute)
      .slice(0, 4);
    for (let index = 0; index + 1 < orderedClusters.length; index += 1) {
      const pair = [orderedClusters[index], orderedClusters[index + 1]];
      if (Math.abs(
        clampNumber(pair[1].dominantCatchupMinute, 0) - clampNumber(pair[0].dominantCatchupMinute, 0)
      ) > DEFAULT_MAX_REGION_SPAN_MINUTES) {
        continue;
      }

      const candidateRows = collectCoupledWindowRebuildCandidateRows(planState, pair, request);
      if (candidateRows.length < 3) {
        continue;
      }

      actions.push({
        type: "coupledWindowRebuild",
        regionId: `${pair[0].sourceRegionId}+${pair[1].sourceRegionId}`,
        clusterIds: pair.map((cluster) => cluster.clusterId),
        lineId,
        tripIds: candidateRows.map((row) => row.id),
        windowStartMinute: candidateRows[0].minute,
        windowEndMinute: candidateRows[candidateRows.length - 1].minute,
        anchorMode: "keep-endpoints"
      });
      break;
    }
  });

  return actions;
}

function rankRegionVirtualBypassActions(planState, normalizedInput, scenario, request) {
  const actions = [];
  if (!(request.maxAdditionalBypassStations > planState.virtualBypassStationIds.length)) {
    return actions;
  }

  summarizeOptimizationRegions(planState.plan)
    .slice(0, 2)
    .forEach((region) => {
      const ranked = rankVirtualBypassCandidates(normalizedInput, scenario, region.regionClusters)
        .filter((station) =>
          !planState.virtualBypassStationIds.includes(station.stationId)
        )
        .slice(0, 2);
      ranked.forEach((station) => {
        actions.push({
          type: "addVirtualBypass",
          regionId: region.regionId,
          stationIds: [station.stationId]
        });
      });
    });

  return actions;
}

function applyLocalWindowRebuildAction(workingRows, action, request) {
  const actionTripIds = new Set(asArray(action.tripIds).filter(Boolean));
  const targetRows = sortRowsByMinute(workingRows.filter((row) => actionTripIds.has(row.id)));
  if (targetRows.length < 2) {
    return workingRows;
  }

  const firstMinute = targetRows[0].minute;
  const lastMinute = targetRows[targetRows.length - 1].minute;
  const intervalMinutes = (lastMinute - firstMinute) / Math.max(1, targetRows.length - 1);
  const maxMoveMinutes = Math.max(1, request.maxLocalRetimeMinutes);
  const rebuiltMinuteByTripId = new Map();
  const centerIndex = Math.floor(targetRows.length / 2);
  const centerMinute = targetRows[centerIndex].minute;
  const startFromCenterMinute = centerMinute - (intervalMinutes * centerIndex);

  targetRows.forEach((row, index) => {
    let targetMinute = Math.round(firstMinute + (intervalMinutes * index));
    if (action.anchorMode === "keep-center") {
      targetMinute = Math.round(startFromCenterMinute + (intervalMinutes * index));
    } else if (action.anchorMode === "keep-endpoints") {
      if (index === 0) {
        targetMinute = firstMinute;
      } else if (index === targetRows.length - 1) {
        targetMinute = lastMinute;
      }
    }
    const limitedMinute = Math.max(row.minute - maxMoveMinutes, Math.min(row.minute + maxMoveMinutes, targetMinute));
    rebuiltMinuteByTripId.set(row.id, quantizeMinuteToStep(limitedMinute));
  });

  return sortRowsByMinute(workingRows.map((row) => (
    rebuiltMinuteByTripId.has(row.id)
      ? {
        ...row,
        minute: rebuiltMinuteByTripId.get(row.id),
        note: [row.note, `window-rebuild:${action.regionId}`].filter(Boolean).join("|")
      }
      : row
  )));
}

function applyRetimeVectorAction(workingRows, action) {
  const deltaByTripId = new Map(
    asArray(action?.tripDeltas).map((entry) => [entry.tripId, entry.deltaMinutes])
  );
  return sortRowsByMinute(workingRows.map((row) => {
    if (!deltaByTripId.has(row.id)) {
      return row;
    }
    const deltaMinutes = deltaByTripId.get(row.id);
    return {
      ...row,
      minute: row.minute + deltaMinutes,
      note: [row.note, `schedule-retime:${action.shape || "vector"}:${deltaMinutes}`].filter(Boolean).join("|")
    };
  }));
}

function applyScheduleAction(planState, action, preparedContext, baseOptions, request, baselineRowById, basePlanByStateSignature) {
  if (action.type === "retimeVector") {
    const shiftedRows = applyRetimeVectorAction(planState.rows, action);
    if (!validateWorkingRows(preparedContext.normalizedInput, preparedContext.scenario, shiftedRows)) {
      return null;
    }
    return buildPlanState(
      preparedContext,
      baseOptions,
      shiftedRows,
      planState.offsetDeltaMinutes,
      planState.virtualBypassStationIds,
      baselineRowById,
      [...planState.actionLog, action],
      basePlanByStateSignature
    );
  }

  if (action.type === "bypassSet") {
    const nextVirtualBypassStationIds = [...new Set([
      ...planState.virtualBypassStationIds,
      ...asArray(action.stationIds).filter(Boolean)
    ])];
    if (nextVirtualBypassStationIds.length > request.maxAdditionalBypassStations) {
      return null;
    }
    return buildPlanState(
      preparedContext,
      baseOptions,
      planState.rows,
      planState.offsetDeltaMinutes,
      nextVirtualBypassStationIds,
      baselineRowById,
      [...planState.actionLog, action],
      basePlanByStateSignature
    );
  }

  if (action.type === "expressOffset") {
    return buildPlanState(
      preparedContext,
      baseOptions,
      planState.rows,
      planState.offsetDeltaMinutes + clampNumber(action.deltaOffsetMinutes, 0),
      planState.virtualBypassStationIds,
      baselineRowById,
      [...planState.actionLog, action],
      basePlanByStateSignature
    );
  }

  return null;
}

function applyRegionSearchAction(planState, action, preparedContext, baseOptions, offsetDeltaMinutes, request, baselineRowById, basePlanByStateSignature) {
  if (action.type === "clusterShift") {
    const shiftedRows = applyShiftPlan(planState.rows, action.shiftPlan);
    if (!validateWorkingRows(preparedContext.normalizedInput, preparedContext.scenario, shiftedRows)) {
      return null;
    }
    return buildPlanState(
      preparedContext,
      baseOptions,
      shiftedRows,
      offsetDeltaMinutes,
      planState.virtualBypassStationIds,
      baselineRowById,
      [...planState.actionLog, action],
      basePlanByStateSignature
    );
  }

  if (action.type === "localWindowRebuild") {
    const rebuiltRows = applyLocalWindowRebuildAction(planState.rows, action, request);
    if (!validateWorkingRows(preparedContext.normalizedInput, preparedContext.scenario, rebuiltRows)) {
      return null;
    }
    return buildPlanState(
      preparedContext,
      baseOptions,
      rebuiltRows,
      offsetDeltaMinutes,
      planState.virtualBypassStationIds,
      baselineRowById,
      [...planState.actionLog, action],
      basePlanByStateSignature
    );
  }

  if (action.type === "coupledWindowRebuild") {
    const rebuiltRows = applyLocalWindowRebuildAction(planState.rows, action, request);
    if (!validateWorkingRows(preparedContext.normalizedInput, preparedContext.scenario, rebuiltRows)) {
      return null;
    }
    return buildPlanState(
      preparedContext,
      baseOptions,
      rebuiltRows,
      offsetDeltaMinutes,
      planState.virtualBypassStationIds,
      baselineRowById,
      [...planState.actionLog, action],
      basePlanByStateSignature
    );
  }

  if (action.type === "addVirtualBypass") {
    const nextVirtualBypassStationIds = [...new Set([
      ...planState.virtualBypassStationIds,
      ...asArray(action.stationIds).filter(Boolean)
    ])];
    if (nextVirtualBypassStationIds.length > request.maxAdditionalBypassStations) {
      return null;
    }
    return buildPlanState(
      preparedContext,
      baseOptions,
      planState.rows,
      offsetDeltaMinutes,
      nextVirtualBypassStationIds,
      baselineRowById,
      [...planState.actionLog, action],
      basePlanByStateSignature
    );
  }

  return null;
}

function searchRegionJointPlans(preparedContext, workingRows, offsetDeltaMinutes, baseOptions, request) {
  const beamWidth = request.scheduleBeamWidth || DEFAULT_SCHEDULE_BEAM_WIDTH;
  const maxIterations = request.scheduleSearchIterations || DEFAULT_SCHEDULE_SEARCH_ITERATIONS;
  const basePlanByStateSignature = new Map();
  const initialVirtualBypassStationIds = request.forcedBypassStationId
    ? (isConfiguredBypassStationId(preparedContext?.normalizedInput, request.forcedBypassStationId)
      ? []
      : [request.forcedBypassStationId])
    : [];
  const baselineRowById = new Map(workingRows.map((row) => [row.id, row]));
  let frontier = [
    buildPlanState(
      preparedContext,
      baseOptions,
      workingRows,
      offsetDeltaMinutes,
      initialVirtualBypassStationIds,
      baselineRowById,
      [],
      basePlanByStateSignature
    )
  ];
  let bestState = frontier[0];
  let iterations = 0;

  while (iterations < maxIterations) {
    let expanded = [...frontier];
    let actionCount = 0;

    frontier.forEach((planState) => {
      const problem = buildScheduleProblem(
        planState,
        preparedContext.normalizedInput,
        preparedContext.scenario,
        request
      );
      const actions = enumerateUnifiedScheduleActions(
        problem,
        planState,
        preparedContext.normalizedInput,
        preparedContext.scenario,
        request
      );
      actions.forEach((action) => {
        const nextState = applyScheduleAction(
          planState,
          action,
          preparedContext,
          baseOptions,
          request,
          baselineRowById,
          basePlanByStateSignature
        );
        if (!nextState) {
          return;
        }
        expanded.push(nextState);
        actionCount += 1;
      });
    });

    if (actionCount === 0) {
      break;
    }

    frontier = selectDiversePlanStates(expanded, beamWidth);
    if (frontier.length > 0 && comparePlanStateQuality(frontier[0], bestState) < 0) {
      bestState = frontier[0];
    }
    iterations += 1;
  }

  const plans = dedupePlanStates(frontier)
    .sort(comparePlanStateQuality)
    .map((planState) => {
      const plan = planState.plan;
      plan.sourceTimetableRows = rowsToStagedRows(planState.rows);
      plan.localRetimeIterations = planState.actionLog.filter((action) =>
        action.type === "retimeVector"
        || action.type === "clusterShift"
        || action.type === "localWindowRebuild"
        || action.type === "coupledWindowRebuild"
      ).length;
      plan.retimeGroupsApplied = planState.actionLog
        .filter((action) => action.type === "retimeVector" || action.type === "clusterShift")
        .map((action) => {
          if (action.type === "retimeVector") {
            return {
              clusterId: asArray(action.clusterIds).join("+"),
              localLineId: action.localLineId,
              mode: "schedule",
              shape: action.shape || "vector",
              reason: action.reason || "",
              targetRegionIds: asArray(action.targetRegionIds),
              riskScore: clampNumber(action.riskScore, 0),
              tripIds: asArray(action.tripDeltas).map((entry) => entry.tripId),
              deltaMinutes: Math.max(...asArray(action.tripDeltas).map((entry) => Math.abs(entry.deltaMinutes)), 0),
              deltaPattern: asArray(action.tripDeltas).map((entry) => entry.deltaMinutes)
            };
          }
          return {
            clusterId: action.shiftPlan.clusterId,
            localLineId: action.shiftPlan.localLineId,
            mode: action.shiftPlan.mode,
            shape: action.shiftPlan.shape || "uniform",
            totalUnresolvedRiskMinutes: action.shiftPlan.totalUnresolvedRiskMinutes,
            dominantSeverityMinutes: action.shiftPlan.dominantSeverityMinutes,
            tripIds: asArray(action.shiftPlan.shifts).map((shift) => shift.tripId),
            deltaMinutes: Math.max(...asArray(action.shiftPlan.shifts).map((shift) => Math.abs(shift.deltaMinutes)), 0),
            deltaPattern: asArray(action.shiftPlan.shifts).map((shift) => shift.deltaMinutes)
          };
        });
      plan.windowRebuildActions = planState.actionLog
        .filter((action) => action.type === "localWindowRebuild" || action.type === "coupledWindowRebuild")
        .map((action) => ({
          type: action.type,
          regionId: action.regionId,
          lineId: action.lineId,
          tripIds: action.tripIds,
          clusterIds: action.clusterIds || [],
          windowStartMinute: action.windowStartMinute,
          windowEndMinute: action.windowEndMinute
        }));
      plan.scheduleActions = buildStructuredScheduleActions(
        planState.actionLog,
        planState.rows,
        preparedContext.normalizedInput
      );
      plan.scheduleProblem = summarizeScheduleProblem(buildScheduleProblem(
        planState,
        preparedContext.normalizedInput,
        preparedContext.scenario,
        request
      ));
      plan.actionLog = planState.actionLog;
      const changedAdjustableRows = planState.rows.filter((row) => {
        if (!isAdjustableRow(row, request)) {
          return false;
        }
        const baselineRow = baselineRowById.get(row.id);
        return baselineRow && baselineRow.minute !== row.minute;
      });
      plan.totalRetimedTrips = changedAdjustableRows.length;
      plan.totalRetimedMinutes = changedAdjustableRows.reduce((sum, row) => {
        const baselineRow = baselineRowById.get(row.id);
        return sum + Math.abs((baselineRow?.minute ?? row.minute) - row.minute);
      }, 0);
      if (plan.localRetimeIterations > 0 || planState.virtualBypassStationIds.length > 0) {
        plan.explanation = [
          ...asArray(plan.explanation),
          `joint search actions ${planState.actionLog.length}`,
          `virtual bypass ${planState.virtualBypassStationIds.length}`
        ];
      }
      return attachPlannerFrontendSummary(plan, preparedContext.scenario);
    });

  return {
    plans,
    iterations,
    bestState
  };
}

export function evaluateExistingOnlyVariant(normalizedInput, scenario, lineRuntimeModels, corridors, options, offsetDeltaMinutes) {
  const trips = buildScenarioTrips(normalizedInput, scenario, lineRuntimeModels, offsetDeltaMinutes);
  const rawCatchupEvents = findCatchupEvents(normalizedInput, scenario, trips, corridors, options);
  const trunkGrouping = buildTrunkGroups(corridors);
  const rawTrunkProblemClusters = buildTrunkProblemClusters(rawCatchupEvents, trunkGrouping);
  const rawOptimizationRegioning = buildOptimizationRegions(rawTrunkProblemClusters);
  const regionSimulation = simulateRegionPlanState(rawCatchupEvents, rawTrunkProblemClusters, rawOptimizationRegioning);
  const catchupEvents = regionSimulation.events;
  const trunkProblemClusters = buildTrunkProblemClusters(catchupEvents, trunkGrouping);
  const optimizationRegioning = buildOptimizationRegions(trunkProblemClusters);
  const metrics = buildPlanMetrics(catchupEvents);
  const departureGapPenalty = computeDepartureGapPenalty(
    normalizedInput,
    trips,
    scenario.selectedLineIds,
    [],
    clampNumber(options.minDepartureGapMinutes, DEFAULT_MIN_DEPARTURE_GAP_MINUTES)
  );
  const score = scoreCatchupScenario(
    options.objective || DEFAULT_OBJECTIVE,
    metrics,
    departureGapPenalty
  );

  const selectedBypassStations = new Map();
  catchupEvents.forEach((event) => {
    if (event.selectedBypassStation?.stationId && !selectedBypassStations.has(event.selectedBypassStation.stationId)) {
      selectedBypassStations.set(event.selectedBypassStation.stationId, event.selectedBypassStation);
    }
  });

  const unresolvedRisks = trunkProblemClusters
    .filter((cluster) => cluster.totalUnresolvedRiskMinutes > 0)
    .map((cluster) => ({
      clusterId: cluster.clusterId,
      linePair: `${cluster.localLineId} -> ${cluster.expressLineId}`,
      severityMinutes: cluster.totalUnresolvedRiskMinutes,
      fromStationIds: cluster.corridorFromStationIds,
      toStationIds: cluster.corridorToStationIds,
      recommendedBypassStationId: cluster.recommendedBypassStation?.stationId || ""
    }));

  const confidenceBase = catchupEvents.length > 0
    ? catchupEvents.reduce((sum, event) => sum + event.confidence, 0) / catchupEvents.length
    : 0.25;
  const confidencePenalty = unresolvedRisks.length > 0 ? Math.min(0.25, unresolvedRisks.length * 0.03) : 0;
  const confidence = Number(Math.max(0.15, confidenceBase - confidencePenalty).toFixed(2));

  const plan = {
    planId: `existingOnly:${scenario.draftKey || scenario.selectedLineId || "default"}:${offsetDeltaMinutes}`,
    scenarioType: options.virtualBypassStationIds?.length ? "virtualBypass" : "existingOnly",
    objective: options.objective || DEFAULT_OBJECTIVE,
    recommendedExpressOffsetDeltaMinutes: offsetDeltaMinutes,
    selectedBypassStations: [...selectedBypassStations.values()],
    trunkProblemClusters,
    optimizationRegions: optimizationRegioning.regions,
    regionResults: regionSimulation.regionResults,
    catchupClusters: trunkProblemClusters,
    catchupEvents,
    unresolvedRisks,
    addedVirtualBypassStations: asArray(options.virtualBypassStationIds).map((stationId) =>
      normalizedInput.candidateBypassStations.find((station) => station.stationId === stationId)
    ).filter(Boolean),
    metrics: {
      ...metrics,
      departureGapPenalty: Number(departureGapPenalty.toFixed(2))
    },
    score,
    confidence
  };
  plan.explanation = buildPlanExplanation(plan, scenario);
  return attachPlannerFrontendSummary(plan, scenario);
}

export function searchExistingBypassPlans(rawInput, options = {}) {
  const context = resolvePlanningContext(rawInput, options);
  const { normalizedInput, scenario, lineRuntimeModels, corridors } = context;
  const requestedMode = options.mode || "existingOnly";
  const offsets = options.freezeExpressOffsets
    ? [0]
    : quantizeOffsetVariants(options.offsetStepMinutes, options.maxOffsetMinutes);
  const plans = offsets.map((offsetDeltaMinutes) =>
    evaluateExistingOnlyVariant(
      normalizedInput,
      scenario,
      lineRuntimeModels,
      corridors,
      { ...options, objective: options.objective || DEFAULT_OBJECTIVE },
      offsetDeltaMinutes
    )
  ).sort((left, right) => {
    if (right.score !== left.score) {
      return right.score - left.score;
    }
    if (left.metrics.totalUnresolvedRiskMinutes !== right.metrics.totalUnresolvedRiskMinutes) {
      return left.metrics.totalUnresolvedRiskMinutes - right.metrics.totalUnresolvedRiskMinutes;
    }
    if (left.metrics.totalLocalExtraWaitMinutes !== right.metrics.totalLocalExtraWaitMinutes) {
      return left.metrics.totalLocalExtraWaitMinutes - right.metrics.totalLocalExtraWaitMinutes;
    }
    const leftOffsetMagnitude = Math.abs(left.recommendedExpressOffsetDeltaMinutes);
    const rightOffsetMagnitude = Math.abs(right.recommendedExpressOffsetDeltaMinutes);
    if (leftOffsetMagnitude !== rightOffsetMagnitude) {
      return leftOffsetMagnitude - rightOffsetMagnitude;
    }
    return left.recommendedExpressOffsetDeltaMinutes - right.recommendedExpressOffsetDeltaMinutes;
  });

  return {
    modeRequested: requestedMode,
    modeUsed: requestedMode === "forceStation" ? "forceStation" : "existingOnly",
    objective: options.objective || DEFAULT_OBJECTIVE,
    generatedAtFrame: normalizedInput.generatedAtFrame,
    draftKey: scenario.draftKey,
    selectedLineId: scenario.selectedLineId,
    analysisWindow: {
      start: scenario.windowStart,
      end: scenario.windowEnd
    },
    selectedLineIds: scenario.selectedLineIds,
    adjustableLineIds: scenario.adjustableLineIds,
    lineRoleSummary: buildScenarioLineRoleSummary(scenario),
    localLineIds: scenario.localLineIds,
    expressLineIds: scenario.expressLineIds,
    corridorCount: corridors.length,
    departureRowCount: scenario.stagedRows.length,
    forcedBypassStationId: options.forcedBypassStationId || "",
    supportedModes: ["existingOnly", "forceStation"],
    plans
  };
}

export function searchJointPlans(rawInput, options = {}) {
  const preparedContext = resolvePlanningContext(rawInput, options);
  const { normalizedInput, draft, scenario, baseRows } = preparedContext;
  const baseRequest = normalizeJointPlannerRequest(options, draft, scenario);
  const tphVariants = enumerateJointTripsPerHourVariants(baseRequest);
  const plans = [];
  let departureRowCount = baseRows.length;

  tphVariants.forEach((expressTripsPerHour) => {
    const request = {
      ...baseRequest,
      expressTripsPerHour
    };
    const workingRows = buildExpressCandidateRows(normalizedInput, scenario, baseRows, request);
    departureRowCount = Math.max(departureRowCount, workingRows.length);
    const offsetVariants = enumerateJointOffsetVariants(request, options);
    offsetVariants.forEach((offsetDeltaMinutes) => {
      const jointSearch = searchRegionJointPlans(
        preparedContext,
        workingRows,
        offsetDeltaMinutes,
        {
          ...options,
          objective: request.objective,
          stagedRowsOverride: rowsToStagedRows(workingRows),
          maxLocalHoldMinutes: request.maxLocalHoldMinutes,
          lineRuntimeModels: preparedContext.lineRuntimeModels,
          corridors: preparedContext.corridors
        },
        request
      );
      jointSearch.plans.forEach((plan) => {
        plan.requestedExpressTripsPerHour = expressTripsPerHour;
        plan.planId = `joint:${scenario.draftKey || scenario.selectedLineId || "default"}:tph:${expressTripsPerHour ?? "existing"}:offset:${plan.recommendedExpressOffsetDeltaMinutes}:candidate:${plans.length}`;
        if (plan.localRetimeIterations > 0 || asArray(plan.addedVirtualBypassStations).length > 0) {
          plan.explanation = [
            ...asArray(plan.explanation),
            `joint unified search tph ${expressTripsPerHour ?? "existing"} offset ${offsetDeltaMinutes}`
          ];
        }
        plans.push(plan);
      });
    });
  });

  plans.sort((left, right) => {
    return comparePlanQuality(left, right);
  });

  return {
    modeRequested: "joint",
    modeUsed: "joint",
    objective: baseRequest.objective,
    generatedAtFrame: normalizedInput.generatedAtFrame,
    draftKey: scenario.draftKey,
    selectedLineId: scenario.selectedLineId,
    analysisWindow: {
      start: scenario.windowStart,
      end: scenario.windowEnd
    },
    selectedLineIds: scenario.selectedLineIds,
    adjustableLineIds: scenario.adjustableLineIds,
    lineRoleSummary: buildScenarioLineRoleSummary(scenario),
    localLineIds: scenario.localLineIds,
    expressLineIds: scenario.expressLineIds,
    departureRowCount,
    request: {
      expressTripsPerHour: baseRequest.expressTripsPerHour,
      expressTripsPerHourCandidates: baseRequest.expressTripsPerHourCandidates,
      expressWindowStart: baseRequest.expressWindowStart,
      expressWindowEnd: baseRequest.expressWindowEnd,
      expressOffsetMinutes: baseRequest.expressOffsetMinutes,
      expressOffsetCandidates: baseRequest.expressOffsetCandidates,
      expressStopStationIds: baseRequest.expressStopStationIds,
      stopPatternSource: baseRequest.stopPatternSource,
      turnbackStationId: baseRequest.turnbackStationId,
      maxLocalHoldMinutes: baseRequest.maxLocalHoldMinutes,
      maxLocalRetimeMinutes: baseRequest.maxLocalRetimeMinutes,
      maxAdditionalBypassStations: baseRequest.maxAdditionalBypassStations,
      stopStartLossMinutesPerSkippedStop: baseRequest.stopStartLossMinutesPerSkippedStop,
      scheduleBeamWidth: baseRequest.scheduleBeamWidth,
      scheduleSearchIterations: baseRequest.scheduleSearchIterations,
      maxScheduleActions: baseRequest.maxScheduleActions,
      stopPatternSpecified: baseRequest.stopPatternSpecified
    },
    stopPatternSupported: true,
    presetTopPlans: rankPlansByPreset(plans),
    plans,
    virtualResult: null
  };
}

export function searchVirtualBypassPlans(rawInput, options = {}) {
  const context = resolvePlanningContext(rawInput, options);
  const { normalizedInput, scenario, lineRuntimeModels, corridors } = context;
  const baseBypassStationIds = getConfiguredBypassStationIdsForLines(normalizedInput, scenario.adjustableLineIds);
  const baseExisting = searchExistingBypassPlans(context, {
    ...options,
    mode: "existingOnly"
  });
  const basePlan = baseExisting.plans[0] || null;
  const rankedCandidates = rankVirtualBypassCandidates(
    normalizedInput,
    scenario,
    basePlan?.trunkProblemClusters || []
  );
  const stationSets = enumerateVirtualBypassStationSets(rankedCandidates, {
    ...options,
    normalizedInput
  });
  const offsets = options.freezeExpressOffsets
    ? [0]
    : quantizeOffsetVariants(options.offsetStepMinutes, options.maxOffsetMinutes);

  const plans = [];
  stationSets.forEach((stationSet) => {
    offsets.forEach((offsetDeltaMinutes) => {
      plans.push(
        evaluateExistingOnlyVariant(
          normalizedInput,
          scenario,
          lineRuntimeModels,
          corridors,
          {
            ...options,
            mode: options.mode || "virtualBypass",
            objective: options.objective || DEFAULT_OBJECTIVE,
            useCandidateBypassPool: true,
            baseBypassStationIds,
            virtualBypassStationIds: stationSet
          },
          offsetDeltaMinutes
        )
      );
    });
  });

  plans.sort((left, right) => {
    if (right.score !== left.score) {
      return right.score - left.score;
    }
    if (left.metrics.totalUnresolvedRiskMinutes !== right.metrics.totalUnresolvedRiskMinutes) {
      return left.metrics.totalUnresolvedRiskMinutes - right.metrics.totalUnresolvedRiskMinutes;
    }
    if (left.addedVirtualBypassStations.length !== right.addedVirtualBypassStations.length) {
      return left.addedVirtualBypassStations.length - right.addedVirtualBypassStations.length;
    }
    return Math.abs(left.recommendedExpressOffsetDeltaMinutes) - Math.abs(right.recommendedExpressOffsetDeltaMinutes);
  });

  return {
    modeRequested: options.mode || "virtualBypass",
    modeUsed: options.forcedBypassStationId ? "forceStation" : "virtualBypass",
    objective: options.objective || DEFAULT_OBJECTIVE,
    generatedAtFrame: normalizedInput.generatedAtFrame,
    draftKey: scenario.draftKey,
    selectedLineId: scenario.selectedLineId,
    analysisWindow: {
      start: scenario.windowStart,
      end: scenario.windowEnd
    },
    selectedLineIds: scenario.selectedLineIds,
    adjustableLineIds: scenario.adjustableLineIds,
    lineRoleSummary: buildScenarioLineRoleSummary(scenario),
    localLineIds: scenario.localLineIds,
    expressLineIds: scenario.expressLineIds,
    corridorCount: corridors.length,
    departureRowCount: scenario.stagedRows.length,
    basePlanScore: basePlan?.score || 0,
    rankedCandidates: rankedCandidates.slice(0, clampNumber(options.virtualCandidateLimit, 8)).map((station) => ({
      stationId: station.stationId,
      lineId: station.lineId,
      name: station.name,
      order: station.order,
      candidateScore: station.candidateScore
    })),
    plans
  };
}

export function searchExpressBypassPlans(rawInput, options = {}) {
  if (options.mode === "joint") {
    return searchJointPlans(rawInput, options);
  }
  if (options.mode === "virtualBypass" || options.mode === "forceStation") {
    return searchVirtualBypassPlans(rawInput, options);
  }
  return searchExistingBypassPlans(rawInput, options);
}
