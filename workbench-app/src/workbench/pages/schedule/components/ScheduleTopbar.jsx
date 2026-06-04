import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import WorkbenchDropdown from "../../../shared/WorkbenchDropdown";
import {
  getLocalizedDepotLabel,
  getLocalizedLineName,
  getLocalizedOriginLabel
} from "../schedule-catalog";
import { DemoDisplayField, DemoTextField } from "./ScheduleFields";

export default function ScheduleTopbar({ topbar, refs, actions }) {
  const { t } = useNativeScheduleI18n();

  return (
    <div className="dw-demo-topbar">
      <WorkbenchDropdown
        label={t("nativeSchedule.topbar.line")}
        value={getLocalizedLineName(topbar.selectedLine, t)}
        options={topbar.lineOptions.map((line) => ({
          value: line?.id || "",
          label: getLocalizedLineName(line, t),
          active: line?.id === topbar.selectedLineId
        }))}
        onSelect={actions.selectLine}
        className="is-line"
        variant="field"
        positioning="portal"
        portalHostRef={refs.dropdownPortalHostRef}
      />

      <div className="dw-demo-field is-kind">
        <label className="dw-demo-label">{t("nativeSchedule.topbar.kind")}</label>
        <div className="dw-demo-toggle-group">
          <button
            type="button"
            className={`dw-demo-toggle ${topbar.selectedLineType === "local" ? "is-active" : ""}`}
            onClick={() => actions.selectLineType("local")}
          >
            {t("nativeSchedule.type.local")}
          </button>
          <button
            type="button"
            className={`dw-demo-toggle ${topbar.selectedLineType === "express" ? "is-active is-express" : ""}`}
            onClick={() => actions.selectLineType("express")}
          >
            {t("nativeSchedule.type.express")}
          </button>
        </div>
      </div>

      <DemoDisplayField
        label={t("nativeSchedule.topbar.origin")}
        value={getLocalizedOriginLabel(topbar.origin, t)}
        className="is-origin"
      />
      <WorkbenchDropdown
        label={t("nativeSchedule.topbar.depot")}
        value={getLocalizedDepotLabel(topbar.selectedDepot, t)}
        options={[
          {
            value: "",
            label: t("nativeSchedule.data.depot.any"),
            active: !topbar.selectedDepot
          },
          ...topbar.availableDepots.map((depot) => ({
            value: depot?.id || "",
            label: depot.label || t(depot.labelKey),
            active: topbar.selectedDepot === depot?.id
          }))
        ]}
        onSelect={actions.changeDepot}
        className="is-depot"
        variant="field"
        positioning="portal"
        portalHostRef={refs.dropdownPortalHostRef}
      />

      <DemoTextField label={topbar.holdMinutesTooSmall ? "\u4e0d\u5f97\u5c0f\u4e8e5\u5206" : t("nativeSchedule.topbar.holdMinutes")} value={topbar.holdMinutes} onCommit={actions.changeHoldMinutes} onDraftChange={actions.setHoldMinutes} className={`is-hold${topbar.holdMinutesTooSmall ? " is-error" : ""}`} suffix={t("nativeSchedule.unit.minutes")} />
      <DemoTextField label={topbar.dwellMinutesTooSmall ? "\u4e0d\u5f97\u5c0f\u4e8e5\u5206" : t("nativeSchedule.topbar.dwellMinutes")} value={topbar.dwellMinutes} onCommit={actions.changeDwellMinutes} onDraftChange={actions.setDwellMinutes} className={`is-dwell${topbar.dwellMinutesTooSmall ? " is-error" : ""}`} suffix={t("nativeSchedule.unit.minutes")} />
      <div className="dw-demo-field is-features">
        <label className="dw-demo-label">{t("nativeSchedule.topbar.runtimeFeatures")}</label>
        <div className="dw-demo-feature-toggle-group">
          <button
            type="button"
            className={`dw-demo-toggle dw-demo-feature-toggle ${topbar.featureSettings.dispatchEnabled ? "is-active" : ""}`}
            onClick={() => actions.toggleFeature("dispatchEnabled")}
          >
            {t("nativeSchedule.feature.dispatch")}
          </button>
          <button
            type="button"
            className={`dw-demo-toggle dw-demo-feature-toggle ${topbar.featureSettings.bypassEnabled ? "is-active" : ""}`}
            onClick={() => actions.toggleFeature("bypassEnabled")}
          >
            {t("nativeSchedule.feature.bypass")}
          </button>
          <button
            type="button"
            className={`dw-demo-toggle dw-demo-feature-toggle ${topbar.featureSettings.broadcastEnabled ? "is-active" : ""}`}
            onClick={() => actions.toggleFeature("broadcastEnabled")}
          >
            {t("nativeSchedule.feature.broadcast")}
          </button>
          <button
            type="button"
            className={`dw-demo-toggle dw-demo-feature-toggle ${topbar.featureSettings.depotLockEnabled ? "is-active" : ""}`}
            onClick={() => actions.toggleFeature("depotLockEnabled")}
          >
            {t("nativeSchedule.feature.depotLock")}
          </button>
        </div>
      </div>
    </div>
  );
}
