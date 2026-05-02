import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  buildLocalObservedModel,
  inferExpressRuntimeFromLocal,
  searchExistingBypassPlans,
  searchJointPlans,
  searchVirtualBypassPlans
} from "../src/lib/express-bypass-planner.js";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const DEFAULT_EXPORT_PATH = path.join(
  process.env.USERPROFILE || "",
  "AppData",
  "LocalLow",
  "Colossal Order",
  "Cities Skylines II",
  "Logs",
  "RapidTransitMod-planner-input-latest.json"
);

function pickArg(index, fallbackValue) {
  const value = process.argv[index];
  return value && value.trim() ? value.trim() : fallbackValue;
}

const jsonPath = pickArg(2, DEFAULT_EXPORT_PATH);
const windowStart = pickArg(3, "00:00");
const windowEnd = pickArg(4, "23:59");

const raw = JSON.parse(await fs.readFile(jsonPath, "utf8"));
const result = searchExistingBypassPlans(raw, {
  windowStart,
  windowEnd
});
const forcedBypassStationId = result.plans[0]?.selectedBypassStations?.[0]?.stationId || "";
const jointResult = searchJointPlans(raw, {
  mode: "joint",
  windowStart,
  windowEnd,
  expressTripsPerHour: 2,
  expressOffsetMinutes: 0,
  maxLocalRetimeMinutes: 2,
  maxAdditionalBypassStations: 3
});
const jointNoBypassResult = searchJointPlans(raw, {
  mode: "joint",
  windowStart,
  windowEnd,
  expressTripsPerHour: 2,
  expressOffsetMinutes: 0,
  maxLocalRetimeMinutes: 2,
  maxAdditionalBypassStations: 0
});
const jointForcedBypassResult = forcedBypassStationId
  ? searchJointPlans(raw, {
    mode: "joint",
    windowStart,
    windowEnd,
    expressTripsPerHour: 2,
    expressOffsetMinutes: 0,
    maxLocalRetimeMinutes: 2,
    maxAdditionalBypassStations: 1,
    forcedBypassStationId
  })
  : null;
const virtualResult = searchVirtualBypassPlans(raw, {
  windowStart,
  windowEnd,
  maxAdditionalBypassStations: 3,
  virtualCandidateLimit: 6
});
const localModel = buildLocalObservedModel(raw, {
  localLineIds: result.localLineIds
});
const inferredExpressRuntime = inferExpressRuntimeFromLocal(raw, {
  localLineId: result.localLineIds[0] || ""
});

function summarizeJointPlan(plan) {
  return plan
    ? {
      offsetMinutes: plan.recommendedExpressOffsetDeltaMinutes,
      score: plan.score,
      localRetimeIterations: plan.localRetimeIterations,
      totalRetimedTrips: plan.totalRetimedTrips,
      totalRetimedMinutes: plan.totalRetimedMinutes,
      addedVirtualBypassStations: (plan.addedVirtualBypassStations || []).map((station) => station.stationId),
      rowCount: plan.sourceTimetableRows.length,
      catchupClusterCount: plan.catchupClusters.length,
      optimizationRegionCount: (plan.optimizationRegions || []).length,
      regionResults: (plan.regionResults || []).map((region) => ({
        regionId: region.regionId,
        eventCount: region.eventCount,
        reservationCount: region.reservationCount,
        reservationConflictCount: region.reservationConflictCount,
        regionLocalDelayMaxMinutes: region.regionLocalDelayMaxMinutes,
        regionExpressDelayMaxMinutes: region.regionExpressDelayMaxMinutes
      })),
      retimeGroupsApplied: (plan.retimeGroupsApplied || []).slice(0, 3),
      windowRebuildActions: (plan.windowRebuildActions || []).slice(0, 3),
      scheduleActions: (plan.scheduleActions || []).slice(0, 5),
      scheduleProblem: plan.scheduleProblem || null,
      metrics: plan.metrics
    }
    : null;
}

const topPlans = result.plans.slice(0, 5).map((plan) => ({
  offsetMinutes: plan.recommendedExpressOffsetDeltaMinutes,
  score: plan.score,
  catchupEventCount: plan.catchupEvents.length,
  selectedBypassStations: plan.selectedBypassStations.map((station) => station.stationId),
  metrics: plan.metrics,
  explanation: plan.explanation
}));

const output = {
  exportPath: jsonPath,
  sharedCorridorExportCount: Array.isArray(raw?.currentTrackScenario?.sharedCorridors)
    ? raw.currentTrackScenario.sharedCorridors.length
    : 0,
  analysisWindow: result.analysisWindow,
  draftKey: result.draftKey,
  localLineIds: result.localLineIds,
  expressLineIds: result.expressLineIds,
  corridorCount: result.corridorCount,
  departureRowCount: result.departureRowCount,
  localObservedSummaries: localModel.summaries.map((summary) => ({
    lineId: summary.lineId,
    totalMinuteSpan: summary.totalMinuteSpan,
    observedStationCount: summary.observedStationCount,
    observedRuntimeSegmentCount: summary.observedRuntimeSegmentCount,
    stationCount: summary.stationCount,
    confidence: summary.confidence
  })),
  inferredExpressRuntime,
  topPlans,
  existingTopClusterCount: result.plans[0]?.catchupClusters?.length || 0,
  jointTopPlan: summarizeJointPlan(jointResult.plans[0])
    ? {
      ...summarizeJointPlan(jointResult.plans[0]),
      presetTopPlans: jointResult.presetTopPlans,
      request: jointResult.request
    }
    : null,
  jointNoBypassTopPlan: summarizeJointPlan(jointNoBypassResult.plans[0])
    ? {
      ...summarizeJointPlan(jointNoBypassResult.plans[0]),
      request: jointNoBypassResult.request
    }
    : null,
  jointForcedBypassTopPlan: summarizeJointPlan(jointForcedBypassResult?.plans?.[0])
    ? {
      forcedBypassStationId,
      ...summarizeJointPlan(jointForcedBypassResult.plans[0]),
      request: jointForcedBypassResult.request
    }
    : null,
  virtualTopPlan: virtualResult.plans[0]
    ? {
      offsetMinutes: virtualResult.plans[0].recommendedExpressOffsetDeltaMinutes,
      score: virtualResult.plans[0].score,
      addedVirtualBypassStations: virtualResult.plans[0].addedVirtualBypassStations.map((station) => station.stationId),
      catchupClusterCount: virtualResult.plans[0].catchupClusters.length,
      metrics: virtualResult.plans[0].metrics
    }
    : null
};

console.log(JSON.stringify(output, null, 2));
