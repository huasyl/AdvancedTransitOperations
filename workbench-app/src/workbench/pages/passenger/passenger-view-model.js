function asArray(value) {
  return Array.isArray(value) ? value : [];
}

const LINE_COLORS = ["#38bdf8", "#f59e0b", "#10b981", "#ef4444", "#a78bfa", "#f472b6", "#22c55e", "#eab308"];

function hashText(text) {
  let hash = 0;
  const source = String(text || "");
  for (let index = 0; index < source.length; index += 1) {
    hash = ((hash * 31) + source.charCodeAt(index)) >>> 0;
  }
  return hash;
}

function lineColor(lineId) {
  return LINE_COLORS[hashText(lineId) % LINE_COLORS.length];
}

function buildLineCatalog(metadataSnapshot) {
  const map = new Map();
  asArray(metadataSnapshot?.lines).forEach((line) => {
    const id = String(line?.id || "");
    if (!id) {
      return;
    }
    const code = String(line?.displayCode || line?.routeNumber || "").trim();
    const name = String(line?.name || "").trim();
    map.set(id, {
      id,
      code,
      name: name || code || "--",
      shortName: name,
      color: line?.color || lineColor(id)
    });
  });
  return map;
}

function buildStationCatalog(snapshot) {
  const map = new Map();
  asArray(snapshot?.stationCatalog).forEach((station) => {
    const id = String(station?.stationId || "");
    const name = String(station?.stationName || "");
    if (id && name) {
      map.set(id, name);
    }
  });
  return map;
}

function stationName(stationCatalog, stationId) {
  const id = String(stationId || "");
  return stationCatalog.get(id) || id;
}

function bucketLabel(entry) {
  const bucketStartMinute = Number(entry?.bucketStartMinute || 0);
  if (!Number.isFinite(bucketStartMinute)) {
    return "";
  }
  const minuteOfDay = ((Math.round(bucketStartMinute) % 1440) + 1440) % 1440;
  const hour = Math.floor(minuteOfDay / 60);
  const minute = minuteOfDay % 60;
  return `${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}`;
}

function addTrendValue(map, key, entry, passengers) {
  const existing = map.get(key);
  if (existing) {
    existing.passengers += passengers;
    return;
  }
  const serviceDayKey = Number(entry?.serviceDayKey ?? entry?.serviceDayIndex ?? 0);
  map.set(key, {
    hour: bucketLabel(entry),
    serviceDayKey: Number.isFinite(serviceDayKey) ? serviceDayKey : 0,
    bucketStartMinute: Number(entry?.bucketStartMinute || 0),
    passengers
  });
}

function sortByBucket(left, right) {
  const dayDelta = Number(left?.serviceDayKey || 0) - Number(right?.serviceDayKey || 0);
  if (dayDelta !== 0) {
    return dayDelta;
  }
  return Number(left?.bucketStartMinute || 0) - Number(right?.bucketStartMinute || 0);
}

function normalizeStationVolume(entry, stationCatalog) {
  const stationId = String(entry?.stationId || "");
  return {
    ...entry,
    lineId: String(entry?.lineId || ""),
    stationId,
    stationName: stationName(stationCatalog, stationId),
    inflow: Number(entry?.boardings || 0),
    outflow: Number(entry?.alightings || 0)
  };
}

function normalizeSectionVolume(entry, stationCatalog) {
  const fromStationId = String(entry?.fromStationId || "");
  const toStationId = String(entry?.toStationId || "");
  const fromStationName = stationName(stationCatalog, fromStationId);
  const toStationName = stationName(stationCatalog, toStationId);
  return {
    ...entry,
    lineId: String(entry?.lineId || ""),
    fromStationId,
    toStationId,
    fromStationName,
    toStationName,
    label: `${fromStationName}-${toStationName}`,
    volume: Number(entry?.averageLoadPassengers || 0)
  };
}

function normalizeOdFlow(entry, stationCatalog) {
  const firstLineId = String(entry?.firstLineId || entry?.lineId || "");
  const lastLineId = String(entry?.lastLineId || firstLineId);
  const originStationId = String(entry?.originStationId || "");
  const destinationStationId = String(entry?.destinationStationId || "");
  const displayLineId = firstLineId && lastLineId && firstLineId !== lastLineId ? `${firstLineId} -> ${lastLineId}` : firstLineId;
  return {
    ...entry,
    lineId: String(entry?.lineId || firstLineId),
    firstLineId,
    lastLineId,
    displayLineId,
    originStationId,
    destinationStationId,
    originName: stationName(stationCatalog, originStationId),
    destinationName: stationName(stationCatalog, destinationStationId),
    volume: Number(entry?.completedCount || 0)
  };
}

function addLine(lineMap, lineCatalog, lineId) {
  const id = String(lineId || "");
  if (!id || lineMap.has(id)) {
    return;
  }
  if (lineCatalog.has(id)) {
    lineMap.set(id, lineCatalog.get(id));
    return;
  }
  lineMap.set(id, {
    id,
    code: "",
    name: "--",
    shortName: "",
    color: lineColor(id)
  });
}

function buildLines(stationVolumes, sectionVolumes, odFlows, lineCatalog) {
  const lineMap = new Map();
  stationVolumes.forEach((entry) => addLine(lineMap, lineCatalog, entry?.lineId));
  sectionVolumes.forEach((entry) => addLine(lineMap, lineCatalog, entry?.lineId));
  odFlows.forEach((entry) => {
    addLine(lineMap, lineCatalog, entry?.lineId);
    addLine(lineMap, lineCatalog, entry?.firstLineId);
    addLine(lineMap, lineCatalog, entry?.lastLineId);
  });
  return [...lineMap.values()];
}

function buildTrends(stationVolumes) {
  const systemMap = new Map();
  const lineMaps = new Map();

  stationVolumes.forEach((entry) => {
    const passengers = Number(entry?.inflow || 0) + Number(entry?.outflow || 0);
    const serviceDayKey = Number(entry?.serviceDayKey ?? entry?.serviceDayIndex ?? 0);
    const bucketKey = `${Number.isFinite(serviceDayKey) ? serviceDayKey : 0}:${Number(entry?.bucketStartMinute || 0)}`;
    addTrendValue(systemMap, bucketKey, entry, passengers);

    const lineId = String(entry?.lineId || "");
    if (!lineId) {
      return;
    }
    if (!lineMaps.has(lineId)) {
      lineMaps.set(lineId, new Map());
    }
    addTrendValue(lineMaps.get(lineId), bucketKey, entry, passengers);
  });

  const lineTrendById = {};
  lineMaps.forEach((lineMap, lineId) => {
    lineTrendById[lineId] = [...lineMap.values()].sort(sortByBucket);
  });

  return {
    systemTrend: [...systemMap.values()].sort(sortByBucket),
    lineTrendById
  };
}

export function buildPassengerFlowViewModel(snapshot = {}, lineCatalogSnapshot = {}) {
  const lineCatalog = buildLineCatalog(lineCatalogSnapshot);
  const stationCatalog = buildStationCatalog(snapshot);
  const stationVolumes = asArray(snapshot?.stationVolumes).map((entry) => normalizeStationVolume(entry, stationCatalog));
  const sectionVolumes = asArray(snapshot?.sectionVolumes).map((entry) => normalizeSectionVolume(entry, stationCatalog));
  const odFlows = asArray(snapshot?.odFlows).map((entry) => normalizeOdFlow(entry, stationCatalog));
  const trends = buildTrends(stationVolumes);

  return {
    lines: buildLines(stationVolumes, sectionVolumes, odFlows, lineCatalog),
    lineTrendById: trends.lineTrendById,
    systemTrend: trends.systemTrend,
    stationVolumes,
    sectionVolumes,
    odFlows,
    warnings: asArray(snapshot?.warnings)
  };
}

function matchesLine(entry, selectedLineId) {
  return entry?.lineId === selectedLineId
    || entry?.firstLineId === selectedLineId
    || entry?.lastLineId === selectedLineId;
}

export function filterPassengerFlow(viewModel, selectedLineId) {
  if (!selectedLineId || selectedLineId === "ALL") {
    return viewModel;
  }

  return {
    ...viewModel,
    systemTrend: viewModel.lineTrendById?.[selectedLineId] || viewModel.systemTrend.filter((entry) => entry?.lineId === selectedLineId),
    stationVolumes: viewModel.stationVolumes.filter((entry) => entry?.lineId === selectedLineId),
    sectionVolumes: viewModel.sectionVolumes.filter((entry) => entry?.lineId === selectedLineId),
    odFlows: viewModel.odFlows.filter((entry) => matchesLine(entry, selectedLineId)),
    warnings: viewModel.warnings.filter((entry) => !entry?.lineId || entry.lineId === selectedLineId)
  };
}
