import { timeToMinutes } from "./planner-time.js";

export function normalizePlannerMergedViewForSave(snapshot, fallbackSelectedLineId = "") {
  const sourceView =
    snapshot?.mergedView && typeof snapshot.mergedView === "object"
      ? snapshot.mergedView
      : {};

  return {
    localLineId: typeof sourceView.localLineId === "string" ? sourceView.localLineId : (fallbackSelectedLineId || ""),
    expressLineId: typeof sourceView.expressLineId === "string" ? sourceView.expressLineId : "",
    localLineIds:
      Array.isArray(sourceView.localLineIds) && sourceView.localLineIds.length > 0
        ? sourceView.localLineIds.filter((lineId) => typeof lineId === "string" && lineId.length > 0)
        : (fallbackSelectedLineId ? [fallbackSelectedLineId] : []),
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

export function buildPlannerLineSettingsForSave(lines = []) {
  return (Array.isArray(lines) ? lines : [])
    .filter((line) => line && typeof line === "object" && line.id)
    .map((line) => ({
      lineId: line.id,
      originHoldLimitMinutes: Number(line?.originHoldLimitMinutes) || 20,
      maxStationDwellMinutes: Number(line?.maxStationDwellMinutes) || 10,
      allowedDepotId: line?.allowedDepotId || "",
      serviceKind: line?.kind === "express" ? "express" : "local"
    }));
}

export function normalizePlannerRow(row, index, importedNote, prefix) {
  return {
    id: String(row?.id || row?.tripId || `${prefix}-${index + 1}`),
    lineId: String(row?.lineId || ""),
    time: String(row?.time || row?.afterTime || ""),
    kind: row?.kind === "express" ? "express" : "local",
    source: String(row?.source || "planner"),
    note: importedNote || row?.note || ""
  };
}

export function buildPlannerReplacementRows(planDetail, importedNote) {
  return (Array.isArray(planDetail?.plannerReplacementRows) ? planDetail.plannerReplacementRows : [])
    .map((row, index) => normalizePlannerRow(row, index, importedNote, "planner-replacement"))
    .filter((row) => row.lineId && row.time)
    .map((row) => ({ ...row, source: "planner" }));
}

export function buildPlannerBaselineRows(planDetail) {
  return (Array.isArray(planDetail?.plannerBaselineRows) ? planDetail.plannerBaselineRows : [])
    .map((row, index) => normalizePlannerRow(row, index, "", "planner-baseline"))
    .filter((row) => row.lineId && row.time);
}

export function buildPlannerImportContract(plannerResult, activePlan, importedRows) {
  const rawPlan = activePlan?.rawPlan;
  if (!rawPlan || !rawPlan.planId) {
    return null;
  }

  const changedRows = (Array.isArray(rawPlan.changedWindows) ? rawPlan.changedWindows : [])
    .flatMap((window) => Array.isArray(window?.rowDiffs) ? window.rowDiffs : []);

  return {
    draftKey: String(plannerResult?.requestEcho?.draftKey || ""),
    importedFrom: "planner-ui",
    importedPlanId: String(rawPlan.planId || ""),
    importedObjectiveId: String(rawPlan.objectiveId || ""),
    importedLineIds: [...new Set((Array.isArray(importedRows) ? importedRows : []).map((row) => row?.lineId).filter(Boolean))],
    requestEcho: plannerResult?.requestEcho || null,
    lineRoleSummary: rawPlan.lineRoleSummary || null,
    selectedBypassStationIds: Array.isArray(rawPlan.selectedBypassStationIds) ? rawPlan.selectedBypassStationIds : [],
    changedRows,
    structuredActions: Array.isArray(rawPlan.structuredScheduleActions) ? rawPlan.structuredScheduleActions : [],
    riskItems: Array.isArray(rawPlan.riskItems) ? rawPlan.riskItems : []
  };
}

export function buildPlannerStagedRowKey(row) {
  const kind = row?.kind === "express" ? "express" : "local";
  const rowId = String(row?.id || "");
  return rowId
    ? `${row?.lineId || ""}|${kind}|${rowId}|${row?.time || ""}`
    : `${row?.lineId || ""}|${kind}|${row?.time || ""}`;
}

export function getSnapshotLineDraftRowsByLineId(snapshot) {
  const rowsByLineId = new Map();
  if (Array.isArray(snapshot?.lineDraftRowsByLineId)) {
    snapshot.lineDraftRowsByLineId.forEach((block) => {
      const lineId = String(block?.lineId || "");
      if (!lineId) {
        return;
      }

      rowsByLineId.set(
        lineId,
        (Array.isArray(block?.lineDraftRows) ? block.lineDraftRows : [])
          .map((row, index) => normalizePlannerRow(row, index, row?.note || "", "current-draft"))
          .filter((row) => row.lineId && row.time)
      );
    });
    return rowsByLineId;
  }
  return rowsByLineId;
}

export function isPlannerRowInsideWindow(row, windowStartMinutes, windowEndMinutes) {
  const minutes = timeToMinutes(row?.time);
  return minutes != null && minutes >= windowStartMinutes && minutes < windowEndMinutes;
}

export function buildPlannerReplacementDraftBlocks(snapshot, baselineRows, replacementRows, requestEcho) {
  const startMinutes = timeToMinutes(requestEcho?.windowStart);
  const endMinutes = timeToMinutes(requestEcho?.windowEnd);
  if (startMinutes == null || endMinutes == null || endMinutes <= startMinutes) {
    return null;
  }

  const affectedLineIds = [...new Set(replacementRows.map((row) => row.lineId).filter(Boolean))];
  if (affectedLineIds.length === 0) {
    return null;
  }

  const affectedLineSet = new Set(affectedLineIds);
  const rowsByLineId = getSnapshotLineDraftRowsByLineId(snapshot);

  for (const lineId of affectedLineIds) {
    const currentRows = rowsByLineId.get(lineId) || [];
    const currentKeys = currentRows
      .filter((row) => isPlannerRowInsideWindow(row, startMinutes, endMinutes))
      .map(buildPlannerStagedRowKey)
      .sort();
    const baselineRowsInWindow = baselineRows
      .filter((row) => row.lineId === lineId && isPlannerRowInsideWindow(row, startMinutes, endMinutes));
    const baselineKeys = baselineRowsInWindow
      .map(buildPlannerStagedRowKey)
      .sort();
    const baselineIsRuntimeOnly = baselineRowsInWindow.length > 0
      && baselineRowsInWindow.every((row) => row?.source === "tripDerived");
    if (currentKeys.length === 0 && baselineIsRuntimeOnly) {
      continue;
    }
    if (currentKeys.length !== baselineKeys.length || currentKeys.some((key, index) => key !== baselineKeys[index])) {
      return null;
    }
  }

  return affectedLineIds.map((lineId) => {
    const currentRows = rowsByLineId.get(lineId) || [];
    const preservedRows = currentRows
      .filter((row) => !isPlannerRowInsideWindow(row, startMinutes, endMinutes));
    const insertedRows = replacementRows
      .filter((row) => row.lineId === lineId && isPlannerRowInsideWindow(row, startMinutes, endMinutes));
    const lineDraftRows = [...preservedRows, ...insertedRows]
      .sort((left, right) => {
        const leftMinutes = timeToMinutes(left?.time) ?? 9999;
        const rightMinutes = timeToMinutes(right?.time) ?? 9999;
        if (leftMinutes !== rightMinutes) {
          return leftMinutes - rightMinutes;
        }

        return String(left?.id || "").localeCompare(String(right?.id || ""));
      })
      .map((row, index) => ({
        id: String(row?.id || `planner-${lineId}-${index + 1}`),
        lineId,
        time: row.time,
        kind: row.kind === "express" ? "express" : "local",
        source: row.source || "planner",
        note: row.note || ""
      }));
    return { lineId, lineDraftRows };
  }).filter((block) => affectedLineSet.has(block.lineId));
}
