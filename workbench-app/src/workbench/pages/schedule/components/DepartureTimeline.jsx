import { useEffect, useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import { quickMinutesToTime } from "../../../../lib/auto-schedule";
import { moveServiceBoundary } from "../../../../lib/service-periods";
import WorkbenchTooltip from "../../../shared/WorkbenchTooltip";
import { ChevronDownIcon } from "./ScheduleIcons";
import { buildDepartureEntries, departureClockMarks, departureHeadingFit, departureHeadings, departureLoadPaths, departureTicks } from "../schedule-view-model";

const HEADING_FONTS = {
  name: { fontSize: "14rem", fontWeight: 600 },
  count: { fontSize: "18rem", fontWeight: 700 },
  unit: { fontSize: "12rem" },
  range: { fontSize: "12rem" }
};

export default function DepartureTimeline({ segments, rows = [], selectedIndex, onSelect, onMoveBoundary, isActive, loads }) {
  const { t } = useNativeScheduleI18n();
  const chartRef = useRef(null);
  const detailRef = useRef(null);
  const unitRef = useRef(null);
  const measureRef = useRef(null);
  const dragRef = useRef(null);
  const moveRef = useRef(onMoveBoundary);
  moveRef.current = onMoveBoundary;
  const [draft, setDraft] = useState(null);
  const [hover, setHover] = useState(-1);
  const [size, setSize] = useState({ width: 750, detailWidth: 750, unit: 1 });
  const width = size.width;
  const tickWidth = Math.max(1, Math.round(2 * size.unit));
  const selected = segments[selectedIndex];
  const selectedCount = (selected?.entries || []).filter((entry) => !entry.skipped).length;
  const dense = selectedCount > 0 && (selected.end - selected.start) / selectedCount < 15;
  useEffect(() => {
    if (!isActive || !chartRef.current) return undefined;
    const node = chartRef.current;
    const measure = () => {
      if (node.clientWidth <= 0) return;
      const unit = (unitRef.current?.clientWidth || 0) / 100 || 1;
      const detailWidth = detailRef.current?.clientWidth || node.clientWidth;
      setSize((current) => current.width === node.clientWidth && current.detailWidth === detailWidth && current.unit === unit ? current : { width: node.clientWidth, detailWidth, unit });
    };
    const observer = typeof ResizeObserver === "function" ? new ResizeObserver(measure) : null;
    observer?.observe(node);
    if (unitRef.current) observer?.observe(unitRef.current);
    if (detailRef.current) observer?.observe(detailRef.current);
    const frame = window.requestAnimationFrame(measure);
    window.addEventListener("resize", measure);
    return () => { observer?.disconnect(); window.cancelAnimationFrame(frame); window.removeEventListener("resize", measure); };
  }, [isActive, dense]);
  useEffect(() => {
    if (!isActive) { setDraft(null); return undefined; }
    function move(event) {
      const drag = dragRef.current;
      if (!drag) return;
      const next = moveServiceBoundary(drag.segments, drag.index, drag.field, (event.clientX - drag.left) / drag.width * 1440, true);
      if (next[drag.index][drag.field] === drag.minute) return;
      drag.minute = next[drag.index][drag.field];
      setDraft(next);
    }
    function up() {
      const drag = dragRef.current;
      if (!drag) return;
      dragRef.current = null;
      setDraft(null);
      if (drag.minute !== drag.segments[drag.index][drag.field]) moveRef.current(drag.index, drag.field, drag.minute);
    }
    document.addEventListener("mousemove", move); document.addEventListener("mouseup", up);
    window.addEventListener("blur", up);
    return () => { dragRef.current = null; document.removeEventListener("mousemove", move); document.removeEventListener("mouseup", up); window.removeEventListener("blur", up); };
  }, [isActive]);
  const countUnit = t("nativeSchedule.line.count", { count: "" }).trim();
  const namesKey = [...new Set(segments.map((segment) => t(segment.labelKey)))].join("\u0000");
  const samples = useMemo(() => [
    ...namesKey.split("\u0000").map((text) => ({ key: `name:${text}`, text, style: HEADING_FONTS.name })),
    { key: "unit", text: countUnit, style: HEADING_FONTS.unit },
    ...Array.from("0123456789").map((text) => ({ key: `count:${text}`, text, style: HEADING_FONTS.count })),
    ...Array.from("0123456789: –").map((text) => ({ key: `range:${text}`, text, style: HEADING_FONTS.range }))
  ], [namesKey, countUnit]);
  const [metrics, setMetrics] = useState(() => new Map());
  useEffect(() => {
    if (!isActive || !measureRef.current) return undefined;
    let frame = 0;
    function measure() {
      window.cancelAnimationFrame(frame);
      const node = measureRef.current;
      if (!node) return;
      const unit = (unitRef.current?.getBoundingClientRect().width || 0) / 100;
      const next = new Map(samples.map((sample, index) => [sample.key, node.children[index].getBoundingClientRect().width / unit]));
      if (unit <= 0 || [...next.values()].some((value) => !(value > 0))) { frame = window.requestAnimationFrame(measure); return; }
      setMetrics((previous) => samples.every((sample) => previous.get(sample.key) === next.get(sample.key)) ? previous : next);
    }
    const observer = typeof ResizeObserver === "function" ? new ResizeObserver(measure) : null;
    if (observer) {
      Array.from(measureRef.current.children).forEach((child) => observer.observe(child));
      if (unitRef.current) observer.observe(unitRef.current);
    }
    frame = window.requestAnimationFrame(() => { frame = window.requestAnimationFrame(measure); });
    return () => { observer?.disconnect(); window.cancelAnimationFrame(frame); };
  }, [isActive, samples]);
  const measured = samples.every((sample) => metrics.has(sample.key));
  const charsWidth = (kind, text) => Array.from(String(text)).reduce((total, char) => total + metrics.get(`${kind}:${char}`), 0);
  function beginBoundary(event, handle) {
    if (event.button !== 0) return;
    const rect = chartRef.current.getBoundingClientRect();
    dragRef.current = { ...handle, segments, left: rect.left, width: rect.width };
    event.preventDefault();
  }
  const periods = draft || segments;
  const handles = periods.flatMap((period, index) => {
    const previous = periods[index - 1];
    const result = [];
    if (period.start > 0 && (!previous || previous.end !== period.start)) result.push({ id: `${period.id}:start`, index, field: "start", minute: period.start });
    if (period.end < 1440) result.push({ id: `${period.id}:end`, index, field: "end", minute: period.end });
    return result;
  });
  const allEntries = buildDepartureEntries(segments, rows);
  const headings = departureHeadings(periods, width);
  const ticks = departureTicks(allEntries, 0, 1440, width, tickWidth);
  const loadHeight = 48 * size.unit;
  const loadPaths = useMemo(() => loads.values ? departureLoadPaths(loads.values, width, 48 * size.unit, loads.split) : null, [loads.values, loads.split, width, size.unit]);
  const regions = periods.map((segment, index) => <button key={segment.id} type="button" aria-label={t(segment.labelKey)} className={`dw-departure-region ${index === hover ? "is-hover" : ""} ${index === selectedIndex ? "is-active" : ""}`} style={{ left: `${segment.start / 14.4}%`, width: `${(segment.end - segment.start) / 14.4}%` }} onClick={() => onSelect(index)} onMouseEnter={() => setHover(index)} onMouseLeave={() => setHover(-1)} />);
  return (
    <section className="dw-departure-timeline" aria-label={t("nativeSchedule.line.dayPreview")}>
      <div ref={chartRef} className="dw-departure-axis">
        <span ref={unitRef} className="dw-departure-unit" aria-hidden="true" />
        <div ref={measureRef} className="dw-departure-measure" aria-hidden="true">{samples.map((sample) => <span key={sample.key} style={sample.style}>{sample.text}</span>)}</div>
        <div className="dw-departure-periods" style={measured ? undefined : { visibility: "hidden" }}>
          {headings.map((box) => {
            const segment = periods[box.index];
            const count = segment.entries.filter((entry) => !entry.skipped).length;
            const range = `${quickMinutesToTime(segment.start)} – ${quickMinutesToTime(segment.end)}`;
            const name = t(segment.labelKey);
            const fit = measured ? departureHeadingFit(box.width / size.unit, { name: metrics.get(`name:${name}`), count: charsWidth("count", count),
              unit: metrics.get("unit"), range: charsWidth("range", range) }) : { unit: true, nameWidth: null, range: true };
            return <button key={segment.id} type="button" className={`dw-departure-period ${box.index === hover ? "is-hover" : ""} ${box.index === selectedIndex ? "is-active" : ""}`}
              style={{ left: `${box.left}px`, width: `${box.width}px` }} onClick={() => onSelect(box.index)} onMouseEnter={() => setHover(box.index)} onMouseLeave={() => setHover(-1)} aria-pressed={box.index === selectedIndex}>
              <span className="dw-departure-period-main">
                <WorkbenchTooltip tooltip={fit.nameWidth == null ? null : name}><span className="dw-departure-period-name" style={fit.nameWidth == null ? undefined : { maxWidth: `${fit.nameWidth}rem` }}>{name}</span></WorkbenchTooltip>
                <span className="dw-departure-count">{count}</span>{fit.unit ? <span className="dw-departure-count-unit">{countUnit}</span> : null}</span>
              {fit.range ? <span className="dw-departure-range">{range}</span> : null}
              {box.width / size.unit >= 32 ? <ChevronDownIcon className="dw-departure-period-chevron" /> : null}
            </button>;
          })}
          {handles.map((handle) => <button key={handle.id} type="button" className="dw-departure-period-handle" style={{ left: `${handle.minute / 1440 * width}px` }}
            onMouseDown={(event) => beginBoundary(event, handle)} aria-label={t("passengerFlow.adjustPeriod")}><span className="dw-departure-period-grip">↔</span></button>)}
        </div>
        <div className="dw-departure-load">
          {regions}
          {loadPaths ? <svg className="dw-departure-load-chart" viewBox={`0 0 ${width} ${loadHeight}`} style={{ width: `${width}px`, height: `${loadHeight}px` }}>
            <path d={loadPaths.area} fill="#5ab4c5" fillOpacity="0.16" stroke="none" />
            {loadPaths.solid ? <path d={loadPaths.solid} fill="none" stroke="#5ab4c5" strokeWidth={2 * size.unit} /> : null}
            {loadPaths.dashed ? <path d={loadPaths.dashed} fill="none" stroke="#5ab4c5" strokeWidth={2 * size.unit} strokeDasharray={`${4 * size.unit} ${3 * size.unit}`} /> : null}
          </svg> : null}
          {!loadPaths && !loads.loading ? <span className="dw-departure-load-note">{t("nativeSchedule.line.loadEmpty")}</span> : null}
        </div>
        <div className="dw-departure-plot">
          {regions}
          {ticks.map((tick) => <span key={tick.minute} className="dw-departure-tick" style={{ left: `${tick.pixel}px`, width: `${tickWidth}px` }} />)}
        </div>
        <div className="dw-departure-clock">{[0, 120, 240, 360, 480, 600, 720, 840, 960, 1080, 1200, 1320, 1440].map((minute) => <span key={minute} className={minute === 0 ? "is-first" : minute === 1440 ? "is-last" : ""} style={{ left: `${minute / 14.4}%` }}>{quickMinutesToTime(minute)}</span>)}</div>
      </div>
      {dense ? <div className="dw-departure-detail">
        <div className="dw-departure-detail-head"><span>{t(selected.labelKey)}</span><span>{quickMinutesToTime(selected.start)} – {quickMinutesToTime(selected.end)}</span></div>
        <div ref={detailRef} className="dw-departure-plot is-detail">{departureTicks(selected.entries, selected.start, selected.end, size.detailWidth, tickWidth).map((tick) => <span key={tick.minute} className="dw-departure-tick" style={{ left: `${tick.pixel}px`, width: `${tickWidth}px` }} />)}</div>
        <div className="dw-departure-clock">{departureClockMarks(selected.start, selected.end, size.detailWidth / size.unit).map((minute) => <span key={minute} className={minute === selected.start ? "is-first" : minute === selected.end ? "is-last" : ""} style={{ left: `${(minute - selected.start) / (selected.end - selected.start) * 100}%` }}>{quickMinutesToTime(minute)}</span>)}</div>
      </div> : null}
    </section>
  );
}
