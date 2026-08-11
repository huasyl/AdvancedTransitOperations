import { useMemo } from "react";
import { minutesToTime, timeToMinutes } from "./timetable-data";

const WIDTH = 960;
const HEIGHT = 300;
const LEFT = 118;
const RIGHT = 24;
const TOP = 18;
const BOTTOM = 34;

export default function RunChart({ stations, series, emptyText }) {
  const model = useMemo(() => {
    const points = series.flatMap((item) => item.points);
    if (stations.length === 0 || points.length === 0) {
      return null;
    }

    const minimum = Math.min(...points.map((point) => point.arrivalTime)) - 15;
    const maximum = Math.max(...points.map((point) => point.departureTime)) + 15;
    const minTime = Math.floor(minimum / 15) * 15;
    const maxTime = Math.ceil(maximum / 15) * 15;
    const minDistance = Math.min(...stations.map((station) => station.distance));
    const maxDistance = Math.max(...stations.map((station) => station.distance));
    const distanceRange = Math.max(1, maxDistance - minDistance);
    const timeRange = Math.max(1, maxTime - minTime);
    const x = (value) => LEFT + ((value - minTime) / timeRange) * (WIDTH - LEFT - RIGHT);
    const y = (value) => TOP + ((value - minDistance) / distanceRange) * (HEIGHT - TOP - BOTTOM);
    const ticks = [];
    for (let minute = minTime; minute <= maxTime; minute += 15) {
      ticks.push(minute);
    }

    return { minTime, maxTime, x, y, ticks };
  }, [series, stations]);

  if (!model) {
    return <div className="rtw-timetable-chart-empty">{emptyText}</div>;
  }

  return (
    <svg className="rtw-timetable-chart-svg" viewBox={`0 0 ${WIDTH} ${HEIGHT}`} preserveAspectRatio="xMidYMid meet" aria-hidden="true">
      {model.ticks.map((tick) => (
        <g key={`time-${tick}`}>
          <line className="rtw-timetable-chart-grid is-time" x1={model.x(tick)} x2={model.x(tick)} y1={TOP} y2={HEIGHT - BOTTOM} />
          <text className="rtw-timetable-chart-time" x={model.x(tick)} y={HEIGHT - 10} textAnchor="middle">{minutesToTime(tick)}</text>
        </g>
      ))}
      {stations.map((station) => (
        <g key={station.id}>
          <line className="rtw-timetable-chart-grid" x1={LEFT} x2={WIDTH - RIGHT} y1={model.y(station.distance)} y2={model.y(station.distance)} />
          <text className="rtw-timetable-chart-station" x={LEFT - 12} y={model.y(station.distance) + 4} textAnchor="end">{station.name}</text>
        </g>
      ))}
      {series.map((item) => {
        const pathPoints = [];
        item.points.forEach((point) => {
          pathPoints.push(`${model.x(point.arrivalTime)},${model.y(point.distance)}`);
          if (point.departureTime !== point.arrivalTime) {
            pathPoints.push(`${model.x(point.departureTime)},${model.y(point.distance)}`);
          }
        });
        return (
          <g key={`${item.lineId}-${item.trainId}`}>
            <polyline className="rtw-timetable-chart-line" points={pathPoints.join(" ")} style={{ stroke: item.color }} />
            {item.points.map((point) => (
              <circle key={`${item.trainId}-${point.stationId}`} className="rtw-timetable-chart-point" cx={model.x(timeToMinutes(point.arrivalLabel))} cy={model.y(point.distance)} r="3" style={{ fill: item.color }} />
            ))}
          </g>
        );
      })}
    </svg>
  );
}
