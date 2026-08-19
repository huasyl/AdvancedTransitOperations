import { Fragment, useCallback, useEffect, useMemo, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import TimetableIcon from "./TimetableIcons";
import { formatServiceMinute } from "./timetable-data";

export default function OperationMonitor({ line, dateMode, isActive, t }) {
  const api = useMemo(() => getWorkbenchApi(), []);
  const [trips, setTrips] = useState([]);
  const [details, setDetails] = useState({});
  const [expandedTripKey, setExpandedTripKey] = useState("");
  const [error, setError] = useState("");
  const stationNames = useMemo(
    () => {
      const names = new Map();
      (line?.stations || []).forEach((station) => {
        if (!station?.name) {
          return;
        }
        if (station.stopKey) {
          names.set(station.stopKey, station.name);
        }
        if (station.id) {
          names.set(station.id, station.name);
        }
      });
      return names;
    },
    [line?.stations]
  );

  const loadDetail = useCallback(async (tripKey) => {
    if (!tripKey) {
      return null;
    }
    const response = await api.loadMonitorTripDetail({ tripKey });
    if (!response?.success) {
      setError(response?.error || "monitor-detail-load-failed");
      return null;
    }
    setDetails((current) => ({ ...current, [tripKey]: response }));
    return response;
  }, [api]);

  const loadHeaders = useCallback(async () => {
    if (!isActive || !line?.id) {
      return;
    }
    const response = await api.loadMonitorTripHeaders({
      dayOffset: dateMode === "yesterday" ? -1 : 0,
      lineId: line.id,
      startMinute: 0,
      endMinute: 1439,
      limit: 256
    });
    if (!response?.success) {
      setError(response?.error || "monitor-list-load-failed");
      return;
    }
    if (response.truncated) {
      setError("monitor-list-truncated");
      return;
    }
    const nextTrips = Array.isArray(response.trips) ? response.trips : [];
    setTrips(nextTrips);
    setError("");
  }, [dateMode, isActive, line?.id, api]);

  useEffect(() => {
    setTrips([]);
    setDetails({});
    setExpandedTripKey("");
  }, [dateMode, line?.id]);

  useEffect(() => {
    if (!isActive) {
      return;
    }
    loadHeaders().catch((loadError) => setError(loadError instanceof Error ? loadError.message : String(loadError)));
  }, [isActive, loadHeaders]);

  async function toggleTrip(tripKey) {
    if (expandedTripKey === tripKey) {
      setExpandedTripKey("");
      return;
    }
    setExpandedTripKey(tripKey);
    if (details[tripKey]) {
      return;
    }
    setDetails((current) => ({ ...current, [tripKey]: null }));
    await loadDetail(tripKey);
  }

  return (
    <div className="rtw-timetable-monitor">
      {error ? <div className="rtw-timetable-monitor-message is-error">{error === "monitor-list-truncated" ? t("timetable.monitor.rangeTooLarge") : error}</div> : null}
      <div className="rtw-timetable-monitor-table-wrap">
        <div className="rtw-timetable-table is-monitor rtw-timetable-fixed-head">
          <div className="rtw-timetable-table-head">
            <div className="is-trip">{t("timetable.monitor.head.trip")}</div>
            <div className="is-actual">{t("timetable.monitor.head.departure")}</div>
            <div className="is-terminal">{t("timetable.monitor.head.plannedEnd")}</div>
            <div className="is-terminal">{t("timetable.monitor.head.actualEnd")}</div>
            <div className="is-trip-status">{t("timetable.monitor.head.status")}</div>
            <div className="is-action">{t("timetable.table.head.action")}</div>
          </div>
        </div>
        <div className="rtw-timetable-table-scroll">
          <div className="rtw-timetable-table is-monitor">
          <div className="rtw-timetable-table-body">
            {trips.map((trip, index) => {
              const expanded = expandedTripKey === trip.tripKey;
              const detail = details[trip.tripKey];
              const custom = String(trip.scheduleType || "").toLowerCase() === "custom";
              return (
                <Fragment key={trip.tripKey}>
                  <div className={`rtw-timetable-table-row rtw-timetable-monitor-row rtw-timetable-stagger-row ${expanded ? "is-expanded" : ""}`} style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }} onClick={() => toggleTrip(trip.tripKey)}>
                    <div className="is-trip is-strong">
                      <span>{formatMinute(trip.plannedStartMinute, t)}</span>
                      <span className={`dw-demo-badge ${custom ? "is-express" : "is-local"}`}>{t(custom ? "timetable.mode.custom" : "timetable.mode.default")}</span>
                    </div>
                    <div className="is-actual is-time">{formatMinute(trip.actualStartMinute, t)}</div>
                    <div className="is-terminal is-time">{formatMinute(trip.plannedEndMinute, t)}</div>
                    <div className="is-terminal is-time">{formatMinute(trip.actualEndMinute, t)}</div>
                    <div className={`is-trip-status ${getTripStatusClass(trip)}`}>{t(getTripStatus(trip))}</div>
                    <div className="is-action"><button type="button" className={`rtw-timetable-table-action ${expanded ? "is-active" : ""}`}>{expanded ? t("timetable.action.collapse") : t("timetable.action.fullRoute")}</button></div>
                  </div>
                  {expanded ? (
                    <TripDetail detail={detail} header={trip} stationNames={stationNames} t={t} />
                  ) : null}
                </Fragment>
              );
            })}
          </div>
          </div>
        </div>
        {!error && line?.id && trips.length === 0 ? <div className="rtw-timetable-monitor-message">{t("timetable.monitor.empty")}</div> : null}
      </div>
    </div>
  );
}

function TripDetail({ detail, header, stationNames, t }) {
  if (!detail) {
    return <div className="rtw-timetable-monitor-message">{t("timetable.monitor.loading")}</div>;
  }
  const stops = Array.isArray(detail.stops) ? detail.stops : [];
  return (
    <div className="rtw-timetable-expanded-row">
      <div className="rtw-timetable-expanded-scroll">
        <div className="rtw-timetable-table is-detail rtw-timetable-content-enter">
          <div className="rtw-timetable-table-head">
            <div className="is-station">{t("timetable.table.head.station")}</div>
            <div className="is-pair">{t("timetable.monitor.head.plannedPair")}</div>
            <div className="is-pair">{t("timetable.monitor.head.actualPair")}</div>
            <div className="is-delta">{t("timetable.monitor.head.delta")}</div>
            <div className="is-status">{t("timetable.monitor.head.status")}</div>
          </div>
          <div className="rtw-timetable-table-body">
            {stops.map((stop, index) => {
              const delta = getStopDelta(stop);
              const status = getStopStatus(
                stop,
                index,
                detail.header || header,
                index === stops.length - 1);
              return (
                <div key={`${stop.stopKey}-${stop.order}`} className="rtw-timetable-table-row rtw-timetable-stagger-row" style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }}>
                  <div className="is-station"><span className="rtw-timetable-station-cell"><span className="rtw-timetable-station-spacer" />{stationNames.get(stop.stopKey) || stop.stopKey}</span></div>
                  <TimePair arrival={stop.plannedArrivalMinute} departure={stop.plannedDepartureMinute} t={t} />
                  <TimePair arrival={stop.actualArrivalMinute} departure={stop.actualDepartureMinute} t={t} />
                  <div className={`is-delta ${stop.skipped ? "is-muted" : delta > 0 ? "is-warning" : delta < 0 ? "is-accent" : "is-good"}`}>{stop.skipped ? t("timetable.monitor.skipped") : delta == null ? "--" : formatDelay(delta, t)}</div>
                  <div className={`is-status ${stop.cleared ? "is-muted" : "is-warning"}`}>{t(status)}</div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
}

function TimePair({ arrival, departure, t }) {
  return <div className="is-pair is-time"><span>{formatMinute(arrival, t)}</span><span className="rtw-timetable-time-sep">/</span><span>{formatMinute(departure, t)}</span></div>;
}

function getStopStatus(stop, index, trip, isClosing) {
  if (stop.cleared) {
    switch (String(trip?.endReason || "").toLowerCase()) {
      case "rebound": return "timetable.status.interruptedRebound";
      case "removed": return "timetable.status.interruptedRemoved";
      case "retired": return "timetable.status.interruptedRetired";
      case "relaunched": return "timetable.status.interruptedRelaunched";
      default: return "timetable.status.interrupted";
    }
  }
  if (String(trip?.state || "").toLowerCase() === "missed") {
    return "timetable.status.missed";
  }
  if (String(trip?.state || "").toLowerCase() === "completed"
    && isClosing
    && stop.actualArrivalMinute != null) {
    return "timetable.status.completed";
  }
  if (stop.actualDepartureMinute != null) {
    return "timetable.status.departed";
  }
  if (stop.actualArrivalMinute != null) {
    return "timetable.status.arrived";
  }
  return "timetable.status.awaitingArrival";
}

function getTripStatus(trip) {
  switch (String(trip?.state || "").toLowerCase()) {
    case "active": return "timetable.status.active";
    case "completed": return "timetable.status.completed";
    case "missed": return "timetable.status.missed";
    case "cleared":
      switch (String(trip?.endReason || "").toLowerCase()) {
        case "rebound": return "timetable.status.interruptedRebound";
        case "removed": return "timetable.status.interruptedRemoved";
        case "retired": return "timetable.status.interruptedRetired";
        case "relaunched": return "timetable.status.interruptedRelaunched";
        default: return "timetable.status.interrupted";
      }
    default: return "timetable.status.unknown";
  }
}

function getTripStatusClass(trip) {
  switch (String(trip?.state || "").toLowerCase()) {
    case "completed": return "is-good";
    case "active": return "is-accent";
    case "missed":
    case "cleared": return "is-warning";
    default: return "is-muted";
  }
}

function getStopDelta(stop) {
  if (stop.actualDepartureMinute != null && stop.plannedDepartureMinute != null) {
    return stop.actualDepartureMinute - stop.plannedDepartureMinute;
  }
  if (stop.actualArrivalMinute != null && stop.plannedArrivalMinute != null) {
    return stop.actualArrivalMinute - stop.plannedArrivalMinute;
  }
  return null;
}

function formatMinute(value, t) {
  return value == null || value < 0
    ? "--"
    : formatServiceMinute(value, (dayOffset) => t("timetable.time.dayOffset", { dayOffset }));
}

function formatDelay(value, t) {
  if (value === 0) {
    return t("timetable.status.onTime");
  }
  return value > 0
    ? t("timetable.status.late", { minutes: value })
    : t("timetable.status.early", { minutes: Math.abs(value) });
}
