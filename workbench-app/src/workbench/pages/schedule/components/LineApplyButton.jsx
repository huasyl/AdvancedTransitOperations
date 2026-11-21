import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

export default function LineApplyButton({ isApplying, conflict, pending, count, message, onApply }) {
  const { t } = useNativeScheduleI18n();
  const quickApply = isApplying ? { text: "nativeSchedule.summary.action.applying", state: "is-disabled" }
    : conflict ? { text: "nativeSchedule.line.applyDay", state: "is-disabled" }
    : pending ? { text: "nativeSchedule.line.applyDay", state: "" }
    : count ? { text: "nativeSchedule.summary.action.applied", state: "is-applied" }
    : { text: "nativeSchedule.line.noTrips", state: "is-disabled" };
  return <>
    <button type="button" className={`dw-departure-quick-apply ${quickApply.state}`} disabled={Boolean(quickApply.state)} onClick={onApply}>{t(quickApply.text)}</button>
    {message ? <span className={`dw-departure-quick-message ${message.tone === "error" ? "is-error" : ""}`}>{message.text}</span> : null}
  </>;
}
