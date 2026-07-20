export const emptyMergedView = {
  localLineId: "",
  expressLineId: "",
  localLineIds: [],
  expressLineIds: [],
  isLoop: true,
  turnbackStationId: "",
  direction: "up",
  windowStart: "06:00",
  windowEnd: "06:30",
  lineWidthScale: 1,
  showStopAnchors: true
};

export const emptyManualRows = [];
export const emptyAutoRules = [];
export const emptyLineDraftRows = [];
export const emptyStagedRows = emptyLineDraftRows;
export const emptyLines = [];
export const emptyDepots = [];
export const emptyStations = [];
export const emptyTrips = [];
export const emptyFeatureSettings = {
  dispatchEnabled: true,
  bypassEnabled: true,
  broadcastEnabled: true,
  depotLockEnabled: true
};

export function createEmptySnapshot(overrides = {}) {
  return {
    selectedLineId: "",
    selectedEditLine: "",
    mergedView: emptyMergedView,
    lines: emptyLines,
    depots: emptyDepots,
    stations: emptyStations,
    lineDraftRowsByLineId: [],
    appliedRows: emptyLineDraftRows,
    version: "empty",
    sourceMode: "backend-fallback",
    clientRequestSequence: 0,
    draftApplied: false,
    featureSettings: { ...emptyFeatureSettings },
    ...overrides
  };
}



