import { timeToMinutes } from "./time";

export const MIN_DEPARTURE_INTERVAL_MINUTES = 5;
export const MAX_AUTO_RULE_TRIPS_PER_HOUR = 240;
export const MAX_AUTO_RULE_GENERATED_TRIPS = 720;

function analyzeAutoRuleGeneration(rule) {
  const start = timeToMinutes(rule.start);
  const end = timeToMinutes(rule.end);
  const departuresPerHour = Number(rule.departuresPerHour) || 0;
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start || departuresPerHour <= 0) {
    return { ok: false, reason: "invalid" };
  }

  if (departuresPerHour > MAX_AUTO_RULE_TRIPS_PER_HOUR) {
    return { ok: false, reason: "frequencyLimit" };
  }

  const estimatedCount = Math.ceil(((end - start) * departuresPerHour) / 60);
  if (!Number.isFinite(estimatedCount) || estimatedCount > MAX_AUTO_RULE_GENERATED_TRIPS) {
    return { ok: false, reason: "tripLimit" };
  }

  return {
    ok: true,
    start,
    end,
    departuresPerHour
  };
}

function enumerateRuleMinutesFromAnalysis(analysis) {
  const interval = 60 / analysis.departuresPerHour;
  const result = [];
  for (let minute = analysis.start; minute < analysis.end; minute += interval) {
    result.push(Math.round(minute));
  }
  return result;
}

export function enumerateRuleMinutes(rule) {
  const analysis = analyzeAutoRuleGeneration(rule);
  if (!analysis.ok) {
    return [];
  }

  return enumerateRuleMinutesFromAnalysis(analysis);
}

export function minutesToTime(totalMinutes) {
  const wrapped = (((totalMinutes % 1440) + 1440) % 1440);
  const hours = Math.floor(wrapped / 60).toString().padStart(2, "0");
  const minutes = (wrapped % 60).toString().padStart(2, "0");
  return `${hours}:${minutes}`;
}

export function hasMinimumDepartureGap(candidateMinute, existingMinutes) {
  return existingMinutes.every((minute) => Math.abs(minute - candidateMinute) >= MIN_DEPARTURE_INTERVAL_MINUTES);
}

export function hasMinimumDepartureGapForOrigin(candidateMinute, candidateOriginStationId, existingRows) {
  if (!candidateOriginStationId) {
    return true;
  }

  return existingRows.every((row) => {
    if (!row.originStationId || row.originStationId !== candidateOriginStationId) {
      return true;
    }

    return Math.abs(row.minute - candidateMinute) >= MIN_DEPARTURE_INTERVAL_MINUTES;
  });
}

export function pickEvenlyDistributedIndexes(totalCount, targetCount) {
  if (targetCount <= 0 || totalCount <= 0) {
    return [];
  }

  const indexes = [];
  for (let i = 0; i < targetCount; i += 1) {
    const index = Math.floor((i * totalCount) / targetCount);
    if (indexes[indexes.length - 1] !== index) {
      indexes.push(index);
    }
  }
  return indexes;
}

export function getLineKinds(rows, lineId) {
  return new Set(
    rows
      .filter((row) => row.lineId === lineId && (row.kind === "local" || row.kind === "express"))
      .map((row) => row.kind)
  );
}

export function getOccupiedDepartureMinutes(rows) {
  return rows
    .map((row) => timeToMinutes(row.time))
    .filter((value) => value !== null);
}

export function buildAutoStagedPlan({
  currentRows = [],
  rowsForLine = [],
  selectedEditLine,
  referenceLineIds = [],
  lineOptions = [],
  replaceExistingAutoRows = true
}) {
  const retainedRows = replaceExistingAutoRows
    ? currentRows.filter((row) => !(row.lineId === selectedEditLine && row.source === "auto"))
    : currentRows;
  const activeKinds = new Set(
    rowsForLine.filter((rule) => rule.enabled).map((rule) => (rule.kind === "express" ? "express" : "local"))
  );
  const existingKinds = getLineKinds(retainedRows, selectedEditLine);
  const hasKindConflict = [...activeKinds].some((kind) => existingKinds.size > 0 && !existingKinds.has(kind));
  if (hasKindConflict) {
    return {
      retainedRows,
      plannedRows: [],
      skippedCount: 0,
      previewsByRule: {},
      hasKindConflict: true
    };
  }

  const referenceLineSet = new Set(referenceLineIds.filter(Boolean));
  const currentLineReferenceRows = retainedRows
    .filter((row) => referenceLineSet.has(row.lineId) && row.kind === "local")
    .sort((left, right) => (left.time || "").localeCompare(right.time || ""));
  const lineOriginById = new Map(lineOptions.map((line) => [line.id, line.originStationId || ""]));
  const selectedOriginStationId = lineOriginById.get(selectedEditLine) || "";
  const occupiedRows = retainedRows
    .map((row) => ({
      minute: timeToMinutes(row.time),
      originStationId: lineOriginById.get(row.lineId) || ""
    }))
    .filter((row) => row.minute !== null);

  const plannedRows = [];
  const previewsByRule = {};
  let skippedCount = 0;

  rowsForLine
    .filter((rule) => rule.enabled)
    .forEach((rule) => {
      const generation = analyzeAutoRuleGeneration(rule);
      const preview = { times: [], entries: [], skippedCount: 0, skipReasons: [], reason: "" };
      const pushPreviewEntry = (minute, { skipped = false, reason = "" } = {}) => {
        const time = Number.isFinite(minute) ? minutesToTime(minute) : "--";
        preview.entries.push({ time, skipped, reason });
        if (skipped) {
          preview.skippedCount += 1;
          if (reason && !preview.skipReasons.includes(reason)) {
            preview.skipReasons.push(reason);
          }
          skippedCount += 1;
          return;
        }

        preview.times.push(time);
      };
      if (!generation.ok) {
        preview.reason = generation.reason;
        previewsByRule[rule.id] = preview;
        return;
      }
      const baseMinutes = enumerateRuleMinutesFromAnalysis(generation);

      if (rule.kind === "express") {
        const windowStart = timeToMinutes(rule.start);
        const windowEnd = timeToMinutes(rule.end);
        const localReferenceMinutes = currentLineReferenceRows
          .map((row) => timeToMinutes(row.time))
          .filter((value) => value !== null && value >= windowStart && value < windowEnd);
        const offsetMinutes = Number(rule.expressOffsetMinutes) || 0;
        const referenceMinutes = localReferenceMinutes.length > 0 ? localReferenceMinutes : baseMinutes;
        const evaluatedCandidates = referenceMinutes.map((referenceMinute) => {
          if (!Number.isFinite(referenceMinute)) {
            return null;
          }

          const candidateMinute =
            rule.expressOffsetMode === "before" ? referenceMinute - offsetMinutes : referenceMinute + offsetMinutes;
          return {
            referenceMinute,
            candidateMinute,
            inWindow: candidateMinute >= windowStart && candidateMinute < windowEnd
          };
        });
        const candidatePairs = evaluatedCandidates.filter((entry) => entry && entry.inWindow);

        if (candidatePairs.length === 0) {
          baseMinutes.forEach((baseMinute, generatedIndex) => {
            const referenceMinute = referenceMinutes[generatedIndex] ?? baseMinute;
            const candidateMinute =
              rule.expressOffsetMode === "before" ? referenceMinute - offsetMinutes : referenceMinute + offsetMinutes;
            pushPreviewEntry(candidateMinute, { skipped: true, reason: "offset" });
          });
          previewsByRule[rule.id] = preview;
          return;
        }

        const targetIndexes = pickEvenlyDistributedIndexes(candidatePairs.length, baseMinutes.length);
        for (let generatedIndex = 0; generatedIndex < baseMinutes.length; generatedIndex += 1) {
          const referenceIndex = targetIndexes[generatedIndex];
          const candidate = Number.isInteger(referenceIndex) ? candidatePairs[referenceIndex] : null;
          if (!candidate) {
            const referenceMinute = referenceMinutes[generatedIndex] ?? baseMinutes[generatedIndex];
            const candidateMinute =
              rule.expressOffsetMode === "before" ? referenceMinute - offsetMinutes : referenceMinute + offsetMinutes;
            pushPreviewEntry(candidateMinute, { skipped: true, reason: "offset" });
            continue;
          }

          if (!hasMinimumDepartureGapForOrigin(candidate.candidateMinute, selectedOriginStationId, occupiedRows)) {
            pushPreviewEntry(candidate.candidateMinute, { skipped: true, reason: "gap" });
            continue;
          }

          occupiedRows.push({
            minute: candidate.candidateMinute,
            originStationId: selectedOriginStationId
          });
          pushPreviewEntry(candidate.candidateMinute);
          plannedRows.push({
            ruleId: rule.id,
            lineId: rule.lineId,
            timeMinutes: candidate.candidateMinute,
            kind: "express",
            generatedIndex,
            noteType: rule.expressOffsetMode === "before" ? "before" : "after",
            offsetMinutes
          });
        }

        previewsByRule[rule.id] = preview;
        return;
      }

      baseMinutes.forEach((candidateMinute, generatedIndex) => {
        if (!hasMinimumDepartureGapForOrigin(candidateMinute, selectedOriginStationId, occupiedRows)) {
          pushPreviewEntry(candidateMinute, { skipped: true, reason: "gap" });
          return;
        }

        occupiedRows.push({
          minute: candidateMinute,
          originStationId: selectedOriginStationId
        });
        pushPreviewEntry(candidateMinute);
        plannedRows.push({
          ruleId: rule.id,
          lineId: rule.lineId,
          timeMinutes: candidateMinute,
          kind: "local",
          generatedIndex,
          noteType: "generated",
          start: rule.start,
          end: rule.end
        });
      });

      previewsByRule[rule.id] = preview;
    });

  return {
    retainedRows,
    plannedRows,
    skippedCount,
    previewsByRule,
    hasKindConflict: false
  };
}

