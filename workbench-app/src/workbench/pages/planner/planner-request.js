import { parsePositiveInt } from "./planner-time.js";
import { buildLineCollections, expandForcedBypassStationIds } from "./planner-view-models.js";

export function pickPlannerDraft(plannerInput) {
  const drafts = Array.isArray(plannerInput?.drafts) ? plannerInput.drafts : [];
  const validLineIds = new Set(
    (Array.isArray(plannerInput?.lines) ? plannerInput.lines : [])
      .map((line) => line?.id)
      .filter((lineId) => typeof lineId === "string" && lineId)
  );

  const preferredDrafts = drafts.filter((draft) =>
    validLineIds.has(draft?.lineKey)
    || validLineIds.has(draft?.selectedLineId)
  );
  const candidateDrafts = preferredDrafts.length > 0 ? preferredDrafts : drafts;

  return candidateDrafts
    .slice()
    .sort((left, right) =>
      (((right?.lineDraftRows || right?.stagedRows)?.length || 0) + (right?.trips?.length || 0))
      - (((left?.lineDraftRows || left?.stagedRows)?.length || 0) + (left?.trips?.length || 0))
    )[0] || null;
}

export function buildPlannerRequest(params) {
  const {
    plannerInput,
    analysisStart,
    analysisEnd,
    adjustableLines,
    expressSource,
    virtualBaseLine,
    existingExpressLine,
    expressStops,
    dispatchMode,
    dispatchInterval,
    dispatchPhaseStart,
    dispatchTripsPerHour,
    phaseAdjustmentRange,
    maxOvertakes,
    maxLocalShift,
    maxLocalWait,
    forcedOvertakes,
    forcedBypassOptions,
    mode
  } = params;

  const lineCollections = buildLineCollections(plannerInput);
  const canonicalizeLineId = lineCollections.canonicalizeLineId || ((lineId) => lineId || "");
  const normalizedAdjustableLineIds = adjustableLines
    .map(canonicalizeLineId)
    .filter(Boolean);
  const request = {
    mode: String(mode || plannerInput?.mode || "train").trim().toLowerCase() || "train",
    draftKey: pickPlannerDraft(plannerInput)?.lineKey || "",
    windowStart: analysisStart,
    windowEnd: analysisEnd,
    adjustableLineIds: [...new Set(normalizedAdjustableLineIds)],
    expressSourceMode: expressSource === "existing" ? "existing" : "virtual",
    expressLineId: expressSource === "existing" ? canonicalizeLineId(existingExpressLine) : "",
    virtualExpressBaseLineId: expressSource === "virtual" ? canonicalizeLineId(virtualBaseLine) : "",
    expressStopStationIds: expressSource === "virtual" ? expressStops.filter(Boolean) : [],
    departureMode: dispatchMode,
    expressTripsPerHour: dispatchMode === "frequency" ? parsePositiveInt(dispatchTripsPerHour, 0) : 0,
    intervalMinutes: dispatchMode === "interval" || dispatchMode === "phase" || dispatchMode === "reinterval"
      ? parsePositiveInt(dispatchInterval, 0)
      : 0,
    phaseTime: dispatchMode === "phase" ? dispatchPhaseStart : "",
    expressOffsetMinutes: 0,
    maxOffsetMinutes: expressSource === "existing" && dispatchMode === "shift"
      ? parsePositiveInt(phaseAdjustmentRange, 0)
      : 0,
    offsetStepMinutes: expressSource === "existing" && dispatchMode === "shift" ? 2 : 0,
    maxLocalRetimeMinutes: parsePositiveInt(maxLocalShift, 0),
    maxLocalWaitMinutes: parsePositiveInt(maxLocalWait, 0),
    maxAdditionalBypassStations: parsePositiveInt(maxOvertakes, 0),
    forcedBypassStationIds: expandForcedBypassStationIds(forcedOvertakes, forcedBypassOptions)
  };

  if (expressSource === "existing" && !lineCollections.expressLineOptions.some((option) => option.value === request.expressLineId)) {
    request.expressLineId = lineCollections.expressLineOptions[0]?.value || "";
  }

  return request;
}
