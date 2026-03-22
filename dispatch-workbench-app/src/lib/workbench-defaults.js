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
export const emptyStagedRows = [];
export const emptyLines = [];
export const emptyDepots = [];
export const emptyStations = [];
export const emptyTrips = [];

export function createEmptySnapshot(overrides = {}) {
  return {
    selectedLineId: "",
    selectedEditLine: "",
    mergedView: emptyMergedView,
    lines: emptyLines,
    depots: emptyDepots,
    stations: emptyStations,
    trips: emptyTrips,
    manualRows: emptyManualRows,
    autoRules: emptyAutoRules,
    stagedRows: emptyStagedRows,
    version: "empty",
    sourceMode: "backend-fallback",
    ...overrides
  };
}



