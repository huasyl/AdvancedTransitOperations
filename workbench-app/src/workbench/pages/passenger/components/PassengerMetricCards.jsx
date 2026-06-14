import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

function total(values, key) {
  return values.reduce((sum, entry) => sum + Number(entry?.[key] || 0), 0);
}

export default function PassengerMetricCards({ data }) {
  const { t } = useNativeScheduleI18n();
  const stationTotal = total(data.stationVolumes, "inflow") + total(data.stationVolumes, "outflow");
  const sectionTotal = total(data.sectionVolumes, "volume");
  const odTotal = total(data.odFlows, "volume");
  const trendPeak = data.systemTrend.reduce((max, point) => Math.max(max, Number(point?.passengers || 0)), 0);
  const items = [
    { label: t("nativeWorkbench.passenger.metric.trendPeak"), value: trendPeak.toLocaleString() },
    { label: t("nativeWorkbench.passenger.metric.stationVolume"), value: stationTotal.toLocaleString() },
    { label: t("nativeWorkbench.passenger.metric.sectionVolume"), value: sectionTotal.toLocaleString() },
    { label: t("nativeWorkbench.passenger.metric.odTotal"), value: odTotal.toLocaleString() }
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
