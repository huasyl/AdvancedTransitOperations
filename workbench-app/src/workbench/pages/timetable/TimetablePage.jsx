import { useEffect, useMemo, useRef, useState } from "react";
import WorkbenchDropdown from "../../shared/WorkbenchDropdown";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import OperationMonitor from "./OperationMonitor";
import RunChart from "./RunChart";
import TimetableIcon from "./TimetableIcons";
import { minutesToTime, timeToMinutes } from "./timetable-data";
import useTimetableController from "./useTimetableController";
import { isValidTimeValue, normalizeTimeInput } from "../schedule/schedule-normalize";
import "../../../styles/timetable-page.css";

const VIEW_TRANSITION_MS = 300;

function diagnosticNow() {
  return typeof performance !== "undefined" && typeof performance.now === "function"
    ? performance.now()
    : Date.now();
}

function diagnosticMilliseconds(value) {
  return Number.isFinite(value) ? Number(value.toFixed(2)) : null;
}

function sectionRouteLabel(section, directory) {
  return section?.stations?.map((station) => directory.find((item) => item.stationId === station.stationId)?.name || station.stationId).join(" → ") || "--";
}

export default function TimetablePage({ activeTransportMode = "train", isActive = true, sharedSnapshot = null }) {
  const { t } = useNativeScheduleI18n();
  const portalHostRef = useRef(null);
  const controller = useTimetableController({ activeTransportMode, isActive, sharedSnapshot });
  const [view, setView] = useState("workspace");
  const [renderedView, setRenderedView] = useState("workspace");
  const [viewStage, setViewStage] = useState("entered");
  const [lineStates, setLineStates] = useState({});
  const [editLineId, setEditLineId] = useState("");
  const [editingTrainId, setEditingTrainId] = useState("");
  const [batchInterval, setBatchInterval] = useState("15");
  const [monitorLineId, setMonitorLineId] = useState("");
  const [dateMode, setDateMode] = useState("today");
  const [chartCollapsed, setChartCollapsed] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [arrivalSource, setArrivalSource] = useState("historical");
  const lines = controller.lines;
  const stations = controller.directory;
  const startStationId = controller.startStationId;
  const endStationId = controller.endStationId;

  useEffect(() => {
    setLineStates((current) => {
      const next = { ...current };
      lines.forEach((line) => {
        next[line.id] ||= { visible: true, dataMode: "historical" };
      });
      return next;
    });
    if (lines[0] && !lines.some((line) => line.id === editLineId)) {
      setEditLineId(lines[0].id);
    }
    if (lines[0] && !lines.some((line) => line.id === monitorLineId)) {
      setMonitorLineId(lines[0].id);
    }
  }, [editLineId, lines, monitorLineId]);

  useEffect(() => {
    if (!isActive || !editLineId || !lines.some((line) => line.id === editLineId)) {
      return;
    }
    controller.ensureTimetableLineLayout(editLineId);
    controller.ensureHistoricalRuntime(editLineId);
  }, [controller.ensureHistoricalRuntime, controller.ensureTimetableLineLayout, editLineId, isActive, lines]);

  useEffect(() => {
    if (!isActive || view !== "monitor" || !monitorLineId || !lines.some((line) => line.id === monitorLineId)) {
      return;
    }
    controller.ensureTimetableLineLayout(monitorLineId);
  }, [controller.ensureTimetableLineLayout, isActive, lines, monitorLineId, view]);

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

  const availableLines = lines;

  const editLine = availableLines.find((line) => line.id === editLineId) || availableLines[0] || { id: "", name: "--", stations: [], trains: [] };
  const monitorLine = lines.find((line) => line.id === monitorLineId) || lines[0] || { id: "", name: "--", stations: [], trains: [] };
  const activeStations = useMemo(() => controller.selectedSection?.stations?.map((station, index) => ({
    id: station.stationId,
    name: stations.find((item) => item.stationId === station.stationId)?.name || station.stationId,
    distance: index,
    occurrence: station.order ?? index
  })) || [], [controller.selectedSection, stations]);

  const chartSeries = useMemo(() => {
    const timing = controller.layoutTimingRef.current;
    const startedAt = timing?.buildMeasured && !timing.chartMeasured ? diagnosticNow() : 0;
    const activeIds = new Set(activeStations.map((station) => station.id));
    const series = availableLines.flatMap((line) => {
      const state = lineStates[line.id];
      if (!state?.visible) {
        return [];
      }
      const source = state.dataMode === "planned" && line.plannedTrains?.length > 0 ? line.plannedTrains : line.trains;
      return source.map((train) => ({
        lineId: line.id,
        trainId: train.id,
        color: line.color,
        points: train.stops
          .filter((stop) => activeIds.has(stop.stationId))
          .map((stop) => {
            const time = stop.arrivalMinute ?? stop.departureMinute;
            if (!Number.isFinite(time)) {
              return null;
            }
            const station = line.stations.find((item) => item.occurrence === stop.occurrence);
            if (!station || !Number.isFinite(station.distance)) {
              return null;
            }
            return {
              stationId: stop.stationId,
              occurrence: stop.occurrence,
              distance: station.distance,
              arrivalTime: stop.arrivalMinute ?? time,
              departureTime: stop.departureMinute ?? time
            };
          })
          .filter(Boolean)
      }));
    });
    if (timing && startedAt) {
      timing.chartSeriesMs = diagnosticNow() - startedAt;
      timing.chartMeasured = true;
    }
    return series;
  }, [activeStations, availableLines, lineStates]);

  useEffect(() => {
    const timing = controller.layoutTimingRef.current;
    if (!timing?.buildMeasured || !timing.chartMeasured || timing.reported) {
      return;
    }
    timing.reported = true;
    const message = `[RT Workbench TimetableLayout] requestId=${timing.requestId}`
      + ` lineId=${timing.lineId} success=${timing.success} error=${timing.error || "-"}`
      + ` engineCallMs=${diagnosticMilliseconds(timing.engineCallMs)}`
      + ` buildLinesMs=${diagnosticMilliseconds(timing.buildLinesMs)}`
      + ` chartSeriesMs=${diagnosticMilliseconds(timing.chartSeriesMs)}`
      + ` snapshotLines=${timing.snapshotLineCount} snapshotRows=${timing.rowCount}`
      + ` targetLineStops=${timing.targetStopCount} targetLineRows=${timing.targetRowCount}`;
    if (typeof console !== "undefined") {
      if (typeof console.debug === "function") {
        console.debug(message);
      } else if (typeof console.log === "function") {
        console.log(message);
      }
    }
    controller.layoutTimingRef.current = null;
  }, [chartSeries, controller.layoutTimingRef]);

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
    const source = controller.runtimeSources[value] || "historical";
    setArrivalSource(source === "theory" ? "theoretical" : "historical");
    controller.setRuntimeSource(value, source);
    controller.ensureTimetableLineLayout(value, true);
    controller.ensureHistoricalRuntime(value, true);
  }

  function handleBatchCustom() {
    if (editLine?.id) {
      controller.markLineCustom(editLine.id);
    }
  }

  function prepareRunTime(source = arrivalSource) {
    const runtimeSource = source === "theoretical" ? "theory" : "historical";
    const pending = controller.pendingQueries[`${editLine?.id || ""}\u001f${runtimeSource}`];
    if (!editLine?.id || pending) {
      return;
    }
    controller.requestRuntime(editLine.id, runtimeSource, false, runtimeSource === "theory");
  }

  function handleTimeChange(trainId, occurrence, value) {
    const train = editLine?.trains.find((item) => item.id === trainId);
    const stop = train?.stops.find((item) => item.occurrence === occurrence);
    if (!stop) {
      return;
    }
    const timeAnchor = stop.departureMinute ?? stop.arrivalMinute ?? train.slotMinute;
    const dayBase = Math.floor(timeAnchor / 1440) * 1440;
    let minute = dayBase + timeToMinutes(value);
    if (stop.arrivalMinute != null && minute < stop.arrivalMinute + 5) {
      minute += 1440;
    }
    controller.updateDeparture(editLine.id, trainId, occurrence, minute);
  }

  function saveEditedTrain() {
    setEditingTrainId("");
  }

  function editTrain(trainId) {
    setEditingTrainId(trainId);
  }

  function changeArrivalSource(source) {
    setArrivalSource(source);
    if (editLine?.id) {
      controller.setRuntimeSource(editLine.id, source === "theoretical" ? "theory" : "historical");
    }
  }

  const theoryState = controller.pendingQueries[`${editLine?.id || ""}\u001ftheory`]
    ? "preparing"
    : controller.runtimes[editLine?.id]?.theory
      ? "ready"
      : "idle";

  return (
    <div className="rtw-timetable-root">
      <div className={`rtw-timetable-shell ${sidebarCollapsed ? "is-sidebar-collapsed" : ""}`}>
        <aside className="rtw-timetable-sidebar">
          {renderedView === "workspace" ? (
            <div key="workspace" className={`rtw-timetable-sidebar-scene is-${viewStage}`}>
            <SidebarSection icon="map" title={t("timetable.interval.title")}>
              <WorkbenchDropdown
                label={t("timetable.interval.start")}
                value={stations.find((station) => station.stationId === startStationId)?.name || t("timetable.interval.choose")}
                options={stations.map((station) => ({ value: station.stationId, label: station.name, active: station.stationId === startStationId }))}
                onSelect={controller.setStartStationId}
                className="rtw-timetable-sidebar-field"
                positioning="portal"
                portalHostRef={portalHostRef}
              />
              <WorkbenchDropdown
                label={t("timetable.interval.end")}
                value={stations.find((station) => station.stationId === endStationId)?.name || t("timetable.interval.choose")}
                options={stations.map((station) => ({ value: station.stationId, label: station.name, active: station.stationId === endStationId }))}
                onSelect={controller.setEndStationId}
                className="rtw-timetable-sidebar-field"
                positioning="portal"
                portalHostRef={portalHostRef}
              />
              {controller.sections.length > 1 ? (
                <WorkbenchDropdown
                  label={t("timetable.interval.section")}
                  value={sectionRouteLabel(controller.selectedSection, controller.directory)}
                  options={controller.sections.map((section) => ({
                    value: section.sectionId,
                    label: sectionRouteLabel(section, controller.directory),
                    active: section.sectionId === controller.sectionId
                  }))}
                  onSelect={controller.setSectionId}
                  className="rtw-timetable-sidebar-field"
                  positioning="portal"
                  portalHostRef={portalHostRef}
                />
              ) : null}
            </SidebarSection>

            <SidebarSection icon="route" title={t("timetable.lines.title")}>
              {availableLines.length > 0 ? availableLines.map((line) => {
                const state = lineStates[line.id] || { visible: true, dataMode: "historical" };
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

        <button
          type="button"
          className="rtw-timetable-sidebar-toggle"
          title={t(sidebarCollapsed ? "timetable.sidebar.expand" : "timetable.sidebar.collapse")}
          aria-label={t(sidebarCollapsed ? "timetable.sidebar.expand" : "timetable.sidebar.collapse")}
          onClick={() => setSidebarCollapsed((current) => !current)}
        >
          <TimetableIcon name={sidebarCollapsed ? "chevron-right" : "chevron-left"} />
        </button>

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
                    <div className="rtw-timetable-current-line">{t("timetable.table.current")} <strong>{editLine.name}</strong></div>
                    <span className="rtw-timetable-count">{t("timetable.table.default", { count: editLine.trains.filter((train) => train.scheduleType !== "custom").length })}</span>
                    <span className="rtw-timetable-count is-custom">{t("timetable.table.custom", { count: editLine.trains.filter((train) => train.scheduleType === "custom").length })}</span>
                  </div>
                  {editingTrainId ? (
                    <button type="button" className="rtw-timetable-primary-button" onClick={saveEditedTrain}>{t("timetable.action.save")}</button>
                  ) : theoryState === "ready" ? (
                    <div className="rtw-timetable-arrival-source">
                      <span>{t("timetable.theory.arrivalSource")}</span>
                      <button type="button" className={arrivalSource === "theoretical" ? "is-active" : ""} onClick={() => changeArrivalSource("theoretical")}>{t("timetable.theory.theoretical")}</button>
                      <button type="button" className={arrivalSource === "historical" ? "is-active" : ""} onClick={() => changeArrivalSource("historical")}>{t("timetable.theory.historical")}</button>
                    </div>
                  ) : (
                    <button type="button" className={`rtw-timetable-theory-button ${theoryState === "preparing" ? "is-preparing" : ""}`} disabled={theoryState === "preparing"} onClick={() => prepareRunTime("theoretical")}>
                      {theoryState === "preparing" ? <span className="rtw-timetable-theory-spinner" /> : null}
                      <span>{t(theoryState === "preparing" ? "timetable.theory.preparing" : "timetable.theory.prepare")}</span>
                    </button>
                  )}
                </div>
                <TimetableEditor
                  line={editLine}
                  historicalRuntime={controller.runtimes[editLine.id]?.historical}
                  theoryRuntime={controller.runtimes[editLine.id]?.theory}
                  editingTrainId={editingTrainId}
                  onEdit={editTrain}
                  onTimeChange={handleTimeChange}
                  t={t}
                />
              </section>
              </div>
            </div>
          ) : (
            <div key="monitor" className="rtw-timetable-main-scroll">
              <div className={`rtw-timetable-view-scene is-${viewStage}`}>
                <OperationMonitor line={monitorLine} dateMode={dateMode} isActive={isActive && view === "monitor"} t={t} />
              </div>
            </div>
          )}
        </main>
      </div>

      <footer className="rtw-timetable-footer">
        <div className={`rtw-timetable-footer-status is-${controller.saveError || controller.loadError ? "error" : controller.saveState}`}>
          {controller.saveError
            ? t("timetable.footer.error")
            : controller.loadError
              ? t("timetable.footer.dataError")
              : t(`timetable.footer.${controller.saveState}`)}
        </div>
        <button
          type="button"
          className={`rtw-timetable-primary-button is-${controller.saveState}`}
          disabled={!controller.dirty || controller.saveState === "saving"}
          onClick={controller.saveAll}
        >
          {t(controller.saveState === "saving"
            ? "timetable.footer.saving"
            : controller.saveState === "applied"
              ? "timetable.footer.applied"
              : controller.saveState === "error"
                ? "timetable.footer.retry"
                : "timetable.footer.save")}
        </button>
      </footer>

      <div ref={portalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}

function SidebarSection({ icon, title, children }) {
  return <section className="rtw-timetable-sidebar-section"><div className="rtw-timetable-sidebar-title"><TimetableIcon name={icon} /><h2>{title}</h2></div>{children}</section>;
}

function TimetableEditor({ line, historicalRuntime, theoryRuntime, editingTrainId, onEdit, onTimeChange, t }) {
  const train = line.trains.find((item) => item.id === editingTrainId);
  if (train) {
    const segmentMinutes = (runtime, index) => {
      const segment = runtime?.segments?.[index];
      const value = Number.isFinite(segment?.segmentMinutesExact)
        ? segment.segmentMinutesExact
        : segment?.segmentMinutes;
      return Number.isFinite(value) ? value : null;
    };
    return (
      <div key={`edit-${train.id}`} className="rtw-timetable-table-frame">
        <div className="rtw-timetable-table is-edit rtw-timetable-fixed-head">
          <div className="rtw-timetable-table-head">
            <div className="is-section">{t("timetable.table.head.station")}</div>
            <div className="is-time">{t("timetable.table.head.arrival")}</div>
            <div className="is-time">{t("timetable.table.head.departure")}</div>
            <div className="is-next">{t("timetable.table.head.nextStation")}</div>
            <div className="is-runtime">{t("timetable.table.head.runtimePair")}</div>
          </div>
        </div>
        <div className="rtw-timetable-table-scroll">
          <div className="rtw-timetable-table is-edit rtw-timetable-content-enter">
          <div className="rtw-timetable-table-body">{train.stops.map((stop, index) => {
            const station = line.stations.find((item) => item.occurrence === stop.occurrence);
            const isLast = index === train.stops.length - 1;
            const nextStop = !isLast ? train.stops[index + 1] : null;
            const next = nextStop
              ? line.stations.find((item) => item.occurrence === nextStop.occurrence) || nextStop
              : null;
            const historicalMinutes = isLast ? null : segmentMinutes(historicalRuntime, index);
            const theoryMinutes = isLast ? null : segmentMinutes(theoryRuntime, index);
            return (
              <div key={stop.occurrence} className="rtw-timetable-table-row rtw-timetable-stop-row rtw-timetable-stagger-row" style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }}>
                <div className="is-section rtw-timetable-stop-cell">
                  <span className="rtw-timetable-stop-name">{station?.name || stop.stationName}</span>
                  {index === 0 ? <span className="rtw-timetable-stop-tag">{t("timetable.table.stop.origin")}</span> : null}
                  {isLast ? <span className="rtw-timetable-stop-tag">{t("timetable.table.stop.terminal")}</span> : null}
                </div>
                <div className="is-time is-arrival">{index === 0 ? "--" : stop.arrivalTime}</div>
                <div className="is-time is-departure">
                  {isLast ? "--" : index === 0 || !train.canEdit ? stop.departureTime : (
                    <>
                      <TimetableIcon name="clock" />
                      <TimetableTimeInput value={stop.departureMinute == null ? "" : stop.departureTime} onCommit={(value) => onTimeChange(train.id, stop.occurrence, value)} />
                    </>
                  )}
                </div>
                <div className="is-next">{next ? <><TimetableIcon name="arrow-right" /><span>{next.name || next.stationName}</span></> : "--"}</div>
                <div className="is-runtime">{formatRuntimePair(historicalMinutes, theoryMinutes, t)}</div>
              </div>
            );
          })}</div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div key="summary" className="rtw-timetable-table-scroll">
      <div className="rtw-timetable-table is-summary rtw-timetable-content-enter">
        <div className="rtw-timetable-table-head">
          <div className="is-trip">{t("timetable.table.head.trip")}</div>
          <div className="is-mode">{t("timetable.table.head.mode")}</div>
          <div className="is-action">{t("timetable.table.head.action")}</div>
        </div>
        <div className="rtw-timetable-table-body">{line.trains.map((item, index) => (
          <div key={item.id} className="rtw-timetable-table-row rtw-timetable-summary-row rtw-timetable-stagger-row" style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }} onClick={() => onEdit(item.id)}>
            <div className="is-trip is-strong">{minutesToTime(item.slotMinute)}</div>
            <div className="is-mode"><span className={`dw-demo-badge ${item.scheduleType === "custom" ? "is-express" : "is-local"}`}>{item.scheduleType === "custom" ? t("timetable.mode.custom") : t("timetable.mode.default")}</span></div>
            <div className="is-action"><button type="button" className="rtw-timetable-link" onClick={(event) => { event.stopPropagation(); onEdit(item.id); }}>{t("timetable.action.edit")}</button></div>
          </div>
        ))}</div>
      </div>
    </div>
  );
}

function formatRuntimePair(historicalMinutes, theoryMinutes, t) {
  if (historicalMinutes == null && theoryMinutes == null) {
    return "--";
  }
  const historical = historicalMinutes == null ? "--" : historicalMinutes.toFixed(1);
  const theory = theoryMinutes == null ? "--" : theoryMinutes.toFixed(1);
  return `${historical} / ${theory} ${t("timetable.unit.minutesLong")}`;
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
