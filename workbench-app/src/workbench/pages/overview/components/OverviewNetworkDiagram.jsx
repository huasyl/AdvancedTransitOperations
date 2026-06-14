import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

export default function OverviewNetworkDiagram({ lines, stations, vehicles, activeMode }) {
  const { t } = useNativeScheduleI18n();
  const activeLines = lines.filter((line) => line.mode === activeMode);
  const activeLineIds = new Set(activeLines.map((line) => line.id));
  const activeVehicleCount = vehicles.filter((vehicle) => activeLineIds.has(vehicle.lineId)).length;

  return (
    <div className="rtw-overview-network-panel">
      <div className="rtw-overview-network-head">
        <div className="rtw-overview-network-title">{t("nativeWorkbench.overview.network.title")}</div>
      </div>
      <div className="rtw-overview-network-stage">
        <div className="rtw-overview-network-placeholder">
          <div className="rtw-overview-network-placeholder-title">{t("nativeWorkbench.overview.network.placeholderTitle")}</div>
          <div className="rtw-overview-network-placeholder-row">
            <span>{activeLines.length}</span>
            <span>{t("nativeWorkbench.overview.network.activeLines")}</span>
          </div>
          <div className="rtw-overview-network-placeholder-row">
            <span>{stations.length}</span>
            <span>{t("nativeWorkbench.overview.network.stations")}</span>
          </div>
          <div className="rtw-overview-network-placeholder-row">
            <span>{activeVehicleCount}</span>
            <span>{t("nativeWorkbench.overview.network.vehicles")}</span>
          </div>
        </div>
      </div>
    </div>
  );
}
