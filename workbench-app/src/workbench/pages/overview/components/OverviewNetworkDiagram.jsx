import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

export default function OverviewNetworkDiagram() {
  const { t } = useNativeScheduleI18n();

  return (
    <div className="rtw-overview-network-panel">
      <div className="rtw-overview-network-head">
        <div className="rtw-overview-network-title">{t("nativeWorkbench.overview.network.title")}</div>
      </div>
      <div className="rtw-overview-network-stage">
        <div className="rtw-overview-network-placeholder">
          <div className="rtw-overview-network-placeholder-title">{t("nativeWorkbench.overview.network.placeholderTitle")}</div>
        </div>
      </div>
    </div>
  );
}
