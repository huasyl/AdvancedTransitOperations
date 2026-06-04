import { MIN_DEPARTURE_INTERVAL_MINUTES } from "../../../lib/auto-schedule";
import { timeToMinutes } from "../../../lib/time";
import {
  findLineOptionById,
  getLocalizedLineName,
  getLocalizedOriginLabel,
  normalizeKind
} from "./schedule-catalog";
import { formatConflictReason } from "./schedule-normalize";

export function getSummaryRowKey(row) {
  return `${row?.lineId || row?.serviceId || ""}|${normalizeKind(row?.kind)}|${row?.time || ""}`;
}

export function getSummaryRowsSignature(rows) {
  return (Array.isArray(rows) ? rows : [])
    .map((row) => getSummaryRowKey(row))
    .sort()
    .join("||");
}

export function getCircularMinuteGap(leftMinute, rightMinute) {
  const dayMinutes = 24 * 60;
  const left = ((Math.round(leftMinute) % dayMinutes) + dayMinutes) % dayMinutes;
  const right = ((Math.round(rightMinute) % dayMinutes) + dayMinutes) % dayMinutes;
  const directGap = Math.abs(right - left);
  return Math.min(directGap, dayMinutes - directGap);
}
export function buildSummaryRowsWithConflicts(rows, t, appliedRowKeySet = null) {
  const duplicateCounts = new Map();
  const lineKinds = new Map();
  const rowsWithMinutes = (Array.isArray(rows) ? rows : [])
    .map((row) => ({ row, minute: timeToMinutes(row.time) }))
    .filter((entry) => entry.minute !== null);

  rowsWithMinutes.forEach(({ row }) => {
    const duplicateKey = `${row.lineId}|${row.kind}|${row.time}`;
    duplicateCounts.set(duplicateKey, (duplicateCounts.get(duplicateKey) || 0) + 1);
    const kinds = lineKinds.get(row.lineId) ?? new Set();
    kinds.add(row.kind);
    lineKinds.set(row.lineId, kinds);
  });

  const tooCloseIds = new Set();
  const rowsByOrigin = new Map();
  rowsWithMinutes.forEach((entry) => {
    const originStationId = entry.row.originStationId || "";
    if (!originStationId) {
      return;
    }

    if (!rowsByOrigin.has(originStationId)) {
      rowsByOrigin.set(originStationId, []);
    }
    rowsByOrigin.get(originStationId).push(entry);
  });

  rowsByOrigin.forEach((originRows) => {
    originRows.sort((left, right) => left.minute - right.minute);
    for (let index = 1; index < originRows.length; index += 1) {
      const current = originRows[index];
      const previous = originRows[index - 1];
      if (current.minute - previous.minute < MIN_DEPARTURE_INTERVAL_MINUTES) {
        tooCloseIds.add(current.row?.id);
        tooCloseIds.add(previous.row?.id);
      }
    }

    if (originRows.length > 1 && originRows[0].minute !== originRows[originRows.length - 1].minute) {
      const first = originRows[0];
      const last = originRows[originRows.length - 1];
      if (getCircularMinuteGap(first.minute, last.minute) < MIN_DEPARTURE_INTERVAL_MINUTES) {
        tooCloseIds.add(first.row?.id);
        tooCloseIds.add(last.row?.id);
      }
    }
  });

  rowsWithMinutes.sort((left, right) => {
    if (left.minute !== right.minute) {
      return left.minute - right.minute;
    }

    return String(left.row?.id || "").localeCompare(String(right.row?.id || ""));
  });

  return rowsWithMinutes
    .map(({ row }) => {
      const duplicateKey = `${row.lineId}|${row.kind}|${row.time}`;
      const isDuplicate = (duplicateCounts.get(duplicateKey) || 0) > 1;
      const hasKindConflict = (lineKinds.get(row.lineId)?.size || 0) > 1;
      const isTooClose = tooCloseIds.has(row.id);
      const isConflict = isDuplicate || hasKindConflict || isTooClose;
      const conflictReasons = [];

      if (isDuplicate) {
        conflictReasons.push(formatConflictReason("duplicate", t, "compact"));
      }

      if (hasKindConflict) {
        conflictReasons.push(formatConflictReason("kind", t, "compact"));
      }

      if (isTooClose) {
        conflictReasons.push(formatConflictReason("gap", t, "compact", { minutes: MIN_DEPARTURE_INTERVAL_MINUTES }));
      }

      const lineOption = findLineOptionById(row.lineId || row.serviceId || "");

      return {
        ...row,
        sourceLabel:
          row.source === "auto"
            ? t("schedule.source.auto")
            : row.source === "planner"
              ? t("schedule.source.planner")
              : t("schedule.source.manual"),
        note: row.note || t("combined.note.direct"),
        lineName: row.lineName || (lineOption ? getLocalizedLineName(lineOption, t) : (row.lineId || "(missing lineId)")),
        origin: row.origin || (lineOption ? getLocalizedOriginLabel(row.originId || lineOption.originId, t) : ""),
        isApplied: appliedRowKeySet instanceof Set && appliedRowKeySet.has(getSummaryRowKey(row)),
        isConflict,
        conflictReasonLabel: conflictReasons.join("/"),
        isExpress: row.kind === "express"
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
