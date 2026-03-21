import { timeToMinutes } from "./time";

export const MIN_DEPARTURE_INTERVAL_MINUTES = 5;

export function enumerateRuleMinutes(rule) {
  const start = Number.parseInt(rule.start?.slice(0, 2), 10) * 60 + Number.parseInt(rule.start?.slice(3, 5), 10);
  const end = Number.parseInt(rule.end?.slice(0, 2), 10) * 60 + Number.parseInt(rule.end?.slice(3, 5), 10);
  const departuresPerHour = Number(rule.departuresPerHour) || 0;
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start || departuresPerHour <= 0) {
    return [];
  }

  const interval = 60 / departuresPerHour;
  const result = [];
  for (let minute = start; minute < end; minute += interval) {
    result.push(Math.round(minute));
  }
  return result;
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
  lineOptions = []
}) {
  const retainedRows = currentRows.filter((row) => !(row.lineId === selectedEditLine && row.source === "auto"));
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
      const baseMinutes = enumerateRuleMinutes(rule);
      const preview = { times: [], skippedCount: 0, reason: "" };
      if (baseMinutes.length === 0) {
        preview.reason = "invalid";
        previewsByRule[rule.id] = preview;
        return;
      }

      if (rule.kind === "express") {
        const windowStart = timeToMinutes(rule.start);
        const windowEnd = timeToMinutes(rule.end);
        const localReferenceMinutes = currentLineReferenceRows
          .map((row) => timeToMinutes(row.time))
          .filter((value) => value !== null && value >= windowStart && value < windowEnd);
                const offsetMinutes = Number(rule.expressOffsetMinutes) || 0;
        const referenceMinutes = localReferenceMinutes.length > 0 ? localReferenceMinutes : baseMinutes;
        const candidatePairs = referenceMinutes
          .map((referenceMinute) => {
            if (!Number.isFinite(referenceMinute)) {
              return null;
            }

            const candidateMinute =
              rule.expressOffsetMode === "before" ? referenceMinute - offsetMinutes : referenceMinute + offsetMinutes;
            if (candidateMinute < windowStart || candidateMinute >= windowEnd) {
              return null;
            }

            return {
              referenceMinute,
              candidateMinute
            };
          })
          .filter((entry) => entry !== null);

        if (candidatePairs.length === 0) {
          preview.skippedCount += baseMinutes.length;
          skippedCount += baseMinutes.length;
          previewsByRule[rule.id] = preview;
          return;
        }

        const targetIndexes = pickEvenlyDistributedIndexes(candidatePairs.length, baseMinutes.length);
        if (targetIndexes.length < baseMinutes.length) {
          const missingCount = baseMinutes.length - targetIndexes.length;
          preview.skippedCount += missingCount;
          skippedCount += missingCount;
        }

        targetIndexes.forEach((referenceIndex, generatedIndex) => {
          const candidate = candidatePairs[referenceIndex];
          if (!candidate) {
            preview.skippedCount += 1;
            skippedCount += 1;
            return;
          }

          if (!hasMinimumDepartureGapForOrigin(candidate.candidateMinute, selectedOriginStationId, occupiedRows)) {
            preview.skippedCount += 1;
            skippedCount += 1;
            return;
          }

          occupiedRows.push({
            minute: candidate.candidateMinute,
            originStationId: selectedOriginStationId
          });
          preview.times.push(minutesToTime(candidate.candidateMinute));
          plannedRows.push({
            ruleId: rule.id,
            lineId: rule.lineId,
            timeMinutes: candidate.candidateMinute,
            kind: "express",
            generatedIndex,
            noteType: rule.expressOffsetMode === "before" ? "before" : "after",
            offsetMinutes
          });
        });

        previewsByRule[rule.id] = preview;
        return;
      }

      baseMinutes.forEach((candidateMinute, generatedIndex) => {
        if (!hasMinimumDepartureGapForOrigin(candidateMinute, selectedOriginStationId, occupiedRows)) {
          preview.skippedCount += 1;
          skippedCount += 1;
          return;
        }

        occupiedRows.push({
          minute: candidateMinute,
          originStationId: selectedOriginStationId
        });
        preview.times.push(minutesToTime(candidateMinute));
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

