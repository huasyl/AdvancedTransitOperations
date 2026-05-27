import React, { useMemo, useState } from "react";

const LINE_STATUS = {
  normal: "正常运行",
  branch: "支线运行",
  loop: "环线运行"
};

const EXAMPLES = {
  trunk: {
    id: "trunk",
    name: "主干走廊 + 支线",
    description: "三条线路共享竖向主干，橙线在换乘站从右侧 lane 分出。",
    lines: [
      { id: "blue", name: "蓝线", color: "#2563eb", laneIndex: -1, status: "normal" },
      { id: "cyan", name: "青线", color: "#0891b2", laneIndex: 0, status: "normal" },
      { id: "orange", name: "橙线", color: "#f97316", laneIndex: 1, status: "branch" }
    ],
    stations: [
      { id: "north", name: "北门", x: 360, y: 80, type: "normal", label: "left" },
      { id: "museum", name: "博物馆", x: 360, y: 160, type: "normal", label: "left" },
      { id: "central", name: "中央换乘", x: 360, y: 250, type: "transfer", label: "left" },
      { id: "river", name: "河岸", x: 360, y: 350, type: "normal", label: "left" },
      { id: "south", name: "南站", x: 360, y: 440, type: "normal", label: "left" },
      { id: "market", name: "市场", x: 500, y: 250, type: "normal", label: "bottom" },
      { id: "expo", name: "会展中心", x: 640, y: 250, type: "terminal", label: "right" }
    ]
  },
  loop: {
    id: "loop",
    name: "环线 + 支线",
    description: "物理环形走廊按 canonical station 合并；线路可跑完整环，也可只覆盖环上一段。",
    lines: [
      { id: "train:3", name: "环线 顺时针", color: "#ae7fe7", laneIndex: 0, status: "loop", rawStops: ["loop:central", "loop:linhai", "loop:dev", "loop:industrial", "loop:cemetery", "loop:cbd", "loop:commercial"], coverage: [{ kind: "loopSpan", from: "central", to: "central", wrap: true, direction: "cw" }] },
      { id: "train:1", name: "区间线", color: "#1de727", laneIndex: 1, status: "normal", rawStops: ["local:industrial", "local:cemetery", "local:cbd", "local:commercial", "local:central"], coverage: [{ kind: "loopSpan", from: "industrial", to: "central", wrap: false, direction: "cw" }] },
      { id: "train:2", name: "直达线", color: "#e7241d", laneIndex: 2, status: "branch", rawStops: ["direct:industrial", "direct:central"], coverage: [{ kind: "loopSpan", from: "industrial", to: "central", wrap: false, direction: "cw" }] },
      { id: "train:4", name: "直通快速", color: "#5c88a6", laneIndex: 3, status: "branch", rawStops: ["rapid:bantian", "rapid:shanqiao", "rapid:central", "rapid:cemetery", "rapid:shatang", "rapid:changtang"], coverage: [{ kind: "loopSpan", from: "central", to: "cemetery", wrap: false, direction: "ccw" }] },
      { id: "train:5", name: "火车5", color: "#1de7bb", laneIndex: 4, status: "normal", rawStops: ["train5:bantian", "train5:shanqiao", "train5:central", "train5:linhai", "train5:dev", "train5:industrial", "train5:cemetery"], coverage: [{ kind: "loopSpan", from: "central", to: "cemetery", wrap: false, direction: "cw" }] }
    ],
    stations: [
      { id: "central", name: "中央火车站", type: "transfer", role: "junction", loc: { kind: "edge", edge: "left", t: 0.52 }, label: "left" },
      { id: "linhai", name: "林海", type: "normal", role: "loopSlot", loc: { kind: "edge", edge: "top", t: 0.26 }, label: "top" },
      { id: "dev", name: "开发区", type: "normal", role: "loopSlot", loc: { kind: "edge", edge: "top", t: 0.58 }, label: "top" },
      { id: "industrial", name: "工业区", type: "normal", role: "junction", loc: { kind: "edge", edge: "right", t: 0.34 }, label: "right" },
      { id: "cemetery", name: "墓园", type: "transfer", role: "junction", loc: { kind: "edge", edge: "right", t: 0.72 }, label: "right" },
      { id: "cbd", name: "CBD", type: "normal", role: "loopSlot", loc: { kind: "edge", edge: "bottom", t: 0.34 }, label: "bottom" },
      { id: "commercial", name: "商业区", type: "normal", role: "loopSlot", loc: { kind: "edge", edge: "bottom", t: 0.68 }, label: "bottom" },
      { id: "bantian", name: "坂田", type: "terminal", role: "branchStation", label: "top" },
      { id: "shanqiao", name: "山脚", type: "normal", role: "branchStation", label: "top" },
      { id: "shatang", name: "沙塘", type: "normal", role: "branchStation", label: "top" },
      { id: "changtang", name: "长塘", type: "terminal", role: "branchStation", label: "top" }
    ],
    topology: {
      style: "rounded-rect-loop",
      physicalLoop: {
        stationOrder: [
          "central",
          "linhai",
          "dev",
          "industrial",
          "cemetery",
          "cbd",
          "commercial"
        ]
      },
      corridors: [
        { id: "west-access", from: "central", direction: "left", stationOrder: ["central", "shanqiao", "bantian"], lineIds: ["train:4", "train:5"], length: 190 },
        { id: "north-spur", from: "cemetery", direction: "top", stationOrder: ["cemetery", "shatang", "changtang"], lineIds: ["train:4"], length: 180 }
      ],
      connectors: [
        { id: "central-west", fromLoopStationId: "central", toCorridorId: "west-access", lineIds: ["train:4", "train:5"], direction: "left" },
        { id: "cemetery-north", fromLoopStationId: "cemetery", toCorridorId: "north-spur", lineIds: ["train:4"], direction: "top" }
      ],
      stopBindings: {
        "loop:central": "central",
        "loop:linhai": "linhai",
        "loop:dev": "dev",
        "loop:industrial": "industrial",
        "loop:cemetery": "cemetery",
        "loop:cbd": "cbd",
        "loop:commercial": "commercial",
        "local:industrial": "industrial",
        "local:cemetery": "cemetery",
        "local:cbd": "cbd",
        "local:commercial": "commercial",
        "local:central": "central",
        "direct:industrial": "industrial",
        "direct:central": "central",
        "rapid:bantian": "bantian",
        "rapid:shanqiao": "shanqiao",
        "rapid:central": "central",
        "rapid:cemetery": "cemetery",
        "rapid:shatang": "shatang",
        "rapid:changtang": "changtang",
        "train5:bantian": "bantian",
        "train5:shanqiao": "shanqiao",
        "train5:central": "central",
        "train5:linhai": "linhai",
        "train5:dev": "dev",
        "train5:industrial": "industrial",
        "train5:cemetery": "cemetery"
      }
    }
  }
};

const DEFAULT_CONTROLS = {
  exampleId: "trunk",
  strokeWidth: 14,
  laneGap: 12,
  stationSize: 13,
  showLabels: true,
  darkMode: false,
  showMockData: false
};

function laneOffset(line, laneGap) {
  return line.laneIndex * laneGap;
}

function trunkPathFor(line, laneGap) {
  const offset = laneOffset(line, laneGap);
  if (line.id === "orange") {
    const x = 360 + offset;
    return `M ${x} 80 L ${x} 222 C ${x} 246 ${x + 30} 250 ${x + 64} 250 L 640 250`;
  }
  return `M ${360 + offset} 80 L ${360 + offset} 440`;
}

function stationPointForTrunk(station, line, laneGap) {
  if (station.x !== 360) {
    return { x: station.x, y: station.y };
  }
  return { x: station.x + laneOffset(line, laneGap), y: station.y };
}

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function loopMetrics(controls) {
  const strokeWidth = clamp(controls.strokeWidth - 4, 8, 10);
  const laneGap = clamp(controls.laneGap - 4, 6, 8);
  const laneStep = strokeWidth + laneGap;
  return {
    cx: 350,
    cy: 270,
    innerWidth: 460,
    innerHeight: 250,
    cornerRadius: 54,
    laneStep,
    strokeWidth,
    stationSize: controls.stationSize
  };
}

function loopRectFor(line, metrics) {
  const lane = line.laneIndex || 0;
  const width = metrics.innerWidth + lane * metrics.laneStep * 2;
  const height = metrics.innerHeight + lane * metrics.laneStep * 2;
  return {
    left: metrics.cx - width / 2,
    right: metrics.cx + width / 2,
    top: metrics.cy - height / 2,
    bottom: metrics.cy + height / 2,
    width,
    height,
    radius: metrics.cornerRadius + lane * metrics.laneStep
  };
}

function loopPathFor(line, metrics) {
  const rect = loopRectFor(line, metrics);
  const r = rect.radius;
  return [
    `M ${rect.left + r} ${rect.top}`,
    `H ${rect.right - r}`,
    `Q ${rect.right} ${rect.top} ${rect.right} ${rect.top + r}`,
    `V ${rect.bottom - r}`,
    `Q ${rect.right} ${rect.bottom} ${rect.right - r} ${rect.bottom}`,
    `H ${rect.left + r}`,
    `Q ${rect.left} ${rect.bottom} ${rect.left} ${rect.bottom - r}`,
    `V ${rect.top + r}`,
    `Q ${rect.left} ${rect.top} ${rect.left + r} ${rect.top}`,
    "Z"
  ].join(" ");
}

function loopPointFromProgress(progress, line, metrics) {
  const rect = loopRectFor(line, metrics);
  const r = rect.radius;
  const topLength = rect.width - 2 * r;
  const sideLength = rect.height - 2 * r;
  const cornerLength = Math.PI * r / 2;
  const parts = [
    { kind: "line", edge: "top", length: topLength },
    { kind: "corner", corner: "ne", length: cornerLength },
    { kind: "line", edge: "right", length: sideLength },
    { kind: "corner", corner: "se", length: cornerLength },
    { kind: "line", edge: "bottom", length: topLength },
    { kind: "corner", corner: "sw", length: cornerLength },
    { kind: "line", edge: "left", length: sideLength },
    { kind: "corner", corner: "nw", length: cornerLength }
  ];
  const perimeter = parts.reduce((sum, part) => sum + part.length, 0);
  let distance = ((progress % 1) + 1) % 1 * perimeter;
  let part = parts[0];
  for (const candidate of parts) {
    if (distance <= candidate.length) {
      part = candidate;
      break;
    }
    distance -= candidate.length;
  }
  const t = part.length === 0 ? 0 : distance / part.length;
  if (part.kind === "line") {
    if (part.edge === "top") {
      return { x: rect.left + r + topLength * t, y: rect.top, tangent: 0, normalX: 0, normalY: -1 };
    }
    if (part.edge === "right") {
      return { x: rect.right, y: rect.top + r + sideLength * t, tangent: 90, normalX: 1, normalY: 0 };
    }
    if (part.edge === "bottom") {
      return { x: rect.right - r - topLength * t, y: rect.bottom, tangent: 180, normalX: 0, normalY: 1 };
    }
    return { x: rect.left, y: rect.bottom - r - sideLength * t, tangent: -90, normalX: -1, normalY: 0 };
  }
  const cornerData = {
    ne: { x: rect.right - r, y: rect.top + r, start: -90 },
    se: { x: rect.right - r, y: rect.bottom - r, start: 0 },
    sw: { x: rect.left + r, y: rect.bottom - r, start: 90 },
    nw: { x: rect.left + r, y: rect.top + r, start: 180 }
  };
  const corner = cornerData[part.corner];
  const angle = corner.start + t * 90;
  const rad = angle * Math.PI / 180;
  return {
    x: corner.x + Math.cos(rad) * r,
    y: corner.y + Math.sin(rad) * r,
    tangent: angle + 90,
    normalX: Math.cos(rad),
    normalY: Math.sin(rad)
  };
}

function pathFromPoints(points) {
  if (!points.length) {
    return "";
  }
  return points.map((point, index) => `${index === 0 ? "M" : "L"} ${point.x} ${point.y}`).join(" ");
}

function inwardLabelPoint(point, metrics) {
  const dx = metrics.cx - point.x;
  const dy = metrics.cy - point.y;
  const length = Math.sqrt(dx * dx + dy * dy) || 1;
  return {
    ...point,
    normalX: dx / length,
    normalY: dy / length
  };
}

function spanPathForLoop(line, span, stationsById, metrics) {
  if (span.wrap) {
    return loopPathFor(line, metrics);
  }
  const startStation = stationsById.get(span.from);
  const endStation = stationsById.get(span.to);
  const start = progressFromLoopLoc(startStation?.loc, line, metrics);
  const end = progressFromLoopLoc(endStation?.loc, line, metrics);
  if (start == null || end == null) {
    return "";
  }
  let adjustedEnd = end >= start ? end : end + 1;
  if (span.direction === "counterclockwise" || span.direction === "ccw") {
    adjustedEnd = end <= start ? end : end - 1;
  }
  if (span.direction === "shortest") {
    const clockwise = end >= start ? end - start : end + 1 - start;
    const counterclockwise = start >= end ? start - end : start + 1 - end;
    adjustedEnd = clockwise <= counterclockwise ? start + clockwise : start - counterclockwise;
  }
  const distance = Math.abs(adjustedEnd - start);
  const steps = Math.max(48, Math.ceil(distance * 220));
  const points = [];
  for (let i = 0; i <= steps; i += 1) {
    points.push(loopPointFromProgress(start + (adjustedEnd - start) * (i / steps), line, metrics));
  }
  return pathFromPoints(points);
}

function branchSpanPathForLoop(line, span, stationProgress, metrics) {
  const startProgress = stationProgress.get(span.from);
  if (startProgress == null) {
    return "";
  }
  const start = loopPointFromProgress(startProgress, line, metrics);
  const end = branchEndpointPoint(start, span);
  return `M ${start.x} ${start.y} C ${start.x + end.normalX * 44} ${start.y + end.normalY * 44} ${end.x - end.normalX * 72} ${end.y - end.normalY * 72} ${end.x} ${end.y}`;
}

function branchEndpointPoint(start, spec) {
  const direction = spec.direction || "right";
  const length = spec.length || 190;
  const normals = {
    top: { x: 0, y: -1 },
    right: { x: 1, y: 0 },
    bottom: { x: 0, y: 1 },
    left: { x: -1, y: 0 }
  };
  const normal = normals[direction] || normals.right;
  const sideOffset = spec.sideOffset || 0;
  const tangentX = normal.y;
  const tangentY = -normal.x;
  return {
    x: start.x + normal.x * length + tangentX * sideOffset,
    y: start.y + normal.y * length + tangentY * sideOffset,
    tangent: direction === "top" || direction === "bottom" ? 0 : 90,
    normalX: normal.x,
    normalY: normal.y
  };
}

function directionVector(direction) {
  const vectors = {
    top: { x: 0, y: -1 },
    right: { x: 1, y: 0 },
    bottom: { x: 0, y: 1 },
    left: { x: -1, y: 0 }
  };
  return vectors[direction] || vectors.right;
}

function loopEdgeOutwardVector(station) {
  const edge = station?.loc?.edge;
  if (edge === "top") {
    return directionVector("top");
  }
  if (edge === "right") {
    return directionVector("right");
  }
  if (edge === "bottom") {
    return directionVector("bottom");
  }
  return directionVector("left");
}

function corridorSourceLaneIndex(corridor, linesById) {
  return corridor.lineIds.reduce((maxLane, lineId) => {
    const line = linesById.get(lineId);
    return Math.max(maxLane, line?.laneIndex || 0);
  }, 0);
}

function corridorLaneOffset(corridor, lineId, metrics) {
  const index = corridor.lineIds.indexOf(lineId);
  const safeIndex = index < 0 ? 0 : index;
  return (safeIndex - (corridor.lineIds.length - 1) / 2) * metrics.laneStep;
}

function junctionCapsuleSideProjection(station, exitDirection, metrics, linesById) {
  const points = Array.from(linesById.values()).map((line) => stationPointForLoop(station, line, metrics));
  if (!points.length) {
    const point = stationPointForLoop(station, { laneIndex: 0 }, metrics);
    return point.x * exitDirection.x + point.y * exitDirection.y;
  }
  const firstPoint = points[0];
  const lastPoint = points[points.length - 1];
  const dx = lastPoint.x - firstPoint.x;
  const dy = lastPoint.y - firstPoint.y;
  const laneSpan = Math.sqrt(dx * dx + dy * dy);
  const axis = laneSpan > 0 ? { x: dx / laneSpan, y: dy / laneSpan } : { x: 1, y: 0 };
  const perp = { x: -axis.y, y: axis.x };
  const center = {
    x: (firstPoint.x + lastPoint.x) / 2,
    y: (firstPoint.y + lastPoint.y) / 2
  };
  const markerWidth = Math.max(metrics.stationSize * 4.1, laneSpan + metrics.stationSize * 3.1);
  const markerHeight = Math.max(metrics.stationSize * 1.68, 26);
  const halfProjection =
    Math.abs(axis.x * exitDirection.x + axis.y * exitDirection.y) * markerWidth / 2 +
    Math.abs(perp.x * exitDirection.x + perp.y * exitDirection.y) * markerHeight / 2;
  return center.x * exitDirection.x + center.y * exitDirection.y + halfProjection + 2;
}

function loopTravelDirectionForLineAtStation(line, station, metrics) {
  const point = stationPointForLoop(station, line, metrics);
  const span = (line.coverage || []).find((candidate) => (
    candidate.kind === "loopSpan" && (candidate.from === station.id || candidate.to === station.id)
  ));
  const reverse = span?.direction === "ccw" || span?.direction === "counterclockwise";
  const angle = (point.tangent + (reverse ? 180 : 0)) * Math.PI / 180;
  return {
    x: Math.cos(angle),
    y: Math.sin(angle)
  };
}

function canonicalStopOrder(line, stopBindings) {
  return (line.rawStops || []).map((rawStop) => stopBindings[rawStop]).filter(Boolean);
}

function corridorFlowForLine(corridor, line, stopBindings) {
  const stops = canonicalStopOrder(line, stopBindings);
  const fromIndex = stops.indexOf(corridor.from);
  const externalIndices = corridor.stationOrder
    .slice(1)
    .map((stationId) => stops.indexOf(stationId))
    .filter((index) => index >= 0);
  if (fromIndex < 0 || !externalIndices.length) {
    return "outbound";
  }
  return Math.min(...externalIndices) < fromIndex ? "inbound" : "outbound";
}

function junctionCapsuleGeometry(station, metrics, linesById) {
  const points = Array.from(linesById.values()).map((line) => stationPointForLoop(station, line, metrics));
  const firstPoint = points[0] || stationPointForLoop(station, { laneIndex: 0 }, metrics);
  const lastPoint = points[points.length - 1] || firstPoint;
  const dx = lastPoint.x - firstPoint.x;
  const dy = lastPoint.y - firstPoint.y;
  const laneSpan = Math.sqrt(dx * dx + dy * dy);
  return {
    center: {
      x: (firstPoint.x + lastPoint.x) / 2,
      y: (firstPoint.y + lastPoint.y) / 2
    },
    axis: laneSpan > 0 ? { x: dx / laneSpan, y: dy / laneSpan } : { x: 1, y: 0 },
    width: Math.max(metrics.stationSize * 4.1, laneSpan + metrics.stationSize * 3.1),
    height: Math.max(metrics.stationSize * 1.68, 26)
  };
}

function capsulePort(station, direction, laneVector, laneOffset, metrics, linesById) {
  const capsule = junctionCapsuleGeometry(station, metrics, linesById);
  const capsulePerp = { x: -capsule.axis.y, y: capsule.axis.x };
  const halfProjection =
    Math.abs(capsule.axis.x * direction.x + capsule.axis.y * direction.y) * capsule.width / 2 +
    Math.abs(capsulePerp.x * direction.x + capsulePerp.y * direction.y) * capsule.height / 2;
  return {
    x: capsule.center.x + direction.x * (halfProjection + 2) + laneVector.x * laneOffset,
    y: capsule.center.y + direction.y * (halfProjection + 2) + laneVector.y * laneOffset
  };
}

function naturalCorridorLaneOffset(corridor, line, metrics, stationsById, linesById) {
  const fromStation = stationsById.get(corridor.from);
  const direction = directionVector(corridor.direction);
  const perp = { x: -direction.y, y: direction.x };
  if (corridor.lineIds.length <= 1) {
    const capsule = junctionCapsuleGeometry(fromStation, metrics, linesById);
    const stationPoint = stationPointForLoop(fromStation, line, metrics);
    return (stationPoint.x - capsule.center.x) * perp.x + (stationPoint.y - capsule.center.y) * perp.y;
  }
  const ordered = corridor.lineIds
    .map((lineId) => linesById.get(lineId))
    .filter(Boolean)
    .map((candidateLine) => ({
      lineId: candidateLine.id,
      score: loopTravelDirectionForLineAtStation(candidateLine, fromStation, metrics).x * perp.x +
        loopTravelDirectionForLineAtStation(candidateLine, fromStation, metrics).y * perp.y
    }))
    .sort((a, b) => b.score - a.score);
  const laneIndex = Math.max(0, ordered.findIndex((candidate) => candidate.lineId === line.id));
  return ((ordered.length - 1) / 2 - laneIndex) * metrics.laneStep;
}

function corridorConnectorGeometry(corridor, line, metrics, stationsById, linesById, stopBindings) {
  const fromStation = stationsById.get(corridor.from);
  const outward = directionVector(corridor.direction);
  const inward = { x: -outward.x, y: -outward.y };
  const laneVector = { x: -outward.y, y: outward.x };
  const laneOffset = naturalCorridorLaneOffset(corridor, line, metrics, stationsById, linesById);
  const flow = corridorFlowForLine(corridor, line, stopBindings);
  const passDirection = flow === "inbound" ? inward : outward;
  const reversePassDirection = { x: -passDirection.x, y: -passDirection.y };
  const entryPort = capsulePort(fromStation, reversePassDirection, laneVector, laneOffset, metrics, linesById);
  const exitPort = capsulePort(fromStation, passDirection, laneVector, laneOffset, metrics, linesById);
  const loopDirection = loopTravelDirectionForLineAtStation(line, fromStation, metrics);
  const stationPoint = stationPointForLoop(fromStation, line, metrics);
  const loopProjection = junctionCapsuleSideProjection(fromStation, loopDirection, metrics, linesById);
  const stationProjection = stationPoint.x * loopDirection.x + stationPoint.y * loopDirection.y;
  const loopPort = {
    x: stationPoint.x + loopDirection.x * (loopProjection - stationProjection),
    y: stationPoint.y + loopDirection.y * (loopProjection - stationProjection)
  };
  const run = Math.max(34, metrics.laneStep * 1.7);
  const radius = Math.min(38, Math.max(22, metrics.laneStep * 1.6));
  return {
    flow,
    outward,
    passDirection,
    loopDirection,
    entryPort,
    exitPort,
    throughEnd: {
      x: exitPort.x + passDirection.x * run,
      y: exitPort.y + passDirection.y * run
    },
    loopPort,
    loopRunEnd: {
      x: loopPort.x + loopDirection.x * run,
      y: loopPort.y + loopDirection.y * run
    },
    externalOrigin: flow === "inbound" ? entryPort : exitPort,
    radius,
    stationPoint
  };
}

function corridorLaneOffsetForLine(corridor, line, direction, fromStation, metrics, linesById) {
  const baseOffset = corridorLaneOffset(corridor, line.id, metrics);
  if (!line) {
    return baseOffset;
  }
  const perp = { x: -direction.y, y: direction.x };
  const sourceLine = { laneIndex: corridorSourceLaneIndex(corridor, linesById) };
  const sourcePoint = stationPointForLoop(fromStation, sourceLine, metrics);
  const lanePoint = stationPointForLoop(fromStation, line, metrics);
  const projected = (lanePoint.x - sourcePoint.x) * perp.x + (lanePoint.y - sourcePoint.y) * perp.y;
  return Math.abs(projected) > 0.1 ? projected : baseOffset;
}

function corridorBasePoint(corridor, stationProgress, metrics, stationsById, linesById) {
  const fromStation = stationsById.get(corridor.from);
  if (!fromStation) {
    return { x: metrics.cx, y: metrics.cy };
  }
  const source = roundedRectPointForLoop(fromStation.loc, { laneIndex: corridorSourceLaneIndex(corridor, linesById) }, metrics);
  const anchorDirection = loopEdgeOutwardVector(fromStation);
  const anchorDistance = Math.max(96, metrics.laneStep * 4.2 + metrics.strokeWidth);
  return {
    x: source.x + anchorDirection.x * anchorDistance,
    y: source.y + anchorDirection.y * anchorDistance
  };
}

function corridorPoint(corridor, lineId, stationId, stationProgress, metrics, stationsById, linesById, stopBindings) {
  const line = linesById.get(lineId);
  const geometry = corridorConnectorGeometry(corridor, line, metrics, stationsById, linesById, stopBindings);
  const stationIndex = Math.max(0, corridor.stationOrder.indexOf(stationId));
  const denominator = Math.max(1, corridor.stationOrder.length - 1);
  const distance = (corridor.length || 170) * (stationIndex / denominator);
  return {
    x: geometry.externalOrigin.x + geometry.outward.x * distance,
    y: geometry.externalOrigin.y + geometry.outward.y * distance,
    tangent: corridor.direction === "top" || corridor.direction === "bottom" ? 90 : 0,
    normalX: geometry.outward.x,
    normalY: geometry.outward.y
  };
}

function connectorPortPoint(station, connector, line, metrics) {
  const junctionPoint = stationPointForLoop(station, line, metrics);
  const direction = directionVector(connector.direction);
  const perp = { x: -direction.y, y: direction.x };
  const laneOffset = ((connector.lineIds.indexOf(line.id) < 0 ? 0 : connector.lineIds.indexOf(line.id)) - (connector.lineIds.length - 1) / 2) * metrics.laneStep;
  const forward = connector.portGap || 18;
  return {
    x: junctionPoint.x + direction.x * forward + perp.x * laneOffset,
    y: junctionPoint.y + direction.y * forward + perp.y * laneOffset,
    tangent: connector.direction === "top" || connector.direction === "bottom" ? 90 : 0,
    normalX: direction.x,
    normalY: direction.y
  };
}

function connectorPathForCorridor(corridor, line, stationProgress, metrics, stationsById, linesById, stopBindings) {
  const fromStation = stationsById.get(corridor.from);
  if (!fromStation || !line) {
    return "";
  }
  const firstExternalStationId = corridor.stationOrder.find((stationId) => stationId !== corridor.from) || corridor.from;
  const firstExternalPoint = corridorPoint(corridor, line.id, firstExternalStationId, stationProgress, metrics, stationsById, linesById, stopBindings);
  const geometry = corridorConnectorGeometry(corridor, line, metrics, stationsById, linesById, stopBindings);
  const sameAxis = Math.abs(geometry.passDirection.x * geometry.loopDirection.x + geometry.passDirection.y * geometry.loopDirection.y) > 0.5;
  if (geometry.flow === "inbound") {
    if (sameAxis) {
      return `M ${firstExternalPoint.x} ${firstExternalPoint.y} L ${geometry.entryPort.x} ${geometry.entryPort.y} L ${geometry.exitPort.x} ${geometry.exitPort.y} L ${geometry.loopRunEnd.x} ${geometry.loopRunEnd.y}`;
    }
    const corner = {
      x: geometry.throughEnd.x + geometry.passDirection.x * geometry.radius,
      y: geometry.throughEnd.y + geometry.passDirection.y * geometry.radius
    };
    const afterCorner = {
      x: corner.x + geometry.loopDirection.x * geometry.radius,
      y: corner.y + geometry.loopDirection.y * geometry.radius
    };
    const turnRunEnd = {
      x: afterCorner.x + geometry.loopDirection.x * Math.max(18, metrics.laneStep),
      y: afterCorner.y + geometry.loopDirection.y * Math.max(18, metrics.laneStep)
    };
    return `M ${firstExternalPoint.x} ${firstExternalPoint.y} L ${geometry.entryPort.x} ${geometry.entryPort.y} L ${geometry.exitPort.x} ${geometry.exitPort.y} L ${geometry.throughEnd.x} ${geometry.throughEnd.y} Q ${corner.x} ${corner.y} ${afterCorner.x} ${afterCorner.y} L ${turnRunEnd.x} ${turnRunEnd.y}`;
  }

  if (sameAxis) {
    return `M ${geometry.stationPoint.x} ${geometry.stationPoint.y} L ${geometry.exitPort.x} ${geometry.exitPort.y} L ${firstExternalPoint.x} ${firstExternalPoint.y}`;
  }
  const corner = {
    x: geometry.loopRunEnd.x + geometry.loopDirection.x * geometry.radius,
    y: geometry.loopRunEnd.y + geometry.loopDirection.y * geometry.radius
  };
  return `M ${geometry.loopRunEnd.x} ${geometry.loopRunEnd.y} Q ${corner.x} ${corner.y} ${geometry.throughEnd.x} ${geometry.throughEnd.y} L ${geometry.exitPort.x} ${geometry.exitPort.y} L ${firstExternalPoint.x} ${firstExternalPoint.y}`;
}

function corridorPathForLine(corridor, line, stationProgress, metrics, stationsById, linesById, stopBindings) {
  const points = corridor.stationOrder
    .filter((stationId) => stationId !== corridor.from)
    .map((stationId) => corridorPoint(corridor, line.id, stationId, stationProgress, metrics, stationsById, linesById, stopBindings));
  return pathFromPoints(points);
}

function progressFromLoopLoc(loc, line, metrics) {
  if (!loc || loc.kind !== "edge") {
    return 0;
  }
  const rect = loopRectFor(line, metrics);
  const r = rect.radius;
  const topLength = rect.width - 2 * r;
  const sideLength = rect.height - 2 * r;
  const cornerLength = Math.PI * r / 2;
  const perimeter = topLength * 2 + sideLength * 2 + cornerLength * 4;
  const offsets = {
    top: 0,
    right: topLength + cornerLength,
    bottom: topLength + cornerLength + sideLength + cornerLength,
    left: topLength + cornerLength + sideLength + cornerLength + topLength + cornerLength
  };
  const lengths = {
    top: topLength,
    right: sideLength,
    bottom: topLength,
    left: sideLength
  };
  return (offsets[loc.edge] + lengths[loc.edge] * loc.t) / perimeter;
}

function roundedRectPointForLoop(loc, line, metrics) {
  const rect = loopRectFor(line, metrics);
  const r = rect.radius;
  if (loc.kind === "branchEnd") {
    const start = roundedRectPointForLoop({ kind: "edge", edge: "right", t: 0.5 }, line, metrics);
    return {
      x: start.x + 260,
      y: start.y + 28,
      tangent: 0,
      normalX: 1,
      normalY: 0
    };
  }

  if (loc.kind === "edge") {
    if (loc.edge === "top") {
      return {
        x: rect.left + r + (rect.width - 2 * r) * loc.t,
        y: rect.top,
        tangent: 0,
        normalX: 0,
        normalY: -1
      };
    }
    if (loc.edge === "right") {
      return {
        x: rect.right,
        y: rect.top + r + (rect.height - 2 * r) * loc.t,
        tangent: 90,
        normalX: 1,
        normalY: 0
      };
    }
    if (loc.edge === "bottom") {
      return {
        x: rect.right - r - (rect.width - 2 * r) * loc.t,
        y: rect.bottom,
        tangent: 180,
        normalX: 0,
        normalY: 1
      };
    }
    return {
      x: rect.left,
      y: rect.bottom - r - (rect.height - 2 * r) * loc.t,
      tangent: -90,
      normalX: -1,
      normalY: 0
    };
  }

  const cornerCenters = {
    ne: { x: rect.right - r, y: rect.top + r, start: -90, normalAnchor: "right" },
    sw: { x: rect.left + r, y: rect.bottom - r, start: 90, normalAnchor: "left" }
  };
  const corner = cornerCenters[loc.corner];
  const angle = corner.start + loc.t * 90;
  const rad = (angle * Math.PI) / 180;
  return {
    x: corner.x + Math.cos(rad) * r,
    y: corner.y + Math.sin(rad) * r,
    tangent: angle + 90,
    normalX: Math.cos(rad),
    normalY: Math.sin(rad)
  };
}

function branchPathForLoop(line, metrics) {
  const start = roundedRectPointForLoop({ kind: "edge", edge: "right", t: 0.5 }, line, metrics);
  const end = roundedRectPointForLoop({ kind: "branchEnd" }, line, metrics);
  const joinX = start.x + 94;
  return `M ${start.x} ${start.y} C ${start.x} ${start.y + 36} ${start.x + 40} ${end.y} ${joinX} ${end.y} L ${end.x} ${end.y}`;
}

function stationPointForLoop(station, line, metrics) {
  if (station.loc?.kind === "branchEnd") {
    const fromProgress = station.__stationProgress?.get(station.loc.from);
    const start = loopPointFromProgress(fromProgress || 0, line, metrics);
    return branchEndpointPoint(start, station.loc);
  }
  if (station.progress != null) {
    return loopPointFromProgress(station.progress, line, metrics);
  }
  return roundedRectPointForLoop(station.loc, line, metrics);
}

function generateLoopLayout(data, controls) {
  const metrics = loopMetrics(controls);
  const linesById = new Map(data.lines.map((line) => [line.id, line]));
  const stationsById = new Map(data.stations.map((station) => [station.id, station]));
  stationsById.__connectors = data.topology.connectors || [];
  const stationOrder = data.topology.physicalLoop.stationOrder;
  const baseLine = data.lines[0];
  const stationProgress = new Map(stationOrder.map((stationId) => {
    const station = stationsById.get(stationId);
    return [stationId, progressFromLoopLoc(station.loc, baseLine, metrics)];
  }));
  const stopBindings = data.topology.stopBindings || {};
  const outerLine = data.lines.reduce((best, line) => (line.laneIndex > best.laneIndex ? line : best), data.lines[0]);
  const physicalStations = stationOrder.map((stationId) => ({
    ...stationsById.get(stationId),
    progress: null
  }));
  const canonicalStopsByLine = new Map(data.lines.map((line) => {
    const stops = new Set((line.rawStops || []).map((rawStop) => stopBindings[rawStop]).filter(Boolean));
    return [line.id, stops];
  }));
  const loopPaths = data.lines.flatMap((line) => (line.coverage || []).map((span, index) => ({
    id: `coverage-${line.id}-${index}`,
    color: line.color,
    d: span.kind === "loopSpan" ? spanPathForLoop(line, span, stationsById, metrics) : ""
  })).filter((path) => path.d));
  const corridors = data.topology.corridors || [];
  const connectorPaths = (data.topology.connectors || []).flatMap((connector) => {
    const corridor = corridors.find((candidate) => candidate.id === connector.toCorridorId);
    if (!corridor) {
      return [];
    }
    return connector.lineIds.map((lineId) => {
      const line = linesById.get(lineId);
      return {
        id: `connector-${connector.id}-${lineId}`,
        color: line.color,
        d: connectorPathForCorridor(corridor, line, stationProgress, metrics, stationsById, linesById, stopBindings)
      };
    });
  });
  const corridorPaths = corridors.flatMap((corridor) => corridor.lineIds.map((lineId) => {
    const line = linesById.get(lineId);
    return {
      id: `corridor-${corridor.id}-${lineId}`,
      color: line.color,
      d: corridorPathForLine(corridor, line, stationProgress, metrics, stationsById, linesById, stopBindings)
    };
  }));
  const paths = [...loopPaths, ...connectorPaths, ...corridorPaths];

  const markers = physicalStations.map((station) => {
    const lanePoints = data.lines
      .filter((line) => canonicalStopsByLine.get(line.id)?.has(station.id))
      .map((line) => ({
        lineId: line.id,
        color: line.color,
        point: stationPointForLoop(station, line, metrics)
      }));
    if (!lanePoints.length) {
      return null;
    }
    return {
      id: station.id,
      station,
      lanePoints,
      point: lanePoints[lanePoints.length - 1].point,
      labelPoint: inwardLabelPoint(stationPointForLoop(station, outerLine, metrics), metrics)
    };
  }).filter(Boolean);
  corridors.forEach((corridor) => {
    corridor.stationOrder.slice(1).forEach((stationId) => {
      const station = stationsById.get(stationId);
      const lanePoints = corridor.lineIds
        .map((lineId) => linesById.get(lineId))
        .filter((line) => line && canonicalStopsByLine.get(line.id)?.has(stationId))
        .map((line) => ({
          lineId: line.id,
          color: line.color,
          point: corridorPoint(corridor, line.id, stationId, stationProgress, metrics, stationsById, linesById, stopBindings)
        }));
      if (!station || !lanePoints.length) {
        return;
      }
      markers.push({
        id: `${corridor.id}-${station.id}`,
        station,
        lanePoints,
        point: lanePoints[lanePoints.length - 1].point,
        labelPoint: {
          ...lanePoints[lanePoints.length - 1].point,
          normalX: directionVector(corridor.direction).x,
          normalY: directionVector(corridor.direction).y
        }
      });
    });
  });

  return {
    metrics,
    paths,
    markers,
    labels: markers.map((marker) => ({
      id: marker.id,
      station: marker.station,
      point: marker.station.role === "branchStation" ? marker.point : marker.lanePoints[0].point
    }))
  };
}

function RoutePath({ color, d, strokeWidth }) {
  return (
    <path
      d={d}
      fill="none"
      stroke={color}
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={strokeWidth}
    />
  );
}

function StationMarker({ station, point, size, darkMode }) {
  const stroke = darkMode ? "#dbeafe" : "#1e293b";
  if (station.type === "transfer") {
    return (
      <rect
        x={point.x - size * 1.45}
        y={point.y - size * 0.72}
        width={size * 2.9}
        height={size * 1.44}
        rx={8}
        fill="#ffffff"
        stroke={stroke}
        strokeWidth="3"
      />
    );
  }
  return (
    <circle
      cx={point.x}
      cy={point.y}
      r={station.type === "terminal" ? size * 0.72 : size * 0.52}
      fill="#ffffff"
      stroke={stroke}
      strokeWidth={station.type === "terminal" ? "3" : "2.5"}
    />
  );
}

function StationLabel({ station, point, darkMode }) {
  const offset = 22;
  const labelColor = darkMode ? "#e2e8f0" : "#0f172a";
  const mutedColor = darkMode ? "#94a3b8" : "#475569";
  const anchor = station.label === "left" ? "end" : station.label === "right" ? "start" : "middle";
  const dx = station.label === "left" ? -offset : station.label === "right" ? offset : 0;
  const dy = station.label === "top" ? -offset : station.label === "bottom" ? offset + 8 : 5;
  return (
    <text
      x={point.x + dx}
      y={point.y + dy}
      fill={station.type === "normal" ? mutedColor : labelColor}
      fontSize={station.type === "normal" ? "14" : "18"}
      fontWeight={station.type === "normal" ? "600" : "700"}
      textAnchor={anchor}
    >
      {station.name}
    </text>
  );
}

function TrunkBranchDiagram({ data, controls }) {
  const markerLine = data.lines.find((line) => line.id === "cyan") || data.lines[0];
  const labelLine = data.lines.find((line) => line.id === "blue") || data.lines[0];
  const branchLine = data.lines.find((line) => line.id === "orange") || data.lines[0];
  const trunkLines = data.lines;
  return (
    <svg viewBox="0 0 760 520" role="img" aria-label="主干走廊加支线示例">
      <g>
        {data.lines.map((line) => (
          <RoutePath key={line.id} color={line.color} d={trunkPathFor(line, controls.laneGap)} strokeWidth={controls.strokeWidth} />
        ))}
      </g>
      <g>
        {data.stations.flatMap((station) => {
          const lines = station.x === 360 && station.type === "normal" ? trunkLines : [station.x === 360 ? markerLine : branchLine];
          return lines.map((line) => (
            <StationMarker
              key={`${station.id}-${line.id}`}
              station={station}
              point={stationPointForTrunk(station, line, controls.laneGap)}
              size={controls.stationSize}
              darkMode={controls.darkMode}
            />
          ));
        })}
      </g>
      {controls.showLabels ? (
        <g>
          {data.stations.map((station) => {
            const line = station.x === 360 ? labelLine : branchLine;
            return (
              <StationLabel
                key={station.id}
                station={station}
                point={stationPointForTrunk(station, line, controls.laneGap)}
                darkMode={controls.darkMode}
              />
            );
          })}
        </g>
      ) : null}
    </svg>
  );
}

function LoopStationMarker({ marker, size, darkMode }) {
  const { station, point, lanePoints } = marker;
  const stroke = darkMode ? "#dbeafe" : "#1e293b";
  if (station.type === "terminal") {
    return (
      <rect
        x={point.x - size * 1.25}
        y={point.y - size * 0.72}
        width={size * 2.5}
        height={size * 1.44}
        rx={8}
        fill="#ffffff"
        stroke={stroke}
        strokeWidth="3"
      />
    );
  }

  if (station.role !== "junction") {
    const renderDot = (lanePoint) => (
      <circle
        key={`${station.id}-${lanePoint.lineId}`}
        cx={lanePoint.point.x}
        cy={lanePoint.point.y}
        r={size * 0.52}
        fill="#ffffff"
        stroke={lanePoint.color}
        strokeWidth="2.5"
      />
    );
    return (
      <g>
        {lanePoints.map(renderDot)}
      </g>
    );
  }

  const firstPoint = lanePoints[0].point;
  const lastPoint = lanePoints[lanePoints.length - 1].point;
  const dx = lastPoint.x - firstPoint.x;
  const dy = lastPoint.y - firstPoint.y;
  const spanLength = Math.sqrt(dx * dx + dy * dy);
  const angle = spanLength > 0 ? Math.atan2(dy, dx) * 180 / Math.PI : point.tangent;
  const centerX = (firstPoint.x + lastPoint.x) / 2;
  const centerY = (firstPoint.y + lastPoint.y) / 2;
  const markerWidth = Math.max(size * 4.1, spanLength + size * 3.1);
  const markerHeight = Math.max(size * 1.68, 26);
  return (
    <rect
      x={centerX - markerWidth / 2}
      y={centerY - markerHeight / 2}
      width={markerWidth}
      height={markerHeight}
      rx={8}
      fill="#ffffff"
      stroke={stroke}
      strokeWidth="3"
      transform={`rotate(${angle} ${centerX} ${centerY})`}
    />
  );
}

function LoopStationLabel({ station, point, metrics, darkMode }) {
  const labelColor = darkMode ? "#e2e8f0" : "#0f172a";
  const mutedColor = darkMode ? "#94a3b8" : "#475569";
  const insideSideByEdge = {
    top: "bottom",
    right: "left",
    bottom: "top",
    left: "right"
  };
  const offset = station.role === "junction" ? Math.max(38, metrics.laneStep * 1.8) : Math.max(30, metrics.laneStep * 1.35);
  const side = station.role === "branchStation" ? (station.label || "right") : (insideSideByEdge[station.loc?.edge] || station.label || "right");
  const labelX = side === "left" ? point.x - offset : side === "right" ? point.x + offset : point.x;
  const labelY = side === "top" ? point.y - offset : side === "bottom" ? point.y + offset + 5 : point.y + 5;
  const anchor = side === "left" ? "end" : side === "right" ? "start" : "middle";
  const color = station.type === "normal" ? mutedColor : labelColor;
  return (
    <text x={labelX} y={labelY} fill={color} fontSize="16" fontWeight="700" textAnchor={anchor}>
      <tspan x={labelX} dy="0">{station.name}</tspan>
      {station.subName ? <tspan x={labelX} dy="20" fill={mutedColor} fontSize="12" fontWeight="700">{station.subName}</tspan> : null}
    </text>
  );
}

function LoopBranchDiagram({ data, controls }) {
  const layout = generateLoopLayout(data, controls);
  return (
    <svg viewBox="-170 20 930 520" role="img" aria-label="环线加支线示例">
      <g>
        {layout.paths.map((path) => (
          <RoutePath key={path.id} color={path.color} d={path.d} strokeWidth={layout.metrics.strokeWidth} />
        ))}
      </g>
      <g>
        {layout.markers.map((marker) => (
          <LoopStationMarker
            key={marker.id}
            marker={marker}
            size={controls.stationSize}
            darkMode={controls.darkMode}
          />
        ))}
      </g>
      {controls.showLabels ? (
        <g>
          {layout.labels.map((label) => (
            <LoopStationLabel
              key={label.id}
              station={label.station}
              point={label.point}
              metrics={layout.metrics}
              darkMode={controls.darkMode}
            />
          ))}
        </g>
      ) : null}
    </svg>
  );
}

function LineLegend({ lines }) {
  return (
    <div className="tdp-legend">
      {lines.map((line) => (
        <div className="tdp-legend-row" key={line.id}>
          <span className="tdp-swatch" style={{ backgroundColor: line.color }} />
          <span className="tdp-legend-name">{line.name}</span>
          <span className="tdp-legend-status">{LINE_STATUS[line.status]}</span>
        </div>
      ))}
    </div>
  );
}

function DiagramControls({ controls, setControls, data }) {
  const setValue = (key, value) => {
    setControls((current) => ({ ...current, [key]: value }));
  };
  const mockPreview = useMemo(() => JSON.stringify({
    lines: data.lines,
    stations: data.stations.slice(0, 5),
    pathDefinitions: data.id === "trunk" ? "shared vertical corridor + orange branch" : "rounded-rectangle loop + cyan branch"
  }, null, 2), [data]);

  return (
    <aside className="tdp-panel">
      <div className="tdp-panel-header">
        <h1>线路图 Playground</h1>
        <p>固定 mock 布局，验证示意化铁路线路图渲染语法。</p>
      </div>

      <section className="tdp-section">
        <h2>示例选择</h2>
        <div className="tdp-segmented">
          {Object.values(EXAMPLES).map((example) => (
            <button
              key={example.id}
              className={controls.exampleId === example.id ? "tdp-segment tdp-segment-active" : "tdp-segment"}
              type="button"
              onClick={() => setValue("exampleId", example.id)}
            >
              {example.name}
            </button>
          ))}
        </div>
        <p className="tdp-note">{data.description}</p>
      </section>

      <section className="tdp-section">
        <h2>视觉参数</h2>
        <label className="tdp-field">
          <span>线路宽度</span>
          <input type="range" min="8" max="22" step="2" value={controls.strokeWidth} onChange={(event) => setValue("strokeWidth", Number(event.target.value))} />
          <strong>{controls.strokeWidth}</strong>
        </label>
        <label className="tdp-field">
          <span>并行线间距</span>
          <input type="range" min="8" max="24" step="4" value={controls.laneGap} onChange={(event) => setValue("laneGap", Number(event.target.value))} />
          <strong>{controls.laneGap}</strong>
        </label>
        <label className="tdp-field">
          <span>站点大小</span>
          <input type="range" min="9" max="17" step="2" value={controls.stationSize} onChange={(event) => setValue("stationSize", Number(event.target.value))} />
          <strong>{controls.stationSize}</strong>
        </label>
        <label className="tdp-check">
          <input type="checkbox" checked={controls.showLabels} onChange={(event) => setValue("showLabels", event.target.checked)} />
          <span>显示站名</span>
        </label>
        <label className="tdp-check">
          <input type="checkbox" checked={controls.darkMode} onChange={(event) => setValue("darkMode", event.target.checked)} />
          <span>深色背景</span>
        </label>
      </section>

      <section className="tdp-section">
        <h2>线路图例</h2>
        <LineLegend lines={data.lines} />
      </section>

      <section className="tdp-section">
        <label className="tdp-check">
          <input type="checkbox" checked={controls.showMockData} onChange={(event) => setValue("showMockData", event.target.checked)} />
          <span>展示只读 mock 数据</span>
        </label>
        {controls.showMockData ? <pre className="tdp-mock">{mockPreview}</pre> : null}
      </section>
    </aside>
  );
}

function TransitDiagramPreview({ data, controls }) {
  return (
    <main className={controls.darkMode ? "tdp-preview tdp-preview-dark" : "tdp-preview"}>
      <div className="tdp-preview-header">
        <h2>{data.name}</h2>
        <p>SVG 预览会随左侧参数实时更新。</p>
      </div>
      <div className="tdp-svg-stage">
        {data.id === "trunk" ? (
          <TrunkBranchDiagram data={data} controls={controls} />
        ) : (
          <LoopBranchDiagram data={data} controls={controls} />
        )}
      </div>
    </main>
  );
}

export default function TransitDiagramPlayground() {
  const [controls, setControls] = useState(DEFAULT_CONTROLS);
  const data = EXAMPLES[controls.exampleId];

  return (
    <div className="tdp-root">
      <DiagramControls controls={controls} setControls={setControls} data={data} />
      <TransitDiagramPreview data={data} controls={controls} />
    </div>
  );
}
