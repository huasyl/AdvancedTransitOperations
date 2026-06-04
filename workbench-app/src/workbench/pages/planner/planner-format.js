export function summarizePlanType(status) {
  if (status === "infeasible" || status === "blocked") {
    return "error";
  }
  if (status === "risk" || status === "fragile" || status === "needsAction") {
    return "warning";
  }
  return "optimal";
}

export function summarizePlanTypeFromRisks(riskItems, fallbackStatus) {
  const displayedTypes = (riskItems || []).map((item) => resolveDisplayedRiskProblemType(item));
  if (displayedTypes.includes("hardCatchup")) {
    return "error";
  }
  if (displayedTypes.includes("lowMargin") || displayedTypes.includes("backgroundConstraint")) {
    return "warning";
  }
  return summarizePlanType(fallbackStatus);
}

export function formatPlannerBadgeLabel(status, riskItems, t) {
  const displayedTypes = (riskItems || []).map((item) => resolveDisplayedRiskProblemType(item));
  if (displayedTypes.includes("hardCatchup")) {
    return formatRiskTypeLabel("hardCatchup", t);
  }
  if (displayedTypes.includes("lowMargin")) {
    return formatRiskTypeLabel("lowMargin", t);
  }
  if (displayedTypes.includes("backgroundConstraint")) {
    return formatRiskTypeLabel("backgroundConstraint", t);
  }
  return formatPlannerStatusLabel(status, t);
}

export function formatNumberValue(value) {
  const numeric = Number(value || 0);
  if (!Number.isFinite(numeric)) {
    return "0";
  }
  const rounded = Math.round(numeric * 10) / 10;
  return String(rounded).replace(/\.0$/, "");
}

export function formatMinutesLabel(value) {
  return `${formatNumberValue(value)}m`;
}

export function readHighestCapacityConsumptionPercent(diagnostic) {
  const percentValue = Number(diagnostic?.highestCapacityConsumptionPercent);
  if (Number.isFinite(percentValue)) {
    return percentValue;
  }
  const ratioValue = Number(diagnostic?.highestCapacityConsumptionRatio);
  if (Number.isFinite(ratioValue)) {
    return ratioValue * 100;
  }
  return 0;
}

export function formatPercentLabel(value) {
  return `${formatNumberValue(value)}%`;
}

export function joinDisplayValues(values, separator = " / ", emptyValue = "--") {
  const parts = (Array.isArray(values) ? values : [])
    .map((value) => String(value || "").trim())
    .filter(Boolean);
  return parts.length > 0 ? parts.join(separator) : emptyValue;
}

export function joinUniqueDisplayValues(values, separator = " / ", emptyValue = "--") {
  const parts = (Array.isArray(values) ? values : [])
    .map((value) => String(value || "").trim())
    .filter(Boolean);
  const uniqueParts = [...new Set(parts)];
  return uniqueParts.length > 0 ? uniqueParts.join(separator) : emptyValue;
}

export function formatPlannerObjectiveTitle(objectiveId, fallbackValue, t) {
  switch (objectiveId) {
    case "balanced":
      return t("planner.objective.balanced");
    case "fastestExpress":
      return t("planner.objective.fastestExpress");
    case "minBypassStations":
      return t("planner.objective.minBypassStations");
    case "maxSystemEfficiency":
      return t("planner.objective.maxSystemEfficiency");
    default:
      return objectiveId || fallbackValue || t("planner.empty.noPlanGenerated");
  }
}

export function formatPlannerStatusLabel(status, t) {
  switch (status) {
    case "feasible":
      return t("planner.badge.feasible");
    case "needsAction":
      return t("planner.riskState.actionable");
    case "blocked":
      return t("planner.riskState.blocked");
    case "fragile":
      return t("planner.badge.fragile");
    case "risk":
      return t("planner.badge.risk");
    case "infeasible":
      return t("planner.badge.infeasible");
    default:
      return t("planner.badge.risk");
  }
}

export function formatRiskSeverityLabel(severityLevel, t) {
  switch (severityLevel) {
    case "high":
      return t("planner.riskLevel.high");
    case "medium":
      return t("planner.riskLevel.medium");
    case "low":
      return t("planner.riskLevel.low");
    case "fragile":
      return t("planner.riskLevel.fragile");
    default:
      return t("planner.badge.feasible");
  }
}

export function shouldShowRiskCluster(risk) {
  const severity = String(risk?.severityLevel || "").trim();
  return severity === "high" || severity === "medium" || severity === "low" || severity === "fragile";
}

export function shouldShowRiskEvent(event) {
  const status = String(event?.statusCode || "").trim();
  return status === "unresolved"
    || status === "fragile"
    || Number(event?.unresolvedRiskMinutes || 0) > 0
    || Number(event?.robustnessRiskMinutes || 0) > 0;
}

export function formatRiskEventStatusLabel(event, fallbackSeverity, t) {
  const status = String(event?.statusCode || "").trim();
  if (status === "unresolved") {
    return t("planner.riskLevel.high");
  }
  if (status === "fragile") {
    return t("planner.riskLevel.fragile");
  }
  return formatRiskSeverityLabel(fallbackSeverity, t);
}

export function formatRiskReasonLabel(reasonCode, t) {
  switch (reasonCode) {
    case "waitLimitExceeded":
      return t("planner.risk.reason.waitLimitExceeded");
    case "unresolvedConflict":
      return t("planner.risk.reason.unresolvedConflict");
    case "lowMargin":
      return t("planner.risk.reason.lowMargin");
    default:
      return "";
  }
}

export function formatRiskTypeLabel(problemType, t) {
  switch (problemType) {
    case "hardCatchup":
      return t("planner.riskType.hardCatchup");
    case "lowMargin":
      return t("planner.riskType.lowMargin");
    case "backgroundConstraint":
      return t("planner.riskType.backgroundConstraint");
    default:
      return t("planner.riskType.lowMargin");
  }
}

export function formatResolutionStateLabel(resolutionState, t) {
  switch (resolutionState) {
    case "resolved":
    case "handled":
      return t("planner.riskState.resolved");
    case "actionable":
    case "needsAction":
    case "fragile":
      return t("planner.riskState.actionable");
    case "blocked":
    case "unresolved":
      return t("planner.riskState.blocked");
    default:
      return t("planner.riskState.actionable");
  }
}

export function resolveRiskStateTone(resolutionState) {
  switch (resolutionState) {
    case "resolved":
    case "handled":
      return "success";
    case "blocked":
    case "unresolved":
      return "error";
    default:
      return "warning";
  }
}

export function resolveRiskTypeTone(problemType) {
  return problemType === "hardCatchup" ? "error" : "warning";
}

export function resolveDisplayedRiskProblemType(item) {
  const problemType = item?.problemType || "";
  const plannedMinutes = Number(item?.plannedAdjustmentMinutes || 0);
  const requiredHoldMinutes = Number(item?.requiredHoldMinutes || 0);
  const requiredMarginMinutes = Number(item?.requiredMarginMinutes || 0);
  const unresolvedRiskMinutes = Number(item?.unresolvedRiskMinutes || 0);
  const robustnessRiskMinutes = Number(item?.robustnessRiskMinutes || 0);
  if (unresolvedRiskMinutes > 0 || requiredHoldMinutes > plannedMinutes) {
    return "hardCatchup";
  }

  if (problemType !== "hardCatchup") {
    return problemType;
  }

  const hardCatchupResolved = unresolvedRiskMinutes <= 0 && plannedMinutes >= requiredHoldMinutes;
  const marginStillShort = robustnessRiskMinutes > 0 || requiredMarginMinutes > plannedMinutes;
  return hardCatchupResolved && marginStillShort ? "lowMargin" : problemType;
}

export function formatBlockReasonLabel(blockReasonCode, t) {
  switch (blockReasonCode) {
    case "noUsableBypassStation":
      return t("planner.blockReason.noUsableBypassStation");
    case "waitBudgetTooLow":
      return t("planner.blockReason.waitBudgetTooLow");
    case "needsBypassStation":
      return t("planner.blockReason.needsBypassStation");
    case "selectedBypassStationNotUsable":
      return t("planner.blockReason.selectedBypassStationNotUsable");
    case "offsetRangeTooSmall":
      return t("planner.blockReason.offsetRangeTooSmall");
    default:
      return "";
  }
}

export function formatSuggestedOptionLabel(optionCode, t) {
  switch (optionCode) {
    case "maxLocalWaitMinutes":
      return t("planner.suggestedOption.maxLocalWaitMinutes");
    case "maxAdditionalBypassStations":
      return t("planner.suggestedOption.maxAdditionalBypassStations");
    case "forcedBypassStationIds":
      return t("planner.suggestedOption.forcedBypassStationIds");
    case "maxLocalRetimeMinutes":
      return t("planner.suggestedOption.maxLocalRetimeMinutes");
    case "maxOffsetMinutes":
      return t("planner.suggestedOption.maxOffsetMinutes");
    case "adjustableLineIds":
      return t("planner.suggestedOption.adjustableLineIds");
    default:
      return "";
  }
}

export function formatPairRoleLabel(pairRole, t) {
  switch (pairRole) {
    case "target-fixed":
      return t("planner.pairRole.targetFixed");
    case "adjustable-fixed":
      return t("planner.pairRole.adjustableFixed");
    case "target-adjustable":
      return t("planner.pairRole.targetAdjustable");
    default:
      return t("planner.pairRole.targetAdjustable");
  }
}

export function formatRiskItemSummary(item, resolvers, t) {
  const fromStationId = item?.catchupFromStationId || item?.fromStationId || "";
  const toStationId = item?.catchupToStationId || item?.toStationId || "";
  const fromStationName = fromStationId ? resolvers.resolveStationName(fromStationId) : "";
  const toStationName = toStationId ? resolvers.resolveStationName(toStationId) : "";
  const interval = [
    fromStationName,
    toStationName
  ].filter(Boolean).join(" - ") || "--";
  const originalProblemType = item?.problemType || "";
  const problemType = resolveDisplayedRiskProblemType(item);
  if (problemType === "hardCatchup") {
    return t("planner.risk.summary.hardCatchup", {
      interval,
      required: formatMinutesLabel(item?.requiredHoldMinutes),
      gap: formatMinutesLabel(item?.currentWorstCaseGapMinutes)
    });
  }
  if (problemType === "backgroundConstraint") {
    return t("planner.risk.summary.backgroundConstraint", {
      interval,
      role: formatPairRoleLabel(item?.pairRole, t),
      gap: formatMinutesLabel(item?.currentWorstCaseGapMinutes)
    });
  }
  const plannedMinutes = Number(item?.plannedAdjustmentMinutes || 0);
  const requiredMarginMinutes = Number(item?.requiredMarginMinutes || 0);
  const marginShortfall = Math.max(0, requiredMarginMinutes - plannedMinutes);
  if (originalProblemType === "hardCatchup") {
    return t("planner.risk.summary.hardCatchupMarginOnly", {
      interval,
      margin: formatMinutesLabel(marginShortfall),
      gap: formatMinutesLabel(item?.currentWorstCaseGapMinutes)
    });
  }
  return t("planner.risk.summary.lowMargin", {
    interval,
    margin: formatMinutesLabel(marginShortfall),
    gap: formatMinutesLabel(item?.currentWorstCaseGapMinutes)
  });
}

export function formatRiskItemAction(item, resolvers, t) {
  const state = item?.resolutionState || "";
  const selectedBypassStationId = item?.selectedBypassStationId || "";
  const stationName = selectedBypassStationId ? resolvers.resolveStationName(selectedBypassStationId, "") : "";
  const plannedMinutes = Number(item?.plannedAdjustmentMinutes || 0);
  if ((state === "resolved" || state === "handled") && plannedMinutes > 0 && stationName) {
    return t("planner.risk.action.resolvedHold", {
      station: stationName,
      minutes: formatMinutesLabel(plannedMinutes)
    });
  }
  if (state === "resolved" || state === "handled") {
    return t("planner.risk.action.resolved");
  }

  const reason = formatBlockReasonLabel(item?.blockReasonCode, t);
  const options = joinDisplayValues((item?.suggestedOptionCodes || [])
    .map((optionCode) => formatSuggestedOptionLabel(optionCode, t))
    .filter(Boolean), " / ", "");
  if (reason && options) {
    return t("planner.risk.action.blockedWithOptions", { reason, options });
  }
  if (reason) {
    return reason;
  }
  if (options) {
    return t("planner.risk.action.suggestedOptions", { options });
  }
  return t("planner.empty.noSuggestedActions");
}

export function formatRiskItemDetail(item, t) {
  const state = item?.resolutionState || "";
  if (state !== "blocked") {
    return "";
  }

  const plannedMinutes = Number(item?.plannedAdjustmentMinutes || 0);
  const requiredHoldMinutes = Number(item?.requiredHoldMinutes || 0);
  const requiredMarginMinutes = Number(item?.requiredMarginMinutes || 0);
  const hardShortfall = Math.max(0, requiredHoldMinutes - plannedMinutes);
  const marginShortfall = Math.max(0, requiredMarginMinutes - plannedMinutes);
  const parts = [];
  if (hardShortfall > 0) {
    parts.push(t("planner.risk.detail.blockedShortfall", {
      catchupTime: item?.catchupTime || "--",
      planned: formatMinutesLabel(plannedMinutes),
      shortfall: formatMinutesLabel(hardShortfall)
    }));
  }
  if (hardShortfall > 0 && marginShortfall > hardShortfall) {
    parts.push(t("planner.risk.detail.blockedMarginShortfall", {
      shortfall: formatMinutesLabel(marginShortfall)
    }));
  }
  return parts.join(" ");
}

export function mapRiskItemToDisplay(item, itemIndex, resolvers, t) {
  const problemType = resolveDisplayedRiskProblemType(item);
  const resolutionState = item?.resolutionState || "";
  const stateTone = resolveRiskStateTone(resolutionState);
  const typeTone = resolveRiskTypeTone(problemType);
  const displayTone = stateTone === "success" ? stateTone : typeTone;
  return {
    id: item?.riskId || `risk-item-${itemIndex}`,
    status: formatRiskTypeLabel(problemType, t),
    stateLabel: formatResolutionStateLabel(resolutionState, t),
    typeToneClass: `is-${typeTone}`,
    stateToneClass: `is-${displayTone}`,
    itemToneClass: `is-${displayTone}`,
    lineSrc: resolvers.resolveLineName(item?.yieldingLineId),
    lineDest: resolvers.resolveLineName(item?.priorityLineId),
    interval: [
      item?.catchupFromStationId || item?.fromStationId
        ? resolvers.resolveStationName(item?.catchupFromStationId || item?.fromStationId)
        : "",
      item?.catchupToStationId || item?.toStationId
        ? resolvers.resolveStationName(item?.catchupToStationId || item?.toStationId)
        : ""
    ].filter(Boolean).join(" - "),
    tripPair: t("planner.risk.tripPair", {
      yieldingTrip: formatTripDescriptor(
        resolvers.resolveLineName(item?.yieldingLineId),
        item?.yieldingDepartTime,
        item?.yieldingTripId
      ),
      priorityTrip: formatTripDescriptor(
        resolvers.resolveLineName(item?.priorityLineId),
        item?.priorityDepartTime,
        item?.priorityTripId
      )
    }),
    summary: formatRiskItemSummary(item, resolvers, t),
    detail: formatRiskItemDetail(item, t),
    action: formatRiskItemAction(item, resolvers, t),
    warning: displayTone === "error",
    events: []
  };
}

export function formatTripDescriptor(lineName, departTime, tripId) {
  const label = [departTime, lineName].filter(Boolean).join(" ");
  return label || tripId || "--";
}

export function formatActionSummary(action, resolvers, t) {
  const actionType = action?.actionType || action?.type || "";
  switch (actionType) {
    case "expressOffset":
      return t("planner.actionSummary.expressOffset", {
        delta: formatNumberValue(action?.deltaOffsetMinutes || action?.deltaMinutes)
      });
    case "bypassSet":
      return t("planner.actionSummary.bypassSet", {
        stationNames: joinDisplayValues((action?.stationIds || []).map((stationId) => resolvers.resolveStationName(stationId)))
      });
    case "retime":
      return t("planner.actionSummary.retime", {
        lineName: resolvers.resolveLineName(action?.affectedLineId),
        delta: formatNumberValue(action?.deltaMinutes)
      });
    case "predictedHold":
      return t("planner.actionSummary.predictedHold", {
        lineName: resolvers.resolveLineName(action?.affectedLineId),
        delta: formatNumberValue(action?.deltaMinutes)
      });
    default:
      return "";
  }
}

export function formatRecommendedActionCode(actionCode, risk, resolvers, t) {
  switch (actionCode) {
    case "addBypassStation":
      return t("planner.recommendedAction.addBypassStation");
    case "relaxWaitLimit":
      return t("planner.recommendedAction.relaxWaitLimit");
    case "shiftExpressOffset":
      return t("planner.recommendedAction.shiftExpressOffset");
    case "addBuffer":
      return t("planner.recommendedAction.addBuffer");
    case "retimeLocalTrip":
      return t("planner.recommendedAction.retimeLocalTrip");
    case "keepCurrentPlan":
      return t("planner.recommendedAction.keepCurrentPlan");
    case "preferBypassStation":
      return t("planner.recommendedAction.preferBypassStation", {
        station: resolvers.resolveStationName(risk?.recommendedBypassStationId)
      });
    default:
      return "";
  }
}

export function formatIssueMessage(issue, clusterById, resolvers, t) {
  const issueType = issue?.type || "";
  const cluster = clusterById.get(issue?.clusterId || "") || null;
  switch (issueType) {
    case "unresolvedConflict":
      return t("planner.issueMessage.unresolvedConflict", {
        priorityLine: resolvers.resolveLineName(issue?.priorityLineId),
        from: resolvers.resolveStationName(cluster?.fromStationId),
        to: resolvers.resolveStationName(cluster?.toStationId),
        yieldingLine: resolvers.resolveLineName(issue?.yieldingLineId),
        station: resolvers.resolveStationName(issue?.recommendedBypassStationId)
      });
    case "waitLimitExceeded":
      return t("planner.issueMessage.waitLimitExceeded", {
        yieldingLine: resolvers.resolveLineName(issue?.yieldingLineId),
        required: formatNumberValue(issue?.requiredHoldMinutes),
        budget: formatNumberValue(issue?.holdBudgetMinutes)
      });
    case "robustnessWeak":
      return t("planner.issueMessage.robustnessWeak", {
        risk: formatNumberValue(issue?.riskMinutes)
      });
    case "fixedLineAffected":
      return t("planner.issueMessage.fixedLineAffected", {
        lineNames: joinDisplayValues((issue?.lineIds || []).map((lineId) => resolvers.resolveLineName(lineId)))
      });
    case "originDepartureGap":
      return t("planner.issueMessage.originDepartureGap", {
        lineNames: joinDisplayValues((issue?.lineIds || []).map((lineId) => resolvers.resolveLineName(lineId))),
        gap: formatNumberValue(5 - Number(issue?.severityMinutes || 0))
      });
    default:
      return "";
  }
}

export function formatDiagnosticMessage(diagnostic, t) {
  const code = String(diagnostic?.code || "").trim();
  switch (code) {
    case "NO_LOCAL_LINES":
      return t("planner.diagnosticCode.noLocalLines");
    case "VIRTUAL_BASE_LINE_MISSING":
      return t("planner.diagnosticCode.virtualBaseLineMissing");
    case "NO_WORKING_ROWS":
      return t("planner.diagnosticCode.noWorkingRows");
    case "BACKEND_ANALYSIS_READY":
      return "";
    default:
      return code || "";
  }
}

export function translatePreviewStatus(row, t) {
  const statusCode = String(row?.statusCode || "").trim();
  switch (statusCode) {
    case "firstDeparture":
      return t("planner.previewStatus.first");
    case "expressPass":
      return t("planner.previewStatus.through");
    case "express":
      return t("planner.previewStatus.express");
    case "normal":
      return t("planner.previewStatus.normal");
    case "delayedByBypass":
      return t("planner.previewStatus.wait", { minutes: formatNumberValue(row?.statusMinutes || row?.deltaMinutes) });
    case "pending":
      return t("planner.previewStatus.pending");
    case "remove":
      return t("planner.previewStatus.remove");
    default:
      break;
  }
  if (row?.deltaMinutes > 0) {
    return t("planner.previewStatus.wait", { minutes: formatNumberValue(row.deltaMinutes) });
  }
  if (row?.kind === "express") {
    return t("planner.previewStatus.express");
  }
  return "--";
}

export function formatChangedWindow(window, resolvers, t) {
  const resolvedLineNames = Array.isArray(window?.lineIds)
    ? window.lineIds.map((lineId, index) => resolvers.resolveLineName(lineId, window?.lineNames?.[index] || ""))
    : [];
  const lineNames = joinDisplayValues(resolvedLineNames, " / ", "")
    || joinDisplayValues(window?.lineNames, " / ", "");
  const rows = Array.isArray(window?.rowDiffs) ? window.rowDiffs : [];
  return {
    id: window?.windowId || "",
    title: lineNames && window?.fromTime && window?.toTime
      ? t("planner.change.windowTitle", {
        lineNames,
        fromTime: window.fromTime,
        toTime: window.toTime
      })
      : t("planner.empty.noLocalAdjustments"),
    rows: rows.map((row) => {
      const timeText = row?.beforeTime && row?.afterTime
        ? t("planner.change.rowTime", {
          beforeTime: row.beforeTime,
          afterTime: row.afterTime
        })
        : "";
      let reason = "";
      if (Number(row?.scheduleShiftMinutes || 0) !== 0) {
        reason = t("planner.change.rowShift", {
          scheduleShift: formatNumberValue(row.scheduleShiftMinutes)
        });
      } else if (Number(row?.predictedDelayMinutes || 0) !== 0) {
        reason = t("planner.change.rowDelay", {
          predictedDelay: formatNumberValue(row.predictedDelayMinutes)
        });
      } else if (Number(row?.totalDeltaMinutes || 0) !== 0) {
        reason = t("planner.change.rowTotal", {
          totalDelta: formatNumberValue(row.totalDeltaMinutes)
        });
      }
      return {
        id: row?.tripId || "",
        line: resolvers.resolveLineName(row?.lineId),
        timeText,
        summary: reason
      };
    })
  };
}

export function formatPreviewRowInfo(rowDiff, t) {
  if (!rowDiff) {
    return "";
  }

  const parts = [];
  if (Number(rowDiff?.scheduleShiftMinutes || 0) !== 0) {
    parts.push(t("planner.change.rowShift", {
      scheduleShift: formatNumberValue(rowDiff.scheduleShiftMinutes)
    }));
  }
  if (Number(rowDiff?.predictedDelayMinutes || 0) !== 0) {
    parts.push(t("planner.change.rowDelay", {
      predictedDelay: formatNumberValue(rowDiff.predictedDelayMinutes)
    }));
  }
  if (Number(rowDiff?.totalDeltaMinutes || 0) !== 0) {
    parts.push(t("planner.change.rowTotal", {
      totalDelta: formatNumberValue(rowDiff.totalDeltaMinutes)
    }));
  }

  return parts.join(" / ");
}

export function buildCondensedTimetableRows(timetableRows, changedWindows, resolvers, t) {
  const orderedRows = Array.isArray(timetableRows) ? timetableRows : [];
  const rowDiffs = (Array.isArray(changedWindows) ? changedWindows : [])
    .flatMap((window) => Array.isArray(window?.rowDiffs) ? window.rowDiffs : []);
  const rowDiffByTripId = new Map(
    rowDiffs
      .filter((row) => row?.tripId)
      .map((row) => [row.tripId, row])
  );
  const changedTripIds = new Set(rowDiffByTripId.keys());

  function mapPreviewRow(row) {
    const rowDiff = rowDiffByTripId.get(row.tripId) || null;
    return {
      id: row.tripId,
      time: rowDiff?.beforeTime && rowDiff?.afterTime
        ? t("planner.change.rowTime", {
          beforeTime: rowDiff.beforeTime,
          afterTime: rowDiff.afterTime
        })
        : row.time,
      line: resolvers.resolveLineName(row.lineId, row.lineName || ""),
      type: row.kind === "express" ? t("nativeSchedule.type.express") : t("nativeSchedule.type.local"),
      dotTone: row.kind === "express" ? "express" : "local",
      station: resolvers.resolveStationName(row.originStationId),
      status: translatePreviewStatus(row, t),
      info: formatPreviewRowInfo(rowDiff, t),
      warning: Number(row.deltaMinutes || 0) > 0 || Number(rowDiff?.totalDeltaMinutes || 0) !== 0
    };
  }

  if (orderedRows.length === 0) {
    return [];
  }

  if (changedTripIds.size === 0) {
    return orderedRows.map(mapPreviewRow);
  }

  const condensedRows = [];
  let skippedCount = 0;

  function flushSkipped() {
    if (skippedCount <= 0) {
      return;
    }
    condensedRows.push({
      id: `skip-${condensedRows.length}`,
      skip: true,
      message: t("planner.table.skip", {
        count: skippedCount
      })
    });
    skippedCount = 0;
  }

  for (let index = 0; index < orderedRows.length; index += 1) {
    const row = orderedRows[index];
    if (!changedTripIds.has(row?.tripId)) {
      skippedCount += 1;
      continue;
    }

    flushSkipped();
    condensedRows.push(mapPreviewRow(row));
  }

  flushSkipped();
  return condensedRows;
}
