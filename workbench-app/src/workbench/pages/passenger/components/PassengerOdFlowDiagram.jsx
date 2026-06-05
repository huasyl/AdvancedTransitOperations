export default function PassengerOdFlowDiagram({ flows }) {
  if (!flows.length) {
    return <div className="rtw-passenger-empty is-large">暂无真实 OD 客流数据</div>;
  }

  const maxValue = Math.max(1, ...flows.map((flow) => Number(flow?.volume || 0)));
  return (
    <svg viewBox="0 0 800 600" className="rtw-passenger-od-svg" preserveAspectRatio="xMidYMid meet">
      {flows.slice(0, 12).map((flow, index) => {
        const y = 60 + index * 42;
        const strokeWidth = 1 + (Number(flow?.volume || 0) / maxValue) * 10;
        return (
          <g key={`${flow?.originStationId || index}-${flow?.destinationStationId || index}`}>
            <text x="70" y={y + 4} fill="#a1a1aa" fontSize="12" textAnchor="end">{flow?.originName || flow?.originStationId}</text>
            <path d={`M 90 ${y} C 270 ${y - 30}, 530 ${y + 30}, 710 ${y}`} fill="none" stroke="#38bdf8" strokeWidth={strokeWidth} strokeOpacity="0.42" />
            <text x="730" y={y + 4} fill="#f4f4f5" fontSize="12">{flow?.destinationName || flow?.destinationStationId}</text>
          </g>
        );
      })}
    </svg>
  );
}
