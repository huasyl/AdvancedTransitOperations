import { useEffect, useMemo, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";

export function departureStops(layout, flow) {
  if (!layout?.success) return [];
  const groupByStop = new Map();
  (flow?.stationGroups || []).forEach((group) => {
    group.memberStationIds.forEach((stopKey) => groupByStop.set(stopKey, group.stationGroupId));
  });
  const volumes = new Map();
  (flow?.summary?.stationVolumes || []).forEach((row) => {
    if (Number.isFinite(row.boardings) && Number.isFinite(row.alightings)) {
      const key = row.stationGroupId || row.stationId;
      volumes.set(key, (volumes.get(key) || 0) + row.boardings + row.alightings);
    }
  });
  const stops = [...(layout.stops || [])].sort((left, right) => left.order - right.order);
  return stops.map((stop) => ({
    ...stop,
    stationId: stop.stopKey,
    stationName: stop.name,
    passengers: volumes.get(groupByStop.get(stop.stopKey) || stop.stopKey)
  }));
}

export default function useDepartureData({ mode, lineId, isActive, catalogRevision, fromMinute, toMinute }) {
  const api = useMemo(() => getWorkbenchApi(), []);
  const [data, setData] = useState(null);
  const [flow, setFlow] = useState(null);
  const [shown, setShown] = useState(null);
  useEffect(() => {
    if (!isActive || !lineId) return undefined;
    let disposed = false;
    api.loadPassengerFlowSnapshot({ mode, day: "today", view: "line", lineId, sectionKind: "stops",
      includeSeries: false, includeSummary: true, includeSections: true, includePurposes: false, includeTransfers: false,
      includeStationWaiting: false, includeLineStops: false, includeStationCatalog: false, fromMinute, toMinute })
      .then((result) => { if (!disposed) setFlow({ mode, lineId, result, error: "" }); })
      .catch((error) => { if (!disposed) setFlow({ mode, lineId, result: null, error: error?.message || String(error) }); });
    return () => { disposed = true; };
  }, [api, mode, lineId, isActive, fromMinute, toMinute]);
  useEffect(() => {
    if (!isActive || !lineId) return undefined;
    let disposed = false;
    api.loadTimetableLineLayout({ lineId }).then((layout) => {
      if (disposed) return;
      if (!layout?.success || layout.lineId !== lineId || layout.mode !== mode) throw new Error(layout?.error || "timetable-line-layout-failed");
      setData({ mode, lineId, layout, error: "" });
    }).catch((error) => { if (!disposed) setData({ mode, lineId, layout: null, error: error?.message || String(error) }); });
    const invalidated = api.onLineInvalidated?.((event) => {
      if (event?.mode === mode && event.lineIds?.includes(lineId)) disposed = true;
    });
    return () => { disposed = true; invalidated?.(); };
  }, [api, mode, lineId, isActive, catalogRevision]);
  useEffect(() => {
    if (!lineId) setShown(null);
    else if (data?.mode === mode && data.lineId === lineId && flow?.mode === mode && flow.lineId === lineId) setShown({ data, flow });
  }, [mode, lineId, data, flow]);
  return {
    stops: useMemo(() => departureStops(shown?.data.layout, shown?.flow.result), [shown]),
    sections: shown?.flow.result?.summary?.sectionVolumes,
    error: [shown?.data.error, shown?.flow.error].filter(Boolean).join("; "),
    routeKey: shown ? `${shown.data.mode}:${shown.data.lineId}:${shown.data.layout?.stopSig || ""}` : "",
    loading: Boolean(lineId) && !(shown?.data.lineId === lineId && shown.data.mode === mode)
  };
}
