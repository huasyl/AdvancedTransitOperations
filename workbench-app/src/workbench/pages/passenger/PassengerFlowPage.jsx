import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import WorkbenchScrollArea from "../../shared/WorkbenchScrollArea";
import { traceWorkbench } from "../../shared/workbench-trace";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import PassengerFlowDashboard from "./PassengerFlowDashboard";
import { DEFAULT_SERVICE_PERIODS } from "../../../lib/service-periods";
import { buildBuckets, stationOptions, mergeStationGroups, restoreStationSelection } from "./passenger-analysis";

const asArray = (value) => Array.isArray(value) ? value : [];
const directoryOnly = { directoryOnly: true, includeSeries: false, includeSummary: false, includePurposes: false,
  includeTransfers: false, includeStationWaiting: false, includeSections: false, includeVehicleSections: false };
function modeToken(value) {
  const token = String(value || "").toLowerCase();
  return ["train", "subway", "tram", "bus"].includes(token) ? token : "train";
}

function buildLines(catalog) {
  const map = new Map();
  asArray(catalog?.lines).forEach((line) => {
    const id = String(line.id || "");
    if (id) map.set(id, { id, code: String(line.displayCode || line.routeNumber || ""),
      name: String(line.name || line.displayCode || line.routeNumber || id),
      color: line.color || "#64748b" });
  });
  return [...map.values()];
}

export default function PassengerFlowPage({ activeTransportMode = "train", isActive = false, registerHostActions, readServicePeriods, writeServicePeriods }) {
  const { t } = useNativeScheduleI18n();
  const mode = modeToken(activeTransportMode);
  const [context, setContext] = useState(null), [analysis, setAnalysis] = useState(null);
  const [catalogs, setCatalogs] = useState({}), [lineCatalogs, setLineCatalogs] = useState({});
  const [stopCatalogs, setStopCatalogs] = useState({});
  const [lineId, setLineId] = useState("ALL"), [stationId, setStationId] = useState("");
  const [renderedLineId, setRenderedLineId] = useState("ALL");
  const [page, setPage] = useState("line"), [range, setRange] = useState(null);
  const [day, setDay] = useState("today");
  const [servicePeriods, setServicePeriods] = useState(DEFAULT_SERVICE_PERIODS);
  useEffect(() => {
    if (isActive) setServicePeriods(readServicePeriods?.(mode, lineId) || DEFAULT_SERVICE_PERIODS);
  }, [isActive, mode, lineId, readServicePeriods]);
  function changeServicePeriods(periods) {
    setServicePeriods(periods);
    writeServicePeriods?.(mode, lineId, periods);
  }
  const [pending, setPending] = useState(false), [error, setError] = useState("");
  const scrollRef = useRef(null);
  const generation = useRef(0), busy = useRef(false), mounted = useRef(false);
  const directoryEpoch = useRef(0), directoryRequests = useRef(new Set());
  const directories = useRef({ catalogs: {}, stations: {}, stops: {} });
  const queued = useRef(null), initialized = useRef(false), contextKey = useRef("");
  const trendRefresh = useRef(false);
  const contextRef = useRef(null), activeRef = useRef(isActive);
  const selectionRef = useRef({ mode, lineId, stationId, page, range, day });
  activeRef.current = isActive;
  const modeRef = useRef(mode); modeRef.current = mode;
  const catalog = catalogs[mode];
  const lines = useMemo(() => buildLines(catalog), [catalog]);
  const stations = useMemo(() => stationOptions(context, lineId), [context, lineId]);
  const activeStation = stationId && stations.some((item) => item.id === stationId) ? stationId : stations[0]?.id || "";
  const bucketCount = buildBuckets(context).length;

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; generation.current += 1; }; }, []);
  const acceptCatalog = useCallback((token, nextCatalog) => {
    directories.current.catalogs[token] = nextCatalog;
    setCatalogs((previous) => ({ ...previous, [token]: nextCatalog }));
    setLineCatalogs((previous) => ({ ...previous, [token]: buildLines(nextCatalog) }));
  }, []);

  const acceptStops = useCallback((token, key, result) => {
    directories.current.groups = { ...directories.current.groups,
      [token]: mergeStationGroups(directories.current.groups?.[token], result?.stationGroups) };
    if (!result?.lineStopsAvailable) return;
    const stops = asArray(result.lineStops);
    const updates = new Map(stops.map((stop) => [stop.stationId, stop]));
    asArray(result.stationGroups).forEach((group) => asArray(group.memberStationIds).forEach((id) =>
      updates.set(id, { ...updates.get(id), stationName: group.stationName, stationGroupId: group.stationGroupId })));
    Object.keys(directories.current.stops).filter((item) => item.startsWith(`${token}:`)).forEach((item) => {
      directories.current.stops[item] = directories.current.stops[item].map((stop) => {
        const update = updates.get(stop.stationId);
        return update ? { ...stop, stationName: update.stationName, stationGroupId: update.stationGroupId } : stop;
      });
    });
    directories.current.stops[key] = stops;
    if (directories.current.stations[token]) directories.current.stations[token] = directories.current.stations[token].map((station) => {
      const update = updates.get(station.stationId);
      return update ? { ...station, stationName: update.stationName, stationGroupId: update.stationGroupId } : station;
    });
  }, []);

  const readSelection = useCallback(async () => {
    if (busy.current) return;
    busy.current = true;
    const api = getWorkbenchApi();
    while (queued.current && mounted.current && activeRef.current) {
      const job = queued.current; queued.current = null;
      const { mode: token, lineId: selectedLine, page: selectedPage, range: selectedRange, day: selectedDay } = job.selection;
      let selectedStation = job.selection.stationId;
      const ticket = job.ticket, epoch = directoryEpoch.current;
      const current = () => mounted.current && ticket === generation.current && modeRef.current === token;
      const stopsKey = `${token}:${selectedLine}`;
      let directoryStops = [];
      let directoryContext = contextRef.current;
      try {
        if (!directories.current.catalogs[token]) {
          const nextCatalog = await api.refreshTransitCatalog({ mode: token });
          if (!current()) continue;
          if (epoch === directoryEpoch.current) acceptCatalog(token, nextCatalog);
        }
        if (!activeRef.current) continue;
        if (!job.selection.refreshStops && selectedLine !== "ALL" && !directories.current.stops[stopsKey] && directories.current.stops[`${token}:ALL`])
          directories.current.stops[stopsKey] = directories.current.stops[`${token}:ALL`].filter((stop) => stop.lineId === selectedLine);
        // 车站选择需要当前方式的停站目录；带站点条件的投影不能覆盖完整站序。
        if (selectedPage === "station" && (job.selection.refreshStops || !directories.current.stops[stopsKey])) {
          const result = await api.loadPassengerFlowSnapshot({ ...directoryOnly, mode: token,
            ...(selectedLine !== "ALL" ? { lineId: selectedLine } : {}),
            includeLineStops: true, includeStationCatalog: false });
          if (!current()) continue;
          directoryContext = result;
          directoryStops = asArray(result.lineStops);
          if (epoch === directoryEpoch.current) {
            acceptStops(token, stopsKey, result);
            selectionRef.current.refreshStops = false;
          }
        }
        if (!activeRef.current) continue;
        const cachedStops = directories.current.stops[stopsKey] || directoryStops;
        if (selectedPage === "station") {
          selectedStation = restoreStationSelection(contextRef.current,
            { lineStops: cachedStops, stationGroups: directories.current.groups?.[token] }, selectedStation, selectedLine);
          if (!selectedStation) {
            contextRef.current = { ...directoryContext, mode: token, lineStops: cachedStops, stationCatalog: directories.current.stations[token] || [] };
            contextKey.current = "";
            setContext(contextRef.current); setAnalysis(null); setStationId("");
            continue;
          }
          selectionRef.current.stationId = selectedStation; setStationId(selectedStation);
        }
        const objectKey = `${token}:${selectedDay}:${selectedPage}:${selectedLine}:${selectedPage === "station" ? selectedStation : ""}`;
        const needsTrend = job.refreshTrend || contextKey.current !== objectKey;
        const needsStops = selectedPage === "line" && selectedLine !== "ALL" && (job.selection.refreshStops || !directories.current.stops[stopsKey]);
        const needsStations = !directories.current.stations[token];
        const request = { mode: token, day: selectedDay, view: selectedPage === "station" ? "station" : selectedLine === "ALL" ? "network" : "line",
          includeSeries: false, includeSummary: true, includePurposes: true,
          includeTransfers: selectedPage === "station", includeStationWaiting: selectedPage === "line" && selectedLine !== "ALL",
          includeLineComparison: selectedPage === "line" && selectedLine === "ALL",
          includeSections: selectedPage === "line", includeVehicleSections: false,
          includeLineStops: false, includeStationCatalog: false,
          ...(selectedPage === "line" ? { sectionKind: selectedLine === "ALL" ? "track" : "stops" } : {}),
          ...(selectedLine !== "ALL" ? { lineId: selectedLine } : {}),
          ...(selectedPage === "station" ? { stationGroupId: selectedStation } : {}) };
        const trendRequest = { ...request, includeSeries: true,
          includeLineStops: needsStops, includeStationCatalog: needsStations };
        traceWorkbench("passenger.load.begin", { mode: token, lineId: selectedLine, page: selectedPage });
        let nextContext = contextRef.current;
        let nextAnalysis;
        if (needsTrend) {
          nextContext = await api.loadPassengerFlowSnapshot(selectedRange ? { ...trendRequest,
            includeSummary: false, includePurposes: false, includeTransfers: false,
            includeStationWaiting: false, includeSections: false } : trendRequest);
          if (!current()) continue;
          if (epoch === directoryEpoch.current) {
            if (needsStations) directories.current.stations[token] = asArray(nextContext.stationCatalog);
            acceptStops(token, stopsKey, nextContext);
            if (needsStops) selectionRef.current.refreshStops = false;
          }
          nextContext = { ...nextContext, stationCatalog: directories.current.stations[token] || asArray(nextContext.stationCatalog),
            lineStops: directories.current.stops[stopsKey] || (selectedPage === "station" ? cachedStops : asArray(nextContext.lineStops)),
            stationGroups: directories.current.groups?.[token] || nextContext.stationGroups };
          if (selectedPage === "line" && selectionRef.current.stationId) {
            const restored = restoreStationSelection(contextRef.current, nextContext, selectionRef.current.stationId, selectedLine);
            selectionRef.current.stationId = restored; setStationId(restored);
          }
          contextRef.current = nextContext; contextKey.current = objectKey;
          trendRefresh.current = false;
          if (!selectedRange) nextAnalysis = nextContext;
        }
        if (!nextAnalysis) {
          if (!activeRef.current) continue;
          nextAnalysis = await api.loadPassengerFlowSnapshot({ ...request,
            includeLineStops: !needsTrend && needsStops, includeStationCatalog: !needsTrend && needsStations,
            ...(selectedRange ? { fromMinute: selectedRange[0] % 1440,
              toMinute: selectedRange[1] - Math.floor(selectedRange[0] / 1440) * 1440 } : {}) });
          if (!current()) continue;
          if (epoch === directoryEpoch.current) acceptStops(token, stopsKey, nextAnalysis);
          if (epoch === directoryEpoch.current && !needsTrend) {
            if (needsStops) selectionRef.current.refreshStops = false;
            if (needsStations) directories.current.stations[token] = asArray(nextAnalysis.stationCatalog);
          }
          nextContext = { ...nextContext, lineStops: directories.current.stops[stopsKey] ||
            (selectedPage === "station" ? cachedStops : needsStops ? asArray(nextAnalysis.lineStops) : asArray(nextContext?.lineStops)),
            stationCatalog: directories.current.stations[token] || asArray(nextContext?.stationCatalog),
            stationGroups: directories.current.groups?.[token] || nextContext?.stationGroups };
          contextRef.current = nextContext;
        }
        setStopCatalogs({ ...directories.current.stops });
        setContext(nextContext); setAnalysis(nextAnalysis); setRenderedLineId(selectedLine); setError("");
        traceWorkbench("passenger.load.done", { mode: token, buckets: buildBuckets(nextContext).length });
      } catch (failure) {
        if (current()) setError(failure?.message || t("nativeWorkbench.passenger.error.loadFailed"));
      }
    }
    busy.current = false;
    if (mounted.current) setPending(false);
  }, [acceptCatalog, acceptStops, t]);

  const querySelection = useCallback((changes = {}, refreshTrend = false) => {
    selectionRef.current = { ...selectionRef.current, ...changes };
    if (refreshTrend) trendRefresh.current = true;
    const ticket = ++generation.current;
    if (!Object.prototype.hasOwnProperty.call(changes, "lineId")) setAnalysis(null);
    setError("");
    if (!activeRef.current) return;
    queued.current = { selection: { ...selectionRef.current }, ticket,
      refreshTrend: trendRefresh.current };
    setPending(true); readSelection();
  }, [readSelection]);

  useEffect(() => {
    if (selectionRef.current.mode !== mode) {
      selectionRef.current = { mode, lineId: "ALL", stationId: "", page: "line", range: null, day: selectionRef.current.day };
      setLineId("ALL"); setRenderedLineId("ALL"); setStationId(""); setRange(null); setPage("line");
      contextRef.current = null; contextKey.current = ""; setContext(null); setAnalysis(null);
      generation.current += 1; queued.current = null; initialized.current = false;
    }
    if (!isActive) { queued.current = null; setPending(false); return; }
    if (!initialized.current) { initialized.current = true; querySelection(); }
  }, [mode, isActive, querySelection]);

  useEffect(() => {
    const api = getWorkbenchApi();
    function updateDirectory(event) {
      const token = event?.mode || event?.sourceMode;
      const tokens = token ? [token] : Object.keys(directories.current.catalogs);
      directoryEpoch.current += 1;
      directoryRequests.current.clear();
      tokens.forEach((item) => {
        if (Array.isArray(event?.lines)) acceptCatalog(item, event);
        else delete directories.current.catalogs[item];
        delete directories.current.stations[item];
        const ids = asArray(event?.lineIds);
        Object.keys(directories.current.stops).forEach((key) => {
          if (key.startsWith(`${item}:`) && (!ids.length || key === `${item}:ALL` || ids.some((id) => key === `${item}:${id}`)))
            delete directories.current.stops[key];
        });
        setStopCatalogs((previous) => Object.fromEntries(Object.entries(previous).filter(([key]) =>
          !key.startsWith(`${item}:`) || ids.length && !ids.some((id) => key === `${item}:${id}`))));
      });
    }
    const offCatalog = api.onCatalogChanged?.(updateDirectory);
    const offLine = api.onLineInvalidated?.(updateDirectory);
    const offSnapshot = api.onSnapshotChanged?.(updateDirectory);
    return () => { offCatalog?.(); offLine?.(); offSnapshot?.(); };
  }, [acceptCatalog]);

  useEffect(() => {
    if (!isActive || page !== "station" || !analysis || analysis.mode !== mode) return;
    const transfers = asArray(analysis?.summary?.transferFlows);
    const epoch = directoryEpoch.current;
    const transferModes = new Set(transfers
      .flatMap((row) => [row.fromMode, row.toMode]).filter((token) => token && token !== mode && !directories.current.catalogs[token]));
    transferModes.forEach((otherMode) => {
      const key = `catalog:${otherMode}`;
      if (directoryRequests.current.has(key)) return;
      directoryRequests.current.add(key);
      getWorkbenchApi().refreshTransitCatalog({ mode: otherMode }).then((result) => {
        if (!mounted.current || modeRef.current !== mode || directoryEpoch.current !== epoch) return;
        acceptCatalog(otherMode, result);
      }).catch((failure) => traceWorkbench("passenger.lineCatalog.error", { mode: otherMode, message: failure?.message || failure }))
        .finally(() => { if (directoryEpoch.current === epoch) directoryRequests.current.delete(key); });
    });
    const transferLines = new Map();
    transfers.forEach((row) => {
      if (row.fromMode && row.fromLineId) transferLines.set(`${row.fromMode}:${row.fromLineId}`, [row.fromMode, row.fromLineId]);
      if (row.toMode && row.toLineId) transferLines.set(`${row.toMode}:${row.toLineId}`, [row.toMode, row.toLineId]);
    });
    transferLines.forEach(([otherMode, otherLineId], key) => {
      if (!directories.current.stops[key] && directories.current.stops[`${otherMode}:ALL`]) {
        const stops = directories.current.stops[`${otherMode}:ALL`].filter((stop) => stop.lineId === otherLineId);
        directories.current.stops[key] = stops;
        setStopCatalogs((previous) => ({ ...previous, [key]: stops }));
      }
      if (directories.current.stops[key] !== undefined || directoryRequests.current.has(`stops:${key}`)) return;
      directoryRequests.current.add(`stops:${key}`);
      getWorkbenchApi().loadPassengerFlowSnapshot({ ...directoryOnly, mode: otherMode, lineId: otherLineId,
        includeLineStops: true, includeStationCatalog: false }).then((result) => {
        if (!mounted.current || modeRef.current !== mode || directoryEpoch.current !== epoch) return;
        if (result?.lineStopsAvailable) directories.current.stops[key] = asArray(result?.lineStops);
        setStopCatalogs((previous) => ({ ...previous, [key]: asArray(result?.lineStops) }));
      }).catch((failure) => traceWorkbench("passenger.stopCatalog.error", { mode: otherMode, lineId: otherLineId, message: failure?.message || failure }))
        .finally(() => { if (directoryEpoch.current === epoch) directoryRequests.current.delete(`stops:${key}`); });
    });
  }, [analysis, acceptCatalog]);

  useEffect(() => {
    if (!isActive) return;
    window.__RT_WORKBENCH_ACTIVE_PAGE__ = "passenger";
    window.__RT_WORKBENCH_SELECTED_LINE_ID__ = lineId === "ALL" ? "" : lineId;
    window.__RT_WORKBENCH_SELECTED_EDIT_LINE__ = "";
    getWorkbenchApi().setHostState?.({ mode, activePage: "passenger", selectedLineId: lineId === "ALL" ? "" : lineId,
      selectedEditLine: "" });
  }, [isActive, mode, lineId]);
  useEffect(() => {
    if (!isActive || typeof registerHostActions !== "function") return undefined;
    registerHostActions({ refreshData: async () => {} });
    return () => registerHostActions(null);
  }, [isActive, registerHostActions]);

  function selectLine(next) {
    setLineId(next);
    querySelection({ lineId: next, refreshStops: next !== "ALL" }, true);
  }
  function selectStation(next) {
    setStationId(next);
    selectionRef.current.stationId = next;
    if (page === "station") querySelection({ stationId: next }, true);
  }
  function selectPage(next) {
    setPage(next); querySelection({ page: next }, true);
  }
  function selectRange(next) {
    setRange(next); querySelection({ range: next });
  }
  function selectDay(next) {
    setDay(next); setRange(null);
    contextRef.current = null; contextKey.current = ""; setContext(null);
    querySelection({ day: next, range: null }, true);
  }
  return <div className="rtw-passenger-root"><WorkbenchScrollArea className="rtw-passenger-body" externalScrollRef={scrollRef}
    metricsKey={`${lineId}:${page}:${bucketCount}:${asArray(analysis?.summary?.sectionVolumes).length}`}>
    {error ? <div className="rtw-passenger-error">{t("passengerFlow.queryFailed")}：{error}</div> : null}
    {!context && !error ? <div className="rtw-passenger-status">{t("passengerFlow.loading")}</div> : null}
    <PassengerFlowDashboard mode={mode} isActive={isActive} context={context} analysis={analysis}
      pending={pending} lines={lines} lineCatalogs={lineCatalogs} stopCatalogs={stopCatalogs} lineId={renderedLineId} selectedLineId={lineId} onLineChange={selectLine}
      page={page} onPageChange={selectPage} stationId={activeStation} onStationChange={selectStation}
      range={range} onRangeChange={selectRange} day={day} onDayChange={selectDay}
      servicePeriods={servicePeriods} onServicePeriodsChange={changeServicePeriods} scrollRef={scrollRef} />
  </WorkbenchScrollArea></div>;
}
