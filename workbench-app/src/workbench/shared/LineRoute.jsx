import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import "./LineRoute.css";

const EMPTY = [];
const rem = (value) => `${value}rem`;
const nameOffset = 16;
const textWidth = (name, size = 14) => Array.from(String(name || "")).reduce((width, character) =>
  width + (character.charCodeAt(0) <= 255 ? .6 : 1), 0) * size;
const nameTokens = (name) => String(name || "").match(/[A-Za-z0-9\u00c0-\u024f]+(?:['’-][A-Za-z0-9\u00c0-\u024f]+)*|\s+|[^\s]/gu) || [];
const rowsOfDetail = (detail) => Array.isArray(detail) ? detail : detail ? [detail] : [];

function stationNameLines(name, limit, measure, size = 14) {
  if (measure(name, size) <= limit) return name ? [String(name)] : [];
  const lines = [];
  let line = "", width = 0;
  const tokens = nameTokens(name);
  let space = false;
  tokens.forEach((token) => {
    if (/^\s+$/u.test(token)) { space = !!line; return; }
    const next = measure(token, size), separator = space && line ? measure(" ", size) : 0;
    if (line && width + separator + next > limit + 1e-6) { lines.push(line); line = ""; width = 0; }
    if (next > limit) {
      Array.from(token).forEach((character) => {
        const nextWidth = measure(character, size);
        if (line && width + nextWidth > limit + 1e-6) { lines.push(line); line = ""; width = 0; }
        line += character; width += nextWidth;
      });
      space = false; return;
    }
    if (space && line) { line += " "; width += measure(" ", size); }
    line += token; width += next; space = false;
  });
  if (line) lines.push(line);
  return lines;
}

export function routeLayout(stops, availableWidth, sectionMap, getSectionText, measure = textWidth, originText = "") {
  if (!stops.length) return { width: availableWidth, height: 180, nodes: [], edges: [] };
  const horizontal = stops.length < 4 ? stops.length : stops.length - 2;
  const counts = [Math.ceil(horizontal / 2), stops.length >= 4 ? 1 : 0,
    Math.floor(horizontal / 2), stops.length >= 4 ? 1 : 0];
  const width = availableWidth;
  const sideInset = Math.min(160, width / 4);
  const railWidth = width - sideInset * 2;
  const gap = 16;
  let cursor = 0;
  const sides = [[], [], [], []];
  const originWidth = Math.max(48, measure(originText, 12) + measure(stops.length >= 4 ? "↑" : "→", 12) + 8);
  function sizeLabel(node, labelWidth) {
    const name = node.stop.stationName || node.stop.stopName || node.stop.stationId;
    node.nameLines = stationNameLines(name, Math.max(1, labelWidth), measure);
    const parts = rowsOfDetail(node.stop.detail);
    node.detailLines = parts.flatMap((part) => stationNameLines(part, Math.max(1, labelWidth), measure, 12));
    node.labelWidth = Math.min(labelWidth, Math.max(node.index === 0 ? originWidth : 24,
      ...node.nameLines.map((line) => measure(line) + 8), ...node.detailLines.map((line) => measure(line, 12) + 8)));
    node.labelHeight = node.nameLines.length * 18 + node.detailLines.length * 16 +
      (node.detailLines.length ? 8 : 0) + (node.index === 0 && originText ? 18 : 0);
  }
  (stops.length >= 4 ? [3, 0, 1, 2] : [0, 1, 2, 3]).forEach((side) => {
    sides[side] = Array.from({ length: counts[side] }, (_, index) => {
    const stop = stops[cursor];
    const parts = rowsOfDetail(stop.detail);
    const wantedWidth = Math.max(cursor === 0 ? originWidth : 24, measure(stop.stationName || stop.stopName || stop.stationId) + 8,
      ...parts.map((part) => measure(part, 12) + 8));
    const node = { stop, index: cursor++, side, sideIndex: index, wantedWidth };
    sizeLabel(node, side % 2 ? Math.min(wantedWidth, Math.max(24, sideInset - gap)) : wantedWidth);
    return node;
    });
  });
  const railHeight = Math.max(72, ...sides[1].map((node) => node.labelHeight + 16),
    ...sides[3].map((node) => node.labelHeight + 16));
  const radiusX = Math.min(railHeight / 2, railWidth / 4), radiusY = railHeight / 2;
  const straight = railWidth - radiusX * 2;
  [sides[0], [...sides[2]].reverse()].forEach((row) => {
    const pitch = straight / Math.max(1, row.length), shift = Math.min(24, pitch / 4), spacing = Math.min(gap, pitch / 4);
    row.forEach((node, index) => {
      node.x = sideInset + radiusX + (index + .5) * pitch;
      node.labelWidth = Math.min(node.wantedWidth, Math.max(1, pitch - spacing));
      node.labelLeft = node.x - node.labelWidth / 2;
    });
    // 短名先占实际宽度；长名只能借相邻空白，中心最多移动四分之一站距、上限 24。
    [...row].sort((a, b) => a.wantedWidth - b.wantedWidth).forEach((node) => {
      const index = row.indexOf(node), before = row[index - 1], after = row[index + 1];
      const left = before ? before.labelLeft + before.labelWidth + spacing : 0;
      const right = after ? after.labelLeft - spacing : width;
      const labelWidth = Math.max(1, Math.min(node.wantedWidth, right - left,
        2 * (node.x + shift - left), 2 * (right - node.x + shift)));
      sizeLabel(node, labelWidth);
      node.labelLeft = Math.max(left, Math.min(right - node.labelWidth, node.x - node.labelWidth / 2));
    });
  });
  const topHeight = Math.max(18, ...sides[0].map((node) => node.labelHeight));
  const bottomHeight = Math.max(18, ...sides[2].map((node) => node.labelHeight));
  const top = topHeight + nameOffset;
  const arc = Math.PI * Math.sqrt((radiusX * radiusX + radiusY * radiusY) / 2);
  const perimeter = 2 * (straight + arc);
  const starts = [0, straight, straight + arc, 2 * straight + arc];
  const lengths = [straight, arc, straight, arc];
  const distances = [];
  sides.forEach((sideNodes, side) => sideNodes.forEach((node, index) => {
    distances[node.index] = starts[side] + (index + .5) / sideNodes.length * lengths[side]
      + (stops.length >= 4 && side !== 3 ? perimeter : 0);
  }));
  function curvePoint(angle, centerX, side) {
    const nx = -radiusY * Math.cos(angle), ny = -radiusX * Math.sin(angle);
    const length = Math.sqrt(nx * nx + ny * ny);
    return { x: centerX + Math.cos(angle) * radiusX,
      y: top + radiusY + Math.sin(angle) * radiusY, side, nx: nx / length, ny: ny / length };
  }
  function pointAt(distance) {
    const value = ((distance % perimeter) + perimeter) % perimeter;
    if (value <= straight) return { x: sideInset + radiusX + value, y: top, side: 0, nx: 0, ny: 1 };
    if (value <= straight + arc) {
      const angle = -Math.PI / 2 + (value - straight) / arc * Math.PI;
      return curvePoint(angle, sideInset + railWidth - radiusX, 1);
    }
    if (value <= 2 * straight + arc) return { x: sideInset + railWidth - radiusX - (value - straight - arc), y: top + railHeight, side: 2, nx: 0, ny: -1 };
    const angle = Math.PI / 2 + (value - 2 * straight - arc) / arc * Math.PI;
    return curvePoint(angle, sideInset + radiusX, 3);
  }
  const nodes = sides.flatMap((sideNodes, side) => sideNodes.map((node) => {
    const distance = distances[node.index], point = pointAt(distance);
    const labelLeft = side === 1 ? point.x + gap : side === 3 ? point.x - gap - node.labelWidth : node.labelLeft;
    return { ...node, ...point, distance, labelLeft };
  })).sort((a, b) => a.index - b.index);
  const turns = [straight, straight + arc, 2 * straight + arc, perimeter]
    .flatMap((distance) => [distance, distance + perimeter]).sort((a, b) => a - b);
  const edges = (nodes.length > 1 ? nodes : []).map((node) => {
    const next = nodes[(node.index + 1) % nodes.length];
    const nextDistance = next.distance + (next.index === 0 ? perimeter : 0);
    const boundaries = [...turns.filter((distance) => distance > node.distance && distance < nextDistance), nextDistance];
    let path = `M${node.x} ${node.y}`, previous = node.distance;
    boundaries.forEach((distance) => {
      const end = pointAt(distance), side = pointAt((previous + distance) / 2).side;
      path += side % 2 ? ` A${radiusX} ${radiusY} 0 0 1 ${end.x} ${end.y}` : ` L${end.x} ${end.y}`;
      previous = distance;
    });
    const middle = pointAt((node.distance + nextDistance) / 2);
    const section = sectionMap.get(`${node.stop.stationId}\u0000${next.stop.stationId}`);
    const label = getSectionText?.(section) || "—";
    const labelWidth = Math.max(42, label.length * 8 + 12);
    const inset = Math.abs(middle.nx) * labelWidth / 2 + Math.abs(middle.ny) * 12;
    const labelX = middle.x + middle.nx * inset;
    const labelY = middle.y + middle.ny * inset - 12;
    return { index: node.index, from: node.stop, to: next.stop,
      path,
      labelX, labelY, labelWidth, side: middle.side };
  });
  return { width, height: top + railHeight + bottomHeight + nameOffset, nodes, edges };
}

export default function LineRoute({ stops = EMPTY, sections = EMPTY, color, selectedStationId = "", isStationSelected,
  selectedSection = "", onStation, onSection, getSectionText, getSectionColor,
  emptyText = "", originText = "", isActive = false, measureBeforeShow = false }) {
  const rootRef = useRef(null), unitRef = useRef(null), measureRef = useRef(null);
  const [width, setWidth] = useState(measureBeforeShow ? 0 : 750);
  const [metrics, setMetrics] = useState(new Map());
  const samples = useMemo(() => [...new Set([originText, "↑", "→", " ", ...stops.flatMap((stop) =>
    [stop.stationName || stop.stopName || stop.stationId, ...rowsOfDetail(stop.detail)])]
    .flatMap((text) => [String(text || ""), ...nameTokens(text), ...Array.from(String(text || ""))]).filter(Boolean))]
    .flatMap((text) => [14, 12].map((size) => ({ text, size, key: `${size}:${text}` }))), [stops, originText]);
  useEffect(() => {
    if (!isActive || !rootRef.current) return undefined;
    const node = rootRef.current;
    function measure() {
      const unit = (unitRef.current?.clientWidth || 0) / 100;
      if (node.clientWidth <= 0 || unit <= 0) return;
      const next = new Map(samples.map((sample, index) => [sample.key,
        measureRef.current.children[index].getBoundingClientRect().width / unit]));
      if ([...next.values()].some((value) => value <= 0)) return;
      setMetrics((previous) => samples.every((sample) => previous.get(sample.key) === next.get(sample.key)) ? previous : next);
      setWidth(node.clientWidth / unit);
    }
    const observer = typeof ResizeObserver === "function" ? new ResizeObserver(measure) : null;
    observer?.observe(node);
    let frame = window.requestAnimationFrame(() => { frame = window.requestAnimationFrame(measure); });
    window.addEventListener("resize", measure);
    return () => { observer?.disconnect(); window.cancelAnimationFrame(frame); window.removeEventListener("resize", measure); };
  }, [isActive, samples]);
  const sectionMap = useMemo(() => new Map(sections.map((section) =>
    [`${section.fromStationId}\u0000${section.toStationId}`, section])), [sections]);
  const measureText = useMemo(() => (text, size = 14) => metrics.get(`${size}:${text}`)
    ?? nameTokens(text).reduce((total, token) => total + (metrics.get(`${size}:${token}`) ?? textWidth(token, size)), 0), [metrics]);
  const layout = useMemo(() => routeLayout(stops, width || 750, sectionMap, getSectionText, measureText, originText),
    [stops, width, sectionMap, getSectionText, measureText, originText]);
  return <div className="rtw-line-route" ref={rootRef} style={{ color: color || "#81d4d4", visibility: measureBeforeShow && !width ? "hidden" : undefined }}>
    <span className="rtw-line-route-unit" ref={unitRef} aria-hidden="true" />
    <div className="rtw-line-route-measure" ref={measureRef} aria-hidden="true">{samples.map((sample) =>
      <span key={sample.key} style={{ fontSize: rem(sample.size) }}>{sample.text}</span>)}</div>
    {!stops.length ? <div className="rtw-line-route-empty">{emptyText}</div> :
      <div className="rtw-line-route-stage" style={{ width: rem(layout.width), height: rem(layout.height) }}>
        <svg className="rtw-line-route-track" viewBox={`0 0 ${layout.width} ${layout.height}`}
          style={{ width: rem(layout.width), height: rem(layout.height) }}>
          {layout.edges.map((edge) => {
            const key = `${edge.from.stationId}\u0000${edge.to.stationId}`;
            const section = sectionMap.get(key);
            return <g key={`${edge.from.waypointIndex}:${edge.index}`}>
              <path d={edge.path} stroke={getSectionColor?.(section) || color || "#82939a"} strokeWidth="4" fill="none" />
              <path className="rtw-line-route-hit" d={edge.path} stroke="transparent" strokeWidth="20" fill="none"
                onClick={() => onSection?.(edge.from, edge.to, section)} />
            </g>;
          })}
        </svg>
        {layout.edges.map((edge) => {
          const section = sectionMap.get(`${edge.from.stationId}\u0000${edge.to.stationId}`);
          const label = getSectionText?.(section) || "—";
          return <button key={`label:${edge.index}`} type="button"
            className={`rtw-line-route-edge${selectedSection === `${edge.from.stationId}\u0000${edge.to.stationId}` ? " is-selected" : ""}`}
            style={{ left: rem(edge.labelX), top: rem(edge.labelY), width: rem(edge.labelWidth) }}
            onClick={() => onSection?.(edge.from, edge.to, section)}>{label}</button>;
        })}
        {layout.nodes.map((node) => <Fragment key={`${node.stop.stationId}:${node.stop.waypointIndex}:${node.stop.stationOccurrence}:${node.index}`}>
          <button type="button" className={`rtw-line-route-dot${(isStationSelected ? isStationSelected(node.stop) : selectedStationId === node.stop.stationId) ? " is-selected" : ""}`}
            style={{ left: rem(node.x - 6), top: rem(node.y - 6) }}
            onClick={() => onStation?.(node.stop)} aria-label={node.stop.stationName || node.stop.stopName || node.stop.stationId} />
          <button type="button" className={`rtw-line-route-label${node.side === 0 ? " is-top" : node.side === 1 ? " is-right" : node.side === 3 ? " is-left" : ""}`}
            style={{ left: rem(node.labelLeft), width: rem(node.labelWidth), height: rem(node.labelHeight),
              top: rem(node.side === 0 ? node.y - nameOffset - node.labelHeight : node.side === 2 ? node.y + nameOffset : node.y - node.labelHeight / 2) }}
            onClick={() => onStation?.(node.stop)}>
            {node.index === 0 && originText ? <span className="rtw-line-route-origin"><span>{originText}</span><span className="rtw-line-route-origin-arrow">{node.side === 3 ? "↑" : "→"}</span></span> : null}
            <span className="rtw-line-route-name">{node.nameLines.map((part, index) => <span key={index}>{part}</span>)}</span>
            {node.detailLines.length ? <span className="rtw-line-route-detail">{node.detailLines.map((part, index) => <span key={index}>{part}</span>)}</span> : null}
          </button>
        </Fragment>)}
      </div>}
  </div>;
}
