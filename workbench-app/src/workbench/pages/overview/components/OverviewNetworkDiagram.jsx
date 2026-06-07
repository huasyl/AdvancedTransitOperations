export default function OverviewNetworkDiagram({ lines, stations, vehicles, activeMode }) {
  const activeLines = lines.filter((line) => line.mode === activeMode);
  const activeLineIds = new Set(activeLines.map((line) => line.id));
  const activeVehicleCount = vehicles.filter((vehicle) => activeLineIds.has(vehicle.lineId)).length;

  return (
    <div className="rtw-overview-network-panel">
      <div className="rtw-overview-network-head">
        <div className="rtw-overview-network-title">线网实时追踪 / NETWORK</div>
        <div className="rtw-overview-network-status"><span />物理链路追踪激活</div>
      </div>
      <div className="rtw-overview-network-stage">
        <div className="rtw-overview-network-placeholder">
          <div className="rtw-overview-network-placeholder-title">SVG network disabled</div>
          <div className="rtw-overview-network-placeholder-row">
            <span>{activeLines.length}</span>
            <span>active lines</span>
          </div>
          <div className="rtw-overview-network-placeholder-row">
            <span>{stations.length}</span>
            <span>stations</span>
          </div>
          <div className="rtw-overview-network-placeholder-row">
            <span>{activeVehicleCount}</span>
            <span>vehicles</span>
          </div>
        </div>
      </div>
    </div>
  );
}
