import { useEffect, useMemo, useRef, useState } from "react";
import WorkbenchDropdown from "../../shared/WorkbenchDropdown";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import OperationMonitor from "./OperationMonitor";
import RunChart from "./RunChart";
import TimetableIcon from "./TimetableIcons";
import { formatServiceMinute, minutesToTime, serviceDayOffset, timeToMinutes } from "./timetable-data";
import useTimetableController from "./useTimetableController";
import { DemoTextField } from "../schedule/components/ScheduleFields";
import { isValidTimeValue } from "../schedule/schedule-normalize";
import "../../../styles/timetable-page.css";

const VIEW_TRANSITION_MS = 300;

function inputKey(lineId, trainId, occurrence) {
  return `${lineId}\u001f${trainId}\u001f${occurrence}`;
}

function formatServiceTime(value, t) {
  return formatServiceMinute(value, (dayOffset) => t("timetable.time.dayOffset", { dayOffset }));
}

function formatDayHint(value, t) {
  const dayOffset = serviceDayOffset(value);
  return dayOffset > 0 ? t("timetable.time.dayHint", { dayOffset }) : "";
}

function renderArrivalTime(value, t) {
  if (!Number.isFinite(value)) {
    return "--";
  }
  const dayOffset = serviceDayOffset(value);
  return (
    <span className="rtw-timetable-service-time">
      <span>{minutesToTime(value)}</span>
      {dayOffset > 0 ? <span className="rtw-timetable-day-mark">{t("timetable.time.dayHint", { dayOffset })}</span> : null}
    </span>
  );
}

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

function lineCoverages(section, lineId) {
  return (section?.coverages || []).filter((coverage) => coverage?.lineId === lineId);
}

function lineSources(state, multiple) {
  if (multiple) {
    return Array.isArray(state?.dataModes) ? state.dataModes : [];
  }
  return state?.dataMode ? [state.dataMode] : [];
}

function coverageStops(coverage) {
  return [coverage?.leadingStop, ...(coverage?.stops || []), coverage?.trailingStop]
    .filter((stop) => Number.isFinite(stop?.waypointIndex) && Number.isFinite(stop?.sectionIndex))
    .sort((left, right) => left.sectionIndex - right.sectionIndex);
}

function monitorCoverageFilter(coverages) {
  return {
    coverages: (coverages || []).map((coverage) => ({
      fromSectionIndex: coverage.fromSectionIndex,
      toSectionIndex: coverage.toSectionIndex,
      points: [coverage.leadingStop, ...(coverage.stops || []), coverage.trailingStop]
        .filter((point) => point?.stationId && Number.isFinite(point.waypointIndex) && Number.isFinite(point.sectionIndex))
        .sort((left, right) => left.sectionIndex - right.sectionIndex)
    }))
  };
}

function actualPoints(detail, coveragePoints) {
  const points = [];
  const stops = detail?.stops || [];
  if (stops.length === 0) {
    return points;
  }
  const origin = stops[0];
  let terminalArrival = Number.isFinite(origin?.actualArrivalMinute)
    ? origin.actualArrivalMinute
    : null;
  let terminalPointAdded = false;
  let completePrefix = true;
  for (let index = 0; index < stops.length; index++) {
    const stop = stops[index];
    const firstOrigin = index === 0;
    const terminal = index > 0
      && stop?.stopKey === origin?.stopKey
      && stop?.waypointIndex === origin?.waypointIndex;
    if ((firstOrigin && !Number.isFinite(stop?.actualDepartureMinute))
      || (!firstOrigin && !Number.isFinite(stop?.actualArrivalMinute))) {
      completePrefix = false;
      break;
    }
    if (terminal && Number.isFinite(stop?.actualArrivalMinute)) {
      terminalArrival = stop.actualArrivalMinute;
    }
    const coverageStop = coveragePoints.find((point) => point?.stationId === stop.stopKey
      && point.waypointIndex === stop.waypointIndex);
    if (coverageStop?.stationId === stop.stopKey) {
      points.push({
        stationId: stop.stopKey,
        pointKey: Number.isFinite(stop.order) ? stop.order : index,
        distance: coverageStop.sectionIndex,
        arrivalTime: firstOrigin ? null : stop.actualArrivalMinute,
        departureTime: terminal ? null : stop.actualDepartureMinute
      });
      terminalPointAdded = terminalPointAdded || terminal;
    }
    if (index + 1 < stops.length
      && !terminal
      && !Number.isFinite(stop?.actualDepartureMinute)) {
      completePrefix = false;
      break;
    }
  }
  if (completePrefix && !terminalPointAdded && Number.isFinite(terminalArrival)) {
    const coverageStop = coveragePoints.find((point) => point?.stationId === origin?.stopKey
      && point.waypointIndex === origin?.waypointIndex);
    if (coverageStop?.stationId === origin?.stopKey) {
      points.push({
        stationId: origin.stopKey,
        pointKey: stops.length,
        distance: coverageStop.sectionIndex,
        arrivalTime: terminalArrival,
        departureTime: null
      });
    }
  }
  return points;
}

function projectTimedPoints(stops, coveragePoints) {
  return (stops || []).map((stop) => {
    const coverageStop = coveragePoints.find((point) => point?.stationId === stop.stationId
      && point.waypointIndex === stop.waypointIndex);
    if (!coverageStop) {
      return null;
    }
    return {
      stationId: stop.stationId,
      occurrence: stop.occurrence,
      distance: coverageStop.sectionIndex,
      arrivalTime: stop.arrivalMinute,
      departureTime: stop.departureMinute
    };
  }).filter(Boolean);
}

function splitRunChartPoints(points) {
  const segments = [];
  let current = [];
  let previousDistance = null;
  (points || []).forEach((point) => {
    if (current.length > 0
      && Number.isFinite(previousDistance)
      && Number.isFinite(point?.distance)
      && point.distance < previousDistance) {
      segments.push(current);
      current = [];
    }
    current.push(point);
    previousDistance = point?.distance;
  });
  if (current.length > 0) {
    segments.push(current);
  }
  return segments;
}

function trainOverlapsRange(train, startMinute, endMinute) {
  let firstMinute = Infinity;
  let lastMinute = -Infinity;
  (train?.stops || []).forEach((stop) => {
    if (Number.isFinite(stop?.arrivalMinute)) {
      firstMinute = Math.min(firstMinute, stop.arrivalMinute);
      lastMinute = Math.max(lastMinute, stop.arrivalMinute);
    }
    if (Number.isFinite(stop?.departureMinute)) {
      firstMinute = Math.min(firstMinute, stop.departureMinute);
      lastMinute = Math.max(lastMinute, stop.departureMinute);
    }
  });
  return lastMinute >= startMinute && firstMinute <= endMinute;
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
  const [batchRequest, setBatchRequest] = useState(null);
  const [monitorLineId, setMonitorLineId] = useState("");
  const [dateMode, setDateMode] = useState("today");
  const [chartCollapsed, setChartCollapsed] = useState(false);
  const [intervalCollapsed, setIntervalCollapsed] = useState(false);
  const [linesCollapsed, setLinesCollapsed] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [chartStart, setChartStart] = useState("");
  const [chartEnd, setChartEnd] = useState("");
  const [arrivalSource, setArrivalSource] = useState("theory");
  const [timeDrafts, setTimeDrafts] = useState({});
  const pendingLocateRef = useRef(null);
  const clearLineTimersRef = useRef([]);
  const [lineClearStage, setLineClearStage] = useState("idle");
  const [lineClearSeconds, setLineClearSeconds] = useState(0);
  const lines = controller.lines;
  const monitorLines = controller.lineDirectory;
  const stations = controller.directory;
  const endStations = controller.endStations;
  const startStationId = controller.startStationId;
  const endStationId = controller.endStationId;
  const editLineExists = Boolean(editLineId && lines.some((line) => line.id === editLineId));
  const monitorLineExists = Boolean(monitorLineId && monitorLines.some((line) => line.id === monitorLineId));
  const editingErrorPrefix = editingTrainId ? inputKey(editLineId, editingTrainId, "") : "";
  const hasEditingErrors = Boolean(editingErrorPrefix
    && Object.keys(controller.inputErrors).some((key) => key.startsWith(editingErrorPrefix)));
  const shortDwellError = useMemo(() => {
    for (const [key, value] of Object.entries(controller.inputErrors)) {
      if (value !== "dwell") {
        continue;
      }
      const [lineId, trainId, occurrence] = key.split("\u001f");
      const line = lines.find((item) => item.id === lineId);
      const train = line?.trains.find((item) => item.id === trainId);
      if (line && train) {
        return {
          lineId,
          trainId,
          occurrence,
          time: formatServiceTime(train.slotMinute, t)
        };
      }
    }
    return null;
  }, [controller.inputErrors, lines, t]);
  const footerButtonState = editingTrainId
    ? "editing"
    : shortDwellError
      ? "conflict"
    : ["saving", "applied", "error"].includes(controller.saveState)
      ? controller.saveState
      : controller.canSave ? "ready" : "disabled";
  const footerIconColor = footerButtonState === "saving"
    ? "#d3f6fb"
    : footerButtonState === "applied"
      ? "#8fd3a0"
      : footerButtonState === "error"
        ? "#dfaaaa"
        : footerButtonState === "conflict"
          ? "#dfaaaa"
        : footerButtonState === "editing" || footerButtonState === "disabled"
          ? "#71808a"
          : "#0a1014";
  const showFooterControlNote = !editingTrainId
    && !controller.saveError
    && !controller.loadError
    && ["clean", "applied"].includes(controller.saveState);

  useEffect(() => {
    clearLineTimersRef.current.forEach((timer) => window.clearTimeout(timer));
    clearLineTimersRef.current = [];
    setLineClearStage("idle");
    setLineClearSeconds(0);
    pendingLocateRef.current = null;
    setEditingTrainId("");
    setTimeDrafts({});
    controller.clearInputErrors();
  }, [activeTransportMode, controller.clearInputErrors]);

  useEffect(() => {
    clearLineTimersRef.current.forEach((timer) => window.clearTimeout(timer));
    clearLineTimersRef.current = [];
    setLineClearStage("idle");
    setLineClearSeconds(0);
    const pendingLocate = pendingLocateRef.current;
    pendingLocateRef.current = null;
    const isPendingLocate = pendingLocate?.lineId === editLineId;
    setEditingTrainId(isPendingLocate ? pendingLocate.trainId : "");
    setTimeDrafts({});
    controller.clearInputErrors("", "", true);
  }, [controller.clearInputErrors, editLineId]);

  useEffect(() => () => {
    clearLineTimersRef.current.forEach((timer) => window.clearTimeout(timer));
  }, []);

  useEffect(() => {
    setLineStates((current) => {
      const next = { ...current };
      let changed = false;
      lines.forEach((line) => {
        if (!next[line.id]) {
          next[line.id] = { visible: true, dataMode: "", dataModes: [] };
          changed = true;
        }
      });
      return changed ? next : current;
    });
    if (editLineId && !editLineExists) setEditLineId("");
    if (monitorLineId && !monitorLineExists) setMonitorLineId("");
  }, [editLineExists, editLineId, lines, monitorLineExists, monitorLineId]);

  useEffect(() => {
    setArrivalSource(controller.runtimeSources[editLineId]
      || (activeTransportMode === "bus" ? "busHistorical" : "theory"));
  }, [activeTransportMode, controller.runtimeSources, editLineId]);

  useEffect(() => {
    if (!isActive || !editLineExists) {
      return undefined;
    }
    let cancelled = false;
    controller.ensureTimetableLineLayout(editLineId).then((layout) => {
      if (cancelled || !layout) {
        return;
      }
      if (activeTransportMode === "bus") {
        controller.ensureBusHistoricalRuntime(editLineId);
      } else {
        controller.requestRuntime(editLineId, "theory").catch(() => {});
        controller.loadMonitorAverageState(editLineId).catch(() => {});
      }
    });
    return () => {
      cancelled = true;
    };
  }, [activeTransportMode, controller.ensureBusHistoricalRuntime, controller.ensureTimetableLineLayout, controller.layoutRevision, controller.loadMonitorAverageState, controller.requestRuntime, editLineExists, editLineId, isActive]);

  useEffect(() => {
    if (!isActive || view !== "monitor" || !monitorLineExists) {
      return;
    }
    controller.ensureTimetableLineLayout(monitorLineId);
  }, [controller.ensureTimetableLineLayout, isActive, monitorLineExists, monitorLineId, view]);

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
  const visibleLines = useMemo(
    () => availableLines.filter((line) => lineStates[line.id]?.visible === true),
    [availableLines, lineStates]
  );
  const visibleLineCount = visibleLines.length;
  const singleVisibleLine = visibleLineCount === 1 ? visibleLines[0] : null;
  const singleChartModes = singleVisibleLine ? lineSources(lineStates[singleVisibleLine.id], false) : [];
  const chartSelectionKey = visibleLines.map((line) => {
    const sources = visibleLineCount === 1
      ? singleChartModes
      : lineSources(lineStates[line.id], false);
    return `${line.id}:${sources.join(",")}`;
  }).join("|");
  const chartStartMinute = isValidTimeValue(chartStart) ? timeToMinutes(chartStart) : null;
  const chartEndMinute = isValidTimeValue(chartEnd) ? timeToMinutes(chartEnd) : null;
  const chartRangeValid = Number.isFinite(chartStartMinute)
    && Number.isFinite(chartEndMinute)
    && chartStartMinute >= 0
    && chartEndMinute < 1440
    && chartStartMinute < chartEndMinute;

  useEffect(() => {
    if (!isActive || activeTransportMode === "bus" || !chartRangeValid) {
      return;
    }
    visibleLines.forEach((line) => {
      const sources = visibleLineCount === 1 ? singleChartModes : lineSources(lineStates[line.id], false);
      if (sources.some(Boolean)) {
        controller.ensureTimetableLineLayout(line.id);
      }
      sources.forEach((source) => {
        if (source === "sliceHistoricalEstimate") {
          controller.requestRuntime(line.id, source);
        } else if (source === "actualToday" || source === "actualYesterday") {
          controller.loadActualTrips(
            line.id,
            source,
            chartStartMinute,
            chartEndMinute,
            monitorCoverageFilter(lineCoverages(controller.selectedSection, line.id))
          ).catch(() => {});
        }
      });
    });
  }, [activeTransportMode, chartEndMinute, chartRangeValid, chartSelectionKey, chartStartMinute, controller.ensureTimetableLineLayout, controller.loadActualTrips, controller.requestRuntime, controller.selectedSection, isActive]);

  function refreshActualChart() {
    if (!isActive || activeTransportMode === "bus" || !chartRangeValid) {
      return;
    }
    visibleLines.forEach((line) => {
      const sources = visibleLineCount === 1 ? singleChartModes : lineSources(lineStates[line.id], false);
      sources
        .filter((source) => source === "actualToday" || source === "actualYesterday")
        .forEach((source) => controller.loadActualTrips(
          line.id,
          source,
          chartStartMinute,
          chartEndMinute,
          monitorCoverageFilter(lineCoverages(controller.selectedSection, line.id))
        ).catch(() => {}));
    });
  }

  const editLine = availableLines.find((line) => line.id === editLineId) || { id: "", name: "--", stations: [], trains: [] };
  const editingTrain = editLine.trains.find((train) => train.id === editingTrainId) || null;
  const hasCurrentTrainDetails = editingTrain?.scheduleType === "custom";
  const hasLineDetails = editLine.trains.some((train) => train.scheduleType === "custom");
  const hasClearableDetails = editingTrainId ? hasCurrentTrainDetails : hasLineDetails;
  const lineClearCooling = !editingTrainId && lineClearStage === "cooldown";
  const lineClearArmed = !editingTrainId && lineClearStage !== "idle";
  const monitorLineData = monitorLines.find((line) => line.id === monitorLineId);
  const monitorLine = monitorLineData
    ? { ...monitorLineData, stations: controller.lineLayouts[monitorLineId]?.value?.stops || [] }
    : { id: "", name: "--", stations: [], trains: [] };
  const activeStations = useMemo(() => controller.selectedSection?.stations?.map((station, index) => ({
    id: station.stationId,
    name: stations.find((item) => item.stationId === station.stationId)?.name || station.stationId,
    distance: index,
    occurrence: station.order ?? index
  })) || [], [controller.selectedSection, stations]);

  const chartSeries = useMemo(() => {
    if (!chartRangeValid) {
      return [];
    }
    const timing = controller.layoutTimingRef.current;
    const startedAt = timing?.buildMeasured && !timing.chartMeasured ? diagnosticNow() : 0;
    const series = visibleLines.flatMap((line) => {
      const sources = visibleLineCount === 1 ? singleChartModes : lineSources(lineStates[line.id], false);
      return lineCoverages(controller.selectedSection, line.id).flatMap((coverage) => {
        const coveragePoints = coverageStops(coverage);
        const coverageKey = `${coverage.directionPhase}:${coverage.fromSectionIndex}:${coverage.toSectionIndex}`;
        return sources
          .filter((source) => ["sliceHistoricalEstimate", "actualToday", "actualYesterday", "plannedApplied"].includes(source))
          .flatMap((sourceName) => {
            if (sourceName === "actualToday" || sourceName === "actualYesterday") {
              return Object.values(controller.actualTrips[line.id]?.[sourceName]?.details || {})
                .flatMap((detail) => splitRunChartPoints(actualPoints(detail, coveragePoints))
                  .map((points, segmentIndex) => ({
                    lineId: line.id,
                    trainId: `${detail.header?.tripKey || ""}:${coverageKey}:segment-${segmentIndex}`,
                    source: sourceName,
                    color: line.color,
                    partial: true,
                    points
                  })));
            }
            const source = sourceName === "plannedApplied"
              ? line.plannedTrains
              : line.sliceTrains;
            return source
              .filter((train) => trainOverlapsRange(train, chartStartMinute, chartEndMinute))
              .flatMap((train) => {
                const points = projectTimedPoints(train.stops || [], coveragePoints);
                return splitRunChartPoints(points).map((segmentPoints, segmentIndex) => ({
                  lineId: line.id,
                  trainId: `${train.id}:${coverageKey}:segment-${segmentIndex}`,
                  source: sourceName,
                  color: line.color,
                  partial: sourceName === "sliceHistoricalEstimate",
                  points: segmentPoints
                }));
              });
          });
      });
    });
    if (timing && startedAt) {
      timing.chartSeriesMs = diagnosticNow() - startedAt;
      timing.chartMeasured = true;
    }
    return series;
  }, [activeStations, chartEndMinute, chartRangeValid, chartStartMinute, controller.actualTrips, controller.selectedSection, lineStates, singleChartModes, visibleLineCount, visibleLines]);

  const chartStatuses = useMemo(() => {
    if (!chartRangeValid) {
      return [];
    }
    const statuses = [];
    const addStatus = (key) => {
      const value = t(key);
      if (value && !statuses.includes(value)) {
        statuses.push(value);
      }
    };
    visibleLines.forEach((line) => {
      const sources = visibleLineCount === 1 ? singleChartModes : lineSources(lineStates[line.id], false);
      sources.forEach((source) => {
        if (source === "sliceHistoricalEstimate") {
          const runtime = controller.runtimes[line.id]?.sliceHistoricalEstimate;
          if (runtime?.state === "Failed") {
            addStatus("timetable.chart.status.runtimeUnavailable");
          } else if (runtime?.state === "Completed" && runtime.complete === false) {
            if (runtime.missingKind === "dwell") {
              addStatus("timetable.chart.status.dwellMissing");
            } else if (runtime.missingKind === "slice") {
              addStatus("timetable.chart.status.sliceMissing");
            }
          }
          return;
        }
        if (source !== "actualToday" && source !== "actualYesterday") {
          return;
        }
        const layer = controller.actualTrips[line.id]?.[source];
        if (!layer) {
          return;
        }
        if (layer.state === "unavailable") {
          addStatus("timetable.chart.status.actualUnavailable");
        } else if (layer.dataComplete === false || layer.persistenceHealthy === false) {
          addStatus("timetable.chart.status.actualIncomplete");
        } else if (layer.hasLineTrips === false) {
          addStatus("timetable.chart.status.actualNoTrips");
        } else if (layer.hasRangeTrips === false) {
          addStatus("timetable.chart.status.actualNoRecords");
        }
      });
    });
    return statuses;
  }, [chartRangeValid, controller.actualTrips, controller.runtimes, lineStates, singleChartModes, t, visibleLineCount, visibleLines]);

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

  function toggleLineSource(lineId, source) {
    setLineStates((current) => {
      const state = current[lineId] || { dataMode: "", dataModes: [] };
      const currentModes = lineSources(state, true);
      const modes = currentModes.includes(source)
        ? currentModes.filter((item) => item !== source)
        : [...currentModes, source];
      const dataMode = modes.includes(state.dataMode)
        ? state.dataMode
        : modes[modes.length - 1] || "";
      return { ...current, [lineId]: { ...state, dataMode, dataModes: modes } };
    });
  }

  function selectLineSource(lineId, source) {
    setLineStates((current) => {
      const state = current[lineId] || { dataMode: "", dataModes: [] };
      return {
        ...current,
        [lineId]: {
          ...state,
          dataMode: source,
          dataModes: [source]
        }
      };
    });
  }

  function changeView(nextView) {
    if (viewStage !== "entered" || nextView === view) {
      return;
    }
    setView(nextView);
  }

  function resetLineClear() {
    clearLineTimersRef.current.forEach((timer) => window.clearTimeout(timer));
    clearLineTimersRef.current = [];
    setLineClearStage("idle");
    setLineClearSeconds(0);
  }

  function armLineClear() {
    resetLineClear();
    setLineClearStage("cooldown");
    setLineClearSeconds(3);
    const timers = [
      window.setTimeout(() => setLineClearSeconds(2), 1000),
      window.setTimeout(() => setLineClearSeconds(1), 2000),
      window.setTimeout(() => {
        setLineClearStage("confirm");
        setLineClearSeconds(0);
        clearLineTimersRef.current.push(window.setTimeout(() => {
          clearLineTimersRef.current = [];
          setLineClearStage("idle");
        }, 5000));
      }, 3000)
    ];
    clearLineTimersRef.current = timers;
  }

  function handleEditLine(value) {
    if (!value) {
      return;
    }
    controller.refreshLineNames();
    if (value === editLineId) {
      controller.ensureTimetableLineLayout(value).then((layout) => {
        if (layout && activeTransportMode !== "bus") {
          controller.loadMonitorAverageState(value).catch(() => {});
        }
      });
      return;
    }
    setEditLineId(value);
    setEditingTrainId("");
    setArrivalSource(activeTransportMode === "bus" ? "busHistorical" : "theory");
    if (activeTransportMode !== "bus") {
      controller.setRuntimeSource(value, "theory");
    }
  }

  function handleMonitorLine(value) {
    if (!value) {
      return;
    }
    controller.refreshLineNames();
    controller.ensureTimetableLineLayout(value);
    setMonitorLineId(value);
  }

  function applyBatchCustom(request) {
    const applied = controller.markLineCustom(request.lineId, request.intervalMinutes, request.trainId);
    if (!applied) {
      return false;
    }
    const prefix = request.trainId
      ? inputKey(request.lineId, request.trainId, "")
      : `${request.lineId}\u001f`;
    setTimeDrafts((current) => Object.fromEntries(
      Object.entries(current).filter(([key]) => !key.startsWith(prefix))
    ));
    controller.clearInputErrors(request.lineId, request.trainId);
    return true;
  }

  async function handleBatchCustom() {
    if (!editLine?.id || batchRequest) {
      return;
    }
    resetLineClear();
    const request = {
      lineId: editLine.id,
      trainId: editingTrainId || "",
      intervalMinutes: Number(batchInterval),
      source: controller.runtimeSources[editLine.id]
        || (activeTransportMode === "bus" ? "busHistorical" : "theory")
    };
    const layout = await controller.ensureTimetableLineLayout(request.lineId);
    if (!layout) {
      return;
    }
    setBatchRequest(request);
    const status = activeTransportMode === "bus"
      ? await controller.ensureBusHistoricalRuntime(request.lineId)
      : await controller.requestRuntime(request.lineId, request.source);
    if (!status || status.state === "Failed" || status.state === "Cancelled") {
      setBatchRequest(null);
    }
  }

  useEffect(() => {
    if (!batchRequest) {
      return;
    }
    const runtime = controller.runtimes[batchRequest.lineId]?.[batchRequest.source];
    if (runtime?.state === "Completed") {
      applyBatchCustom(batchRequest);
      setBatchRequest(null);
    } else if (runtime?.state === "Failed" || runtime?.state === "Cancelled") {
      setBatchRequest(null);
    }
  }, [batchRequest, controller.runtimes]);

  function discardTimeDrafts(lineId, trainId = "") {
    const prefix = trainId
      ? inputKey(lineId, trainId, "")
      : `${lineId}\u001f`;
    setTimeDrafts((current) => Object.fromEntries(
      Object.entries(current).filter(([key]) => !key.startsWith(prefix))
    ));
    controller.clearInputErrors(lineId, trainId);
  }

  function handleClearDetails() {
    if (!editLine.id || !hasClearableDetails || batchRequest) {
      return;
    }
    if (editingTrainId) {
      if (controller.clearTrainDetails(editLine.id, editingTrainId)) {
        discardTimeDrafts(editLine.id, editingTrainId);
      }
      return;
    }
    if (lineClearStage !== "confirm") {
      armLineClear();
      return;
    }
    if (controller.clearLineDetails(editLine.id)) {
      discardTimeDrafts(editLine.id);
    }
    resetLineClear();
  }

  function handleTimeDraft(trainId, occurrence, value) {
    const key = inputKey(editLine.id, trainId, occurrence);
    if (String(value || "").length < 5) {
      controller.setInputError(key, "");
      setTimeDrafts((current) => ({ ...current, [key]: { value, minute: null } }));
      return { error: "", minute: null };
    }
    const result = controller.validateDeparture(editLine.id, trainId, occurrence, value);
    controller.setInputError(key, result.error);
    setTimeDrafts((current) => ({ ...current, [key]: { value, minute: result.minute } }));
    return result;
  }

  function handleInvalidTime(trainId, occurrence, value) {
    const key = inputKey(editLine.id, trainId, occurrence);
    controller.setInputError(key, "format");
    setTimeDrafts((current) => ({ ...current, [key]: { value, minute: null } }));
  }

  function handleTimeChange(trainId, occurrence, value) {
    const key = inputKey(editLine.id, trainId, occurrence);
    if (!value) {
      controller.clearDeparture(editLine.id, trainId, occurrence);
      controller.setInputError(key, "");
      setTimeDrafts((current) => {
        if (!current[key]) {
          return current;
        }
        const next = { ...current };
        delete next[key];
        return next;
      });
      return;
    }
    const result = handleTimeDraft(trainId, occurrence, value);
    if (result.error || !Number.isFinite(result.minute)) {
      return;
    }
    controller.updateDeparture(editLine.id, trainId, occurrence, result.minute);
    setTimeDrafts((current) => {
      if (!current[key]) {
        return current;
      }
      const next = { ...current };
      delete next[key];
      return next;
    });
  }

  function saveEditedTrain() {
    if (hasEditingErrors) {
      return;
    }
    setEditingTrainId("");
  }

  function editTrain(trainId) {
    resetLineClear();
    setEditingTrainId(trainId);
  }

  function changeArrivalSource(source) {
    if (activeTransportMode === "bus" || !editLine?.id || controller.sourceTransactions[editLine.id]) {
      return;
    }
    controller.switchRuntimeSource(editLine.id, source);
  }

  function locateShortDwellError() {
    if (!shortDwellError) {
      return;
    }
    resetLineClear();
    if (shortDwellError.lineId !== editLineId) {
      pendingLocateRef.current = {
        lineId: shortDwellError.lineId,
        trainId: shortDwellError.trainId
      };
      setEditLineId(shortDwellError.lineId);
      setArrivalSource(controller.runtimeSources[shortDwellError.lineId]
        || (activeTransportMode === "bus" ? "busHistorical" : "theory"));
      return;
    }
    setEditingTrainId(shortDwellError.trainId);
    controller.ensureTimetableLineLayout(shortDwellError.lineId).catch(() => {});
  }

  function toggleChartCollapsed() {
    if (!chartCollapsed) {
      setIntervalCollapsed(true);
      setLinesCollapsed(true);
    }
    setChartCollapsed((current) => !current);
  }

  const theoryRuntime = controller.runtimes[editLine?.id]?.theory;
  const theoryState = theoryRuntime?.state === "Completed"
    ? "ready"
    : theoryRuntime?.state === "Unavailable" || theoryRuntime?.state === "Failed"
      ? "unavailable"
      : theoryRuntime?.state === "Queued" || theoryRuntime?.state === "Running"
        || controller.pendingQueries[`${editLine?.id || ""}\u001ftheory`]
        ? "calculating"
        : "waiting";
  const monitorAverageReady = controller.monitorAverageStates[editLine?.id]?.ready === true;
  const sourceChanging = Boolean(controller.sourceTransactions[editLine?.id]);

  return (
    <div className="rtw-timetable-root">
      <div className={`rtw-timetable-shell ${sidebarCollapsed ? "is-sidebar-collapsed" : ""}`}>
        <aside className="rtw-timetable-sidebar">
          {renderedView === "workspace" ? (
            <div key="workspace" className={`rtw-timetable-sidebar-scene is-${viewStage}`}>
            {activeTransportMode !== "bus" ? <SidebarSection icon="map" title={t("timetable.interval.title")} collapsed={intervalCollapsed} onToggle={() => setIntervalCollapsed((current) => !current)}>
              <WorkbenchDropdown
                label={t("timetable.interval.start")}
                value={stations.find((station) => station.stationId === startStationId)?.name || t("timetable.interval.choose")}
                options={stations.map((station) => ({ value: station.stationId, label: station.name, active: station.stationId === startStationId }))}
                onSelect={controller.selectStartStation}
                onOpen={controller.refreshStationNames}
                className="rtw-timetable-sidebar-field"
                positioning="portal"
                portalHostRef={portalHostRef}
              />
              <WorkbenchDropdown
                label={t("timetable.interval.end")}
                value={endStations.find((station) => station.stationId === endStationId)?.name || t("timetable.interval.choose")}
                options={endStations.map((station) => ({ value: station.stationId, label: station.name, active: station.stationId === endStationId }))}
                onSelect={controller.selectEndStation}
                onOpen={controller.refreshStationNames}
                className="rtw-timetable-sidebar-field"
                positioning="portal"
                portalHostRef={portalHostRef}
              />
              <div className="rtw-timetable-sidebar-time-row">
                <DemoTextField label={t("timetable.interval.startTime")} value={chartStart} onCommit={setChartStart} className="rtw-timetable-sidebar-field" timeMode />
                <DemoTextField label={t("timetable.interval.endTime")} value={chartEnd} onCommit={setChartEnd} className="rtw-timetable-sidebar-field" timeMode />
              </div>
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
            </SidebarSection> : null}

            <SidebarSection icon="route" title={t("timetable.lines.title")} collapsed={linesCollapsed} onToggle={() => setLinesCollapsed((current) => !current)}>
              {availableLines.length > 0 ? availableLines.map((line) => {
                const state = lineStates[line.id] || { visible: true, dataMode: "", dataModes: [] };
                const selectedSources = lineSources(state, false);
                return (
                    <div key={line.id} className={`rtw-timetable-line-item ${editLine.id === line.id ? "is-editing" : ""}`} onClick={() => handleEditLine(line.id)}>
                    <div className="rtw-timetable-line-row">
                      {activeTransportMode !== "bus" ? <button type="button" className={`rtw-timetable-check ${state.visible ? "is-checked" : ""}`} style={{ backgroundColor: state.visible ? line.color : "transparent", borderColor: state.visible ? line.color : "rgba(255,255,255,0.30)" }} onClick={(event) => { event.stopPropagation(); updateLineState(line.id, { visible: !state.visible }); }}>{state.visible ? <TimetableIcon name="check" /> : null}</button> : null}
                      <span className="rtw-timetable-line-dot" style={{ backgroundColor: line.color }} />
                      <span className="rtw-timetable-line-name">{line.name}</span>
                    </div>
                    {activeTransportMode !== "bus" && <ChartSourceDropdown
                      value={selectedSources[0] || ""}
                      options={["sliceHistoricalEstimate", "actualToday", "actualYesterday", "plannedApplied"].map((mode) => ({ value: mode, label: t(`timetable.data.${mode}`) }))}
                      onSelect={(mode) => selectLineSource(line.id, mode)}
                      portalHostRef={portalHostRef}
                      emptyLabel={t("timetable.interval.choose")}
                    />}
                  </div>
                );
              }) : <div className="rtw-timetable-sidebar-empty">{t("timetable.lines.empty")}</div>}
            </SidebarSection>

            {availableLines.length > 0 ? (
              <SidebarSection icon="sliders" title={t("timetable.edit.title")}>
                {activeTransportMode !== "bus" ? <WorkbenchDropdown
                  label={t("timetable.edit.line")}
                  value={editLine.name}
                  options={availableLines.map((line) => ({ value: line.id, label: line.name, active: line.id === editLine.id }))}
                  onSelect={handleEditLine}
                  onOpen={controller.refreshLineNames}
                  className="rtw-timetable-sidebar-field"
                  positioning="portal"
                  portalHostRef={portalHostRef}
                /> : null}
                <WorkbenchDropdown
                  label={t("timetable.edit.rule")}
                  value={t(`timetable.edit.interval.${batchInterval}`)}
                  options={["10", "15", "20", "30"].map((value) => ({ value, label: t(`timetable.edit.interval.${value}`), active: batchInterval === value }))}
                  onSelect={setBatchInterval}
                  className="rtw-timetable-sidebar-field"
                  positioning="portal"
                  portalHostRef={portalHostRef}
                />
                <button type="button" className="rtw-timetable-secondary-button" disabled={Boolean(batchRequest)} onClick={handleBatchCustom}>{t(batchRequest ? "timetable.theory.preparing" : editingTrainId ? "timetable.edit.batchCurrent" : "timetable.edit.batch")}</button>
                <button type="button" className={`rtw-timetable-secondary-button ${lineClearArmed ? "is-danger" : ""}`} disabled={Boolean(batchRequest) || !hasClearableDetails || lineClearCooling} onClick={handleClearDetails}>{t(editingTrainId ? "timetable.edit.clearCurrent" : lineClearStage === "cooldown" ? "timetable.edit.clearLineCountdown" : lineClearStage === "confirm" ? "timetable.edit.clearLineConfirm" : "timetable.edit.clearLine", { seconds: lineClearSeconds })}</button>
              </SidebarSection>
            ) : null}

            {activeTransportMode !== "bus" ? <div className="rtw-timetable-legend">
              <div className="rtw-timetable-legend-title">{t("timetable.legend")}</div>
              {visibleLines.map((line) => {
                const modes = visibleLineCount === 1 ? singleChartModes : lineSources(lineStates[line.id], false);
                return <div key={`legend-${line.id}`} className="rtw-timetable-legend-row"><span style={{ backgroundColor: line.color }} /><span>{line.name}{modes.length > 0 ? ` · ${modes.map((mode) => t(`timetable.data.${mode}`)).join(" / ")}` : ""}</span></div>;
              })}
            </div> : null}
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
                options={monitorLines.map((line) => ({ value: line.id, label: line.name, active: monitorLineId === line.id }))}
                onSelect={handleMonitorLine}
                onOpen={controller.refreshLineNames}
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
              {activeTransportMode !== "bus" ? <section className={`rtw-timetable-chart-section ${chartCollapsed ? "is-collapsed" : ""}`}>
                <div className="rtw-timetable-chart-head">
                  <div className="rtw-timetable-panel-title"><TimetableIcon name="chart" /><span>{t("timetable.chart.title")}</span></div>
                  <div className="rtw-timetable-chart-actions">
                    <button type="button" className="rtw-timetable-chart-toggle" title={t("timetable.chart.refresh")} aria-label={t("timetable.chart.refresh")} onClick={refreshActualChart}>
                      <TimetableIcon name="refresh" />
                    </button>
                    <button type="button" className="rtw-timetable-chart-toggle" title={t(chartCollapsed ? "timetable.chart.expand" : "timetable.chart.collapse")} onClick={toggleChartCollapsed}>
                      <TimetableIcon name={chartCollapsed ? "chevron-down" : "chevron-up"} />
                    </button>
                  </div>
                </div>
                {!chartCollapsed ? <>
                  {chartStatuses.length > 0 ? <div className="rtw-timetable-chart-status">{chartStatuses.map((status) => <div key={status}>{status}</div>)}</div> : null}
                  <RunChart stations={activeStations} series={chartSeries} startMinute={chartStartMinute} endMinute={chartEndMinute} emptyText={chartStatuses.length > 0 ? "" : t("timetable.chart.empty")} sidebarCollapsed={sidebarCollapsed} />
                </> : null}
              </section> : null}
              <section className="rtw-timetable-schedule-section">
                <div className="rtw-timetable-schedule-head">
                  <div className="rtw-timetable-schedule-copy">
                    <div className="rtw-timetable-panel-title"><TimetableIcon name="calendar-clock" /><span>{t("timetable.table.title")}</span></div>
                    <div className="rtw-timetable-current-line">{t("timetable.table.current")} <strong>{editLine.name}</strong></div>
                    <span className="rtw-timetable-count">{t("timetable.table.default", { count: editLine.trains.filter((train) => train.scheduleType !== "custom").length })}</span>
                    <span className="rtw-timetable-count is-custom">{t("timetable.table.custom", { count: editLine.trains.filter((train) => train.scheduleType === "custom").length })}</span>
                  </div>
                  {editingTrainId ? (
                    <button type="button" className="rtw-timetable-primary-button" disabled={hasEditingErrors} onClick={saveEditedTrain}>{t("timetable.action.save")}</button>
                  ) : activeTransportMode !== "bus" ? (
                    <div className="rtw-timetable-arrival-source">
                      <span>{t("timetable.theory.arrivalSource")}</span>
                      <button type="button" className={`is-theory is-${theoryState} ${theoryState === "ready" && arrivalSource === "theory" ? "is-active" : ""}`} disabled={sourceChanging || theoryState !== "ready"} onClick={() => changeArrivalSource("theory")}>
                        {theoryState === "calculating" ? <span className="rtw-timetable-theory-spinner" /> : null}
                        <span>{t(theoryState === "calculating"
                          ? "timetable.theory.calculating"
                          : theoryState === "unavailable"
                            ? "timetable.theory.unavailable"
                            : theoryState === "waiting"
                              ? "timetable.theory.waiting"
                              : "timetable.theory.theoretical")}</span>
                      </button>
                      <button type="button" className={arrivalSource === "monitorAverage" ? "is-active" : ""} disabled={!monitorAverageReady || sourceChanging} onClick={() => changeArrivalSource("monitorAverage")}>{t(monitorAverageReady ? "timetable.theory.monitorAverage" : "timetable.theory.monitorUnavailable")}</button>
                    </div>
                  ) : null}
                </div>
                <TimetableEditor
                  line={editLine}
                  historicalRuntime={activeTransportMode === "bus"
                    ? controller.runtimes[editLine.id]?.busHistorical
                    : controller.runtimes[editLine.id]?.monitorAverage}
                  theoryRuntime={controller.runtimes[editLine.id]?.theory}
                  showHistoricalOnly={activeTransportMode === "bus"}
                  editingTrainId={editingTrainId}
                  onEdit={editTrain}
                  onTimeChange={handleTimeChange}
                  onTimeDraft={handleTimeDraft}
                  onInvalidTime={handleInvalidTime}
                  timeDrafts={timeDrafts}
                  inputErrors={controller.inputErrors}
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
          <footer className="rtw-timetable-footer">
        <div className={`rtw-timetable-footer-status is-${editingTrainId ? "editing" : shortDwellError || controller.saveError || controller.loadError ? "error" : controller.saveState}`}>
          {editingTrainId
            ? t("timetable.footer.editingHint")
            : shortDwellError
              ? t("timetable.footer.shortDwell", { time: shortDwellError.time })
              : showFooterControlNote
                ? t("timetable.footer.controlNote")
            : controller.saveError
              ? t("timetable.footer.error")
              : controller.loadError
                ? t("timetable.footer.dataError")
                : controller.saveState === "saving"
                  ? t("nativeSchedule.summary.action.applying")
                  : controller.saveState === "applied"
                    ? t("nativeSchedule.summary.action.applied")
                    : t(`timetable.footer.${controller.saveState}`)}
        </div>
        <button
          type="button"
          className={`rtw-timetable-primary-button is-${footerButtonState}`}
          disabled={Boolean(editingTrainId) || (!shortDwellError && !controller.canSave)}
          onClick={shortDwellError ? locateShortDwellError : controller.saveAll}
        >
          <span className="rtw-timetable-footer-button-content">
            <TimetableIcon
              name={editingTrainId
                ? "clock"
                : shortDwellError
                  ? "alert"
                : controller.saveState === "saving" || controller.saveState === "error"
                  ? "refresh"
                  : controller.saveState === "applied"
                    ? "check"
                    : "calendar"}
              className="rtw-timetable-footer-button-icon"
              strokeColor={footerIconColor}
            />
            <span>{t(editingTrainId
              ? "timetable.footer.finishEditing"
              : shortDwellError
                ? "timetable.footer.locateError"
              : controller.saveState === "saving"
                ? "nativeSchedule.summary.action.applying"
                : controller.saveState === "applied"
                  ? "nativeSchedule.summary.action.applied"
                  : controller.saveState === "error"
                    ? "timetable.footer.retry"
                    : "nativeSchedule.summary.action.apply")}</span>
          </span>
        </button>
          </footer>
        </main>
      </div>

      <div ref={portalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}

function SidebarSection({ icon, title, collapsed = false, onToggle, children }) {
  return <section className={`rtw-timetable-sidebar-section ${collapsed ? "is-collapsed" : ""}`}>
    {onToggle ? (
      <button type="button" className="rtw-timetable-sidebar-title is-toggle" onClick={onToggle}>
        <TimetableIcon name={icon} />
        <h2>{title}</h2>
        <TimetableIcon name={collapsed ? "chevron-down" : "chevron-up"} className="rtw-timetable-section-chevron" />
      </button>
    ) : <div className="rtw-timetable-sidebar-title"><TimetableIcon name={icon} /><h2>{title}</h2></div>}
    <div className="rtw-timetable-sidebar-section-content">{children}</div>
  </section>;
}

function ChartSourceDropdown({ value, options, onSelect, portalHostRef, emptyLabel }) {
  const selectedValue = Array.isArray(value) ? value[0] || "" : value || "";
  const selectedLabel = options.find((option) => option.value === selectedValue)?.label || selectedValue;
  return <WorkbenchDropdown
    value={selectedLabel || emptyLabel}
    options={options.map((option) => ({
      ...option,
      active: option.value === selectedValue
    }))}
    onSelect={onSelect}
    className="rtw-timetable-line-mode"
    positioning="portal"
    portalHostRef={portalHostRef}
  />;
}

function TimetableEditor({ line, historicalRuntime, theoryRuntime, showHistoricalOnly, editingTrainId, onEdit, onTimeChange, onTimeDraft, onInvalidTime, timeDrafts, inputErrors, t }) {
  const timeInputRefs = useRef(new Map());
  const train = line.trains.find((item) => item.id === editingTrainId);
  if (train) {
    const editableOccurrences = train.canEdit
      ? train.stops.slice(1, -1).map((stop) => stop.occurrence)
      : [];
    const getTimeInputRef = (occurrence) => {
      if (!timeInputRefs.current.has(occurrence)) {
        timeInputRefs.current.set(occurrence, { current: null });
      }
      return timeInputRefs.current.get(occurrence);
    };
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
            <div className="is-time is-departure">{t("timetable.table.head.departure")}</div>
            <div className="is-next">{t("timetable.table.head.nextStation")}</div>
            <div className="is-runtime">{t(showHistoricalOnly
              ? "timetable.table.head.historicalDuration"
              : "timetable.table.head.runtimePair")}</div>
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
            const key = inputKey(line.id, train.id, stop.occurrence);
            const error = inputErrors[key] || "";
            const draft = timeDrafts[key];
            const draftMinute = draft?.minute;
            const editableIndex = editableOccurrences.indexOf(stop.occurrence);
            const inputRef = editableIndex >= 0 ? getTimeInputRef(stop.occurrence) : null;
            const previousOccurrence = editableIndex > 0 ? editableOccurrences[editableIndex - 1] : null;
            const nextOccurrence = editableIndex >= 0 ? editableOccurrences[editableIndex + 1] : null;
            const previousInputRef = previousOccurrence != null ? getTimeInputRef(previousOccurrence) : null;
            const nextInputRef = nextOccurrence != null ? getTimeInputRef(nextOccurrence) : null;
            const hint = error
              ? t(`timetable.validation.${error}`)
              : formatDayHint(Number.isFinite(draftMinute) ? draftMinute : stop.departureMinute, t);
            return (
              <div key={stop.occurrence} className="rtw-timetable-table-row rtw-timetable-stop-row rtw-timetable-stagger-row" style={{ animationDelay: `${Math.min(index, 5) * 70}ms` }}>
                <div className="is-section rtw-timetable-stop-cell">
                  <span className="rtw-timetable-stop-name">{station?.name || stop.stationName}</span>
                  {index === 0 ? <span className="rtw-timetable-stop-tag">{t("timetable.table.stop.origin")}</span> : null}
                  {isLast ? <span className="rtw-timetable-stop-tag">{t("timetable.table.stop.terminal")}</span> : null}
                </div>
                <div className="is-time is-arrival">{index === 0 ? "--" : renderArrivalTime(stop.arrivalMinute, t)}</div>
                <div className={`is-time is-departure ${error ? "has-error" : ""}`} onBlur={(event) => {
                  const value = event.target?.value ?? "";
                  if (value && !isLast && index > 0 && train.canEdit && !isValidTimeValue(value)) {
                    onInvalidTime(train.id, stop.occurrence, value);
                  }
                }}>
                  {isLast ? "--" : index === 0 || !train.canEdit ? formatServiceTime(stop.departureMinute, t) : (
                    <>
                      <TimetableIcon name="clock" />
                      <DemoTextField label="" value={draft?.value ?? (stop.departureMinute == null ? "" : minutesToTime(stop.departureMinute))} onCommit={(value) => onTimeChange(train.id, stop.occurrence, value)} onDraftChange={(value) => onTimeDraft(train.id, stop.occurrence, value)} inputRef={inputRef} previousInputRef={previousInputRef} nextInputRef={nextInputRef} errorText={error ? hint : ""} preserveInvalidTime allowEmptyCommit className="rtw-timetable-time-field" timeMode />
                      <span className={`rtw-timetable-time-hint ${error ? "is-error" : ""}`}>{hint}</span>
                    </>
                  )}
                </div>
                <div className="is-next">{next ? <><TimetableIcon name="arrow-right" /><span>{next.name || next.stationName}</span></> : "--"}</div>
                <div className="is-runtime">{showHistoricalOnly
                  ? formatHistoricalRuntime(historicalMinutes, t)
                  : formatRuntimePair(historicalMinutes, theoryMinutes, t)}</div>
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
        <div className="rtw-timetable-table-body">{line.trains.map((item, index) => (
          <button type="button" key={item.id} className="rtw-timetable-summary-item rtw-timetable-stagger-row" style={{ animationDelay: `${Math.min(Math.floor(index / 3), 5) * 70}ms` }} onClick={() => onEdit(item.id)}>
            <span className="rtw-timetable-summary-time">{formatServiceTime(item.slotMinute, t)}</span>
            <span className={`dw-demo-badge ${item.scheduleType === "custom" ? "is-express" : "is-local"}`}>{item.scheduleType === "custom" ? t("timetable.mode.custom") : t("timetable.mode.default")}</span>
          </button>
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

function formatHistoricalRuntime(minutes, t) {
  return minutes == null ? "--" : `${minutes.toFixed(1)} ${t("timetable.unit.minutesLong")}`;
}
