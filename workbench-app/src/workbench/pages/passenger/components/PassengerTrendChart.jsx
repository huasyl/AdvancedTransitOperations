function buildPolyline(points, width, height) {
  const maxValue = Math.max(1, ...points.map((point) => Number(point?.passengers || 0)));
  return points.map((point, index) => {
    const x = points.length <= 1 ? 0 : (index / (points.length - 1)) * width;
    const y = height - (Number(point?.passengers || 0) / maxValue) * height;
    return `${x},${y}`;
  }).join(" ");
}

export default function PassengerTrendChart({ points }) {
  if (!points.length) {
    return <div className="rtw-passenger-empty">暂无真实分时客流数据</div>;
  }

  const polyline = buildPolyline(points, 720, 210);
  return (
    <svg viewBox="0 0 720 250" className="rtw-passenger-chart-svg" preserveAspectRatio="none">
      <polyline points={`0,230 ${polyline} 720,230`} fill="rgba(56, 189, 248, 0.16)" stroke="none" />
      <polyline points={polyline} fill="none" stroke="#38bdf8" strokeWidth="3" />
    </svg>
  );
}
