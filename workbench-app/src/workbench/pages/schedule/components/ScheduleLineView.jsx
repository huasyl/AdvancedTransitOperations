import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import LineRoute from "../../../shared/LineRoute";
import RouteTransition from "../../../shared/RouteTransition";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { quickMinutesToTime } from "../../../../lib/auto-schedule";
import { timeToMinutes } from "../../../../lib/time";
import { animateScrollTopWithTransform } from "../../broadcast/components/BroadcastAnimatedPanels";
import { sectionColor } from "../../passenger/passenger-analysis";
import { buildLineGroups, buildLineItems, lineWindow, LINE_GROUP_GAP, LINE_GROUP_HEAD, LINE_ROW_HEIGHT } from "../schedule-view-model";
import DepartureTimeline from "./DepartureTimeline";
import ScheduleEditor from "./ScheduleEditor";
import { ChevronDownIcon } from "./ScheduleIcons";

const JUMP_MS = 420;
const CLEAR_IDLE = { step: "idle", seconds: 0 };

export default function ScheduleLineView({ controller, diagram, loads, records, isActive, onOpenRecord }) {
  const { t } = useNativeScheduleI18n();
  const { line, auto, topbar, actions } = controller;
  const scrollRef = useRef(null);
  const listRef = useRef(null);
  const tripsRef = useRef(null);
  const contentRef = useRef(null);
  const unitRef = useRef(null);
  const travelRef = useRef(null);
  const travelTimerRef = useRef(0);
  const jumpCleanupRef = useRef(null);
  const routeColor = useRef(topbar.selectedLine.color);
  if (!diagram.loading) routeColor.current = topbar.selectedLine.color;
  const [unit, setUnit] = useState(1);
  const [rowRange, setRowRange] = useState({ start: 0, end: 1248 });
  const [more, setMore] = useState(false);
  const [flash, setFlash] = useState(null);
  const [clearStage, setClearStage] = useState(CLEAR_IDLE);
  const clearTimersRef = useRef([]);
  const rowHeight = Math.round(LINE_ROW_HEIGHT * unit);
  const groupHead = Math.round(LINE_GROUP_HEAD * unit);
  const groupGap = Math.round(LINE_GROUP_GAP * unit);
  const groups = useMemo(() => buildLineGroups(line.rows, auto.quickSegments, rowHeight, groupHead, groupGap), [line.rows, auto.quickSegments, rowHeight, groupHead, groupGap]);
  const items = useMemo(() => buildLineItems(groups, rowHeight, groupHead), [groups, rowHeight, groupHead]);
  const routeStops = useMemo(() => diagram.stops.map((stop) => ({ ...stop,
    detail: Number.isFinite(stop.passengers) ? t("nativeSchedule.line.passengers", { count: stop.passengers }) : undefined })), [diagram.stops, t]);
  const sectionText = useCallback((section) => section ? [t("nativeSchedule.line.passengers", { count: section.loadPassengersSum })] : [], [t]);
  function statusCell(row) {
    const reasons = [
      row.isConflict ? t("nativeSchedule.summary.status.conflict.compact", { reason: row.conflictReasonLabel || t("nativeSchedule.summary.status.conflictUnknown") }) : "",
      row.timedStopsTooShort ? t("nativeSchedule.line.timedShort", { minutes: topbar.minimumScheduleMinutes }) : ""
    ].filter(Boolean);
    const tone = reasons.length ? "is-conflict" : row.isApplied ? "is-applied" : "is-pending";
    const text = reasons.length ? reasons.join(" / ")
      : row.isApplied ? t("nativeSchedule.summary.status.applied")
      : t("nativeSchedule.summary.status.pending");
    return <span className={`dw-demo-status-text ${tone}`}>{text}</span>;
  }
  function runCell(record) {
    if (!record) return null;
    if (record.actualStartMinute != null) {
      const delta = record.actualStartMinute - record.plannedStartMinute;
      return <span className={`dw-departure-trip-run ${delta > 0 ? "is-warning" : delta < 0 ? "is-accent" : "is-good"}`}>{delta === 0 ? t("timetable.status.onTime") : delta > 0 ? t("timetable.status.late", { minutes: delta }) : t("timetable.status.early", { minutes: -delta })}</span>;
    }
    const state = String(record.state || "").toLowerCase();
    const text = { active: "timetable.status.active", missed: "timetable.status.missed", suspended: "timetable.status.suspended", cleared: "timetable.status.interrupted" }[state];
    return text ? <span className={`dw-departure-trip-run ${state === "active" ? "is-accent" : state === "suspended" ? "" : "is-warning"}`}>{t(text)}</span> : null;
  }
  function tripCell(row) {
    if (!row) return <div className="dw-departure-slot" />;
    const flagged = row.isConflict || row.timedStopsTooShort;
    const record = row.isApplied && !flagged ? records?.get(timeToMinutes(row.time)) : null;
    return <div className="dw-departure-slot"><div className={`dw-departure-trip ${flagged ? "is-conflict" : ""}`}>
      <span className="dw-departure-trip-time">{row.time}</span>
      <span><span className={`dw-demo-badge ${row.hasTimedStops ? "is-express" : "is-local"}`}>{t(row.hasTimedStops ? "timetable.mode.custom" : "timetable.mode.default")}</span></span>
      {statusCell(row)}
      {runCell(record)}
      <span className="dw-departure-trip-actions">
        {record ? <button type="button" className="dw-departure-trip-link" onClick={() => onOpenRecord(topbar.selectedLineId, record.tripKey)}>{t("nativeSchedule.line.record")}</button> : null}
        <button type="button" className="dw-demo-link-danger dw-demo-row-action" disabled={line.isApplying} onClick={() => actions.removeSummaryRow(row.id)}>{t("nativeSchedule.summary.action.remove")}</button>
      </span>
    </div></div>;
  }
  function offsetOf(node) {
    const scroll = scrollRef.current;
    let offset = 0;
    while (node && node !== scroll) {
      offset += node.offsetTop;
      node = node.offsetParent;
    }
    return node === scroll ? offset : 0;
  }
  function syncWindow(scrollTop) {
    const scroll = scrollRef.current;
    if (!scroll) return;
    const nextUnit = (unitRef.current?.clientWidth || 100) / 100;
    const nextRow = Math.round(LINE_ROW_HEIGHT * nextUnit);
    const viewport = scroll.clientHeight || 624;
    const from = Math.min(scrollTop, travelRef.current ?? scrollTop);
    const to = Math.max(scrollTop, travelRef.current ?? scrollTop);
    const next = lineWindow(from, to + viewport, offsetOf(listRef.current), nextRow);
    setUnit(nextUnit);
    setRowRange((current) => current.start === next.start && current.end === next.end ? current : next);
    setMore(offsetOf(tripsRef.current) > scrollTop + viewport - nextRow);
  }
  useEffect(() => {
    const scroll = scrollRef.current;
    if (!scroll || !isActive) return undefined;
    let frame = 0;
    function update() {
      frame = 0;
      syncWindow(scroll.scrollTop);
    }
    function schedule() { if (!frame) frame = window.requestAnimationFrame(update); }
    scroll.addEventListener("scroll", schedule);
    window.addEventListener("resize", schedule);
    const observer = typeof ResizeObserver === "function" ? new ResizeObserver(schedule) : null;
    observer?.observe(scroll);
    if (contentRef.current) observer?.observe(contentRef.current);
    schedule();
    return () => { if (frame) window.cancelAnimationFrame(frame); observer?.disconnect(); scroll.removeEventListener("scroll", schedule); window.removeEventListener("resize", schedule); };
  }, [isActive, items.length, topbar.selectedLineId, auto.activeRightTab, line.manualMode]);
  useEffect(() => {
    setFlash(null);
    if (scrollRef.current) { scrollRef.current.scrollTop = 0; syncWindow(0); scrollRef.current.dispatchEvent(new Event("scroll")); }
  }, [topbar.selectedLineId]);
  useEffect(() => () => {
    resetClear();
    window.clearTimeout(travelTimerRef.current);
    window.clearTimeout(jumpCleanupRef.current);
    travelRef.current = null;
    jumpCleanupRef.current = null;
    if (contentRef.current) { contentRef.current.style.transition = ""; contentRef.current.style.transform = ""; }
  }, [isActive, topbar.selectedLineId]);
  function resetClear() {
    clearTimersRef.current.forEach((timer) => window.clearTimeout(timer));
    clearTimersRef.current = [];
    setClearStage(CLEAR_IDLE);
  }
  function handleClear() {
    if (clearStage.step === "confirm") {
      resetClear();
      actions.clearLineSchedule();
      return;
    }
    resetClear();
    setClearStage({ step: "cooldown", seconds: 3 });
    clearTimersRef.current = [
      window.setTimeout(() => setClearStage({ step: "cooldown", seconds: 2 }), 1000),
      window.setTimeout(() => setClearStage({ step: "cooldown", seconds: 1 }), 2000),
      window.setTimeout(() => {
        setClearStage({ step: "confirm", seconds: 0 });
        clearTimersRef.current.push(window.setTimeout(resetClear, 5000));
      }, 3000)
    ];
  }
  function jump(target) {
    const scroll = scrollRef.current;
    if (!scroll) return;
    const top = Math.max(0, Math.min(target, scroll.scrollHeight - scroll.clientHeight));
    window.clearTimeout(travelTimerRef.current);
    travelRef.current = scroll.scrollTop;
    syncWindow(top);
    animateScrollTopWithTransform(scroll, contentRef.current, top, JUMP_MS, jumpCleanupRef);
    scroll.dispatchEvent(new Event("scroll"));
    travelTimerRef.current = window.setTimeout(() => { travelRef.current = null; syncWindow(scroll.scrollTop); }, JUMP_MS + 80);
  }
  function locate(index) {
    const group = groups[index];
    if (!group) return;
    setFlash({ id: group.id, token: Date.now() });
    jump(offsetOf(listRef.current) + group.offset - groupGap);
  }
  const selectedGroupId = groups[line.selectedSegment]?.id;
  const flashGroup = flash ? groups.find((group) => group.id === flash.id) : null;
  const lastGroup = groups[groups.length - 1];
  const tripCount = t("nativeSchedule.line.count", { count: line.rows.length });
  const clearDisabled = line.isApplying || clearStage.step === "cooldown" || (!line.rows.length && !line.quickPending);
  const clearText = clearStage.step === "cooldown" ? t("nativeSchedule.line.clearCountdown", { seconds: clearStage.seconds })
    : clearStage.step === "confirm" ? t("nativeSchedule.line.clearConfirm") : t("nativeSchedule.line.clear");
  return <div className="dw-departure-view">
    <WorkbenchScrollArea className="dw-departure-content" externalScrollRef={scrollRef} metricsKey={`${topbar.selectedLineId}:${items.length}:${auto.activeRightTab}:${line.manualMode}`}>
      <div ref={contentRef} className="dw-departure-flow">
        <span ref={unitRef} className="dw-departure-unit" aria-hidden="true" />
        <div className="dw-departure-upper">
          {diagram?.error ? <div className="dw-departure-skip-note">{t("nativeSchedule.line.diagramFailed", { message: diagram.error })}</div> : null}
          <RouteTransition routeKey={diagram.routeKey} isActive={isActive}>
            <LineRoute stops={routeStops} sections={diagram.sections} color={routeColor.current} isActive={isActive} measureBeforeShow
              emptyText={diagram.loading ? "" : t("nativeSchedule.line.routeEmpty")} originText={t("timetable.table.stop.origin")} getSectionText={sectionText} getSectionColor={(section) => sectionColor(section?.loadRatio)} />
          </RouteTransition>
          {diagram.stops.length ? <div className="dw-departure-load-legend"><span>{t("passengerFlow.loadRatio")}</span>
            {["low", "medium", "high", "missing"].map((key, index) => <span key={key}><i style={{ backgroundColor: sectionColor([0, .3, .8, null][index]) }} />{t(`passengerFlow.${key}`)}</span>)}</div> : null}
          <DepartureTimeline rows={line.draftRows} segments={auto.displaySegments} selectedIndex={line.selectedSegment} onSelect={actions.selectSegment} onMoveBoundary={actions.moveQuickBoundary} isActive={isActive} loads={loads} />
          <ScheduleEditor controller={controller} singleLine onLocateSegment={() => locate(line.selectedSegment)} />
        </div>
        <section ref={tripsRef} className="dw-departure-all-trips">
          <div className="dw-departure-list-title"><h2>{t("nativeSchedule.line.allTrips")}</h2><span>{tripCount}</span>
            <button type="button" className={`dw-demo-link-danger dw-departure-clear ${clearStage.step === "confirm" ? "is-confirm" : ""} ${clearDisabled ? "is-disabled" : ""}`} disabled={clearDisabled} onClick={handleClear}>{clearText}</button>
          </div>
          <div ref={listRef} className="dw-departure-list" style={{ height: `${lastGroup ? lastGroup.offset + lastGroup.height : 0}px` }}>
            {groups.map((group) => <div key={group.id} className={`dw-departure-group ${group.id === selectedGroupId ? "is-selected" : ""}`} style={{ top: `${group.offset}px`, height: `${groupHead}px` }}>
              <strong>{t(group.labelKey)}</strong>{Number.isFinite(group.start) ? <span>{quickMinutesToTime(group.start)} – {quickMinutesToTime(group.end)}</span> : null}<span>{t("nativeSchedule.line.count", { count: group.rows.length })}</span><i />
            </div>)}
            {items.filter((item) => item.top < rowRange.end && item.top + rowHeight > rowRange.start).map((item) => <div key={item.key} className="dw-departure-row" style={{ top: `${item.top}px`, height: `${rowHeight}px` }}>
              {tripCell(item.left)}{tripCell(item.right)}
            </div>)}
            {flashGroup ? <div key={flash.token} className="dw-departure-flash" style={{ top: `${flashGroup.offset}px`, height: `${flashGroup.height}px` }} /> : null}
          </div>
        </section>
      </div>
    </WorkbenchScrollArea>
    {more ? <div className="dw-departure-more"><div className="dw-departure-more-glass"><button type="button" aria-label={t("nativeSchedule.line.allTrips")} onClick={() => jump(offsetOf(tripsRef.current))}><ChevronDownIcon className="dw-departure-more-arrow" /></button></div></div> : null}
  </div>;
}
