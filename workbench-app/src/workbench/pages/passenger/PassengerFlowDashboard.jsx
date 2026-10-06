import { useCallback, useEffect, useMemo, useState } from "react";
import WorkbenchDropdown from "../../../components/WorkbenchDropdown";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import LineRoute from "../../shared/LineRoute";
import PassengerLineTabs from "./components/PassengerLineTabs";
import PassengerOdFlowDiagram from "./components/PassengerOdFlowDiagram";
import PassengerTimeline from "./components/PassengerTimeline";
import PassengerComparisonChart from "./components/PassengerComparisonChart";
import PassengerRouteTransition from "./components/PassengerRouteTransition";
import usePassengerSectionDetails from "./usePassengerSectionDetails";
import { buildBuckets, formatPassengerNumber, lineTextColor, lineTrend, matchingLine, odRanking, PURPOSE_COLORS,
  purposeValues, rangeLabel, sectionColor, stationCatalog, stationName,
  stationOptions, stationTrend, summaryRows, lineComparison, stationComparison, stationGroupId } from "./passenger-analysis";
import "./PassengerFlowDashboard.css";

const rows = (value) => Array.isArray(value) ? value : [];
const number = (value) => Number.isFinite(Number(value)) ? Number(value) : 0;
const format = formatPassengerNumber;
const QuietButton = ({ active, children, ...props }) => <button type="button"
  className={`quiet-button${active ? " is-active" : ""}`} aria-pressed={active === undefined ? undefined : active} {...props}>{children}</button>;
const ChoiceButton = ({ active, children, ...props }) => <button type="button"
  className={`rtw-passenger-line-tab${active ? " is-active" : ""}`} aria-pressed={active} {...props}>{children}</button>;
function Motion({ children, className = "", kind = "content", free = false, visible = true, overview = false }) {
  return <div className={`passenger-motion-mask${free ? " is-unclipped" : ""}${className ? ` ${className}` : ""}`} data-motion-content={kind}
    data-motion-overview={overview ? "true" : undefined} style={visible ? undefined : { display: "none" }}>
    <div className="passenger-motion-body"><div className="passenger-motion-content">{children}</div></div></div>;
}
function Heading({ title, action, motion = false, detailTitle = title }) { return <div className="section-heading"><h2 data-motion-heading={motion ? "true" : undefined}
  data-motion-detail-title={motion ? detailTitle : undefined}>{title}</h2>
  {motion && action ? <Motion free>{action}</Motion> : action}</div>; }
function Dropdown({ label, value, options, onSelect, className = "" }) {
  return <WorkbenchDropdown label={label} value={options.find((item) => item.value === value)?.label || "—"}
    options={options.map((item) => ({ ...item, active: item.value === value }))}
    onSelect={onSelect} className={className} />;
}

function Purpose({ source, field, denominator, title, stationPage, stationKind, onStationKind, motion = false }) {
  const { t } = useNativeScheduleI18n();
  const values = purposeValues(source, field);
  const names = ["purposeWork", "purposeSchool", "purposeShopping", "purposeLeisure", "purposeHome", "purposeOther", "purposeUnknown"];
  return <section className="purpose-section"><Heading title={t("passengerFlow.purpose")} motion={motion}
    action={stationPage ? <div className="small-switch"><ChoiceButton active={stationKind === "boardings"} onClick={() => onStationKind("boardings")}>{t("passengerFlow.boarding")}</ChoiceButton>
      <ChoiceButton active={stationKind === "alightings"} onClick={() => onStationKind("alightings")}>{t("passengerFlow.alighting")}</ChoiceButton></div> : null} />
    <Motion><div className="purpose-caption">{title ? <span>{title}</span> : null}<span>{format(denominator)} {t("passengerFlow.trips")}</span></div></Motion>
    {values == null ? <Motion><div className="rtw-passenger-empty">{t("passengerFlow.purposeMissing")}</div></Motion> : denominator == null || denominator === 0 ?
      <Motion><div className="rtw-passenger-empty">{t("passengerFlow.noData")}</div></Motion> : <>
        <Motion><div className="purpose-composition">{values.map((value, index) => <div key={index}
          style={{ width: `${value / denominator * 100}%`, backgroundColor: PURPOSE_COLORS[index] }} />)}</div></Motion>
        <div className="purpose-list">{values.map((value, index) => <Motion key={index} kind="row"><div className="purpose-row">
          <span className="purpose-dot" style={{ backgroundColor: PURPOSE_COLORS[index] }} /><span className="purpose-name">{t(`passengerFlow.${names[index]}`)}</span>
          <div className="purpose-track"><div style={{ width: `${value / denominator * 100}%`, backgroundColor: PURPOSE_COLORS[index] }} /></div>
          <span className="purpose-number">{format(value)}</span><span className="purpose-percent">{format(value / denominator * 100, 1)}%</span>
        </div></Motion>)}</div></>}
  </section>;
}

function Sections({ sections, stops, lines, catalog, focus, onFocus, details, pending, periodText }) {
  const { t } = useNativeScheduleI18n();
  const lineNames = new Map(lines.map((line) => [line.id, line.name]));
  const lineColors = new Map(lines.map((line) => [line.id, line.color]));
  const byLine = new Map();
  stops.forEach((stop) => { if (!byLine.has(stop.lineId)) byLine.set(stop.lineId, []); byLine.get(stop.lineId).push(stop); });
  const sorted = [...sections].sort((a, b) => number(b.loadPassengersSum) - number(a.loadPassengersSum)
    || String(a.lineId || "").localeCompare(String(b.lineId || ""))
    || String(a.fromStationId).localeCompare(String(b.fromStationId))
    || String(a.toStationId).localeCompare(String(b.toStationId)));
  const max = Math.max(1, ...sorted.map((row) => number(row.loadPassengersSum)));
  function renderRow(row) { const key = `${row.lineId}\u0000${row.fromStationId}\u0000${row.toStationId}`;
      const selected = focus.kind === "section" && focus.key === key;
      return <Motion key={key} className="section-record" kind="row"><button type="button" className={`section-row${selected ? " is-selected" : ""}`}
        aria-pressed={selected} onClick={() => onFocus(selected ? { kind: "line" } : { kind: "section", key, row })}>
        <span className="section-name" title={`${stationName(catalog, row.fromStationId)} → ${stationName(catalog, row.toStationId)}`}>
          {row.lineId && byLine.size > 1 ? <span>{lineNames.get(row.lineId) || row.lineId}</span> : null}
          <span>{`${stationName(catalog, row.fromStationId)} → ${stationName(catalog, row.toStationId)}`}</span></span>
        <span className="section-track"><span className="section-fill" style={{ width: `${number(row.loadPassengersSum) / max * 100}%`, backgroundColor: sectionColor(row.loadRatio) }} /></span>
        <span className="section-number">{format(row.loadPassengersSum)}</span>
        <span className="section-ratio">{row.loadRatio == null ? "—" : `${format(row.loadRatio * 100, 0)}%`}</span>
      </button>{selected ? rows(row.lineContributions).map((part) =>
        <div key={part.lineId} className="section-contribution">
          <span className="purpose-dot" style={{ backgroundColor: lineColors.get(part.lineId) || "#64748b" }} />
          <span className="section-contribution-name">{lineNames.get(part.lineId) || part.lineId}</span>
          <span className="section-contribution-count">{`${format(part.loadPassengersSum)} ${t("passengerFlow.trips")}`}</span>
          <span className="section-ratio">{format(row.loadPassengersSum > 0 ? part.loadPassengersSum / row.loadPassengersSum * 100 : 0, 1)}%</span>
        </div>) : null}</Motion>;
  }
  return <section className="section-loads"><div className="section-summary-head"><Heading motion detailTitle={t("passengerFlow.sectionDetails")} title={t(details.detail ? "passengerFlow.sectionDetails" : "passengerFlow.sections")}
    action={sorted.length > 6 || details.detail ? <QuietButton onClick={details.toggle} disabled={details.moving || pending}
      aria-expanded={details.detail}>{details.detail ? `← ${t("passengerFlow.backOverview")}` : t("passengerFlow.viewDetails")}</QuietButton> : null} />
    {details.detail ? <Motion><div className="section-summary-caption">{periodText}</div></Motion> : null}
    <Motion><div className="section-row section-columns"><span className="section-name">{t("passengerFlow.directedSection")}</span><span className="section-track" /><span className="section-number">{t("passengerFlow.sectionTotal")}</span><span className="section-ratio">{t("passengerFlow.loadRatio")}</span></div></Motion>
    </div>{sorted.slice(0, 6).map(renderRow)}
    <div className="section-remainder" style={{ display: details.detail ? "block" : "none" }}>
      <div className="section-remainder-content">{sorted.slice(6).map(renderRow)}</div>
    </div>
    {!sorted.length ? <Motion><div className="rtw-passenger-empty">{t("passengerFlow.sectionEmpty")}</div></Motion> : null}
  </section>;
}

function Od({ flows, lines, catalog, focus, onFocus, isActive }) {
  const { t } = useNativeScheduleI18n();
  const [hovered, setHovered] = useState("");
  const ranking = useMemo(() => odRanking(flows, catalog), [flows, catalog]);
  const active = ranking.some((row) => row.originStationId === hovered || row.destinationStationId === hovered) ? hovered : "";
  const departures = active ? ranking.filter((row) => row.originStationId === active && row.volume > 0) : [];
  const arrivals = active ? ranking.filter((row) => row.destinationStationId === active && row.volume > 0) : [];
  const split = departures.length > 0 && arrivals.length > 0;
  const hover = useCallback((stationId) => setHovered(stationId), []);
  const lineColors = new Map(lines.map((line) => [line.id, line.color]));
  function relationColor(row) {
    const primary = lineColors.get(row.dominantLineId || row.firstLineId);
    if (primary) return primary;
    const volumes = new Map();
    rows(row.lineContributions).forEach((part) => {
      if (lineColors.get(part.firstLineId)) volumes.set(part.firstLineId,
        (volumes.get(part.firstLineId) || 0) + number(part.completedCount));
    });
    const available = [...volumes].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));
    return available.length ? lineColors.get(available[0][0]) : "#81d4d4";
  }
  function renderRanking(title, source, direction) {
    const visible = source.slice(0, 7), max = Math.max(1, ...visible.map((row) => row.volume));
    return <div className="od-ranking" key={direction}><div className="section-heading"><h3>{title}</h3><span className="subtle">{t("passengerFlow.trips")}</span></div>
      {visible.map((row, index) => { const key = `${row.originStationId}\u0000${row.destinationStationId}`;
        const name = direction === "out" ? row.destinationName : direction === "in" ? row.originName : `${row.originName} → ${row.destinationName}`;
        return <button type="button" key={key} className={`od-relation${focus.kind === "od" && focus.key === key ? " is-selected" : ""}`}
          aria-pressed={focus.kind === "od" && focus.key === key}
          onClick={() => onFocus(focus.kind === "od" && focus.key === key ? { kind: "line" } : { kind: "od", key, row })}><span className="relation-rank">{String(index + 1).padStart(2, "0")}</span>
          <span className="relation-name" title={name}>{name}</span>
          <span className="relation-track"><span className="relation-bar" style={{ width: `${row.volume / max * 100}%`, backgroundColor: relationColor(row) }} /></span>
          <strong>{format(row.volume)}</strong></button>; })}
      {!visible.length ? <div className="rtw-passenger-empty">{t("passengerFlow.noRecords")}</div> : null}</div>;
  }
  return <section className="od-section"><Heading title={t("passengerFlow.od")} action={<span className="subtle">{focus.kind === "station" ? stationName(catalog, focus.stationId) : t("passengerFlow.allStations")}</span>} />
    {!ranking.some((row) => row.volume > 0) ? <div className="rtw-passenger-empty">{t("passengerFlow.noRecords")}</div> :
    <div className="od-layout"><div className="chord-wrap"><PassengerOdFlowDiagram flows={flows} lines={lines} isActive={isActive} onStationHover={hover} /></div>
      <div className="od-relations"><div className="ranking-station">{active ? stationName(catalog, active) : t("passengerFlow.completed")}</div>
        <div className={`od-rankings${split ? " is-split" : ""}`}>{active ? <>
          {departures.length > 0 ? renderRanking(t("passengerFlow.departureRanking"), departures, "out") : null}
          {arrivals.length > 0 ? renderRanking(t("passengerFlow.arrivalRanking"), arrivals, "in") : null}</> : renderRanking(t("passengerFlow.odRanking"), ranking, "all")}</div>
      </div></div>}
  </section>;
}


function Transfers({ transfers, stationId, lineId, mode, catalog, lineCatalogs }) {
  const { t } = useNativeScheduleI18n();
  const line = (modeKey, id) => lineCatalogs?.[modeKey]?.find((item) => item.id === id);
  const filtered = transfers.filter((row) =>
    (row.fromStationGroupId || row.fromStationId) === stationId && row.fromMode === mode && (lineId === "ALL" || row.fromLineId === lineId) ||
    (row.toStationGroupId || row.toStationId) === stationId && row.toMode === mode && (lineId === "ALL" || row.toLineId === lineId));
  function renderRow(row) {
    const key = `${row.fromMode}:${row.fromLineId}:${row.fromStationId}:${row.fromStationOccurrence}>${row.toMode}:${row.toLineId}:${row.toStationId}:${row.toStationOccurrence}`;
    const from = line(row.fromMode, row.fromLineId), to = line(row.toMode, row.toLineId);
    return <div key={key} className="transfer-row">
      <span className="transfer-names">
        <span className="transfer-end"><span className="line-tag" style={{ backgroundColor: from?.color || "#64748b", color: lineTextColor(from?.color || "#64748b") }}>{from?.name || row.fromLineId}</span>
          <span className="station-label">{stationName(catalog, row.fromStationId)}</span></span>
        <span className="transfer-arrow" aria-hidden="true">→</span>
        <span className="transfer-end"><span className="line-tag" style={{ backgroundColor: to?.color || "#64748b", color: lineTextColor(to?.color || "#64748b") }}>{to?.name || row.toLineId}</span>
          <span className="station-label">{stationName(catalog, row.toStationId)}</span></span>
      </span>
      <span className="transfer-meta"><span className="transfer-type">{t(row.fromStationId === row.toStationId ? "passengerFlow.same" : "passengerFlow.outside")}</span>
        <span className="transfer-time">{row.averageWalkMinutes == null ? "—" : `${format(row.averageWalkMinutes, 1)}${t("passengerFlow.minutes")}`}</span>
        <span className="transfer-walk">{row.averageWalkPathMeters == null ? "—" : `${format(row.averageWalkPathMeters)}${t("passengerFlow.meters")}`}</span></span></div>;
  }
  return <section className="transfer-section"><Heading title={t("passengerFlow.transfers")} />
    <div className="transfer-table">
      {filtered.map((row) => renderRow(row))}{!filtered.length ? <div className="rtw-passenger-empty">{t("passengerFlow.noRecords")}</div> : null}
    </div>
  </section>;
}

export default function PassengerFlowDashboard({ mode, isActive, context, analysis, pending, lines, lineCatalogs, stopCatalogs,
  lineId, selectedLineId = lineId, onLineChange, page, onPageChange, stationId, onStationChange, range, onRangeChange, day, onDayChange, servicePeriods, onServicePeriodsChange, scrollRef }) {
  const { t } = useNativeScheduleI18n();
  const [focus, setFocus] = useState({ kind: "line" });
  const [stationKind, setStationKind] = useState("boardings");
  const details = usePassengerSectionDetails({ scrollRef, isActive,
    selectionKey: `${mode}:${lineId}:${page}:${day}:${range?.join(":") || ""}` });
  useEffect(() => { setFocus({ kind: "line" }); }, [lineId, mode, range, day]);
  const buckets = useMemo(() => buildBuckets(context), [context]);
  const catalog = useMemo(() => stationCatalog(context), [context]);
  const options = useMemo(() => stationOptions(context, lineId), [context, lineId]);
  const station = stationId || options[0]?.id || "";
  const points = useMemo(() => page === "station" ? stationTrend(context, lineId, station, buckets)
    .map((row) => ({ ...row, value: row.available ? row.boardings + row.alightings : null }))
    : lineTrend(context, lineId, buckets), [page, context, lineId, station, buckets]);

  const summary = analysis?.summary;
  const stationRows = summaryRows(analysis, "stationVolumes").filter((row) => matchingLine(row, lineId));
  const sectionRows = summaryRows(analysis, "sectionVolumes").filter((row) => matchingLine(row, lineId));
  const odRows = summaryRows(analysis, "odFlows").filter((row) => matchingLine(row, lineId))
    .map((row) => ({ ...row, originStationId: row.originStationGroupId || row.originStationId,
      destinationStationId: row.destinationStationGroupId || row.destinationStationId }));
  const transferRows = summaryRows(analysis, "transferFlows");
  const lineStops = rows(context?.lineStops).filter((row) => lineId === "ALL" || row.lineId === lineId);
  useEffect(() => {
    setFocus((previous) => {
      if (previous.kind !== "station") return previous;
      const members = previous.memberStationIds || [previous.stationId];
      const surviving = lineStops.find((stop) => members.includes(stop.stationId));
      if (!surviving) return { kind: "line" };
      const id = stationGroupId(surviving);
      return { ...previous, stationId: id,
        memberStationIds: rows(context?.stationGroups).find((group) => group.stationGroupId === id)?.memberStationIds || [surviving.stationId] };
    });
  }, [context]);
  const selectedLine = lines.find((line) => line.id === lineId);
  const comparisonRows = useMemo(() => lineId === "ALL" ? lineComparison(analysis, lines, mode)
    : stationComparison(analysis, lineStops, catalog, mode, lineId, selectedLine?.color),
    [analysis, lines, mode, lineId, context, catalog, selectedLine?.color]);
  const boardings = summary?.boardings;
  let purposeRows = [{ purposeCounts: summary?.purposeCounts }], purposeField = "boardings", purposeDenominator = boardings;
  let purposeTitle = "";
  if (page === "station") {
    purposeRows = [{ purposeCounts: summary?.purposeCounts }];
    purposeField = stationKind;
    purposeDenominator = summary?.[stationKind];
  } else if (focus.kind === "station") {
    purposeRows = stationRows.filter((row) => stationGroupId(row) === focus.stationId && row.lineId === focus.lineId);
    purposeDenominator = purposeRows.reduce((sum, row) => sum + number(row.boardings), 0);
    purposeTitle = stationName(catalog, focus.stationId);
  } else if (focus.kind === "section") {
    purposeRows = sectionRows.filter((row) => `${row.lineId}\u0000${row.fromStationId}\u0000${row.toStationId}` === focus.key);
    purposeField = "loadPassengersSum";
    purposeDenominator = purposeRows.reduce((sum, row) => sum + number(row.loadPassengersSum), 0);
    purposeTitle = `${stationName(catalog, focus.row.fromStationId)} → ${stationName(catalog, focus.row.toStationId)}`;
  } else if (focus.kind === "od") {
    purposeRows = odRows.filter((row) => `${row.originStationId}\u0000${row.destinationStationId}` === focus.key);
    purposeField = "completedCount";
    purposeDenominator = purposeRows.reduce((sum, row) => sum + number(row.completedCount), 0);
    purposeTitle = `${stationName(catalog, focus.row.originStationId)} → ${stationName(catalog, focus.row.destinationStationId)}`;
  }
  return <div ref={details.dashboardRef} className={`rtw-passenger-dashboard${details.moving ? " is-detail-moving" : ""}`}
    data-detail-phase={details.phase === "exit-headings" ? "exit" : details.phase} style={details.motionStyle}>
    <Motion free overview visible={details.overviewVisible}>
    <div className="rtw-passenger-subtabs"><nav aria-label={t("passengerFlow.passengers")}>{[["line", t("passengerFlow.linePage")], ["station", t("passengerFlow.stationPage")]].map(([key, title]) =>
      <button key={key} type="button" className={`rtw-passenger-subtab${page === key ? " is-active" : ""}`}
        onClick={() => onPageChange(key)} aria-pressed={page === key}>{title}</button>)}</nav>
      <div className="small-switch">{["today", "yesterday"].map((value) =>
        <ChoiceButton key={value} active={day === value} onClick={() => onDayChange(value)}>{t(`passengerFlow.${value}`)}</ChoiceButton>)}</div></div></Motion>
    <Motion free overview visible={details.overviewVisible}>
    <div className="passenger-toolbar">
      <PassengerLineTabs lines={lines} selectedLineId={selectedLineId} onSelect={onLineChange} allLabel={t("passengerFlow.allLines")} />
      {page === "station" ? <div className="station-selector"><Dropdown label={t("passengerFlow.station")} value={station} options={options.map((item) => ({ value: item.id, label: item.name }))}
        onSelect={onStationChange} /></div> : null}</div></Motion>
    <div data-motion-overview="true" style={{ display: details.overviewVisible ? "block" : "none" }}><Motion>
    <PassengerTimeline buckets={buckets} points={points} range={range} onChange={onRangeChange}
      station={page === "station"} boardings={summary?.boardings} alightings={summary?.alightings} color={selectedLine?.color || "#81d4d4"} pending={pending} isActive={isActive} periods={servicePeriods} onPeriodsChange={onServicePeriodsChange} />
    </Motion></div>
    <div className="analysis-content">
      {context && !context.samplingReady ? <Motion><div className="rtw-passenger-status">{t("passengerFlow.samplingNotReady")}</div></Motion> : null}
      {analysis && !summary ? <Motion><div className="rtw-passenger-status">{t("passengerFlow.summaryMissing")}</div></Motion> : null}
      {page === "line" ? <>
        <div data-motion-overview="true" style={{ display: details.overviewVisible ? "block" : "none" }}><Motion>
        {lineId !== "ALL" ? <section className="routes-section">{lines.filter((line) => line.id === lineId).map((line) => {
          const stops = lineStops.filter((stop) => stop.lineId === line.id);
          return <PassengerRouteTransition routeKey={`${mode}:${line.id}`} key="passenger-route" isActive={isActive && !details.detail}>
            <LineRoute stops={stops} sections={sectionRows.filter((row) => row.lineId === line.id)} color={line.color} isActive={isActive} measureBeforeShow
              emptyText={t("passengerFlow.routeMissing")} originText={t("passengerFlow.origin")}
              getSectionColor={(section) => sectionColor(section?.loadRatio)}
              getSectionText={(section) => section?.loadRatio == null ? "—" : `${format(section.loadRatio * 100)}%`}
              isStationSelected={(stop) => focus.kind === "station" && stationGroupId(stop) === focus.stationId} selectedSection={focus.kind === "section" ? focus.key.split("\u0000").slice(1).join("\u0000") : ""}
              onStation={(stop) => { const id = stationGroupId(stop); onStationChange(id);
                setFocus(focus.kind === "station" && focus.stationId === id ? { kind: "line" } : { kind: "station", stationId: id, lineId: line.id,
                  memberStationIds: rows(context?.stationGroups).find((group) => group.stationGroupId === id)?.memberStationIds || [stop.stationId] }); }}
              onSection={(from, to, row) => { const key = `${line.id}\u0000${from.stationId}\u0000${to.stationId}`;
                setFocus(focus.kind === "section" && focus.key === key ? { kind: "line" } : { kind: "section", key,
                  row: row || { lineId: line.id, fromStationId: from.stationId, toStationId: to.stationId } }); }} /></PassengerRouteTransition>;
        })}<div className="route-footer"><div className="load-legend"><span>{t("passengerFlow.loadRatio")}</span>
          {["low", "medium", "high", "missing"].map((key, index) => <span key={key}><i style={{ backgroundColor: sectionColor([0, .3, .8, null][index]) }} />{t(`passengerFlow.${key}`)}</span>)}</div>
          {focus.kind === "station" ? <QuietButton onClick={() => onPageChange("station")}>{t("passengerFlow.stationPage")} {stationName(catalog, focus.stationId)} →</QuietButton> : null}
          </div></section> : null}
        <PassengerComparisonChart key={`${mode}:${lineId}`} rows={comparisonRows} station={lineId !== "ALL"} pending={pending} isActive={isActive} />
        </Motion></div>
        <div className="analysis-columns" ref={details.columnsRef}><div className="analysis-left"><Sections sections={sectionRows} stops={lineStops} lines={lines} catalog={catalog} focus={focus} onFocus={setFocus} details={details} pending={pending || !summary}
          periodText={range ? rangeLabel(...range) : "00:00 — 24:00"} /></div>
          <div className="analysis-right"><Purpose motion source={purposeRows} field={purposeField} denominator={summary ? purposeDenominator : null}
            title={purposeTitle} /></div></div>
        <div style={{ display: details.overviewVisible ? "block" : "none" }}><Motion>
        <Od flows={odRows.filter((row) => focus.kind !== "station" ||
          matchingLine(row, focus.lineId) && (row.originStationId === focus.stationId || row.destinationStationId === focus.stationId))
          .map((row) => ({ ...row, volume: row.completedCount,
          originName: stationName(catalog, row.originStationId), destinationName: stationName(catalog, row.destinationStationId) }))}
          lines={lines} catalog={catalog} focus={focus} onFocus={setFocus} isActive={isActive} />
        </Motion></div>
      </> : <>
        <div className="analysis-columns station-columns"><div className="analysis-left"><Transfers transfers={transferRows}
          stationId={station} lineId={lineId} mode={mode} catalog={catalog} lineCatalogs={lineCatalogs} />
          </div>
          <div className="analysis-right"><Purpose source={purposeRows} field={purposeField} denominator={summary ? purposeDenominator : null}
            title={purposeTitle} stationPage stationKind={stationKind} onStationKind={setStationKind} /></div></div>
      </>}
    </div>
  </div>;
}
