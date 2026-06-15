import { descending } from "d3-array";
import { chordDirected, ribbonArrow } from "d3-chord";
import { arc } from "d3-shape";
import { useEffect, useMemo, useState } from "react";
import { traceWorkbench } from "../../../shared/workbench-trace";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

const WIDTH = 540;
const HEIGHT = 540;
const INNER_RADIUS = 176;
const OUTER_RADIUS = 184;
const MAX_STATIONS = 14;
const ENABLE_PASSENGER_CHART_HOVER = true;
const FALLBACK_COLORS = ["#3b82f6", "#ef4444", "#eab308", "#10b981", "#f97316", "#ec4899"];
const MIN_VISUAL_OD = 2.6;
const VISUAL_OD_POWER = 0.35;
const VISUAL_CAP_PERCENTILE = 0.90;

function hashText(text) {
  let hash = 0;
  const source = String(text || "");
  for (let index = 0; index < source.length; index += 1) {
    hash = ((hash * 31) + source.charCodeAt(index)) >>> 0;
  }
  return hash;
}

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

function getFlowLineId(flow) {
  return String(flow?.firstLineId || flow?.lineId || flow?.lastLineId || "");
}

function getFlowDestinationLineId(flow) {
  return String(flow?.lastLineId || flow?.lineId || flow?.firstLineId || "");
}

function addLineVolume(volumeMap, key, lineId, volume) {
  if (!key || !lineId || volume <= 0) {
    return;
  }
  if (!volumeMap.has(key)) {
    volumeMap.set(key, new Map());
  }
  const lineVolumes = volumeMap.get(key);
  lineVolumes.set(lineId, (lineVolumes.get(lineId) || 0) + volume);
}

function dominantLineId(lineVolumes) {
  let bestLineId = "";
  let bestVolume = -1;
  (lineVolumes || new Map()).forEach((volume, lineId) => {
    if (volume > bestVolume) {
      bestLineId = lineId;
      bestVolume = volume;
    }
  });
  return bestLineId;
}

function percentile(values, ratio) {
  const sorted = values
    .filter((value) => Number.isFinite(value) && value > 0)
    .sort((left, right) => left - right);
  if (!sorted.length) {
    return 1;
  }
  const index = Math.max(0, Math.min(sorted.length - 1, Math.floor((sorted.length - 1) * ratio)));
  return sorted[index] || 1;
}

function visualOdValue(volume, cap) {
  const capped = Math.min(Math.max(0, volume), cap);
  if (capped <= 0) {
    return 0;
  }
  return Math.max(MIN_VISUAL_OD, Math.pow(capped, VISUAL_OD_POWER));
}

function splitStationLabel(value) {
  const text = String(value || "").trim();
  if (!text || text.length <= 12) {
    return [text];
  }

  const words = text.split(/\s+/).filter(Boolean);
  if (words.length >= 2) {
    const midpoint = Math.ceil(words.length / 2);
    return [words.slice(0, midpoint).join(" "), words.slice(midpoint).join(" ")];
  }

  const pivot = Math.ceil(text.length / 2);
  return [text.slice(0, pivot), text.slice(pivot)];
}

function buildChordInput(flows, lines) {
  const stationTotals = new Map();
  const stationNames = new Map();
  const stationLineVolumes = new Map();
  const destinationStationLineVolumes = new Map();
  const pairLineVolumes = new Map();
  const pairVolumes = new Map();
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
    const lineId = getFlowLineId(flow);
    const destinationLineId = getFlowDestinationLineId(flow);
    const pairKey = `${originId}->${destinationId}`;
    addLineVolume(stationLineVolumes, originId, lineId, volume);
    addLineVolume(destinationStationLineVolumes, destinationId, destinationLineId, volume);
    addLineVolume(pairLineVolumes, pairKey, lineId, volume);
    pairVolumes.set(pairKey, (pairVolumes.get(pairKey) || 0) + volume);
  });

  const stationIds = [...stationTotals.entries()]
    .sort((left, right) => Number(right[1] || 0) - Number(left[1] || 0))
    .slice(0, MAX_STATIONS)
    .map(([id]) => id);
  const stationIndex = new Map(stationIds.map((id, index) => [id, index]));
  const matrix = stationIds.map(() => stationIds.map(() => 0));
  const visualCap = percentile([...pairVolumes.values()], VISUAL_CAP_PERCENTILE);

  pairVolumes.forEach((volume, pairKey) => {
    const parts = String(pairKey || "").split("->");
    const originIndex = stationIndex.get(parts[0]);
    const destinationIndex = stationIndex.get(parts[1]);
    if (originIndex === undefined || destinationIndex === undefined || originIndex === destinationIndex) {
      return;
    }
    matrix[originIndex][destinationIndex] = visualOdValue(volume, visualCap);
  });

  const colors = stationIds.map((stationId, index) => {
    const departureLineId = dominantLineId(stationLineVolumes.get(stationId));
    const fallbackArrivalLineId = departureLineId ? "" : dominantLineId(destinationStationLineVolumes.get(stationId));
    const lineColor = lineColors.get(departureLineId || fallbackArrivalLineId);
    if (lineColor) {
      return lineColor;
    }
    return FALLBACK_COLORS[hashText(stationId || index) % FALLBACK_COLORS.length];
  });
  const pairColors = stationIds.map((originId, originIndex) => {
    return stationIds.map((destinationId) => {
      const lineColor = lineColors.get(dominantLineId(pairLineVolumes.get(`${originId}->${destinationId}`)));
      return lineColor || colors[originIndex] || "#38bdf8";
    });
  });

  return {
    matrix,
    names: stationIds.map((stationId) => stationNames.get(stationId) || stationId),
    colors,
    pairColors
  };
}

export default function PassengerOdFlowDiagram({ flows, lines, isActive = false }) {
  const { t } = useNativeScheduleI18n();
  const [hoveredGroup, setHoveredGroup] = useState(null);
  const chordInput = useMemo(() => buildChordInput(flows, lines), [flows, lines]);
  const chordData = useMemo(() => chordDirected().padAngle(0.04).sortSubgroups(descending)(chordInput.matrix), [chordInput.matrix]);
  const arcPath = useMemo(() => arc().innerRadius(INNER_RADIUS).outerRadius(OUTER_RADIUS), []);
  const ribbonPath = useMemo(() => ribbonArrow().radius(INNER_RADIUS).headRadius(15), []);

  useEffect(() => {
    traceWorkbench("passenger.od.mount");
    return () => traceWorkbench("passenger.od.unmount");
  }, []);

  useEffect(() => {
    traceWorkbench("passenger.od.active", { active: isActive, hoveredGroup: hoveredGroup === null ? "" : hoveredGroup });
  }, [hoveredGroup, isActive]);

  if (!flows.length || chordInput.names.length < 2) {
    return <div className="rtw-passenger-empty is-large">{t("nativeWorkbench.passenger.empty.odFlow")}</div>;
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
  const labelNodes = chordData.groups.map((group, index) => {
    const angle = (group.startAngle + group.endAngle) / 2;
    const radius = OUTER_RADIUS + 30;
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
            const fillOpacity = hoveredGroup === null ? 0.22 : (isHovered ? 0.68 : 0.04);
            return (
              <path
                key={`flow-${index}`}
                d={ribbonPath(entry) || ""}
                fill={chordInput.pairColors?.[entry.source.index]?.[entry.target.index] || chordInput.colors[entry.source.index] || "#38bdf8"}
                fillOpacity={fillOpacity}
              />
            );
          })}
          {chordData.groups.map((group, index) => {
            return (
              <g key={`station-${index}`}>
                <path
                  d={arcPath(group) || ""}
                  fill={chordInput.colors[index] || "#71717a"}
                />
              </g>
            );
          })}
        </g>
      </svg>
      <div className="rtw-passenger-od-label-layer">
        {labelNodes.map((node) => (
          <div
            key={`od-label-${node.index}`}
            className={`rtw-passenger-od-label ${hoveredGroup === node.index ? "is-hovered" : ""}`}
            style={{ left: node.left, top: node.top }}
          >
            {splitStationLabel(node.label).map((line, lineIndex) => (
              <span key={`od-label-${node.index}-line-${lineIndex}`}>{line}</span>
            ))}
          </div>
        ))}
      </div>
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
        </div>
      ) : null}
    </div>
  );
}
