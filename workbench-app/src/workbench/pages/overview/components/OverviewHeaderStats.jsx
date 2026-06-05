function formatNumber(value) {
  const numeric = Number(value || 0);
  return numeric.toLocaleString();
}

export default function OverviewHeaderStats({ summary }) {
  const items = [
    { label: "总客流数据", value: formatNumber(summary.estimatedPassengerLoad), unit: "/h" },
    { label: "服役车辆", value: `${summary.activeVehicleCount}/${summary.scheduledVehicleCount}`, unit: "" },
    { label: "最高负荷枢纽", value: summary.peakStationName || "暂无数据", unit: "" },
    { label: "运行准点率", value: `${summary.healthPercent.toFixed(1)}%`, unit: "" }
  ];

  return (
    <div className="rtw-overview-stats">
      {items.map((item) => (
        <div key={item.label} className="rtw-overview-stat-item">
          <div className="rtw-overview-stat-label">{item.label}</div>
          <div className="rtw-overview-stat-value">
            {item.value}
            {item.unit ? <span className="rtw-overview-stat-unit">{item.unit}</span> : null}
          </div>
        </div>
      ))}
    </div>
  );
}
