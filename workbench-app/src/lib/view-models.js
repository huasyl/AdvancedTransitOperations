import { formatDirection, timeToMinutes } from "./time";
import { collectOriginDepartureConflictRowIds } from "./auto-schedule";
import { buildValidationIssues } from "./validation";

export function getFilteredTrips({ viewMode, selectedLineId, mergedView, trips }) {
  if (viewMode === "single") {
    return trips.filter((trip) => trip.lineId === selectedLineId);
  }

  const localLineIds = Array.isArray(mergedView.localLineIds) && mergedView.localLineIds.length > 0
    ? mergedView.localLineIds
    : mergedView.localLineId
      ? [mergedView.localLineId]
      : [];
  const expressLineIds = Array.isArray(mergedView.expressLineIds) && mergedView.expressLineIds.length > 0
    ? mergedView.expressLineIds
    : mergedView.expressLineId
      ? [mergedView.expressLineId]
      : [];
  const mergedLineSet = new Set([...localLineIds, ...expressLineIds].filter(Boolean));
  if (mergedLineSet.size === 0) {
    return [];
  }

  return trips.filter((trip) => mergedLineSet.has(trip.lineId));
}

export function getSelectedTrip(filteredTrips, selectedTripId) {
  return filteredTrips.find((trip) => trip.id === selectedTripId) ?? filteredTrips[0] ?? null;
}

export function buildOverviewSideContext({
  viewMode,
  mergedView,
  selectedLine,
  selectedTrip,
  filteredTrips,
  stations,
  t
}) {
  const lineSummary = [
    { label: t("context.view"), value: viewMode === "merged" ? t("overview.form.mode.merged") : t("overview.form.mode.single") },
    {
      label: t("context.line"),
      value: viewMode === "merged" ? t("context.localPlusExpress") : selectedLine?.name ?? t("context.none")
    },
    { label: t("context.direction"), value: formatDirection(mergedView.direction, t) },
    { label: t("context.window"), value: `${mergedView.windowStart} - ${mergedView.windowEnd}` },
    { label: t("context.trips"), value: `${filteredTrips.length}` }
  ];

  const modeSummary =
    viewMode === "merged"
      ? [
          t("context.note.merged.1"),
          t("context.note.merged.2"),
          t("context.note.merged.3")
        ]
      : [
          t("context.note.single.1"),
          t("context.note.single.2"),
          t("context.note.single.3")
        ];

  const validationSummary = selectedTrip
    ? [
        t("context.validation.selectedTrip", { id: selectedTrip.id, depart: selectedTrip.depart }),
        t("context.validation.stationCoverage", { count: stations.length }),
        t("context.validation.overviewViewOnly")
      ]
    : [t("context.validation.noTrip")];

  const suggestedActions = [
    { key: "departure-control", label: t("context.action.departureControl") },
    { key: "single-line-view", label: t("context.action.singleLine") },
    { key: "narrow-window", label: t("context.action.narrowWindow") }
  ];

  return { lineSummary, modeSummary, validationSummary, suggestedActions };
}

export function buildScheduleSideContext({
  selectedEditLine,
  lineOptions = [],
  manualRows,
  autoRules,
  validatedRows,
  t
}) {
  const issues = buildValidationIssues(validatedRows, t);
  const errorCount = issues.filter((issue) => issue.severity === "error").length;
  const warningCount = issues.filter((issue) => issue.severity === "warning").length;
  const enabledRuleCount = autoRules.filter((rule) => rule.enabled).length;

  const selectedEditLineOption = lineOptions.find((line) => line.id === selectedEditLine) ?? null;
  const lineSummary = [
    {
      label: t("context.editing"),
      value: selectedEditLineOption?.name ?? selectedEditLineOption?.rawName ?? selectedEditLine ?? t("context.none")
    },
    { label: t("context.manualRows"), value: String(manualRows.length) },
    { label: t("context.enabledRules"), value: String(enabledRuleCount) },
    { label: t("validation.errors"), value: String(errorCount) },
    { label: t("validation.warnings"), value: String(warningCount) }
  ];

  const modeSummary = [
    t("context.note.schedule.1"),
    t("context.note.schedule.2"),
    t("context.note.schedule.3")
  ];

  const validationSummary =
    issues.length > 0
      ? issues.map((issue) => issue.message)
      : [
          t("context.validation.manualPassed"),
          t("context.validation.previewSave")
        ];

  const suggestedActions = [
    { key: "sort-manual-rows", label: t("schedule.button.sortRows") },
    { key: "preview-generation", label: t("schedule.button.previewGeneration") },
    { key: "apply-draft", label: t("schedule.button.applyDraft") }
  ];

  return {
    errorCount,
    warningCount,
    validationIssues: issues,
    lineSummary,
    modeSummary,
    validationSummary,
    suggestedActions
  };
}

export function buildPreviewSummary({ selectedEditLine, lineOptions = [], validatedRows, stagedRows = [], t }) {
  const issues = buildValidationIssues(validatedRows, t);
  const selectedEditLineOption = lineOptions.find((line) => line.id === selectedEditLine) ?? null;
  const visibleStagedRows = stagedRows.filter((row) => !selectedEditLine || row.lineId === selectedEditLine);
  const firstValidRow = visibleStagedRows[0] ?? validatedRows.find((row) => row.validation.status !== "error");
  const earliestStart = firstValidRow?.time ?? "--:--";

  return {
    title: selectedEditLineOption?.name ?? selectedEditLineOption?.rawName ??
      t("preview.title.local"),
    description:
      issues.length > 0
        ? t("preview.description.invalid")
        : t("preview.description.valid"),
    generatedTrips: visibleStagedRows.length,
    earliestStart,
    issueCount: issues.length
  };
}

export function buildCombinedScheduleRows({ stagedRows = [], lineOptions = [], selectedEditLine, t }) {
  const duplicateCounts = new Map();
  const lineKinds = new Map();
  const lineOriginById = new Map(
    lineOptions.map((line) => [line.id, {
      id: line.originStationId || "",
      name: line.originStationName || ""
    }])
  );
  stagedRows.forEach((row) => {
    const key = `${row.lineId}|${row.kind}|${row.time}`;
    duplicateCounts.set(key, (duplicateCounts.get(key) || 0) + 1);
    const kinds = lineKinds.get(row.lineId) ?? new Set();
    kinds.add(row.kind);
    lineKinds.set(row.lineId, kinds);
  });

  const tooCloseIds = collectOriginDepartureConflictRowIds(
    stagedRows,
    new Map(lineOptions.map((line) => [line.id, line.originStationId || ""]))
  );

  return stagedRows
    .filter((row) => timeToMinutes(row.time) !== null)
    .map((row) => {
      const line = lineOptions.find((option) => option.id === row.lineId);
      const origin = lineOriginById.get(row.lineId) || { id: "", name: "" };
      const duplicateKey = `${row.lineId}|${row.kind}|${row.time}`;
      const isDuplicate = (duplicateCounts.get(duplicateKey) || 0) > 1;
      const isTooClose = tooCloseIds.has(row.id);
      const hasKindConflict = (lineKinds.get(row.lineId)?.size || 0) > 1;
      const isConflict = isDuplicate || isTooClose || hasKindConflict;
      return {
        id: row.id,
        lineId: row.lineId,
        lineName: line?.name || row.lineId || t("context.none"),
        lineColor: line?.color || "",
        time: row.time,
        kind: row.kind,
        source: row.source === "auto" ? t("schedule.source.auto") : t("schedule.source.manual"),
        sourceKey: row.source,
        note: row.note || t("combined.note.direct"),
        originStationId: origin.id,
        originStationName: origin.name || "",
        isCurrentLine: !!selectedEditLine && row.lineId === selectedEditLine,
        isConflict
      };
    })
    .sort((left, right) => {
    const leftMinutes = timeToMinutes(left.time) ?? 9999;
    const rightMinutes = timeToMinutes(right.time) ?? 9999;
    if (leftMinutes !== rightMinutes) {
      return leftMinutes - rightMinutes;
    }

    if (left.kind !== right.kind) {
      return left.kind.localeCompare(right.kind);
    }

      if (left.lineName !== right.lineName) {
        return left.lineName.localeCompare(right.lineName);
      }

      return left.source.localeCompare(right.source);
    });
}

export function normalizeSelectedTripId(filteredTrips, selectedTripId) {
  if (filteredTrips.some((trip) => trip.id === selectedTripId)) {
    return selectedTripId;
  }

  return filteredTrips[0]?.id ?? "";
}

export function isWindowValid(windowStart, windowEnd) {
  const start = timeToMinutes(windowStart);
  const end = timeToMinutes(windowEnd);
  return start !== null && end !== null && end > start;
}


