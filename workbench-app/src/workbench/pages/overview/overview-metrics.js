function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function stationCatalogMap(snapshot) {
  const map = new Map();
  asArray(snapshot?.stationCatalog).forEach((entry) => {
    const stationId = String(entry?.stationId || "");
    if (!stationId) {
      return;
    }
    const stationName = String(entry?.stationName || "").trim();
    map.set(stationId, stationName || stationId);
  });
  return map;
}

export function buildOverviewMetrics(passengerSnapshot = {}) {
  const stationVolumes = asArray(passengerSnapshot?.stationVolumes);
  const sectionVolumes = asArray(passengerSnapshot?.sectionVolumes);
  const stationNames = stationCatalogMap(passengerSnapshot);

  let totalBoardingsAlightings24h = 0;
  let peakSectionLoad = 0;
  const stationTotals = new Map();
  const bucketTotals = new Map();
  const sectionTotals = new Map();

  stationVolumes.forEach((entry) => {
    const boardings = Number(entry?.boardings || 0);
    const alightings = Number(entry?.alightings || 0);
    const total = boardings + alightings;
    totalBoardingsAlightings24h += total;

    const stationId = String(entry?.stationId || "");
    const entryStationName = String(entry?.stationName || "").trim();
    const stationName = stationNames.get(stationId) || entryStationName || stationId;
    if (stationId) {
      const currentStation = stationTotals.get(stationId) || { stationId, stationName, total: 0 };
      currentStation.total += total;
      if (!currentStation.stationName && stationName) {
        currentStation.stationName = stationName;
      }
      stationTotals.set(stationId, currentStation);
    }

    const bucketKey = `${Number(entry?.serviceDayIndex || 0)}:${Number(entry?.bucketStartMinute || 0)}`;
    bucketTotals.set(bucketKey, Number(bucketTotals.get(bucketKey) || 0) + total);
  });

  sectionVolumes.forEach((entry) => {
    const fromStationId = String(entry?.fromStationId || "");
    const toStationId = String(entry?.toStationId || "");
    if (!fromStationId || !toStationId) {
      return;
    }
    const averageLoadPassengers = Number(entry?.averageLoadPassengers || 0);
    const sampleCount = Number(entry?.sampleCount || 0);
    const total = sampleCount > 0 ? averageLoadPassengers * sampleCount : averageLoadPassengers;
    const sectionKey = `${fromStationId}->${toStationId}`;
    sectionTotals.set(sectionKey, Number(sectionTotals.get(sectionKey) || 0) + total);
  });

  sectionTotals.forEach((total) => {
    peakSectionLoad = Math.max(peakSectionLoad, Number(total || 0));
  });

  let busiestStationName = "";
  let busiestStationTotal = 0;
  stationTotals.forEach((entry) => {
    if (entry.total > busiestStationTotal) {
      busiestStationName = entry.stationName;
      busiestStationTotal = entry.total;
    }
  });

  let peakQuarterHourFlow = 0;
  bucketTotals.forEach((total) => {
    peakQuarterHourFlow = Math.max(peakQuarterHourFlow, Number(total || 0));
  });

  return {
    totalBoardingsAlightings24h,
    busiestStationName,
    busiestStationTotal,
    peakSectionLoad,
    peakQuarterHourFlow
  };
}
