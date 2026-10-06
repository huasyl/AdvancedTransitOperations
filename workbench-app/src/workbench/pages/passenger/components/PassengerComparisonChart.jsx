import { useEffect, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import { comparisonLightColor, comparisonNameLines, comparisonPageLayout, formatPassengerNumber } from "../passenger-analysis";
import { passengerScale } from "../passenger-scale";

const GRAPH_HEIGHT = 160;
const BAR_WIDTH = 16, BAR_SPACE = 8;
const BOARD_COLOR = "#369eff", ALIGHT_COLOR = "#ed5bbf";

function scaleTop(values) {
  const max = Math.max(0, ...values.filter((value) => value != null));
  if (max <= 1) return 1;
  const step = 10 ** Math.floor(Math.log10(max));
  return Math.ceil(max / step) * step;
}

export default function PassengerComparisonChart({ rows, station = false, pending = false, isActive = false }) {
  const { t } = useNativeScheduleI18n();
  const fieldRef = useRef(null);
  const [width, setWidth] = useState(0), [page, setPage] = useState(0);
  const layout = comparisonPageLayout(rows.length, width, page);
  const visible = rows.slice(layout.start, layout.end);
  useEffect(() => {
    if (!isActive || !fieldRef.current) return undefined;
    const node = fieldRef.current;
    function measure() {
      const rect = node.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0) setWidth(rect.width / (rect.height / GRAPH_HEIGHT));
    }
    const observer = typeof ResizeObserver === "function" ? new ResizeObserver(measure) : null;
    observer?.observe(node);
    const frame = window.requestAnimationFrame(measure);
    window.addEventListener("resize", measure);
    return () => { observer?.disconnect(); window.cancelAnimationFrame(frame); window.removeEventListener("resize", measure); };
  }, [isActive, rows.length]);
  const firstTitle = t(station ? "passengerFlow.waitingCount" : "passengerFlow.passengerVolume");
  const secondTitle = t("passengerFlow.loadRatio");
  const firstUnit = t(station ? "passengerFlow.person" : "passengerFlow.trips"), secondUnit = "%";
  const { top: firstTop, ticks } = passengerScale(rows.flatMap((row) => station ? [row.total, row.first] : [row.first]));
  const secondTop = scaleTop(rows.map((row) => row.second));
  const leftUnit = station ? t("passengerFlow.quantity") : firstUnit;
  const columnCount = 2, groupWidth = columnCount * BAR_WIDTH + (columnCount - 1) * BAR_SPACE;
  const fractions = ticks.map((value) => value / firstTop);
  const names = visible.map((row) => comparisonNameLines(row.name, layout.nameUnits));
  const nameHeight = Math.max(48, ...names.map((parts) => parts.length * 18 + 24));
  const legendColor = station ? rows[0]?.color : null;
  const valueText = (value, digits = 1) => formatPassengerNumber(value, digits);
  function axis(top, unit, side) {
    return <div className={`comparison-axis is-${side}`}><span className="comparison-unit">{unit}</span>
      {fractions.map((fraction) => <span key={fraction} className="comparison-tick" style={{ top: `${(1 - fraction) * 100}%` }}>
        {formatPassengerNumber(top * fraction, side === "left" ? 0 : 1)}</span>)}</div>;
  }
  return <section className="comparison-section" aria-busy={pending} style={{ "--comparison-height": `${GRAPH_HEIGHT}rem`, "--comparison-bar-width": `${BAR_WIDTH}rem` }}>
    <div className="section-heading"><h2>{t(station ? "passengerFlow.stationComparison" : "passengerFlow.lineComparison")}</h2>
      <div className="comparison-legend">{station ? <>
        <span><i className="comparison-key" style={{ backgroundColor: BOARD_COLOR }} />{t("passengerFlow.boarding")} ({t("passengerFlow.trips")})</span>
        <span><i className="comparison-key" style={{ backgroundColor: ALIGHT_COLOR }} />{t("passengerFlow.alighting")} ({t("passengerFlow.trips")})</span>
      </> : null}<span><i className="comparison-key" style={legendColor ? { backgroundColor: legendColor } : undefined} />{firstTitle} ({firstUnit})</span>
        {!station ? <span><i className="comparison-key is-secondary" />{secondTitle} ({secondUnit})</span> : null}</div></div>
    {!rows.length ? <div className="rtw-passenger-empty">{t("passengerFlow.noRecords")}</div> :
      <div className="comparison-chart">{axis(firstTop, leftUnit, "left")}
        <div className="comparison-main"><div className="comparison-canvas" style={{ height: `${GRAPH_HEIGHT + nameHeight}rem` }}>
          <div className="comparison-field" ref={fieldRef}>
          {fractions.map((fraction) => <div key={fraction} className="comparison-gridline" style={{ top: `${(1 - fraction) * 100}%` }} />)}
          {visible.map((row, index) => <div key={row.id} className="comparison-category" style={{ left: `${index / visible.length * 100}%`, width: `${100 / visible.length}%` }}>
            {[...(station ? [{ value: row.total, top: firstTop, segments: [
              { value: row.boardings, color: BOARD_COLOR }, { value: row.alightings, color: ALIGHT_COLOR }] }] : []),
              { value: row.first, top: firstTop, color: row.color },
              ...(!station ? [{ value: row.second, top: secondTop, color: comparisonLightColor(row.color), secondary: true }] : [])].map((item, metricIndex) => {
              const height = item.value == null ? 0 : item.value / item.top * GRAPH_HEIGHT;
              const offset = -groupWidth / 2 + metricIndex * (BAR_WIDTH + BAR_SPACE);
              const title = item.segments ? t("passengerFlow.movementTotal") : item.secondary ? secondTitle : firstTitle;
              return <div key={metricIndex} className={`comparison-column${item.secondary ? " is-secondary" : ""}`} style={{ transform: `translateX(${offset}rem)` }}>
                <span className="comparison-value" title={title} style={{ bottom: `${height + 8}rem` }}>{valueText(item.value, !station && !item.secondary ? 0 : 1)}</span>
                {item.segments ? item.segments.map((segment, segmentIndex) => segment.value > 0 ? <div key={segmentIndex} className="comparison-bar"
                  style={{ bottom: `${segmentIndex ? row.boardings / firstTop * GRAPH_HEIGHT : 0}rem`, height: `${segment.value / firstTop * GRAPH_HEIGHT}rem`, backgroundColor: segment.color }} /> : null)
                  : item.value > 0 ? <div className="comparison-bar" style={{ height: `${height}rem`, backgroundColor: item.color }} /> : null}
              </div>;
            })}
            <div className="comparison-name" title={row.name}>{names[index].map((part, partIndex) => <span key={partIndex}>{part}</span>)}</div>
          </div>)}
          </div>
        </div></div>{!station ? axis(secondTop, secondUnit, "right") : null}</div>}
    {layout.pages > 1 ? <div className="comparison-pages">
      <button type="button" className={`quiet-button${layout.page === 0 ? " is-disabled" : ""}`} onClick={() => setPage(Math.max(0, layout.page - 1))} disabled={layout.page === 0}>{t("passengerFlow.previousPage")}</button>
      <span>{layout.start + 1}–{layout.end} / {rows.length}</span>
      <button type="button" className={`quiet-button${layout.page === layout.pages - 1 ? " is-disabled" : ""}`} onClick={() => setPage(Math.min(layout.pages - 1, layout.page + 1))} disabled={layout.page === layout.pages - 1}>{t("passengerFlow.nextPage")}</button>
    </div> : null}
  </section>;
}
