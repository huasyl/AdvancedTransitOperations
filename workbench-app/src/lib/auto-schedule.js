import { timeToMinutes } from "./time";

export const MIN_DEPARTURE_INTERVAL_MINUTES = 5;
export const MAX_AUTO_RULE_TRIPS_PER_HOUR = 60 / MIN_DEPARTURE_INTERVAL_MINUTES;
export const MAX_AUTO_RULE_GENERATED_TRIPS = (24 * 60) / MIN_DEPARTURE_INTERVAL_MINUTES;

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
    return false;
  }

  return existingRows.every((row) => {
    if (!row.originStationId || row.originStationId !== candidateOriginStationId) {
      return true;
    }

    const directGap = Math.abs(row.minute - candidateMinute);
    const circularGap = Math.min(directGap, (24 * 60) - directGap);
    return circularGap >= MIN_DEPARTURE_INTERVAL_MINUTES;
  });
}

export function collectOriginDepartureConflictRowIds(
  rows,
  lineOriginById,
  minGapMinutes = MIN_DEPARTURE_INTERVAL_MINUTES
) {
  const stagedWithMinutes = (Array.isArray(rows) ? rows : [])
    .map((row) => ({
      row,
      minute: timeToMinutes(row?.time),
      originStationId: lineOriginById.get(row?.lineId) || ""
    }))
    .filter((entry) => entry.minute !== null)
    .sort((left, right) => left.minute - right.minute);

  const conflictIds = new Set();
  for (let index = 1; index < stagedWithMinutes.length; index += 1) {
    const current = stagedWithMinutes[index];
    const previous = stagedWithMinutes[index - 1];
    if (!current.originStationId || current.originStationId !== previous.originStationId) {
      continue;
    }

    if (current.minute - previous.minute < minGapMinutes) {
      conflictIds.add(current.row.id);
      conflictIds.add(previous.row.id);
    }
  }

  return conflictIds;
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

function getSegmentCapacity(segment, minGapMinutes) {
  return Math.floor((segment.end - segment.start) / minGapMinutes) + 1;
}

function buildOriginAvailableDepartureSegments({
  windowStart,
  windowEnd,
  originStationId,
  occupiedRows,
  minGapMinutes = MIN_DEPARTURE_INTERVAL_MINUTES
}) {
  const startMinute = Math.ceil(windowStart);
  const endMinute = Math.ceil(windowEnd) - 1;
  if (!originStationId || endMinute < startMinute) {
    return [];
  }

  const occupiedMinutes = (Array.isArray(occupiedRows) ? occupiedRows : [])
    .filter((row) => row.originStationId === originStationId && Number.isFinite(row.minute))
    .map((row) => Math.round(row.minute))
    .sort((left, right) => left - right);

  const segments = [];
  let cursor = startMinute;
  occupiedMinutes.forEach((occupiedMinute) => {
    if (cursor > endMinute) {
      return;
    }

    const segmentEnd = Math.min(endMinute, occupiedMinute - minGapMinutes);
    if (segmentEnd >= cursor) {
      segments.push({ start: cursor, end: segmentEnd });
    }

    cursor = Math.max(cursor, occupiedMinute + minGapMinutes);
  });

  if (cursor <= endMinute) {
    segments.push({ start: cursor, end: endMinute });
  }

  return segments
    .map((segment) => ({
      ...segment,
      capacity: getSegmentCapacity(segment, minGapMinutes),
      length: segment.end - segment.start + 1
    }))
    .filter((segment) => segment.capacity > 0);
}

function allocateSegmentDepartureCounts(segments, targetCount) {
  const totalCapacity = segments.reduce((sum, segment) => sum + segment.capacity, 0);
  const plannedCount = Math.min(targetCount, totalCapacity);
  if (plannedCount <= 0) {
    return [];
  }

  const totalLength = segments.reduce((sum, segment) => sum + segment.length, 0);
  const allocations = segments.map((segment, index) => {
    const rawCount = totalLength > 0 ? (plannedCount * segment.length) / totalLength : 0;
    const count = Math.min(segment.capacity, Math.floor(rawCount));
    return {
      index,
      count,
      remainder: rawCount - count,
      length: segment.length
    };
  });

  let remaining = plannedCount - allocations.reduce((sum, allocation) => sum + allocation.count, 0);
  allocations
    .slice()
    .sort((left, right) => {
      if (right.remainder !== left.remainder) {
        return right.remainder - left.remainder;
      }

      return right.length - left.length;
    })
    .forEach((allocation) => {
      if (remaining <= 0) {
        return;
      }

      const segment = segments[allocation.index];
      if (allocation.count >= segment.capacity) {
        return;
      }

      allocation.count += 1;
      remaining -= 1;
    });

  return allocations
    .sort((left, right) => left.index - right.index)
    .map((allocation) => allocation.count);
}

function hasMinimumGapBetweenMinutes(minutes, minGapMinutes) {
  for (let index = 1; index < minutes.length; index += 1) {
    if (minutes[index] - minutes[index - 1] < minGapMinutes) {
      return false;
    }
  }

  return true;
}

function canUseBaseDepartureMinutes(baseMinutes, originStationId, occupiedRows) {
  const validationRows = [...occupiedRows];
  return baseMinutes.every((minute) => {
    if (!hasMinimumDepartureGapForOrigin(minute, originStationId, validationRows)) {
      return false;
    }

    validationRows.push({ minute, originStationId });
    return true;
  });
}

function distributeMinutesInSegment(segment, count, minGapMinutes) {
  if (count <= 0) {
    return [];
  }

  if (count === 1) {
    return [Math.round((segment.start + segment.end) / 2)];
  }

  const length = segment.end - segment.start + 1;
  const minutes = [];
  for (let index = 0; index < count; index += 1) {
    const rawMinute = segment.start + ((index + 0.5) * length) / count - 0.5;
    const minute = Math.max(segment.start, Math.min(segment.end, Math.round(rawMinute)));
    minutes.push(minute);
  }

  for (let index = 1; index < minutes.length; index += 1) {
    if (minutes[index] - minutes[index - 1] < minGapMinutes) {
      minutes[index] = minutes[index - 1] + minGapMinutes;
    }
  }

  if (
    minutes[0] < segment.start ||
    minutes[minutes.length - 1] > segment.end ||
    !hasMinimumGapBetweenMinutes(minutes, minGapMinutes)
  ) {
    const packedSpan = (count - 1) * minGapMinutes;
    const packedStart = Math.max(
      segment.start,
      Math.min(segment.end - packedSpan, Math.round(segment.start + (segment.end - segment.start - packedSpan) / 2))
    );
    return Array.from({ length: count }, (_, index) => packedStart + index * minGapMinutes);
  }

  return minutes;
}

function getAnchorSlot(baseMinutes, index, windowStart, windowEnd) {
  const anchor = baseMinutes[index];
  const previousAnchor = baseMinutes[index - 1];
  const nextAnchor = baseMinutes[index + 1];
  const start = Number.isFinite(previousAnchor)
    ? Math.max(Math.ceil(windowStart), Math.ceil((previousAnchor + anchor) / 2))
    : Math.ceil(windowStart);
  const end = Number.isFinite(nextAnchor)
    ? Math.min(Math.ceil(windowEnd) - 1, Math.ceil((anchor + nextAnchor) / 2) - 1)
    : Math.ceil(windowEnd) - 1;

  return { start, end };
}

function pickAnchoredAvailableMinute(anchor, slot, originStationId, occupiedRows) {
  if (!originStationId || slot.end < slot.start) {
    return null;
  }

  const segments = buildOriginAvailableDepartureSegments({
    windowStart: slot.start,
    windowEnd: slot.end + 1,
    originStationId,
    occupiedRows
  });
  if (segments.length === 0) {
    return null;
  }

  const ordered = segments
    .map((segment) => ({
      segment,
      distance: anchor < segment.start
        ? segment.start - anchor
        : anchor > segment.end
          ? anchor - segment.end
          : 0,
      isAfterAnchor: segment.start >= anchor
    }))
    .sort((left, right) => {
      if (left.distance !== right.distance) {
        return left.distance - right.distance;
      }

      if (left.isAfterAnchor !== right.isAfterAnchor) {
        return left.isAfterAnchor ? -1 : 1;
      }

      return right.segment.length - left.segment.length;
    });
  const selected = ordered[0]?.segment;
  if (!selected) {
    return null;
  }

  if (anchor >= selected.start && anchor <= selected.end) {
    return anchor;
  }

  return distributeMinutesInSegment(selected, 1, MIN_DEPARTURE_INTERVAL_MINUTES)[0] ?? null;
}

function buildAutoDepartureSlots(baseMinutes, resolveMinute) {
  return (Array.isArray(baseMinutes) ? baseMinutes : []).map((anchorMinute, generatedIndex) => ({
    anchorMinute,
    generatedIndex,
    minute: typeof resolveMinute === "function" ? resolveMinute(anchorMinute, generatedIndex) : null
  }));
}

function distributeAutoDepartureMinutes({
  baseMinutes,
  windowStart,
  windowEnd,
  originStationId,
  occupiedRows
}) {
  if (!originStationId || !Array.isArray(baseMinutes) || baseMinutes.length === 0) {
    return buildAutoDepartureSlots(baseMinutes);
  }

  if (canUseBaseDepartureMinutes(baseMinutes, originStationId, occupiedRows)) {
    return buildAutoDepartureSlots(baseMinutes, (anchorMinute) => anchorMinute);
  }

  const validationRows = [...occupiedRows];
  return buildAutoDepartureSlots(baseMinutes, (anchor, index) => {
    const slot = getAnchorSlot(baseMinutes, index, windowStart, windowEnd);
    const minute = pickAnchoredAvailableMinute(anchor, slot, originStationId, validationRows);
    if (minute === null) {
      return null;
    }

    if (!hasMinimumDepartureGapForOrigin(minute, originStationId, validationRows)) {
      return null;
    }

    validationRows.push({ minute, originStationId });
    return minute;
  });
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

      const distributedSlots = distributeAutoDepartureMinutes({
        baseMinutes,
        windowStart: generation.start,
        windowEnd: generation.end,
        originStationId: selectedOriginStationId,
        occupiedRows
      });

      distributedSlots.forEach((slot) => {
        const generatedIndex = slot?.generatedIndex ?? 0;
        const candidateMinute = slot?.minute;
        if (!Number.isFinite(candidateMinute)) {
          pushPreviewEntry(slot?.anchorMinute, { skipped: true, reason: "gap" });
          return;
        }

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

