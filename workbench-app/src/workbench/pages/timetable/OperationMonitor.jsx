import { Fragment, useMemo, useState } from "react";
import TimetableIcon from "./TimetableIcons";
import { minutesToTime, timeToMinutes } from "./timetable-data";

export default function OperationMonitor({ line, dateMode, t }) {
  const [expandedTrainId, setExpandedTrainId] = useState("");
  const actualData = useMemo(() => {
    if (!line) {
      return [];
    }

    return line.trains.map((train, index) => {
      const originStop = train.stops[0];
      const hash = train.id.length + dateMode.length + index;
      const delay = hash % 3 === 0 ? 0 : hash % 4 === 1 ? (hash % 10) + 2 : -(hash % 3);
      const plannedOrigin = timeToMinutes(originStop.departureTime);
      const actualOrigin = plannedOrigin + (hash % 5 === 0 ? (hash % 5) + 1 : hash % 7 === 0 ? -1 : 0);
      let runningDelay = actualOrigin - plannedOrigin;
      const stops = train.stops.map((stop, stopIndex) => {
        runningDelay += (hash + stopIndex) % 3 === 0 ? 0 : (hash + stopIndex) % 2 === 0 ? 1 : -1;
        return {
          stationId: stop.stationId,
          stationName: line.stations.find((station) => station.id === stop.stationId)?.name || "",
          plannedArrival: stop.arrivalTime,
          plannedDeparture: stop.departureTime,
          actualArrival: minutesToTime(timeToMinutes(stop.arrivalTime) + runningDelay),
          actualDeparture: minutesToTime(timeToMinutes(stop.departureTime) + Math.max(0, runningDelay)),
          delay: runningDelay,
          isOrigin: stopIndex === 0,
          isDestination: stopIndex === train.stops.length - 1
        };
      });

      return {
        trainId: train.id,
        trainName: train.name,
        delay,
        departedCorrectly: actualOrigin <= plannedOrigin + 2,
        plannedOriginDeparture: originStop.departureTime,
        actualOriginArrival: minutesToTime(actualOrigin - 10 - (hash % 5)),
        actualOriginDeparture: minutesToTime(actualOrigin),
        stops
      };
    });
  }, [dateMode, line]);

  const onTimeRate = actualData.length > 0
    ? Math.round((actualData.filter((item) => item.delay <= 2).length / actualData.length) * 100)
    : 0;
  const originRate = actualData.length > 0
    ? Math.round((actualData.filter((item) => item.departedCorrectly).length / actualData.length) * 100)
    : 0;
  const averageDelay = actualData.length > 0
    ? Math.max(0, Math.round(actualData.reduce((sum, item) => sum + Math.max(0, item.delay), 0) / actualData.length))
    : 0;

  return (
    <div className="rtw-timetable-monitor">
      <div className="rtw-timetable-metrics">
        <Metric label={t("timetable.monitor.metric.total")} value={actualData.length} />
        <Metric label={t("timetable.monitor.metric.onTime")} value={`${onTimeRate}%`} tone="good" />
        <Metric label={t("timetable.monitor.metric.origin")} value={`${originRate}%`} tone="accent" />
        <Metric label={t("timetable.monitor.metric.delay")} value={`${averageDelay} ${t("nativeSchedule.unit.minutes")}`} tone="warning" />
      </div>

      <div className="rtw-timetable-monitor-table-wrap">
        <div className="rtw-timetable-table is-monitor">
          <div className="rtw-timetable-table-head">
            <div className="is-trip">{t("timetable.monitor.head.trip")}</div>
            <div className="is-planned">{t("timetable.monitor.head.planned")}</div>
            <div className="is-actual">{t("timetable.monitor.head.arrival")}</div>
            <div className="is-actual">{t("timetable.monitor.head.departure")}</div>
            <div className="is-action">{t("timetable.table.head.action")}</div>
          </div>
          <div className="rtw-timetable-table-body">
            {actualData.map((item, index) => {
              const expanded = expandedTrainId === item.trainId;
              return (
                <Fragment key={item.trainId}>
                  <div className={`rtw-timetable-table-row rtw-timetable-monitor-row rtw-timetable-stagger-row ${expanded ? "is-expanded" : ""}`} style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }} onClick={() => setExpandedTrainId(expanded ? "" : item.trainId)}>
                    <div className="is-trip is-strong">{item.trainName}</div>
                    <div className="is-planned is-time">{item.plannedOriginDeparture}</div>
                    <div className="is-actual is-time">{item.actualOriginArrival}</div>
                    <div className="is-actual is-time">{item.actualOriginDeparture}</div>
                    <div className="is-action"><button type="button" className={`rtw-timetable-table-action ${expanded ? "is-active" : ""}`}>{expanded ? t("timetable.action.collapse") : t("timetable.action.fullRoute")}</button></div>
                  </div>
                  {expanded ? (
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
                              {item.stops.map((stop, index) => (
                                <div key={stop.stationId} className="rtw-timetable-table-row rtw-timetable-stagger-row" style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }}>
                                  <div className="is-station"><span className="rtw-timetable-station-cell">{index < item.stops.length - 1 ? <TimetableIcon name="arrow-down" /> : <span className="rtw-timetable-station-end" />}{stop.stationName}</span></div>
                                  <div className="is-pair is-time"><span>{stop.isOrigin ? "--" : stop.plannedArrival}</span><span className="rtw-timetable-time-sep">/</span><span>{stop.isDestination ? "--" : stop.plannedDeparture}</span></div>
                                  <div className="is-pair is-time"><span>{stop.isOrigin ? "--" : stop.actualArrival}</span><span className="rtw-timetable-time-sep">/</span><span>{stop.isDestination ? "--" : stop.actualDeparture}</span></div>
                                  <div className={`is-delta ${stop.delay > 0 ? "is-warning" : stop.delay < 0 ? "is-accent" : "is-good"}`}>{formatDelay(stop.delay, t)}</div>
                                  <div className={`is-status ${stop.delay > 2 ? "is-warning" : "is-muted"}`}>{stop.delay > 2 ? t("timetable.status.abnormal") : t("timetable.status.normal")}</div>
                                </div>
                              ))}
                          </div>
                        </div>
                      </div>
                    </div>
                  ) : null}
                </Fragment>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
}

function Metric({ label, value, tone = "" }) {
  return <div className="rtw-timetable-metric"><div className="rtw-timetable-metric-label">{label}</div><div className={`rtw-timetable-metric-value ${tone ? `is-${tone}` : ""}`}>{value}</div></div>;
}

function formatDelay(value, t) {
  if (value === 0) {
    return t("timetable.status.onTime");
  }
  return value > 0
    ? t("timetable.status.late", { minutes: value })
    : t("timetable.status.early", { minutes: Math.abs(value) });
}
