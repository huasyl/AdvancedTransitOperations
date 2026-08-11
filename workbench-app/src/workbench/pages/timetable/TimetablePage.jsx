import { useEffect, useMemo, useRef, useState } from "react";
import WorkbenchDropdown from "../../shared/WorkbenchDropdown";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import OperationMonitor from "./OperationMonitor";
import RunChart from "./RunChart";
import TimetableIcon from "./TimetableIcons";
import { INITIAL_TIMETABLE_LINES, minutesToTime, timeToMinutes } from "./timetable-data";
import { isValidTimeValue, normalizeTimeInput } from "../schedule/schedule-normalize";
import "../../../styles/timetable-page.css";

const INITIAL_LINE_STATES = {
  "line-1": { visible: true, dataMode: "historical" },
  "line-2": { visible: true, dataMode: "planned" },
  "line-3": { visible: false, dataMode: "historical" }
};

const SAMPLE_SECTION_MINUTES = {
  "line-1": [null, 13.5, 19.8, 22.1, 15]
};

const VIEW_TRANSITION_MS = 300;

export default function TimetablePage() {
  const { t } = useNativeScheduleI18n();
  const portalHostRef = useRef(null);
  const [view, setView] = useState("workspace");
  const [renderedView, setRenderedView] = useState("workspace");
  const [viewStage, setViewStage] = useState("entered");
  const [lines, setLines] = useState(INITIAL_TIMETABLE_LINES);
  const [lineStates, setLineStates] = useState(INITIAL_LINE_STATES);
  const [startStationId, setStartStationId] = useState("s1");
  const [endStationId, setEndStationId] = useState("s5");
  const [editLineId, setEditLineId] = useState("line-1");
  const [editingTrainId, setEditingTrainId] = useState("");
  const [batchInterval, setBatchInterval] = useState("15");
  const [monitorLineId, setMonitorLineId] = useState("line-1");
  const [dateMode, setDateMode] = useState("today");
  const [chartCollapsed, setChartCollapsed] = useState(false);
  const [theoryState, setTheoryState] = useState("idle");
  const [arrivalSource, setArrivalSource] = useState("theoretical");
  const theoryTimerRef = useRef(null);

  useEffect(() => () => {
    if (theoryTimerRef.current) {
      window.clearTimeout(theoryTimerRef.current);
    }
  }, []);

  useEffect(() => {
    if (view === renderedView) {
      return undefined;
    }

    setViewStage("exiting");
    const timer = window.setTimeout(() => {
      setRenderedView(view);
      setViewStage("entering");
      window.requestAnimationFrame(() => {
        window.requestAnimationFrame(() => setViewStage("entered"));
      });
    }, VIEW_TRANSITION_MS);

    return () => window.clearTimeout(timer);
  }, [renderedView, view]);

  const stations = useMemo(() => {
    const unique = new Map();
    lines.forEach((line) => line.stations.forEach((station) => unique.set(station.id, station)));
    return [...unique.values()];
  }, [lines]);

  const availableLines = useMemo(() => {
    if (!startStationId || !endStationId) {
      return [];
    }
    return lines.filter((line) => line.stations.some((station) => station.id === startStationId)
      && line.stations.some((station) => station.id === endStationId));
  }, [endStationId, lines, startStationId]);

  const editLine = availableLines.find((line) => line.id === editLineId) || availableLines[0] || lines[0];
  const monitorLine = lines.find((line) => line.id === monitorLineId) || lines[0];
  const activeStations = useMemo(() => {
    const unique = new Map();
    availableLines.forEach((line) => {
      if (!lineStates[line.id]?.visible) {
        return;
      }
      const startIndex = line.stations.findIndex((station) => station.id === startStationId);
      const endIndex = line.stations.findIndex((station) => station.id === endStationId);
      const from = Math.min(startIndex, endIndex);
      const to = Math.max(startIndex, endIndex);
      line.stations.slice(from, to + 1).forEach((station) => unique.set(station.id, station));
    });
    return [...unique.values()].sort((left, right) => left.distance - right.distance);
  }, [availableLines, endStationId, lineStates, startStationId]);

  const chartSeries = useMemo(() => {
    const activeIds = new Set(activeStations.map((station) => station.id));
    return availableLines.flatMap((line) => {
      const state = lineStates[line.id];
      if (!state?.visible) {
        return [];
      }
      const source = state.dataMode === "planned" && line.plannedTrains?.length > 0 ? line.plannedTrains : line.trains;
      return source.map((train) => ({
        lineId: line.id,
        trainId: train.id,
        color: line.color,
        points: train.stops.filter((stop) => activeIds.has(stop.stationId)).map((stop) => {
          const station = line.stations.find((item) => item.id === stop.stationId);
          return {
            stationId: stop.stationId,
            distance: station?.distance || 0,
            arrivalTime: timeToMinutes(stop.arrivalTime),
            departureTime: timeToMinutes(stop.departureTime),
            arrivalLabel: stop.arrivalTime
          };
        })
      }));
    });
  }, [activeStations, availableLines, lineStates]);

  function updateLineState(lineId, update) {
    setLineStates((current) => ({ ...current, [lineId]: { ...current[lineId], ...update } }));
  }

  function changeView(nextView) {
    if (viewStage !== "entered" || nextView === view) {
      return;
    }
    setView(nextView);
  }

  function handleEditLine(value) {
    setEditLineId(value);
    setEditingTrainId("");
  }

  function handleBatchCustom() {
    setLines((current) => current.map((line) => line.id === editLine.id
      ? { ...line, trains: line.trains.map((train) => ({ ...train, scheduleType: "custom" })) }
      : line));
  }

  function prepareTheoryData() {
    if (theoryState !== "idle") {
      return;
    }

    setTheoryState("preparing");
    theoryTimerRef.current = window.setTimeout(() => {
      setTheoryState("ready");
      theoryTimerRef.current = null;
    }, 1200);
  }

  function handleTimeChange(trainId, stationId, value) {
    setLines((current) => current.map((line) => {
      if (line.id !== editLine.id) {
        return line;
      }
      return {
        ...line,
        trains: line.trains.map((train) => {
          if (train.id !== trainId) {
            return train;
          }
          const stopIndex = train.stops.findIndex((stop) => stop.stationId === stationId);
          const delta = stopIndex >= 0 ? timeToMinutes(value) - timeToMinutes(train.stops[stopIndex].departureTime) : 0;
          return {
            ...train,
            stops: train.stops.map((stop, index) => index < stopIndex ? stop : {
              ...stop,
              arrivalTime: index === stopIndex ? stop.arrivalTime : minutesToTime(timeToMinutes(stop.arrivalTime) + delta),
              departureTime: index === stopIndex ? value : minutesToTime(timeToMinutes(stop.departureTime) + delta)
            })
          };
        })
      };
    }));
  }

  function saveEditedTrain() {
    setLines((current) => current.map((line) => line.id === editLine.id
      ? { ...line, trains: line.trains.map((train) => train.id === editingTrainId ? { ...train, scheduleType: "custom" } : train) }
      : line));
    setEditingTrainId("");
  }

  return (
    <div className="rtw-timetable-root">
      <div className="rtw-timetable-shell">
        <aside className="rtw-timetable-sidebar">
          {renderedView === "workspace" ? (
            <div key="workspace" className={`rtw-timetable-sidebar-scene is-${viewStage}`}>
            <SidebarSection icon="map" title={t("timetable.interval.title")}>
              <WorkbenchDropdown
                label={t("timetable.interval.start")}
                value={stations.find((station) => station.id === startStationId)?.name || t("timetable.interval.choose")}
                options={stations.map((station) => ({ value: station.id, label: station.name, active: station.id === startStationId }))}
                onSelect={setStartStationId}
                className="rtw-timetable-sidebar-field"
                positioning="portal"
                portalHostRef={portalHostRef}
              />
              <WorkbenchDropdown
                label={t("timetable.interval.end")}
                value={stations.find((station) => station.id === endStationId)?.name || t("timetable.interval.choose")}
                options={stations.map((station) => ({ value: station.id, label: station.name, active: station.id === endStationId }))}
                onSelect={setEndStationId}
                className="rtw-timetable-sidebar-field"
                positioning="portal"
                portalHostRef={portalHostRef}
              />
            </SidebarSection>

            <SidebarSection icon="route" title={t("timetable.lines.title")}>
              {availableLines.length > 0 ? availableLines.map((line) => {
                const state = lineStates[line.id];
                return (
                  <div key={line.id} className={`rtw-timetable-line-item ${editLine.id === line.id ? "is-editing" : ""}`} onClick={() => handleEditLine(line.id)}>
                    <div className="rtw-timetable-line-row">
                      <button type="button" className={`rtw-timetable-check ${state.visible ? "is-checked" : ""}`} style={{ backgroundColor: state.visible ? line.color : "transparent", borderColor: state.visible ? line.color : "rgba(255,255,255,0.30)" }} onClick={(event) => { event.stopPropagation(); updateLineState(line.id, { visible: !state.visible }); }}>{state.visible ? <TimetableIcon name="check" /> : null}</button>
                      <span className="rtw-timetable-line-dot" style={{ backgroundColor: line.color }} />
                      <span className="rtw-timetable-line-name">{line.name}</span>
                    </div>
                    <WorkbenchDropdown
                      value={t(`timetable.data.${state.dataMode}`)}
                      options={["historical", "planned"].map((mode) => ({ value: mode, label: t(`timetable.data.${mode}.full`), active: state.dataMode === mode }))}
                      onSelect={(mode) => updateLineState(line.id, { dataMode: mode })}
                      className="rtw-timetable-line-mode"
                      positioning="portal"
                      portalHostRef={portalHostRef}
                    />
                  </div>
                );
              }) : <div className="rtw-timetable-sidebar-empty">{t("timetable.lines.empty")}</div>}
            </SidebarSection>

            {availableLines.length > 0 ? (
              <SidebarSection icon="sliders" title={t("timetable.edit.title")}>
                <WorkbenchDropdown
                  label={t("timetable.edit.line")}
                  value={editLine.name}
                  options={availableLines.map((line) => ({ value: line.id, label: line.name, active: line.id === editLine.id }))}
                  onSelect={handleEditLine}
                  className="rtw-timetable-sidebar-field"
                  positioning="portal"
                  portalHostRef={portalHostRef}
                />
                <WorkbenchDropdown
                  label={t("timetable.edit.rule")}
                  value={t(`timetable.edit.interval.${batchInterval}`)}
                  options={["10", "15", "20", "30"].map((value) => ({ value, label: t(`timetable.edit.interval.${value}`), active: batchInterval === value }))}
                  onSelect={setBatchInterval}
                  className="rtw-timetable-sidebar-field"
                  positioning="portal"
                  portalHostRef={portalHostRef}
                />
                <button type="button" className="rtw-timetable-secondary-button" onClick={handleBatchCustom}>{t("timetable.edit.batch")}</button>
              </SidebarSection>
            ) : null}

            <div className="rtw-timetable-legend">
              <div className="rtw-timetable-legend-title">{t("timetable.legend")}</div>
              {availableLines.filter((line) => lineStates[line.id]?.visible).map((line) => <div key={`legend-${line.id}`} className="rtw-timetable-legend-row"><span style={{ backgroundColor: line.color }} /><span>{line.name} · {t(`timetable.data.${lineStates[line.id].dataMode}`)}</span></div>)}
            </div>
            </div>
          ) : (
            <div key="monitor" className={`rtw-timetable-sidebar-scene is-${viewStage}`}>
              <SidebarSection icon="sliders" title={t("timetable.monitor.filter.title")}>
              <WorkbenchDropdown
                label={t("timetable.monitor.filter.date")}
                value={t(`timetable.monitor.date.${dateMode}`)}
                options={["today", "yesterday"].map((value) => ({ value, label: t(`timetable.monitor.date.${value}`), active: dateMode === value }))}
                onSelect={setDateMode}
                className="rtw-timetable-sidebar-field"
                positioning="portal"
                portalHostRef={portalHostRef}
              />
              <WorkbenchDropdown
                label={t("timetable.monitor.filter.line")}
                value={monitorLine.name}
                options={lines.map((line) => ({ value: line.id, label: line.name, active: monitorLineId === line.id }))}
                onSelect={setMonitorLineId}
                className="rtw-timetable-sidebar-field"
                positioning="portal"
                portalHostRef={portalHostRef}
              />
              </SidebarSection>
            </div>
          )}
        </aside>

        <main className="rtw-timetable-main">
          <div className="rtw-timetable-main-header">
            <div className="rtw-timetable-view-tabs">
              <button type="button" className={`rtw-timetable-view-tab ${view === "workspace" ? "is-active" : ""}`} onClick={() => changeView("workspace")}>{t("timetable.view.workspace")}</button>
              <button type="button" className={`rtw-timetable-view-tab ${view === "monitor" ? "is-active" : ""}`} onClick={() => changeView("monitor")}>{t("timetable.view.monitor")}</button>
            </div>
          </div>

          {renderedView === "workspace" ? (
            <div key="workspace" className="rtw-timetable-main-scroll">
              <div className={`rtw-timetable-view-scene is-${viewStage}`}>
              <section className={`rtw-timetable-chart-section ${chartCollapsed ? "is-collapsed" : ""}`}>
                <div className="rtw-timetable-chart-head">
                  <div className="rtw-timetable-panel-title"><TimetableIcon name="chart" /><span>{t("timetable.chart.title")}</span></div>
                  <button type="button" className="rtw-timetable-chart-toggle" title={t(chartCollapsed ? "timetable.chart.expand" : "timetable.chart.collapse")} onClick={() => setChartCollapsed((current) => !current)}>
                    <TimetableIcon name={chartCollapsed ? "chevron-down" : "chevron-up"} />
                  </button>
                </div>
                {!chartCollapsed ? <RunChart stations={activeStations} series={chartSeries} emptyText={t("timetable.chart.empty")} /> : null}
              </section>
              <section className="rtw-timetable-schedule-section">
                <div className="rtw-timetable-schedule-head">
                  <div className="rtw-timetable-schedule-copy">
                    <div className="rtw-timetable-panel-title"><TimetableIcon name="calendar" /><span>{t("timetable.table.title")}</span></div>
                    <div className="rtw-timetable-current-line">{t("timetable.table.current")} <strong>{editLine.name}</strong>{editingTrainId ? ` / ${editLine.trains.find((train) => train.id === editingTrainId)?.name || ""}` : ""}</div>
                    <span className="rtw-timetable-count">{t("timetable.table.default", { count: editLine.trains.filter((train) => train.scheduleType !== "custom").length })}</span>
                    <span className="rtw-timetable-count is-custom">{t("timetable.table.custom", { count: editLine.trains.filter((train) => train.scheduleType === "custom").length })}</span>
                  </div>
                  {editingTrainId ? (
                    <button type="button" className="rtw-timetable-primary-button" onClick={saveEditedTrain}>{t("timetable.action.save")}</button>
                  ) : theoryState === "ready" ? (
                    <div className="rtw-timetable-arrival-source">
                      <span>{t("timetable.theory.arrivalSource")}</span>
                      <button type="button" className={arrivalSource === "theoretical" ? "is-active" : ""} onClick={() => setArrivalSource("theoretical")}>{t("timetable.theory.theoretical")}</button>
                      <button type="button" className={arrivalSource === "historical" ? "is-active" : ""} onClick={() => setArrivalSource("historical")}>{t("timetable.theory.historical")}</button>
                    </div>
                  ) : (
                    <button type="button" className={`rtw-timetable-theory-button ${theoryState === "preparing" ? "is-preparing" : ""}`} disabled={theoryState === "preparing"} onClick={prepareTheoryData}>
                      {theoryState === "preparing" ? <span className="rtw-timetable-theory-spinner" /> : null}
                      <span>{t(theoryState === "preparing" ? "timetable.theory.preparing" : "timetable.theory.prepare")}</span>
                    </button>
                  )}
                </div>
                <TimetableEditor line={editLine} editingTrainId={editingTrainId} onEdit={setEditingTrainId} onTimeChange={handleTimeChange} t={t} />
              </section>
              </div>
            </div>
          ) : (
            <div key="monitor" className="rtw-timetable-main-scroll">
              <div className={`rtw-timetable-view-scene is-${viewStage}`}>
                <OperationMonitor line={monitorLine} dateMode={dateMode} t={t} />
              </div>
            </div>
          )}
        </main>
      </div>

      <div ref={portalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}

function SidebarSection({ icon, title, children }) {
  return <section className="rtw-timetable-sidebar-section"><div className="rtw-timetable-sidebar-title"><TimetableIcon name={icon} /><h2>{title}</h2></div>{children}</section>;
}

function TimetableEditor({ line, editingTrainId, onEdit, onTimeChange, t }) {
  const train = line.trains.find((item) => item.id === editingTrainId);
  if (train) {
    return (
      <div key={`edit-${train.id}`} className="rtw-timetable-table-scroll">
        <div className="rtw-timetable-table is-edit rtw-timetable-content-enter">
          <div className="rtw-timetable-table-head">
            <div className="is-section">{t("timetable.table.head.section")}</div>
            <div className="is-time">{t("timetable.table.head.arrival")}</div>
            <div className="is-time">{t("timetable.table.head.departure")}</div>
            <div className="is-duration">{t("timetable.table.head.duration")}</div>
          </div>
          <div className="rtw-timetable-table-body">{train.stops.map((stop, index) => {
            const station = line.stations.find((item) => item.id === stop.stationId);
            const previous = index > 0 ? line.stations.find((item) => item.id === train.stops[index - 1].stationId) : null;
            const computedMinutes = index > 0 ? timeToMinutes(stop.arrivalTime) - timeToMinutes(train.stops[index - 1].departureTime) : 0;
            const duration = SAMPLE_SECTION_MINUTES[line.id]?.[index] ?? computedMinutes;
            return (
              <div key={stop.stationId} className="rtw-timetable-table-row rtw-timetable-stagger-row" style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }}>
                <div className="is-section">
                  {index === 0 ? (
                    <span className="rtw-timetable-origin-name">{t("timetable.table.originPrefix")} {station?.name}</span>
                  ) : (
                    <span className="rtw-timetable-section-route">
                      <span className="rtw-timetable-section-from">{previous?.name}</span>
                      <TimetableIcon name="arrow-right" />
                      <span className="rtw-timetable-section-to">{station?.name}</span>
                    </span>
                  )}
                </div>
                <div className="is-time is-arrival">{index === 0 ? "--" : stop.arrivalTime}</div>
                <div className="is-time is-departure">
                  <TimetableIcon name="clock" />
                  <TimetableTimeInput value={stop.departureTime} onCommit={(value) => onTimeChange(train.id, stop.stationId, value)} />
                </div>
                <div className="is-duration is-muted">{index === 0 ? "--" : `${duration} ${t("timetable.unit.minutesLong")}`}</div>
              </div>
            );
          })}</div>
        </div>
      </div>
    );
  }

  return (
    <div key="summary" className="rtw-timetable-table-scroll">
      <div className="rtw-timetable-table is-summary rtw-timetable-content-enter">
        <div className="rtw-timetable-table-head">
          <div className="is-trip">{t("timetable.table.head.trip")}</div>
          <div className="is-time">{t("timetable.table.head.departure")}</div>
          <div className="is-time">{t("timetable.table.head.arrival")}</div>
          <div className="is-mode">{t("timetable.table.head.mode")}</div>
          <div className="is-action">{t("timetable.table.head.action")}</div>
        </div>
        <div className="rtw-timetable-table-body">{line.trains.map((item, index) => (
          <div key={item.id} className="rtw-timetable-table-row rtw-timetable-summary-row rtw-timetable-stagger-row" style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }} onClick={() => onEdit(item.id)}>
            <div className="is-trip is-strong">{item.name}</div>
            <div className="is-time">{item.stops[0]?.departureTime}</div>
            <div className="is-time">{item.stops[item.stops.length - 1]?.arrivalTime}</div>
            <div className="is-mode"><span className={`dw-demo-badge ${item.scheduleType === "custom" ? "is-express" : "is-local"}`}>{item.scheduleType === "custom" ? t("timetable.mode.custom") : t("timetable.mode.default")}</span></div>
            <div className="is-action"><button type="button" className="rtw-timetable-link" onClick={(event) => { event.stopPropagation(); onEdit(item.id); }}>{t("timetable.action.edit")}</button></div>
          </div>
        ))}</div>
      </div>
    </div>
  );
}

function TimetableTimeInput({ value, onCommit }) {
  const [draftValue, setDraftValue] = useState(String(value ?? ""));

  useEffect(() => {
    setDraftValue(String(value ?? ""));
  }, [value]);

  function commitCurrentValue() {
    const nextValue = normalizeTimeInput(draftValue);
    if (!isValidTimeValue(nextValue)) {
      setDraftValue(String(value ?? ""));
      return;
    }

    setDraftValue(nextValue);
    onCommit(nextValue);
  }

  return (
    <input
      type="text"
      className="dw-demo-input rtw-timetable-time-input"
      inputMode="numeric"
      maxLength={5}
      value={draftValue}
      onChange={(event) => setDraftValue(normalizeTimeInput(event.currentTarget.value))}
      onPaste={(event) => {
        event.preventDefault();
        setDraftValue(normalizeTimeInput(event.clipboardData?.getData("text") || ""));
      }}
      onBlur={commitCurrentValue}
      onKeyDown={(event) => {
        const allowedKeys = ["Backspace", "Delete", "Tab", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "Enter"];
        const isDigitKey = event.key >= "0" && event.key <= "9";
        const isCtrlCommand = event.ctrlKey || event.metaKey;
        if (!isDigitKey && !allowedKeys.includes(event.key) && !isCtrlCommand) {
          event.preventDefault();
          return;
        }

        if (event.key === "Enter") {
          commitCurrentValue();
          event.currentTarget.blur();
        }
      }}
    />
  );
}
