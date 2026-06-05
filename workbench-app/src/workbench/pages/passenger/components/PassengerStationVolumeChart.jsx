export default function PassengerStationVolumeChart({ volumes }) {
  if (!volumes.length) {
    return <div className="rtw-passenger-empty">暂无真实站点进出站数据</div>;
  }

  const maxValue = Math.max(1, ...volumes.map((entry) => Number(entry?.inflow || 0) + Number(entry?.outflow || 0)));
  const barWidth = 720 / Math.max(1, volumes.length);
  return (
    <svg viewBox="0 0 720 250" className="rtw-passenger-chart-svg" preserveAspectRatio="none">
      {volumes.map((entry, index) => {
        const inflowHeight = (Number(entry?.inflow || 0) / maxValue) * 190;
        const outflowHeight = (Number(entry?.outflow || 0) / maxValue) * 190;
        const x = index * barWidth + barWidth * 0.25;
        return (
          <g key={`${entry?.stationId || index}`}>
            <rect x={x} y={220 - inflowHeight} width={barWidth * 0.22} height={inflowHeight} rx="4" fill="#10b981" />
            <rect x={x + barWidth * 0.26} y={220 - outflowHeight} width={barWidth * 0.22} height={outflowHeight} rx="4" fill="#f59e0b" />
          </g>
        );
      })}
    </svg>
  );
}
