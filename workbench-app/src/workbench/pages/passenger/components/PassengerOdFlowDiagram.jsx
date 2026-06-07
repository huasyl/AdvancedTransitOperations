import { descending } from "d3-array";
import { chord, ribbon } from "d3-chord";
import { arc } from "d3-shape";
import { useEffect, useMemo, useState } from "react";
import { traceWorkbench } from "../../../shared/workbench-trace";

const WIDTH = 800;
const HEIGHT = 800;
const INNER_RADIUS = 260;
const OUTER_RADIUS = 272;
const MAX_STATIONS = 14;
const ENABLE_PASSENGER_CHART_HOVER = true;

function getFlowVolume(flow) {
  const value = Number(flow?.volume || 0);
  return Number.isFinite(value) && value > 0 ? value : 0;
}

function getFlowStationId(flow, key) {
  return String(flow?.[key] || "");
}

function getFlowStationName(flow, idKey, nameKey) {
  return String(flow?.[nameKey] || flow?.[idKey] || "");
}

function buildChordInput(flows, lines) {
  const stationTotals = new Map();
  const stationNames = new Map();
  const stationLineIds = new Map();
  const lineColors = new Map((Array.isArray(lines) ? lines : []).map((line) => [line.id, line.color]));

  flows.forEach((flow) => {
    const volume = getFlowVolume(flow);
    if (volume <= 0) {
      return;
    }

    const originId = getFlowStationId(flow, "originStationId");
    const destinationId = getFlowStationId(flow, "destinationStationId");
    if (!originId || !destinationId || originId === destinationId) {
      return;
    }

    stationTotals.set(originId, (stationTotals.get(originId) || 0) + volume);
    stationTotals.set(destinationId, (stationTotals.get(destinationId) || 0) + volume);
    stationNames.set(originId, getFlowStationName(flow, "originStationId", "originName"));
    stationNames.set(destinationId, getFlowStationName(flow, "destinationStationId", "destinationName"));
    stationLineIds.set(originId, flow?.lineId || stationLineIds.get(originId) || "");
    stationLineIds.set(destinationId, flow?.lineId || stationLineIds.get(destinationId) || "");
  });

  const stationIds = [...stationTotals.entries()]
    .sort((left, right) => Number(right[1] || 0) - Number(left[1] || 0))
    .slice(0, MAX_STATIONS)
    .map(([id]) => id);
  const stationIndex = new Map(stationIds.map((id, index) => [id, index]));
  const matrix = stationIds.map(() => stationIds.map(() => 0));

  flows.forEach((flow) => {
    const originIndex = stationIndex.get(getFlowStationId(flow, "originStationId"));
    const destinationIndex = stationIndex.get(getFlowStationId(flow, "destinationStationId"));
    if (originIndex === undefined || destinationIndex === undefined || originIndex === destinationIndex) {
      return;
    }
    matrix[originIndex][destinationIndex] += getFlowVolume(flow);
  });

  const colors = stationIds.map((stationId, index) => {
    const lineColor = lineColors.get(stationLineIds.get(stationId));
    if (lineColor) {
      return lineColor;
    }
    return ["#3b82f6", "#ef4444", "#eab308", "#10b981", "#f97316", "#ec4899"][index % 6];
  });

  return {
    matrix,
    names: stationIds.map((stationId) => stationNames.get(stationId) || stationId),
    colors,
    totals: stationIds.map((stationId) => stationTotals.get(stationId) || 0)
  };
}

export default function PassengerOdFlowDiagram({ flows, lines, isActive = false }) {
  const [hoveredGroup, setHoveredGroup] = useState(null);
  const chordInput = useMemo(() => buildChordInput(flows, lines), [flows, lines]);
  const chordData = useMemo(() => chord().padAngle(0.04).sortSubgroups(descending)(chordInput.matrix), [chordInput.matrix]);
  const arcPath = useMemo(() => arc().innerRadius(INNER_RADIUS).outerRadius(OUTER_RADIUS), []);
  const ribbonPath = useMemo(() => ribbon().radius(INNER_RADIUS), []);
  const hoveredInfo = !ENABLE_PASSENGER_CHART_HOVER || hoveredGroup === null ? null : {
    name: chordInput.names[hoveredGroup],
    value: chordInput.totals[hoveredGroup] || 0,
    color: chordInput.colors[hoveredGroup] || "#38bdf8"
  };

  useEffect(() => {
    traceWorkbench("passenger.od.mount");
    return () => traceWorkbench("passenger.od.unmount");
  }, []);

  useEffect(() => {
    traceWorkbench("passenger.od.active", { active: isActive, hoveredGroup: hoveredGroup === null ? "" : hoveredGroup });
  }, [hoveredGroup, isActive]);

  if (!flows.length || chordInput.names.length < 2) {
    return <div className="rtw-passenger-empty is-large">暂无真实 OD 客流数据</div>;
  }

  function setStationHover(nextGroup) {
    setHoveredGroup((previousGroup) => {
      if (previousGroup === nextGroup) {
        return previousGroup;
      }
      traceWorkbench("passenger.od.hover.enter", {
        active: isActive,
        group: nextGroup,
        name: chordInput.names[nextGroup] || "",
        previous: previousGroup === null ? "" : previousGroup
      });
      return nextGroup;
    });
  }

  function handleGroupLeave() {
    if (hoveredGroup !== null) {
      traceWorkbench("passenger.od.hover.leave", { group: hoveredGroup });
    }
    setHoveredGroup(null);
  }

  const hoveredGroupShape = hoveredGroup === null ? null : chordData.groups.find((group) => group.index === hoveredGroup);
  const hoveredTooltipPosition = hoveredGroupShape ? (() => {
    const angle = (hoveredGroupShape.startAngle + hoveredGroupShape.endAngle) / 2;
    const radius = OUTER_RADIUS + 72;
    return {
      left: `${Math.max(6, Math.min(88, ((WIDTH / 2 + Math.sin(angle) * radius) / WIDTH) * 100))}%`,
      top: `${Math.max(8, Math.min(92, ((HEIGHT / 2 - Math.cos(angle) * radius) / HEIGHT) * 100))}%`
    };
  })() : { left: "50%", top: "50%" };
  const hitNodes = chordData.groups.map((group, index) => {
    const angle = (group.startAngle + group.endAngle) / 2;
    const radius = OUTER_RADIUS + 18;
    return {
      index,
      left: `${((WIDTH / 2 + Math.sin(angle) * radius) / WIDTH) * 100}%`,
      top: `${((HEIGHT / 2 - Math.cos(angle) * radius) / HEIGHT) * 100}%`,
      label: chordInput.names[index]
    };
  });

  return (
    <div className="rtw-passenger-od-chord">
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} className="rtw-passenger-od-svg">
        <g transform={`translate(${WIDTH / 2} ${HEIGHT / 2})`}>
          {chordData.map((entry, index) => {
            const isHovered = hoveredGroup === entry.source.index || hoveredGroup === entry.target.index;
            const fillOpacity = hoveredGroup === null ? 0.35 : (isHovered ? 0.82 : 0.06);
            return (
              <path
                key={`flow-${index}`}
                d={ribbonPath(entry) || ""}
                fill={chordInput.colors[entry.source.index] || "#38bdf8"}
                fillOpacity={fillOpacity}
              />
            );
          })}
          {chordData.groups.map((group, index) => {
            const angle = (group.startAngle + group.endAngle) / 2;
            const degree = (angle * 180) / Math.PI - 90;
            const flipped = angle > Math.PI;
            const labelTransform = `rotate(${degree}) translate(${OUTER_RADIUS + 15} 0)${flipped ? " rotate(180) translate(-8 0)" : ""}`;
            return (
              <g key={`station-${index}`}>
                <path
                  d={arcPath(group) || ""}
                  fill={chordInput.colors[index] || "#71717a"}
                  stroke="#09090b"
                  strokeWidth="2"
                />
                <text
                  transform={labelTransform}
                  fill={hoveredGroup === index ? "#f4f4f5" : "#a1a1aa"}
                  fontSize={hoveredGroup === index ? "18" : "16"}
                  fontWeight={hoveredGroup === index ? "800" : "700"}
                  textAnchor={flipped ? "end" : "start"}
                  pointerEvents="none"
                >
                  {chordInput.names[index]}
                </text>
              </g>
            );
          })}
        </g>
      </svg>
      {ENABLE_PASSENGER_CHART_HOVER ? (
        <div className="rtw-passenger-od-hit-layer" onMouseLeave={handleGroupLeave}>
          {hitNodes.map((node) => (
            <button
              key={`od-hit-${node.index}`}
              type="button"
              className="rtw-passenger-od-hit"
              style={{ left: node.left, top: node.top }}
              onMouseEnter={() => setStationHover(node.index)}
              onMouseLeave={handleGroupLeave}
              aria-label={node.label}
            />
          ))}
          {hoveredInfo ? (
            <div className="rtw-passenger-od-tooltip" style={hoveredTooltipPosition}>
              <div className="rtw-passenger-chart-tooltip-title">{hoveredInfo.name}</div>
              <div className="rtw-passenger-chart-tooltip-value">OD 汇总: {hoveredInfo.value.toLocaleString()}</div>
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
