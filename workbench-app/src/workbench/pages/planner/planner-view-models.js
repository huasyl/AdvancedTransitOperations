import {
  buildCondensedTimetableRows,
  formatActionSummary,
  formatChangedWindow,
  formatDiagnosticMessage,
  formatIssueMessage,
  formatMinutesLabel,
  formatNumberValue,
  formatPlannerBadgeLabel,
  formatPlannerObjectiveTitle,
  formatRecommendedActionCode,
  formatRiskEventStatusLabel,
  formatRiskReasonLabel,
  formatRiskSeverityLabel,
  formatTripDescriptor,
  joinUniqueDisplayValues,
  mapRiskItemToDisplay,
  readHighestCapacityConsumptionPercent,
  shouldShowRiskCluster,
  shouldShowRiskEvent,
  summarizePlanTypeFromRisks
} from "./planner-format.js";

export function buildLineCollections(plannerInput) {
  const lines = Array.isArray(plannerInput?.lines) ? plannerInput.lines : [];
  const lineById = new Map(lines
    .filter((line) => line && typeof line.id === "string" && line.id)
    .map((line) => [line.id, line]));
  const aliasToCanonicalLineId = new Map();
  lines.forEach((line) => {
    if (!line || typeof line.id !== "string" || !line.id) {
      return;
    }
    aliasToCanonicalLineId.set(line.id, line.id);
    if (Number.isInteger(line.entityIndex) && line.entityIndex >= 0) {
      aliasToCanonicalLineId.set(String(line.entityIndex), line.id);
    }
  });
  const allLineOptions = lines
    .filter((line) => line && typeof line.id === "string" && line.id)
    .map((line) => ({
      value: line.id,
      label: typeof line.name === "string" && line.name ? line.name : line.id
    }));
  const localLineOptions = allLineOptions.filter(({ value }) => {
    const line = lineById.get(value);
    const kind = String(line?.configuredKind || line?.kind || "local").toLowerCase();
    return kind !== "express";
  });
  const expressLineOptions = allLineOptions.filter(({ value }) => {
    const line = lineById.get(value);
    const kind = String(line?.configuredKind || line?.kind || "").toLowerCase();
    return kind === "express";
  });
  return {
    lineById,
    canonicalizeLineId(lineId) {
      return aliasToCanonicalLineId.get(lineId) || lineId || "";
    },
    allLineOptions,
    localLineOptions,
    expressLineOptions
  };
}

export function buildStationOptionsForLine(plannerInput, lineId) {
  const stations = Array.isArray(plannerInput?.stations) ? plannerInput.stations : [];
  return stations
    .filter((station) => station && station.lineId === lineId)
    .sort((left, right) => Number(left?.order || 0) - Number(right?.order || 0))
    .map((station) => ({
      value: station.workbenchStationId || station.id,
      label: station.name || station.id,
      stationId: station.id,
      lineId: station.lineId
    }));
}

export function buildRelatedLineOptionsForTarget(plannerInput, targetLineId, lineOptions) {
  const target = String(targetLineId || "");
  if (!target) {
    return [];
  }

  const optionByLineId = new Map((Array.isArray(lineOptions) ? lineOptions : []).map((option) => [option.value, option]));
  const relatedIds = new Set();
  const corridors = Array.isArray(plannerInput?.currentTrackScenario?.sharedCorridors)
    ? plannerInput.currentTrackScenario.sharedCorridors
    : [];
  corridors.forEach((corridor) => {
    if (!corridor
      || String(corridor.traversalRelation || "").toLowerCase() !== "samedirection"
      || corridor.hasMirroredContext
      || Number(corridor.orderedRun || 0) <= 0
      || Number(corridor.physicalOverlap || 0) <= 0) {
      return;
    }
    if (corridor.lineId === target && optionByLineId.has(corridor.otherLineId)) {
      relatedIds.add(corridor.otherLineId);
    }
    if (corridor.otherLineId === target && optionByLineId.has(corridor.lineId)) {
      relatedIds.add(corridor.lineId);
    }
  });

  if (optionByLineId.has(target)) {
    relatedIds.add(target);
  }

  return [...relatedIds]
    .map((lineId) => optionByLineId.get(lineId))
    .filter(Boolean)
    .sort((left, right) => String(left.label || "").localeCompare(String(right.label || "")));
}

export function buildForcedBypassOptions(plannerInput, expressSource, virtualBaseLine, adjustableLineIds) {
  const relevantLineIds = expressSource === "virtual" && virtualBaseLine
    ? [virtualBaseLine]
    : (Array.isArray(adjustableLineIds) && adjustableLineIds.length > 0 ? adjustableLineIds : []);
  const relevantLineIdSet = new Set((Array.isArray(relevantLineIds) ? relevantLineIds : []).filter(Boolean));
  const grouped = new Map();
  const allStations = [
    ...(Array.isArray(plannerInput?.configuredBypassStations) ? plannerInput.configuredBypassStations : []),
    ...(Array.isArray(plannerInput?.candidateBypassStations) ? plannerInput.candidateBypassStations : [])
  ];
  allStations.forEach((station) => {
    if (!station || !relevantLineIdSet.has(station.lineId) || !station.stationId) {
      return;
    }
    const nextOrder = Number(station?.order || 0);
    const label = station.name || station.stationId;
    const buildingEntityIndex = Number(station?.buildingEntityIndex);
    const groupKey = Number.isFinite(buildingEntityIndex) && buildingEntityIndex >= 0
      ? `building:${buildingEntityIndex}`
      : `name:${String(label || "").trim().toLowerCase()}`;
    const existing = grouped.get(groupKey);
    if (existing) {
      existing.stationIds.push(station.stationId);
      existing.order = Math.min(existing.order, Number.isFinite(nextOrder) ? nextOrder : existing.order);
      return;
    }

    grouped.set(groupKey, {
      value: groupKey,
      label,
      stationIds: [station.stationId],
      order: Number.isFinite(nextOrder) ? nextOrder : 0
    });
  });
  return [...grouped.values()]
    .map((option) => ({
      ...option,
      stationIds: [...new Set(option.stationIds)]
    }))
    .sort((left, right) => {
      if (left.order !== right.order) {
        return left.order - right.order;
      }
      return String(left.label || "").localeCompare(String(right.label || ""));
    })
    .map(({ value, label, stationIds }) => ({ value, label, stationIds }));
}

export function expandForcedBypassStationIds(selectedValues, forcedBypassOptions) {
  const optionByValue = new Map((Array.isArray(forcedBypassOptions) ? forcedBypassOptions : [])
    .map((option) => [option.value, option]));
  return [...new Set((Array.isArray(selectedValues) ? selectedValues : []).flatMap((value) => {
    const option = optionByValue.get(value);
    if (option && Array.isArray(option.stationIds) && option.stationIds.length > 0) {
      return option.stationIds;
    }
    return value ? [value] : [];
  }).filter(Boolean))];
}

export function buildPlannerResolvers(plannerInput, result, t) {
  const lineNameById = new Map();
  const stationNameById = new Map();
  const lines = Array.isArray(plannerInput?.lines) ? plannerInput.lines : [];
  const stations = Array.isArray(plannerInput?.stations) ? plannerInput.stations : [];
  const targetLineIds = new Set(result?.lineRoleSummary?.targetLineIds || result?.selectedPlan?.lineRoleSummary?.targetLineIds || []);
  const expressSourceMode = String(result?.requestEcho?.expressSourceMode || "").toLowerCase();

  lines.forEach((line) => {
    if (line && line.id) {
      lineNameById.set(line.id, line.name || line.id);
    }
  });
  stations.forEach((station) => {
    if (!station) {
      return;
    }
    const label = station.name || station.id || station.workbenchStationId || "";
    if (station.id) {
      stationNameById.set(station.id, label);
    }
    if (station.workbenchStationId) {
      stationNameById.set(station.workbenchStationId, label);
    }
  });

  return {
    resolveLineName(lineId, fallbackValue = "") {
      if (lineNameById.has(lineId)) {
        return lineNameById.get(lineId);
      }
      if (lineId && targetLineIds.has(lineId) && expressSourceMode === "virtual") {
        return t("planner.line.virtualExpress");
      }
      if (fallbackValue) {
        return fallbackValue;
      }
      return lineId || "--";
    },
    resolveStationName(stationId, fallbackValue = "") {
      if (fallbackValue) {
        return fallbackValue;
      }
      if (stationNameById.has(stationId)) {
        return stationNameById.get(stationId);
      }
      return stationId || "--";
    }
  };
}

export function mapPlannerResultToDisplay(result, plannerInput, t) {
  const selectedPlan = result?.selectedPlan;
  const rawPlanDetails = Array.isArray(result?.plans) && result.plans.length > 0
    ? result.plans
    : (selectedPlan ? [selectedPlan] : []);
  const seenObjectiveIds = new Set();
  const planDetails = rawPlanDetails.filter((plan) => {
    const objectiveId = String(plan?.objectiveId || "").trim();
    if (!objectiveId) {
      return true;
    }
    if (seenObjectiveIds.has(objectiveId)) {
      return false;
    }
    seenObjectiveIds.add(objectiveId);
    return true;
  });
  if (planDetails.length === 0) {
    return { plans: [], activePlanId: "" };
  }

  const resolvers = buildPlannerResolvers(plannerInput, result, t);
  const summaryById = new Map((result.planSummaries || []).map((plan) => [plan.planId, plan]));
  const baselineHighestCapacityConsumptionPercent = readHighestCapacityConsumptionPercent(result.baselineCapacityDiagnostic);
  const plans = planDetails.map((plan, planIndex) => {
    const summary = summaryById.get(plan.planId) || {};
    const riskClusters = Array.isArray(plan.riskClusters) ? plan.riskClusters : [];
    const riskItems = Array.isArray(plan.riskItems) ? plan.riskItems : [];
    const structuredActions = Array.isArray(plan.structuredScheduleActions) ? plan.structuredScheduleActions : [];
    const affectedWaitTripIds = new Set();
    structuredActions.forEach((action) => {
      if ((action?.actionType || action?.type) !== "predictedHold") {
        return;
      }
      (action?.affectedTripIds || action?.tripIds || []).forEach((tripId) => {
        if (tripId) {
          affectedWaitTripIds.add(tripId);
        }
      });
    });
    const affectedWaitTripCount = affectedWaitTripIds.size;
    const localWaitMinutes = Number(summary.localWaitMinutes ?? plan.metrics?.localWaitMinutes ?? 0);
    const optimizedHighestCapacityConsumptionPercent = readHighestCapacityConsumptionPercent(
      summary.capacityDiagnostic
      ?? plan.capacityDiagnostic
      ?? (result.selectedPlan?.planId === plan.planId ? result.selectedPlan?.capacityDiagnostic : null)
    );
    const problemIssues = Array.isArray(plan.problemIssues) ? plan.problemIssues : [];
    const timetableRows = Array.isArray(plan.timetablePreviewRows) ? plan.timetablePreviewRows : [];
    const changedWindows = Array.isArray(plan.changedWindows) ? plan.changedWindows : [];
    const clusterById = new Map(riskClusters.map((riskCluster) => [riskCluster.clusterId, riskCluster]));
    const issueMessages = problemIssues
      .map((issue) => formatIssueMessage(issue, clusterById, resolvers, t))
      .filter(Boolean);
    const fallbackDiagnostics = (plan.diagnostics || [])
      .map((item) => formatDiagnosticMessage(item, t))
      .filter(Boolean);
    const combinedDiagnostics = [...new Set([...issueMessages, ...fallbackDiagnostics])];
    const badgeStatus = summary.status || plan.status || "risk";
    const primaryRiskItems = Array.isArray(riskItems) ? riskItems : [];

    return {
      id: plan.planId || `planner-plan-${planIndex}`,
      rawPlan: plan,
      title: formatPlannerObjectiveTitle(plan.objectiveId, "", t),
      type: summarizePlanTypeFromRisks(primaryRiskItems, badgeStatus),
      badgeLabel: formatPlannerBadgeLabel(badgeStatus, primaryRiskItems, t),
      metrics: {
        expressSave: Number(summary.expressSavedMinutes ?? plan.metrics?.expressSavedMinutes ?? 0),
        baselineHighestCapacityConsumptionPercent,
        optimizedHighestCapacityConsumptionPercent,
        averageLocalWait: affectedWaitTripCount > 0
          ? Number((localWaitMinutes / affectedWaitTripCount).toFixed(1))
          : 0,
        affectedWaitTrips: affectedWaitTripCount,
        overtakes: Number(summary.addedBypassStationCount ?? plan.metrics?.addedBypassStationCount ?? 0)
      },
      stations: Array.isArray(plan.selectedBypassStationIds) && plan.selectedBypassStationIds.length > 0
        ? joinUniqueDisplayValues(plan.selectedBypassStationIds.map((stationId) => resolvers.resolveStationName(stationId)))
        : "--",
      diagnostics: combinedDiagnostics,
      risks: riskItems.length > 0 ? riskItems.map((riskItem, riskItemIndex) =>
        mapRiskItemToDisplay(riskItem, riskItemIndex, resolvers, t)
      ) : riskClusters.filter(shouldShowRiskCluster).map((risk, riskIndex) => {
        const actionMessages = structuredActions
          .filter((action) =>
            (action?.clusterIds || []).includes(risk.clusterId)
            || (action?.reasonClusterIds || []).includes(risk.clusterId))
          .map((action) => formatActionSummary(action, resolvers, t))
          .filter(Boolean);
        const recommendedActionMessages = (risk?.recommendedActionCodes || [])
          .map((actionCode) => formatRecommendedActionCode(actionCode, risk, resolvers, t))
          .filter(Boolean);
        const representativeEvents = (Array.isArray(risk?.representativeEvents) ? risk.representativeEvents : [])
          .filter(shouldShowRiskEvent);
        return {
          id: risk.clusterId || `risk-${riskIndex}`,
          status: formatRiskSeverityLabel(risk.severityLevel, t),
          lineSrc: resolvers.resolveLineName(risk.yieldingLineId),
          lineDest: resolvers.resolveLineName(risk.priorityLineId),
          interval: [
            resolvers.resolveStationName(risk.fromStationId),
            resolvers.resolveStationName(risk.toStationId)
          ].filter(Boolean).join(" - "),
          catchups: Number(risk.catchupCount || 0),
          severity: formatMinutesLabel(risk.maxSeverityMinutes),
          suggestion: actionMessages.length > 0
            ? actionMessages.join(" / ")
            : (recommendedActionMessages.length > 0
              ? recommendedActionMessages.join(" / ")
              : t("planner.empty.noSuggestedActions")),
          warning: risk.severityLevel === "high",
          events: representativeEvents.map((event, eventIndex) => ({
            id: event?.eventId || `${risk.clusterId || "risk"}-event-${eventIndex}`,
            status: formatRiskEventStatusLabel(event, risk.severityLevel, t),
            warning: String(event?.statusCode || "") === "unresolved" || Number(event?.unresolvedRiskMinutes || 0) > 0,
            tripPair: t("planner.risk.tripPair", {
              yieldingTrip: formatTripDescriptor(
                resolvers.resolveLineName(event?.yieldingLineId),
                event?.yieldingDepartTime,
                event?.yieldingTripId
              ),
              priorityTrip: formatTripDescriptor(
                resolvers.resolveLineName(event?.priorityLineId),
                event?.priorityDepartTime,
                event?.priorityTripId
              )
            }),
            interval: [
              resolvers.resolveStationName(event?.catchupFromStationId || event?.fromStationId),
              resolvers.resolveStationName(event?.catchupToStationId || event?.toStationId)
            ].filter(Boolean).join(" - "),
            catchupTime: event?.catchupTime || "",
            waitStation: resolvers.resolveStationName(event?.selectedBypassStationId),
            required: formatMinutesLabel(event?.requiredHoldMinutes),
            budget: formatMinutesLabel(event?.holdBudgetMinutes),
            planned: Number.isFinite(Number(event?.plannedAdjustmentMinutes))
              ? t("planner.risk.plannedAdjustmentValue", {
                planned: formatNumberValue(event?.plannedAdjustmentMinutes)
              })
              : "",
            reason: formatRiskReasonLabel(event?.reasonCode, t)
          }))
        };
      }),
      changedWindows: changedWindows.map((window) => formatChangedWindow(window, resolvers, t)),
      timetableRows: buildCondensedTimetableRows(timetableRows, changedWindows, resolvers, t)
    };
  });

  return {
    plans,
    activePlanId: result?.defaultPlanId || result?.selectedPlan?.planId || plans[0]?.id || ""
  };
}
