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
        value={
          topbar.selectedLine?.dispatchSupported === false && topbar.selectedLine?.originMessageKey
            ? t(topbar.selectedLine.originMessageKey)
            : getLocalizedOriginLabel(topbar.origin, t)
        }
        className={`is-origin${topbar.selectedLine?.dispatchSupported === false ? " is-error" : ""}`}
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

      <DemoTextField label={topbar.holdMinutesTooSmall ? t("nativeSchedule.topbar.minimumFiveMinutes") : t("nativeSchedule.topbar.holdMinutes")} value={topbar.holdMinutes} onCommit={actions.changeHoldMinutes} onDraftChange={actions.changeHoldMinutes} className={`is-hold${topbar.holdMinutesTooSmall ? " is-error" : ""}`} suffix={t("nativeSchedule.unit.minutes")} />
      <DemoTextField label={topbar.dwellMinutesTooSmall ? t("nativeSchedule.topbar.minimumFiveMinutes") : t("nativeSchedule.topbar.dwellMinutes")} value={topbar.dwellMinutes} onCommit={actions.changeDwellMinutes} onDraftChange={actions.changeDwellMinutes} className={`is-dwell${topbar.dwellMinutesTooSmall ? " is-error" : ""}`} suffix={t("nativeSchedule.unit.minutes")} />
    </div>
  );
}
