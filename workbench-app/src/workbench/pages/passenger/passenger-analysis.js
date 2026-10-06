import { DEFAULT_SERVICE_PERIODS } from "../../../lib/service-periods";

const array = (value) => Array.isArray(value) ? value : [];
const number = (value) => Number.isFinite(Number(value)) ? Number(value) : 0;

export function formatPassengerNumber(value, digits = 0) {
  if (value == null) return "—";
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) return "—";
  const places = Math.max(0, Math.min(2, digits));
  const scale = 10 ** places;
  const fixed = (Math.round(numeric * scale) / scale).toFixed(places);
  const trimmed = places ? fixed.replace(/\.0+$/, "") : fixed;
  const [whole, fraction] = trimmed.split(".");
  return whole.replace(/\B(?=(\d{3})+(?!\d))/g, ",") + (fraction ? `.${fraction}` : "");
}

export function bucketTime(bucket) {
  if (!bucket) return NaN;
  return bucket.dayIndex * 1440 + bucket.bucketStartMinute;
}

export function timeBucket(time) {
  return { dayIndex: Math.floor(time / 1440), bucketStartMinute: time % 1440 };
}

export function timeLabel(time) {
  if (!Number.isFinite(time)) return "—";
  const minute = ((Math.round(time) % 1440) + 1440) % 1440;
  return `${String(Math.floor(minute / 60)).padStart(2, "0")}:${String(minute % 60).padStart(2, "0")}`;
}

export function rangeLabel(from, to) {
  if (!Number.isFinite(from) || !Number.isFinite(to)) return "暂无时段";
  return `${timeLabel(from)} — ${to > from && to % 1440 === 0 ? "24:00" : timeLabel(to)}`;
}

export function buildBuckets(snapshot) {
  const from = snapshot?.selectedDayIndex * 1440;
  const to = from + 1440;
  const step = number(snapshot?.bucketMinutes);
  if (!Number.isFinite(from) || !Number.isFinite(to) || step <= 0) return [];
  const result = [];
  for (let time = from; time < to && result.length < 96; time += step) result.push(time);
  return result;
}

export function presetsForBuckets(buckets, periods = DEFAULT_SERVICE_PERIODS) {
  if (!buckets.length) return [];
  const start = buckets[0], end = buckets[buckets.length - 1] + 15;
  const result = [];
  for (let day = Math.floor(start / 1440) * 1440; day < end; day += 1440) {
    periods.forEach((period, index) => {
      const from = Math.max(start, day + period.start);
      const to = Math.min(end, day + period.end);
      if (from < to) result.push({ id: `${period.id}:${day}`, labelKey: period.labelKey,
        periodIndex: index, day, from, to });
    });
  }
  return result;
}

export function lineTrend(snapshot, lineId, buckets) {
  const source = lineId === "ALL" ? array(snapshot?.networkTimeLoads).filter((row) => row.mode === snapshot?.mode)
    : array(snapshot?.lineTimeLoads).filter((row) => row.lineId === lineId);
  const byTime = new Map(source
    .map((row) => [bucketTime(row), row.averageOnboardPassengers]));
  return buckets.map((time) => ({ time, value: byTime.has(time) ? byTime.get(time) : null }));
}

export function stationGroupId(row) { return row?.stationGroupId || row?.stationId || ""; }

export function mergeStationGroups(previous, incoming) {
  const changed = new Set(array(incoming).flatMap((group) => array(group.memberStationIds)));
  return [...array(previous).filter((group) => !array(group.memberStationIds).some((id) => changed.has(id))), ...array(incoming)];
}

export function restoreStationSelection(previous, next, selected, lineId) {
  const options = stationOptions(next, lineId);
  if (options.some((item) => item.id === selected)) return selected;
  const members = array(previous?.stationGroups).find((group) => group.stationGroupId === selected)?.memberStationIds || [selected];
  const stop = array(next?.lineStops).find((row) => (lineId === "ALL" || row.lineId === lineId) && members.includes(row.stationId));
  return stop ? stationGroupId(stop) : options[0]?.id || "";
}

export function stationTrend(snapshot, lineId, stationId, buckets) {
  const byTime = new Map();
  array(snapshot?.stationVolumes).filter((row) => stationGroupId(row) === stationId &&
    (lineId === "ALL" || row.lineId === lineId)).forEach((row) => {
    const time = bucketTime(row);
    const value = byTime.get(time) || { boardings: 0, alightings: 0 };
    value.boardings += number(row.boardings);
    value.alightings += number(row.alightings);
    byTime.set(time, value);
  });
  return buckets.map((time) => ({ time, ...(byTime.get(time) || { boardings: 0, alightings: 0 }),
    available: byTime.has(time) }));
}

export function summaryRows(snapshot, key) { return array(snapshot?.summary?.[key]); }
export function matchingLine(row, lineId) { return lineId === "ALL" || row.lineId === lineId ||
  row.firstLineId === lineId || row.lastLineId === lineId ||
  array(row.lineContributions).some((part) => part.firstLineId === lineId || part.lastLineId === lineId); }
export function stationName(catalog, id) { return catalog.get(id) || id || "—"; }

export function stationNameLines(name, units = 11) {
  const result = [];
  let line = "", width = 0;
  Array.from(String(name || "")).forEach((character) => {
    const next = character.charCodeAt(0) <= 255 ? .6 : 1;
    if (line && width + next > units) { result.push(line); line = ""; width = 0; }
    line += character; width += next;
  });
  if (line) result.push(line);
  return result;
}

export function stationCatalog(snapshot) {
  const catalog = new Map();
  array(snapshot?.stationCatalog).forEach((row) => catalog.set(row.stationId, row.stationName || row.stationId));
  array(snapshot?.lineStops).forEach((row) => {
    catalog.set(row.stationId, row.stationName || row.stopName || row.stationId);
    catalog.set(stationGroupId(row), row.stationName || row.stopName || row.stationId);
  });
  array(snapshot?.stationGroups).forEach((group) => {
    catalog.set(group.stationGroupId, group.stationName || group.stationGroupId);
    array(group.memberStationIds).forEach((id) => catalog.set(id, group.stationName || id));
  });
  return catalog;
}

export function stationOptions(snapshot, lineId) {
  const seen = new Set();
  const stops = array(snapshot?.lineStops).filter((row) => lineId === "ALL" || row.lineId === lineId);
  return stops.filter((row) => { const id = stationGroupId(row); if (seen.has(id)) return false; seen.add(id); return true; })
    .map((row) => ({ id: stationGroupId(row), name: row.stationName || row.stopName || row.stationId }));
}

const comparisonOrder = (a, b, field = "first") => (b[field] ?? -Infinity) - (a[field] ?? -Infinity) || a.id.localeCompare(b.id);

export function comparisonLightColor(color) {
  const source = /^#[0-9a-f]{6}$/i.test(color || "") ? color : "#81d4d4";
  return "#" + [1, 3, 5].map((index) => Math.round(parseInt(source.slice(index, index + 2), 16) * .6 + 255 * .4)
    .toString(16).padStart(2, "0")).join("");
}

export function comparisonNameLines(name, units) {
  const lines = [];
  let line = "", width = 0, space = false;
  const tokens = String(name || "").match(/[A-Za-z0-9\u00c0-\u024f]+(?:['’-][A-Za-z0-9\u00c0-\u024f]+)*|\s+|[^\s]/gu) || [];
  tokens.forEach((token) => {
    if (/^\s+$/u.test(token)) { space = !!line; return; }
    const next = Array.from(token).reduce((total, character) => total + (character.charCodeAt(0) <= 255 ? .6 : 1), 0);
    if (line && width + (space ? .6 : 0) + next > units + 1e-6) { lines.push(line); line = ""; width = 0; }
    if (space && line) { line += " "; width += .6; }
    line += token; width += next; space = false;
  });
  if (line) lines.push(line);
  return lines;
}

export function comparisonPageLayout(count, width, requestedPage = 0) {
  const capacity = width > 0 ? Math.max(1, Math.floor(width / 80)) : Math.max(1, count);
  const pages = Math.max(1, Math.ceil(count / capacity));
  const page = Math.max(0, Math.min(pages - 1, requestedPage));
  const start = page * capacity, end = Math.min(count, start + capacity);
  const categoryWidth = width > 0 ? width / Math.max(1, end - start) : 128;
  return { page, pages, start, end, nameUnits: Math.max(1, (categoryWidth - 16) / 14) };
}

export function lineComparison(snapshot, lines, mode) {
  const loads = new Map(summaryRows(snapshot, "lineVolumes").filter((row) => row.mode === mode)
    .map((row) => [row.lineId, row]));
  return lines.map((line) => {
    const row = loads.get(line.id);
    return { id: line.id, name: line.name, color: line.color || "#81d4d4",
      first: row ? row.boardings : null,
      second: row?.capacityFrames > 0 ? row.averageLoadRatio * 100 : null };
  }).sort(comparisonOrder);
}

export function stationComparison(snapshot, stops, catalog, mode, lineId, color) {
  const stations = new Map();
  const ensure = (id) => {
    if (!stations.has(id)) stations.set(id, { id, name: stationName(catalog, id), color: color || "#81d4d4",
      boardings: null, alightings: null, first: null, frames: 0, waitTotal: 0 });
    return stations.get(id);
  };
  stops.filter((stop) => stop.lineId === lineId && stop.mode === mode && stop.stationId).forEach((stop) => ensure(stationGroupId(stop)));
  summaryRows(snapshot, "stationVolumes").filter((row) => row.mode === mode && row.lineId === lineId && row.stationId)
    .forEach((row) => {
      const station = ensure(stationGroupId(row));
      station.boardings = (station.boardings ?? 0) + row.boardings; station.alightings = (station.alightings ?? 0) + row.alightings;
    });
  summaryRows(snapshot, "stationWaiting").filter((row) => row.mode === mode && row.lineId === lineId && row.stationId)
    .forEach((row) => {
      const station = ensure(stationGroupId(row));
      if (!(row.observedFrames > 0)) return;
      station.first = (station.first ?? 0) + row.averageWaitingCount;
      station.waitTotal += row.averageEstimatedWaitMinutes * row.observedFrames;
      station.frames += row.observedFrames;
    });
  return [...stations.values()].map((station) => ({ id: station.id, name: station.name, color: station.color,
    boardings: station.boardings, alightings: station.alightings,
    total: station.boardings == null ? null : station.boardings + station.alightings, first: station.first,
    second: station.frames > 0 ? station.waitTotal / station.frames : null })).sort((a, b) => comparisonOrder(a, b, "total"));
}

export function groupedStations(rows, count = 12) {
  const size = Math.max(1, Math.ceil(rows.length / count));
  const groups = [];
  for (let index = 0; index < rows.length; index += size) {
    const part = rows.slice(index, index + size);
    groups.push({ time: part[0].time, end: part[part.length - 1].time + 15,
      boardings: part.reduce((sum, row) => sum + row.boardings, 0),
      alightings: part.reduce((sum, row) => sum + row.alightings, 0),
      available: part.some((row) => row.available) });
  }
  return groups;
}

export const PURPOSE_COLORS = ["#a98bff", "#f5df62", "#edad7e", "#84beeb", "#51d9d2", "#b9bacb", "#7f8b99"];
export function purposeIndex(category) {
  switch (category) {
    case "work": return 0;
    case "school": return 1;
    case "shopping": return 2;
    case "leisure":
    case "sightseeing": return 3;
    case "home":
    case "hotel": return 4;
    case "movingIn":
    case "movingAway":
    case "leavingCity":
    case "other": return 5;
    default: return 6;
  }
}
export function purposeValues(rows, field) {
  if (rows.some((row) => !Array.isArray(row.purposeCounts))) return null;
  const values = Array(7).fill(0);
  rows.forEach((row) => array(row.purposeCounts).forEach((entry) => {
    values[purposeIndex(entry.category)] += number(entry[field]);
  }));
  return values;
}

export function sectionColor(ratio) {
  if (ratio == null) return "#86939e";
  return ratio >= .8 ? "#ff4d59" : ratio >= .3 ? "#ffc33b" : "#26d77b";
}

export function lineTextColor(color) {
  const channels = [1, 3, 5].map((index) => parseInt(color.slice(index, index + 2), 16) / 255);
  const linear = channels.map((value) => value <= .04045 ? value / 12.92 : Math.pow((value + .055) / 1.055, 2.4));
  const luminance = linear[0] * .2126 + linear[1] * .7152 + linear[2] * .0722;
  return luminance >= .42 ? "#000000" : "#ffffff";
}

export function odRanking(rows, catalog) {
  const pairs = new Map();
  const firstLineVolumes = new Map();
  rows.forEach((row) => {
    const key = `${row.originStationId}\u0000${row.destinationStationId}`;
    const item = pairs.get(key) || { ...row, volume: 0, originName: stationName(catalog, row.originStationId),
      destinationName: stationName(catalog, row.destinationStationId), purposeCounts: [], lineContributions: [] };
    item.volume += number(row.completedCount);
    item.purposeCounts.push(...array(row.purposeCounts));
    const contributions = Array.isArray(row.lineContributions) ? row.lineContributions :
      [{ firstLineId: row.firstLineId, lastLineId: row.lastLineId, completedCount: row.completedCount }];
    item.lineContributions.push(...contributions);
    const volumes = firstLineVolumes.get(key) || new Map();
    contributions.forEach((part) => volumes.set(part.firstLineId,
      (volumes.get(part.firstLineId) || 0) + number(part.completedCount)));
    firstLineVolumes.set(key, volumes);
    item.dominantLineId = pairs.has(key)
      ? [...volumes].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0))[0]?.[0]
      : row.dominantLineId || row.firstLineId;
    pairs.set(key, item);
  });
  return [...pairs.values()].sort((a, b) => b.volume - a.volume);
}
