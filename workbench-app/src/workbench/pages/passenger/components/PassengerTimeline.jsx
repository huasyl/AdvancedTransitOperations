import { useEffect, useMemo, useRef, useState } from "react";
import { formatPassengerNumber, presetsForBuckets, rangeLabel, timeLabel } from "../passenger-analysis";
import { DEFAULT_SERVICE_PERIODS, SERVICE_PERIOD_STEP, moveServiceBoundary } from "../../../../lib/service-periods";
import { selectionFrame, timeRangeAt } from "../passenger-timeline-layout";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import { passengerScale } from "../passenger-scale";

const format = formatPassengerNumber;

export default function PassengerTimeline({ buckets, points, range, onChange, station = false, boardings, alightings, color = "#81d4d4", pending = false, isActive = false, periods = DEFAULT_SERVICE_PERIODS, onPeriodsChange }) {
  const { t } = useNativeScheduleI18n();
  const graphRef = useRef(null), dragRef = useRef(null), changeRef = useRef(onChange);
  changeRef.current = onChange;
  const [draft, setDraft] = useState(null);
  const [periodDraft, setPeriodDraft] = useState(null);
  const periodsRef = useRef(periods), periodsChangeRef = useRef(onPeriodsChange);
  periodsRef.current = periods; periodsChangeRef.current = onPeriodsChange;
  const [graphBounds, setGraphBounds] = useState({ left: 0, width: 0, height: 0, ratio: 1 });
  const columnEdges = useMemo(() => Array.from({ length: points.length + 1 }, (_, index) =>
    Math.round((graphBounds.left + graphBounds.width * index / Math.max(1, points.length)) * graphBounds.ratio)
      / graphBounds.ratio - graphBounds.left), [graphBounds, points.length]);
  const step = buckets.length > 1 ? buckets[1] - buckets[0] : 15;
  const start = buckets[0], end = buckets.length ? buckets[buckets.length - 1] + step : NaN;
  const selection = draft || range;
  const indexOf = (time) => Math.max(0, Math.min(buckets.length, Math.round((time - start) / step)));
  const selected = selection && Number.isFinite(start) ? [indexOf(selection[0]), indexOf(selection[1])] : null;
  const presets = useMemo(() => presetsForBuckets(buckets, periodDraft || periods), [buckets, periods, periodDraft]);
  const grid = SERVICE_PERIOD_STEP / step;
  const staggerTicks = graphBounds.width > 0 && graphBounds.width / 24 < 40 * graphBounds.height / 160;
  const borderControls = presets.flatMap((period, index) => {
    const previous = presets[index - 1];
    const result = [];
    if (period.from > start && (!previous || previous.to !== period.from))
      result.push({ id: `${period.id}:start`, time: period.from, index: period.periodIndex, field: "start", day: period.day });
    if (period.to < end) result.push({ id: `${period.id}:end`, time: period.to, index: period.periodIndex, field: "end", day: period.day });
    return result;
  });
  const showStartHandle = selected && selected[0] > 0;
  const showEndHandle = selected && selected[1] < buckets.length;
  const frameUnit = graphBounds.height / 160;
  const frameLeft = selected ? Math.round((graphBounds.left + selected[0] / buckets.length * graphBounds.width) * graphBounds.ratio) / graphBounds.ratio - graphBounds.left : 0;
  const frameRight = selected ? Math.round((graphBounds.left + selected[1] / buckets.length * graphBounds.width) * graphBounds.ratio) / graphBounds.ratio - graphBounds.left : 0;
  const framePath = selected && graphBounds.width > 0 ? selectionFrame(
    frameLeft, frameRight, graphBounds.height, frameUnit, showStartHandle, showEndHandle) : "";
  const gripPath = [showStartHandle ? frameLeft - 3 * frameUnit : null, showEndHandle ? frameRight + 3 * frameUnit : null]
    .filter((x) => x != null).map((x) => `M${x} ${graphBounds.height / 2 - 6 * frameUnit} v${12 * frameUnit}`).join(" ");
  const values = points.map((point) => point?.value);
  const { top, ticks } = passengerScale(values);
  const boundaries = [...new Set(presets.flatMap((period) => [period.from, period.to]))]
    .filter((time) => time > start && time < end).sort((a, b) => a - b);

  useEffect(() => {
    if (!isActive || !graphRef.current) return undefined;
    const node = graphRef.current;
    function measure() {
      const rect = node.getBoundingClientRect();
      if (rect.width <= 0) return;
      const ratio = window.devicePixelRatio || 1;
      setGraphBounds((current) => current.left === rect.left && current.width === rect.width && current.height === rect.height && current.ratio === ratio
        ? current : { left: rect.left, width: rect.width, height: rect.height, ratio });
    }
    const observer = typeof ResizeObserver === "function" ? new ResizeObserver(measure) : null;
    observer?.observe(node);
    const frame = window.requestAnimationFrame(measure);
    window.addEventListener("resize", measure);
    return () => { observer?.disconnect(); window.cancelAnimationFrame(frame); window.removeEventListener("resize", measure); };
  }, [isActive]);

  useEffect(() => {
    if (!isActive) { dragRef.current = null; setDraft(null); setPeriodDraft(null); return undefined; }
    function move(event) {
      const drag = dragRef.current;
      if (!drag) return;
      if (drag.mode === "boundary") {
        const minute = Math.round((event.clientX - drag.left) / drag.width * (end - start) / SERVICE_PERIOD_STEP)
          * SERVICE_PERIOD_STEP + start - drag.day;
        drag.latestPeriods = moveServiceBoundary(drag.latestPeriods, drag.index, drag.field, minute, true);
        setPeriodDraft(drag.latestPeriods);
        return;
      }
      const delta = Math.round((event.clientX - drag.x) / drag.width * drag.count / grid) * grid;
      if (Math.abs(event.clientX - drag.x) > 3) drag.moved = true;
      if (!drag.moved) return;
      let from = drag.from, to = drag.to;
      if (drag.mode === "start") from = Math.max(0, Math.min(to - grid, from + delta));
      else if (drag.mode === "end") to = Math.min(drag.count, Math.max(from + grid, to + delta));
      else if (drag.mode === "move") {
        const size = to - from;
        from = Math.max(0, Math.min(drag.count - size, from + delta)); to = from + size;
      } else {
        const current = Math.max(0, Math.min(drag.count - grid, Math.floor((event.clientX - drag.left) / drag.width * drag.count / grid) * grid));
        from = Math.min(drag.anchor, current); to = Math.max(drag.anchor, current) + grid;
      }
      drag.latest = [drag.start + from * drag.step, drag.start + to * drag.step];
      setDraft(drag.latest);
    }
    function up() {
      const drag = dragRef.current;
      if (!drag) return;
      dragRef.current = null;
      if (drag.mode === "boundary") {
        setPeriodDraft(null);
        if (drag.latestPeriods !== drag.originalPeriods) periodsChangeRef.current?.(drag.latestPeriods);
        return;
      }
      const next = drag.moved ? drag.latest : drag.mode === "move" || drag.mode === "new"
        ? timeRangeAt(drag.clickedTime, presetsForBuckets(buckets, periodsRef.current), start, end) : drag.original;
      setDraft(null);
      if (next !== drag.original) changeRef.current(next);
    }
    document.addEventListener("mousemove", move); document.addEventListener("mouseup", up);
    window.addEventListener("blur", up);
    return () => { dragRef.current = null; document.removeEventListener("mousemove", move); document.removeEventListener("mouseup", up); window.removeEventListener("blur", up); };
  }, [isActive, start, end, grid, buckets]);
  function begin(event) {
    if (!isActive || event.button !== 0 || !buckets.length) return;
    const rect = graphRef.current.getBoundingClientRect();
    const bucket = Math.max(0, Math.min(buckets.length - 1, Math.floor((event.clientX - rect.left) / rect.width * buckets.length)));
    const handle = event.target.closest("[data-handle]");
    const anchor = Math.min(buckets.length - grid, Math.floor(bucket / grid) * grid);
    const inside = selected && bucket >= selected[0] && bucket < selected[1];
    dragRef.current = { mode: handle ? handle.dataset.handle : inside ? "move" : "new",
      x: event.clientX, left: rect.left, width: rect.width, count: buckets.length, step, start,
      from: selected ? selected[0] : anchor, to: selected ? selected[1] : anchor + grid,
      clickedTime: start + bucket * step, anchor, moved: false, original: range, latest: range };
    event.preventDefault();
  }
  function beginBoundary(event, control) {
    if (!isActive || event.button !== 0 || !graphRef.current) return;
    const rect = graphRef.current.getBoundingClientRect();
    const originalPeriods = periodsRef.current;
    dragRef.current = { mode: "boundary", left: rect.left, width: rect.width, day: control.day,
      index: control.index, field: control.field, originalPeriods, latestPeriods: originalPeriods };
    event.preventDefault();
  }
  function boundaryKey(event, control) {
    if (!["ArrowLeft", "ArrowRight"].includes(event.key)) return;
    event.preventDefault();
    const minute = periods[control.index][control.field] + (event.key === "ArrowRight" ? SERVICE_PERIOD_STEP : -SERVICE_PERIOD_STEP);
    onPeriodsChange?.(moveServiceBoundary(periods, control.index, control.field, minute, true));
  }
  function key(event, edge) {
    if (event.key === "Escape") { onChange(null); return; }
    if (!selected || !["ArrowLeft", "ArrowRight"].includes(event.key)) return;
    event.preventDefault();
    const delta = event.key === "ArrowRight" ? grid : -grid;
    const from = edge === "start" ? Math.max(0, Math.min(selected[1] - grid, selected[0] + delta)) : selected[0];
    const to = edge === "end" ? Math.min(buckets.length, Math.max(selected[0] + grid, selected[1] + delta)) : selected[1];
    onChange([start + from * step, start + to * step]);
  }
  const matched = selection && presets.find((item) => item.from === selection[0] && item.to === selection[1]);
  return <section className="timeline-section">
    <div className="section-heading timeline-heading"><h2>{t(station ? "passengerFlow.stationMovements" : "passengerFlow.onboardAverage")}</h2>
      <div className="time-context"><span className="range-label">{selection ? rangeLabel(selection[0], selection[1])
        : Number.isFinite(start) ? rangeLabel(start, end) : t("passengerFlow.noRecords")}</span>
        {selection ? <button type="button" className="range-clear-button" onClick={() => onChange(null)}>{t("passengerFlow.clear")}</button> : null}</div>
      {pending ? <span className="subtle">{t("passengerFlow.updating")}</span> : null}</div>
    <div className="timeline-chart"><div className="y-axis"><div className="timeline-scale">
      {ticks.map((value, index) => <span className="y-tick" key={index} style={{ top: `${index / (ticks.length - 1) * 100}%` }}>{format(value)}</span>)}</div></div>
      <div className="timeline-main"><div className="period-band">{presets.map((period) => <button key={period.id} type="button"
        className={`period-preset-button${matched?.id === period.id ? " is-active" : ""}`} aria-pressed={matched?.id === period.id}
        style={{ left: `${(period.from - start) / (end - start) * 100}%`, width: `${(period.to - period.from) / (end - start) * 100}%` }}
        onClick={() => onChange(matched?.id === period.id ? null : [period.from, period.to])}>
        <span className="period-name">{t(period.labelKey)}</span><span className="period-time">{rangeLabel(period.from, period.to)}</span></button>)}
        {borderControls.map((control) => <button key={control.id} type="button" className="period-divider"
          style={{ left: `${(control.time - start) / (end - start) * 100}%` }}
          onMouseDown={(event) => beginBoundary(event, control)} onKeyDown={(event) => boundaryKey(event, control)}
          aria-label={t("passengerFlow.adjustPeriod")}><span className="period-grip">↔</span></button>)}</div>
        <div className="graph-field" ref={graphRef} onMouseDown={begin}>
        {ticks.map((_, index) => <div className="horizontal-gridline" key={index} style={{ top: `${index / (ticks.length - 1) * 100}%` }} />)}
        <div className="timeline-bars">{points.map((point, index) => <div key={buckets[index] || index}
          className={`timeline-bar${station ? " is-station" : ""}${!selected || index >= selected[0] && index < selected[1] ? " is-in-range" : ""}`}
          style={{ left: columnEdges[index], width: columnEdges[index + 1] - columnEdges[index],
            transform: station ? undefined : `scaleY(${point?.value == null ? 0 : Math.max(0, point.value) / top})`,
            backgroundColor: station ? "transparent" : !selected || index >= selected[0] && index < selected[1] ? color : "#596160" }}>
            {station ? <>
              <div className="alight-bar" style={{ backgroundColor: !selected || index >= selected[0] && index < selected[1] ? undefined : "#646a69", transform: `translateY(${-Math.max(0, point?.boardings || 0) / top * 100}%) scaleY(${Math.max(0, point?.alightings || 0) / top})` }} />
              <div className="board-bar" style={{ backgroundColor: !selected || index >= selected[0] && index < selected[1] ? undefined : "#505958", transform: `scaleY(${Math.max(0, point?.boardings || 0) / top})` }} />
            </> : null}
          </div>)}</div>
        {boundaries.map((time) => <div key={time} className="service-divider" style={{ left: `${(time - start) / (end - start) * 100}%` }} />)}
        {framePath ? <svg className="range-outline" viewBox={`0 0 ${graphBounds.width} ${graphBounds.height}`} aria-hidden="true">
          <path d={framePath} className="range-glow" strokeWidth={6 * graphBounds.height / 160} />
          <path d={framePath} className="range-stroke" strokeWidth={frameUnit} />
          {gripPath ? <path d={gripPath} className="range-grip" strokeWidth={frameUnit} /> : null}</svg> : null}
        {selected && selected[1] > selected[0] ? <div className="selected-range"
          style={{ left: `${selected[0] / buckets.length * 100}%`, width: `${(selected[1] - selected[0]) / buckets.length * 100}%` }}>
          {showStartHandle ? <button type="button" data-handle="start" className="range-handle is-start" onKeyDown={(event) => key(event, "start")} aria-label={t("passengerFlow.adjustStart")} /> : null}
          {showEndHandle ? <button type="button" data-handle="end" className="range-handle is-end" onKeyDown={(event) => key(event, "end")} aria-label={t("passengerFlow.adjustEnd")} /> : null}</div> : null}
        </div>
        <div className="x-axis" style={{ height: staggerTicks ? "52rem" : "32rem" }}>{Array.from({ length: 25 }, (_, hour) => hour / 24).map((ratio) => <span key={ratio}
          className={ratio === 0 ? "first" : ratio === 1 ? "last" : ""}
          style={{ left: `${ratio * 100}%`, top: staggerTicks && Math.round(ratio * 24) % 2 ? "32rem" : "12rem" }}>{ratio === 1 && end % 1440 === 0 ? "24:00" : timeLabel(start + (end - start) * ratio)}</span>)}</div></div></div>
    {station ? <div className="chart-legend"><span><i className="board-swatch" />{t("passengerFlow.boarding")} {format(boardings)}</span>
      <span><i className="alight-swatch" />{t("passengerFlow.alighting")} {format(alightings)}</span></div> : null}
  </section>;
}
