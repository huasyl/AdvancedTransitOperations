import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

function formatNumber(value) {
  const numeric = Number(value || 0);
  return numeric.toLocaleString();
}

function withSoftBreaks(value) {
  return String(value || "")
    .replace(/[A-Za-z0-9]{12,}/g, (token) => token.replace(/(.{10})/g, "$1\u200b"))
    .replace(/([/_-])/g, "$1\u200b");
}

export default function OverviewHeaderStats({ summary, showSectionLoad = true }) {
  const { t } = useNativeScheduleI18n();
  const items = [
    { label: t("nativeWorkbench.overview.stats.totalBoardings"), value: formatNumber(summary.totalBoardingsAlightings24h), unit: "" },
    { label: t("nativeWorkbench.overview.stats.busiestStation"), value: summary.busiestStationName || t("nativeWorkbench.overview.empty.noData"), unit: "", isText: true },
    ...(showSectionLoad ? [{ label: t("nativeWorkbench.overview.stats.peakSectionLoad"), value: formatNumber(summary.peakSectionLoad), unit: "" }] : []),
    { label: t("nativeWorkbench.overview.stats.peakQuarterHourFlow"), value: formatNumber(summary.peakQuarterHourFlow), unit: "" }
  ];

  return (
    <div className="rtw-overview-stats">
      {items.map((item) => (
        <div key={item.label} className="rtw-overview-stat-item">
          <div className="rtw-overview-stat-label">{item.label}</div>
          <div className={`rtw-overview-stat-value ${item.isText ? "is-text" : ""}`}>
            {item.isText ? withSoftBreaks(item.value) : item.value}
            {item.unit ? <span className="rtw-overview-stat-unit">{item.unit}</span> : null}
          </div>
        </div>
      ))}
    </div>
  );
}
