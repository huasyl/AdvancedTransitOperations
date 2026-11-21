import { useEffect, useMemo, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { departureLoads } from "./schedule-view-model";

export default function useDepartureLoads({ mode, lineId, isActive }) {
  const api = useMemo(() => getWorkbenchApi(), []);
  const [state, set] = useState(null);
  useEffect(() => {
    if (!isActive || !lineId) return undefined;
    let disposed = false;
    const load = (day) => api.loadPassengerFlowSnapshot({ mode, day, view: "line", lineId, includeSeries: true, includeSummary: false, includeSections: false,
      includePurposes: false, includeTransfers: false, includeStationWaiting: false, includeLineStops: false, includeStationCatalog: false });
    Promise.all([load("today"), load("yesterday")])
      .then(([today, yesterday]) => { if (!disposed) set({ mode, lineId, series: departureLoads(today, yesterday, lineId) }); })
      .catch(() => { if (!disposed) set({ mode, lineId, series: null }); });
    return () => { disposed = true; };
  }, [api, mode, lineId, isActive]);
  const current = state?.mode === mode && state.lineId === lineId;
  const series = current ? state.series : null;
  return { values: series?.values || null, split: series?.split, loading: Boolean(lineId) && !current };
}
