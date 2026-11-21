import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import WorkbenchDropdown from "../../../shared/WorkbenchDropdown";
import {
  getLocalizedDepotLabel,
  getLocalizedLineName,
  getLocalizedOriginLabel,
  sortLineOptions
} from "../schedule-catalog";
import { DemoDisplayField, DemoTextField } from "./ScheduleFields";

export default function ScheduleTopbar({ topbar, refs, actions, hideLine = false }) {
  const { t } = useNativeScheduleI18n();

  return (
    <div className="dw-demo-topbar">
      <div className="dw-departure-topbar-main">
      {!hideLine ? <WorkbenchDropdown
        label={t("nativeSchedule.topbar.line")}
        value={getLocalizedLineName(topbar.selectedLine, t)}
        options={sortLineOptions(topbar.lineOptions, t).map((line) => ({
          value: line?.id || "",
          label: getLocalizedLineName(line, t),
          active: line?.id === topbar.selectedLineId
        }))}
        onSelect={actions.selectLine}
        onOpen={actions.refreshNames}
        className="is-line"
        variant="field"
        positioning="portal"
        portalHostRef={refs.dropdownPortalHostRef}
      /> : null}

      {topbar.supportsExpress ? (
        <div className="dw-demo-field is-kind">
          <label className="dw-demo-label">{t("nativeSchedule.topbar.kind")}</label>
          <div className="dw-demo-toggle-group">
            <button
              type="button"
              className={`dw-demo-toggle ${topbar.selectedLineType === "local" ? "is-active" : ""}`}
              onClick={() => actions.selectLineType("local")}
              disabled={topbar.editDisabled}
            >
              {t("nativeSchedule.type.local")}
            </button>
            <button
              type="button"
              className={`dw-demo-toggle ${topbar.selectedLineType === "express" ? "is-active is-express" : ""}`}
              onClick={() => actions.selectLineType("express")}
              disabled={topbar.editDisabled}
            >
              {t("nativeSchedule.type.express")}
            </button>
          </div>
        </div>
      ) : topbar.supportsSignalPriority ? (
        <div className="dw-demo-field is-kind">
          <label className="dw-demo-label">{t("nativeSchedule.topbar.signalPriority")}</label>
          <div className="dw-demo-toggle-group">
            <button
              type="button"
              className={`dw-demo-toggle ${!topbar.signalPriorityEnabled ? "is-active" : ""}`}
              onClick={() => actions.selectSignalPriority(false)}
              disabled={topbar.editDisabled}
            >
              {t("nativeSchedule.signalPriority.off")}
            </button>
            <button
              type="button"
              className={`dw-demo-toggle ${topbar.signalPriorityEnabled ? "is-active" : ""}`}
              onClick={() => actions.selectSignalPriority(true)}
              disabled={topbar.editDisabled}
            >
              {t("nativeSchedule.signalPriority.on")}
            </button>
          </div>
        </div>
      ) : null}

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
        disabled={topbar.editDisabled}
        onOpen={actions.refreshNames}
        className="is-depot"
        variant="field"
        positioning="portal"
        portalHostRef={refs.dropdownPortalHostRef}
      />
      </div>

      <div className="dw-departure-topbar-limits">
      <DemoTextField readOnly={topbar.editDisabled} label={t("nativeSchedule.topbar.holdMinutes")} labelTooltip={t("nativeSchedule.topbar.holdMinutes.tooltip")} value={topbar.holdMinutes} onCommit={actions.changeHoldMinutes} onDraftChange={actions.changeHoldMinutes} className={`is-hold${topbar.holdMinutesTooSmall ? " is-error" : ""}`} suffix={t("nativeSchedule.unit.minutes")} />
      <DemoTextField readOnly={topbar.editDisabled} label={t("nativeSchedule.topbar.dwellMinutes")} labelTooltip={t("nativeSchedule.topbar.dwellMinutes.tooltip")} value={topbar.dwellMinutes} onCommit={actions.changeDwellMinutes} onDraftChange={actions.changeDwellMinutes} className={`is-dwell${topbar.dwellMinutesTooSmall ? " is-error" : ""}`} suffix={t("nativeSchedule.unit.minutes")} />
      </div>
    </div>
  );
}
