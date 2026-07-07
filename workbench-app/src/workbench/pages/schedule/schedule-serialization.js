import { timeToMinutes } from "../../../lib/time";
import {
  LINE_OPTIONS,
  clampPositiveMinutes,
  normalizeKind,
  normalizeRuntimeFeatureSettings
} from "./schedule-catalog";

export function createNativeMergedViewForSave(selectedLineId, snapshotMergedView = null) {
  const sourceView =
    snapshotMergedView && typeof snapshotMergedView === "object"
      ? snapshotMergedView
      : {};

  return {
    localLineId: typeof sourceView.localLineId === "string" ? sourceView.localLineId : (selectedLineId || ""),
    expressLineId: typeof sourceView.expressLineId === "string" ? sourceView.expressLineId : "",
    localLineIds:
      Array.isArray(sourceView.localLineIds) && sourceView.localLineIds.length > 0
        ? sourceView.localLineIds.filter((lineId) => typeof lineId === "string" && lineId.length > 0)
        : (selectedLineId ? [selectedLineId] : []),
    expressLineIds:
      Array.isArray(sourceView.expressLineIds)
        ? sourceView.expressLineIds.filter((lineId) => typeof lineId === "string" && lineId.length > 0)
        : [],
    isLoop: typeof sourceView.isLoop === "boolean" ? sourceView.isLoop : true,
    turnbackStationId: typeof sourceView.turnbackStationId === "string" ? sourceView.turnbackStationId : "",
    direction: typeof sourceView.direction === "string" && sourceView.direction ? sourceView.direction : "up",
    windowStart: typeof sourceView.windowStart === "string" && sourceView.windowStart ? sourceView.windowStart : "06:00",
    windowEnd: typeof sourceView.windowEnd === "string" && sourceView.windowEnd ? sourceView.windowEnd : "06:30"
  };
}

export function serializeNativeLineSettings(lines = LINE_OPTIONS) {
  return (Array.isArray(lines) ? lines : [])
    .filter((line) => line && typeof line === "object" && line.id)
    .map((line) => ({
      lineId: line.id,
      originHoldLimitMinutes: clampPositiveMinutes(line.hold, 20),
      maxStationDwellMinutes: clampPositiveMinutes(line.dwell, 10),
      allowedDepotId: line.depotId === "any-depot" ? "" : (line.depotId || ""),
      serviceKind: normalizeKind(line.kind)
    }));
}

export function serializeRuntimeFeatureSettings(featureSettings) {
  const normalized = normalizeRuntimeFeatureSettings(featureSettings);
  return {
    dispatchEnabled: normalized.dispatchEnabled,
    bypassEnabled: normalized.bypassEnabled,
    broadcastEnabled: normalized.broadcastEnabled,
    depotLockEnabled: normalized.depotLockEnabled
  };
}

export function serializeNativeLineDraftRows(rows = []) {
  return [...(Array.isArray(rows) ? rows : [])]
    .filter((row) => row?.lineId)
    .sort((left, right) => {
      const leftMinutes = timeToMinutes(left?.time) ?? 9999;
      const rightMinutes = timeToMinutes(right?.time) ?? 9999;
      if (leftMinutes !== rightMinutes) {
        return leftMinutes - rightMinutes;
      }

      if ((left?.lineId || "") !== (right?.lineId || "")) {
        return String(left?.lineId || "").localeCompare(String(right?.lineId || ""));
      }

      if (normalizeKind(left?.kind) !== normalizeKind(right?.kind)) {
        return normalizeKind(left?.kind).localeCompare(normalizeKind(right?.kind));
      }

      return String(left?.id || "").localeCompare(String(right?.id || ""));
    })
    .map((row, index) => ({
      id: String(row?.id || `staged-${index + 1}`),
      lineId: row.lineId,
      time: row?.time || "",
      kind: normalizeKind(row?.kind),
      source: row?.source || "manual",
      note: row?.note || ""
    }));
}

export function serializeNativeLineDraftRowsByLineId(rows = []) {
  const rowsByLineId = new Map();
  (Array.isArray(rows) ? rows : []).forEach((row) => {
    const lineId = row?.lineId || row?.serviceId || "";
    if (!lineId) {
      return;
    }

    if (!rowsByLineId.has(lineId)) {
      rowsByLineId.set(lineId, []);
    }
    rowsByLineId.get(lineId).push({ ...row, lineId });
  });

  return [...rowsByLineId.entries()].map(([lineId, lineRows]) => ({
    lineId,
    lineDraftRows: serializeNativeLineDraftRows(lineRows)
  }));
}

export function flattenSnapshotLineDraftRowsByLineId(blocks = []) {
  const flattened = [];
  (Array.isArray(blocks) ? blocks : []).forEach((block) => {
    const lineId = String(block?.lineId || "");
    if (!lineId) {
      return;
    }

    (Array.isArray(block?.lineDraftRows) ? block.lineDraftRows : []).forEach((row, index) => {
      flattened.push({
        id: row?.id || `draft-${lineId}-${index + 1}`,
        lineId: row?.lineId || lineId,
        serviceId: row?.lineId || lineId,
        time: row?.time || "",
        kind: row?.kind === "express" ? "express" : "local",
        source: row?.source || "manual",
        note: row?.note || ""
      });
    });
  });
  return flattened;
}

export function serializeRemovedLineIds(lineIds = []) {
  return [...new Set((Array.isArray(lineIds) ? lineIds : []).filter((lineId) => typeof lineId === "string" && lineId.length > 0))]
    .sort((left, right) => left.localeCompare(right));
}

export function serializeRuntimeLineRefs(lines = LINE_OPTIONS) {
  return (Array.isArray(lines) ? lines : [])
    .filter((line) => line && typeof line === "object" && line.id)
    .map((line) => ({
      lineId: line.id,
      sourceLineId: typeof line.sourceLineId === "string"
        ? line.sourceLineId
        : (typeof line.corridorId === "string" ? line.corridorId : "")
    }))
    .sort((left, right) => left.lineId.localeCompare(right.lineId));
}

export function mapSnapshotManualRows(rows = [], fallbackLineId = "") {
  return (Array.isArray(rows) ? rows : []).map((row, index) => ({
    id: row?.id || `manual-${index + 1}`,
    lineId: row?.lineId || fallbackLineId,
    serviceId: row?.lineId || fallbackLineId,
    time: row?.time || "",
    kind: row?.kind === "express" ? "express" : "local",
    offsetMode: row?.offsetMode || "none",
    offsetMinutes: row?.offsetMinutes === 0 ? "0" : String(row?.offsetMinutes || "")
  }));
}

export function mapSnapshotAutoRules(rows = [], fallbackLineId = "") {
  return (Array.isArray(rows) ? rows : []).map((rule, index) => {
    const kind = rule?.kind === "express" ? "express" : "local";
    const departuresPerHour =
      Number(rule?.departuresPerHour) > 0
        ? Number(rule.departuresPerHour)
        : kind === "express"
          ? Number(rule?.expressPerHour) || 0
          : Number(rule?.localPerHour) || 0;

    return {
      id: rule?.id || `rule-${index + 1}`,
      lineId: rule?.lineId || fallbackLineId,
      serviceId: rule?.lineId || fallbackLineId,
      kind,
      enabled: rule?.enabled !== false,
      start: rule?.start || "08:00",
      end: rule?.end || "10:00",
      departuresPerHour,
      expressOffsetMode: rule?.expressOffsetMode || "after",
      expressOffsetMinutes: Number(rule?.expressOffsetMinutes) || 0,
      localPerHour: kind === "local" ? departuresPerHour : 0,
      expressPerHour: kind === "express" ? departuresPerHour : 0
    };
  });
}

export function mapSnapshotSummaryRows(rows = []) {
  return (Array.isArray(rows) ? rows : []).map((row, index) => ({
    id: row?.id || `summary-${index + 1}`,
    serviceId: row?.lineId || "",
    lineId: row?.lineId || "",
    time: row?.time || "",
    kind: row?.kind === "express" ? "express" : "local",
    source: row?.source || "manual",
    note: row?.note || ""
  }));
}
