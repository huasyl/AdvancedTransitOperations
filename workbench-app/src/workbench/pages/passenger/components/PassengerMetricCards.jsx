function total(values, key) {
  return values.reduce((sum, entry) => sum + Number(entry?.[key] || 0), 0);
}

export default function PassengerMetricCards({ data }) {
  const stationTotal = total(data.stationVolumes, "inflow") + total(data.stationVolumes, "outflow");
  const sectionTotal = total(data.sectionVolumes, "volume");
  const odTotal = total(data.odFlows, "volume");
  const trendPeak = data.systemTrend.reduce((max, point) => Math.max(max, Number(point?.passengers || 0)), 0);
  const items = [
    { label: "分时峰值", value: trendPeak.toLocaleString() },
    { label: "站点进出", value: stationTotal.toLocaleString() },
    { label: "断面流量", value: sectionTotal.toLocaleString() },
    { label: "OD 汇总", value: odTotal.toLocaleString() }
  ];

  return (
    <div className="rtw-passenger-metrics">
      {items.map((item) => (
        <div key={item.label} className="rtw-passenger-metric">
          <div className="rtw-passenger-metric-label">{item.label}</div>
          <div className="rtw-passenger-metric-value">{item.value}</div>
        </div>
      ))}
    </div>
  );
}
