import { timeToMinutes } from "../../../lib/time";
import {
  findLineOptionById,
  getLocalizedLineName,
  getLocalizedOriginLabel,
  normalizeKind
} from "./schedule-catalog";

export function normalizeTimeInput(rawValue) {
  const rawText = String(rawValue || "");
  const digitsOnly = rawText.replace(/\D/g, "").slice(0, 4);
  if (digitsOnly.length < 2) {
    return digitsOnly;
  }
  if (digitsOnly.length === 2) {
    return rawText.indexOf(":") >= 0 ? digitsOnly + ":" : digitsOnly;
  }

  return digitsOnly.slice(0, 2) + ":" + digitsOnly.slice(2);
}

export function normalizeFrequencyInput(rawValue) {
  const source = String(rawValue || "");
  let result = "";
  let dotSeen = false;

  for (let index = 0; index < source.length; index += 1) {
    const char = source[index];
    if (char >= "0" && char <= "9") {
      result += char;
      continue;
    }

    if (char === "." && !dotSeen) {
      result += char;
      dotSeen = true;
    }
  }

  return result;
}

export function isValidTimeValue(value) {
  const match = /^(\d{2}):(\d{2})$/.exec(String(value || "").trim());
  if (!match) {
    return false;
  }

  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  return Number.isFinite(hours) && Number.isFinite(minutes) && hours >= 0 && hours <= 23 && minutes >= 0 && minutes <= 59;
}

export function parseFrequencyValue(value) {
  const numeric = Number(String(value || "").trim());
  if (!Number.isFinite(numeric) || numeric <= 0) {
    return 0;
  }

  return numeric;
}
export function buildCombinedNote(noteType, t, values = {}) {
  if (noteType === "before") {
    return t("combined.note.beforePaired", values);
  }

  if (noteType === "after") {
    return t("combined.note.afterPaired", values);
  }

  if (noteType === "generated") {
    return t("combined.note.generated", values);
  }

  return t("combined.note.direct");
}

export function createSummaryEntry({
  id,
  time,
  lineId,
  serviceId,
  kind,
  source = "manual",
  note = ""
}, t) {
  const resolvedLineId = lineId || serviceId || "";
  const lineOption = resolvedLineId ? findLineOptionById(resolvedLineId) : null;
  const resolvedKind = normalizeKind(kind || lineOption?.kind);

  return {
    id,
    lineId: resolvedLineId,
    serviceId: resolvedLineId,
    lineNameKey: lineOption?.nameKey || "",
    lineName: lineOption ? getLocalizedLineName(lineOption, t) : (resolvedLineId || "(missing lineId)"),
    lineColor: lineOption?.color || "#9ca3af",
    time,
    kind: resolvedKind,
    source,
    note: note || t("combined.note.direct"),
    originId: lineOption?.originId || "",
    originStationId: lineOption?.originStationId || "",
    origin: lineOption ? getLocalizedOriginLabel(lineOption.originId, t) : ""
  };
}
export function normalizeSummaryEntries(rows, t) {
  return (Array.isArray(rows) ? rows : [])
    .map((row, index) => createSummaryEntry({
      id: row?.id || `summary-${index + 1}`,
      time: row?.time || "",
      lineId: row?.lineId || row?.serviceId || "",
      serviceId: row?.serviceId || row?.lineId || "",
      kind: row?.kind || normalizeKind(row?.type),
      source: row?.source || "manual",
      note: row?.note || t("combined.note.direct")
    }, t));
}
export function resolveOffsetMinutes(direction, minutesText) {
  const minutes = Number(minutesText);
  if (!Number.isFinite(minutes) || minutes <= 0) {
    return 0;
  }

  if (direction === "early") {
    return -minutes;
  }

  if (direction === "late") {
    return minutes;
  }

  return 0;
}

export function formatOffsetLabel(direction, minutesText, t, variant = "regular") {
  const minutes = Math.abs(resolveOffsetMinutes(direction, minutesText));
  if (minutes === 0) {
    return t(variant === "compact" ? "nativeSchedule.offset.none.compact" : "nativeSchedule.offset.none");
  }

  const directionLabel =
    variant === "compact"
      ? t(direction === "early" ? "nativeSchedule.offset.direction.early.compact" : "nativeSchedule.offset.direction.late.compact")
      : t(direction === "early" ? "nativeSchedule.toggle.early" : "nativeSchedule.toggle.late");

  return t(variant === "compact" ? "nativeSchedule.offset.label.compact" : "nativeSchedule.offset.label", {
    direction: directionLabel,
    minutes
  });
}

export function formatConflictReason(kind, t, variant = "regular", values = {}) {
  const suffix = variant === "compact" ? ".compact" : "";
  return t(`nativeSchedule.conflict.${kind}${suffix}`, values);
}

export function buildPreviewMetaText(preview, hasKindConflict = false, t, { detailedSkipReason = false } = {}) {
  if (hasKindConflict) {
    return t("nativeSchedule.preview.meta.kindConflict");
  }

  if (!preview) {
    return "";
  }

  if (preview.reason === "invalid") {
    return t("nativeSchedule.preview.meta.invalidWindow");
  }

  if (preview.reason === "frequencyLimit") {
    return t("nativeSchedule.preview.meta.frequencyLimit");
  }

  if (preview.reason === "tripLimit") {
    return t("nativeSchedule.preview.meta.tripLimit");
  }

  if (preview.skippedCount > 0) {
    if (!detailedSkipReason) {
      return t("nativeSchedule.preview.meta.skipped", {
        count: preview.skippedCount
      });
    }

    const reasons = Array.isArray(preview.skipReasons) ? preview.skipReasons : [];
    const reasonText = reasons
      .map((reason) => t(`nativeSchedule.preview.reason.${reason}`))
      .filter(Boolean)
      .join(" / ");
    return t("nativeSchedule.preview.meta.skippedDetailed", {
      count: preview.skippedCount,
      reason: reasonText || t("nativeSchedule.preview.reason.gap")
    });
  }

  return "";
}

export function sortManualDraftRows(rows) {
  return [...rows].sort((left, right) => {
    const leftMinutes = timeToMinutes(left.time) ?? 9999;
    const rightMinutes = timeToMinutes(right.time) ?? 9999;
    if (leftMinutes !== rightMinutes) {
      return leftMinutes - rightMinutes;
    }

    return String(left?.id ?? "").localeCompare(String(right?.id ?? ""));
  });
}

export function sortAutoRuleRows(rows) {
  return [...rows].sort((left, right) => {
    const leftStart = timeToMinutes(left.start) ?? 9999;
    const rightStart = timeToMinutes(right.start) ?? 9999;
    if (leftStart !== rightStart) {
      return leftStart - rightStart;
    }

    const leftEnd = timeToMinutes(left.end) ?? 9999;
    const rightEnd = timeToMinutes(right.end) ?? 9999;
    if (leftEnd !== rightEnd) {
      return leftEnd - rightEnd;
    }

    return String(left?.id ?? "").localeCompare(String(right?.id ?? ""));
  });
}
