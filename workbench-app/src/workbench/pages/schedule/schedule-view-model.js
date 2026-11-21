import { timeToMinutes } from "../../../lib/time";
import { passengerScale } from "../passenger/passenger-scale";

export const LINE_ROW_HEIGHT = 40;
export const LINE_GROUP_HEAD = 32;
export const LINE_GROUP_GAP = 16;

export function rowsInSegment(rows, segment) {
  return rows.filter((row) => {
    const minute = timeToMinutes(row.time);
    return minute !== null && minute >= segment.start && minute < segment.end;
  });
}

export function buildLineGroups(rows, segments, rowHeight, head, gap) {
  const groups = segments.map((segment) => ({ ...segment, rows: [] }));
  const other = { id: "other", labelKey: "nativeSchedule.line.other", rows: [] };
  rows.forEach((row) => {
    const minute = timeToMinutes(row.time);
    const group = minute === null ? null : groups.find((item) => minute >= item.start && minute < item.end);
    (group || other).rows.push(row);
  });
  if (other.rows.length) groups.push(other);
  let offset = 0;
  return groups.map((group) => {
    const lines = Math.ceil(group.rows.length / 2);
    const result = { ...group, lines, offset, height: head + lines * rowHeight };
    offset += result.height + gap;
    return result;
  });
}

export function buildLineItems(groups, rowHeight, head) {
  return groups.flatMap((group) => Array.from({ length: group.lines }, (_, index) => ({
    key: `${group.id}:${index}`,
    group,
    index,
    top: group.offset + head + index * rowHeight,
    left: group.rows[index],
    right: group.rows[index + group.lines]
  })));
}

export function lineWindow(top, bottom, listOffset, rowHeight) {
  return {
    start: (Math.floor((top - listOffset) / rowHeight) - 6) * rowHeight,
    end: (Math.ceil((bottom - listOffset) / rowHeight) + 6) * rowHeight
  };
}

export function departureTicks(entries, start, end, width, tickWidth = 1) {
  const bins = new Map();
  entries.forEach((entry) => {
    const minute = timeToMinutes(entry.time);
    if (entry.skipped || minute === null || minute < start || minute >= end) return;
    const position = (minute - start) / (end - start);
    const pixel = Math.min(Math.max(0, Math.floor(width) - tickWidth), Math.floor(position * Math.max(1, width)));
    if (!bins.has(pixel)) bins.set(pixel, { minute, pixel });
  });
  return [...bins.values()];
}

export function departureClockMarks(start, end, width) {
  const step = [5, 10, 15, 30, 60, 120].find((value) => value / (end - start) * width >= 60) || 180;
  const marks = [];
  for (let minute = Math.ceil(start / step) * step; minute <= end; minute += step) marks.push(minute);
  return marks;
}

export function buildLineRows(rows, snapshot, lineId, minimum) {
  const draftRows = snapshot?.lineDraftRowsByLineId?.find((block) => block.lineId === lineId)?.lineDraftRows || [];
  const draftById = new Map(draftRows.map((row) => [row.id, row]));
  const appliedById = new Map((snapshot?.appliedRows || []).filter((row) => row.lineId === lineId).map((row) => [row.id, row]));
  return rows.filter((row) => row.serviceId === lineId).map((row) => {
    const draft = draftById.get(row.id);
    const applied = appliedById.get(row.id);
    const timedStops = draft?.time === row.time && Array.isArray(draft.timedStops)
      ? draft.timedStops
      : applied?.time === row.time ? applied.timedStops : [];
    return {
      ...row,
      hasTimedStops: (timedStops || []).length > 0,
      timedStopsTooShort: (timedStops || []).some((stop) => Number.isFinite(stop?.arrive) && Number.isFinite(stop?.depart) && stop.depart - stop.arrive < minimum)
    };
  });
}

export function buildLineIssues(rows, snapshot, lines, minimum) {
  return new Map(lines.map((line) => {
    const lineRows = buildLineRows(rows, snapshot, line.id, minimum);
    return [line.id, {
      hold: Number(line.hold) < minimum,
      dwell: Number(line.dwell) < minimum,
      conflict: lineRows.some((row) => row.isConflict),
      timed: lineRows.some((row) => row.timedStopsTooShort)
    }];
  }));
}

export function buildDepartureSegments(planSegments, inputs, rows, fixedRows) {
  return planSegments.map((segment, index) => {
    const entries = inputs[index].edited
      ? [...segment.entries, ...rowsInSegment(fixedRows, segment).map((row) => ({ time: row.time, skipped: false }))]
      : rowsInSegment(rows, segment).map((row) => ({ time: row.time, skipped: false }));
    return { ...segment, entries: entries.sort((left, right) => left.time.localeCompare(right.time)) };
  });
}

export function buildDepartureEntries(segments, rows) {
  const outside = rows.filter((row) => {
    const minute = timeToMinutes(row.time);
    return minute !== null && !segments.some((segment) => minute >= segment.start && minute < segment.end);
  });
  return [...segments.flatMap((segment) => segment.entries), ...outside.map((row) => ({ time: row.time, skipped: false }))];
}

export function departureHeadings(segments, width) {
  return segments.map((segment, index) => ({
    index,
    left: segment.start / 1440 * width,
    width: (segment.end - segment.start) / 1440 * width
  }));
}

export function departureHeadingFit(available, widths) {
  const countWidth = 12 + widths.count;
  const showRange = widths.range <= available;
  if (widths.name + countWidth + 8 + widths.unit <= available) return { unit: true, nameWidth: null, range: showRange };
  if (widths.name + countWidth <= available) return { unit: false, nameWidth: null, range: showRange };
  return { unit: false, nameWidth: Math.max(0, available - countWidth), range: showRange };
}

export function departureLoads(today, yesterday, lineId) {
  const table = (snapshot) => new Map((snapshot?.lineTimeLoads || [])
    .filter((item) => item.lineId === lineId && item.dayIndex === snapshot.selectedDayIndex)
    .map((item) => [item.bucketStartMinute, item.averageOnboardPassengers]));
  const todayLoads = table(today);
  const yesterdayLoads = table(yesterday);
  if (!todayLoads.size && !yesterdayLoads.size) return null;
  const values = [];
  let split = 0;
  for (let minute = 0; minute < 1440; minute += today.bucketMinutes) {
    if (minute <= today.currentMinute) split += 1;
    const value = (minute <= today.currentMinute ? todayLoads : yesterdayLoads).get(minute);
    values.push(Number.isFinite(value) ? value : 0);
  }
  return { values, split };
}

export function departureLoadPaths(values, width, height, split = values.length) {
  const top = passengerScale(values).top;
  const gap = width / values.length;
  const round = (number) => Math.round(number * 100) / 100;
  const point = (x, y) => `${round(x)} ${round(y)}`;
  const xs = values.map((_, index) => (index + 0.5) * gap);
  const ys = values.map((value) => height - value / top * height);
  const slopes = ys.slice(1).map((y, index) => (y - ys[index]) / gap);
  const tangents = ys.map((_, index) => {
    if (index === 0) return slopes[0];
    if (index === ys.length - 1) return slopes[index - 1];
    const before = slopes[index - 1];
    const after = slopes[index];
    return before * after <= 0 ? 0 : 2 * before * after / (before + after);
  });
  const curves = ys.slice(1).map((y, index) => `C${point(xs[index] + gap / 3, ys[index] + tangents[index] * gap / 3)} ${point(xs[index + 1] - gap / 3, y - tangents[index + 1] * gap / 3)} ${point(xs[index + 1], y)}`);
  const start = [`M${point(0, ys[0])}`, `L${point(xs[0], ys[0])}`];
  const end = `L${point(width, ys[ys.length - 1])}`;
  const line = [...start, ...curves, end].join(" ");
  const cut = Math.max(0, Math.min(split, values.length));
  const solid = cut ? [...start, ...curves.slice(0, cut - 1), ...(cut === values.length ? [end] : [])].join(" ") : "";
  const dashed = cut < values.length ? [...(cut ? [`M${point(xs[cut - 1], ys[cut - 1])}`] : start), ...curves.slice(Math.max(0, cut - 1)), end].join(" ") : "";
  return { solid, dashed, area: `${line} L${point(width, height)} L${point(0, height)} Z` };
}
