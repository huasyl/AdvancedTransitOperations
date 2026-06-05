function pathForLine(line, stationsById) {
  return line.stationIds
    .map((stationId, index) => {
      const station = stationsById.get(stationId);
      if (!station) {
        return "";
      }
      return `${index === 0 ? "M" : "L"} ${station.x} ${station.y}`;
    })
    .filter(Boolean)
    .join(" ");
}

function resolveVehiclePosition(vehicle, stationsById) {
  const start = stationsById.get(vehicle.currentStationId);
  const end = stationsById.get(vehicle.nextStationId);
  if (!start || !end) {
    return null;
  }
  const progress = Math.max(0, Math.min(1, Number(vehicle.progress || 0)));
  return {
    x: start.x + (end.x - start.x) * progress,
    y: start.y + (end.y - start.y) * progress
  };
}

export default function OverviewNetworkDiagram({ lines, stations, vehicles, activeMode }) {
  const activeLines = lines.filter((line) => line.mode === activeMode);
  const activeLineIds = new Set(activeLines.map((line) => line.id));
  const stationsById = new Map(stations.map((station) => [station.id, station]));

  return (
    <div className="rtw-overview-network-panel">
      <div className="rtw-overview-network-head">
        <div className="rtw-overview-network-title">线网实时追踪 / NETWORK</div>
        <div className="rtw-overview-network-status"><span />物理链路追踪激活</div>
      </div>
      <div className="rtw-overview-network-stage">
        <svg viewBox="0 0 800 800" className="rtw-overview-network-svg" preserveAspectRatio="xMidYMid meet">
          {activeLines.map((line) => (
            <path
              key={`track-${line.id}`}
              d={pathForLine(line, stationsById)}
              fill="none"
              stroke="#3f3f46"
              strokeWidth="8"
              strokeLinecap="round"
              strokeLinejoin="round"
              opacity="0.8"
            />
          ))}
          {activeLines.map((line) => (
            <path
              key={`track-inner-${line.id}`}
              d={pathForLine(line, stationsById)}
              fill="none"
              stroke={line.color}
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              opacity="0.52"
            />
          ))}
          {stations.map((station) => (
            <g key={station.id}>
              <circle cx={station.x} cy={station.y} r="5" fill="#09090b" stroke="#52525b" strokeWidth="2.5" />
              <circle cx={station.x} cy={station.y} r="2" fill="#a1a1aa" />
              <text x={station.x} y={station.y - 12} fill="#18181b" fontSize="12" textAnchor="middle" stroke="#18181b" strokeWidth="3">{station.name}</text>
              <text x={station.x} y={station.y - 12} fill="#f4f4f5" fontSize="12" textAnchor="middle">{station.name}</text>
            </g>
          ))}
          {vehicles.filter((vehicle) => activeLineIds.has(vehicle.lineId)).map((vehicle) => {
            const position = resolveVehiclePosition(vehicle, stationsById);
            const line = activeLines.find((candidate) => candidate.id === vehicle.lineId);
            if (!position || !line) {
              return null;
            }
            return (
              <g key={vehicle.id} transform={`translate(${position.x} ${position.y})`}>
                <rect x="-14" y="-8" width="28" height="16" rx="4" fill="#09090b" stroke={line.color} strokeWidth="1.5" />
                <polygon points="14,-8 18,0 14,8" fill={line.color} />
              </g>
            );
          })}
        </svg>
      </div>
    </div>
  );
}
