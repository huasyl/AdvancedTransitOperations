import { useEffect, useMemo, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";

const REFRESH_MS = 30000;

export default function useDepartureRecords({ lineId, isActive }) {
  const api = useMemo(() => getWorkbenchApi(), []);
  const [state, set] = useState(null);
  useEffect(() => {
    if (!isActive || !lineId) return undefined;
    let disposed = false;
    const load = () => api.loadMonitorTripHeaders({ dayOffset: 0, lineId, startMinute: 0, endMinute: 1439, limit: 256 })
      .then((response) => {
        if (disposed) return;
        const table = new Map();
        if (response?.success && !response.truncated) (response.trips || []).forEach((trip) => {
          const current = table.get(trip.plannedStartMinute);
          if (!current || trip.actualStartMinute != null || current.actualStartMinute == null) table.set(trip.plannedStartMinute, trip);
        });
        set({ lineId, table });
      })
      .catch(() => { if (!disposed) set({ lineId, table: new Map() }); });
    load();
    const timer = window.setInterval(load, REFRESH_MS);
    return () => { disposed = true; window.clearInterval(timer); };
  }, [api, lineId, isActive]);
  return state?.lineId === lineId ? state.table : null;
}
