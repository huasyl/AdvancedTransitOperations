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
    map.set(stationId, String(entry?.stationName || stationId));
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

  stationVolumes.forEach((entry) => {
    const boardings = Number(entry?.boardings || 0);
    const alightings = Number(entry?.alightings || 0);
    const total = boardings + alightings;
    totalBoardingsAlightings24h += total;

    const stationId = String(entry?.stationId || "");
    const stationName = String(entry?.stationName || "") || stationNames.get(stationId) || stationId;
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
    peakSectionLoad = Math.max(peakSectionLoad, Number(entry?.averageLoadPassengers || 0));
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
