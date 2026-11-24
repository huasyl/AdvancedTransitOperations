import { useMemo } from "react";
import useOverviewFeatureSettings from "../../overview/useOverviewFeatureSettings";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import OverviewModeRail from "../../overview/components/OverviewModeRail";
import OverviewSystemSwitches from "../../overview/components/OverviewSystemSwitches";
import { getLocalizedLineName, sortLineOptions } from "../schedule-catalog";

export default function ScheduleSidebar({ controller, activeTransportMode, onTransportModeChange, compact = false }) {
  const { t } = useNativeScheduleI18n();
  const { topbar, line, actions } = controller;
  const modes = ["Bus", "Train", "Tram", "Subway"].map((mode) => ({ mode, label: t(`nativeWorkbench.overview.mode.${mode.toLowerCase()}`) }));
  const selectedMode = modes.find((mode) => mode.mode.toLowerCase() === activeTransportMode)?.mode || "Train";
  const settings = line.featureSettings;
  const systems = useMemo(() => settings ? ["dispatch", "broadcast", "depotLock"].map((name) => ({ key: `${name}Enabled`, title: t(`nativeWorkbench.overview.system.${name}`), enabled: settings[`${name}Enabled`] !== false })) : [], [settings, t]);
  const features = useOverviewFeatureSettings({ systems, featureSettings: settings, canEdit: Boolean(settings), onSaved: actions.acceptFeatureSettings });
  return <aside className={compact ? "dw-departure-sidebar is-compact" : "dw-departure-sidebar"}>
  <div className="dw-departure-sidebar-clip"><div className="dw-departure-sidebar-body">
    <OverviewModeRail modes={modes} activeMode={selectedMode} onModeChange={(mode) => onTransportModeChange?.(mode.toLowerCase())} showMetrics={false} compact={compact} />
    <WorkbenchScrollArea className="dw-departure-sidebar-scroll" metricsKey={topbar.lineOptions.length}>
      <div className="dw-departure-lines">
        {sortLineOptions(topbar.lineOptions, t).map((option) => <button key={option.id} type="button" className={`dw-departure-line-button ${line.view === "line" && option.id === topbar.selectedLineId ? "is-active" : ""}`} onClick={() => {
          actions.selectLine(option.id);
          actions.setScheduleView("line");
          actions.refreshNames();
        }}>
          <span className="dw-demo-dot" style={{ backgroundColor: option.color }} /><span className="dw-departure-line-name">{getLocalizedLineName(option, t)}</span>{Object.values(line.issues.get(option.id) || {}).some(Boolean) ? <span className="dw-departure-line-mark">!</span> : null}
        </button>)}
      </div>
    </WorkbenchScrollArea>
    <button type="button" className={`dw-demo-flat-button dw-departure-all-lines ${line.view === "all" ? "is-theme" : ""}`} onClick={() => actions.setScheduleView("all")}>{t("nativeSchedule.line.allLines")}</button>
    <OverviewSystemSwitches systems={features.systems} disabled={!settings} onSystemToggle={features.toggleFeature} />
    {features.error ? <div className="dw-departure-skip-note">{t("nativeSchedule.message.summary.saveFailed", { message: features.error })}</div> : null}
  </div></div>
  </aside>;
}
