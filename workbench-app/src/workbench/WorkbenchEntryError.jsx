import React from "react";
import { useNativeScheduleI18n } from "../workbench-i18n";

export default function WorkbenchEntryError({ error, title, body }) {
  const { t } = useNativeScheduleI18n();
  const resolvedTitle = title || t("nativeSchedule.error.title");
  const resolvedBody = body || t("nativeSchedule.error.mountFailed");
  const resolvedDetail = error?.message || t("nativeSchedule.error.unknown");

  return (
    <div className="dw-native-schedule-root">
      <div className="dw-native-schedule-error">
        <div className="dw-native-schedule-error-title">{resolvedTitle}</div>
        <div className="dw-native-schedule-error-text">
          {resolvedBody}
        </div>
        <div className="dw-native-schedule-error-detail">
          {resolvedDetail}
        </div>
      </div>
    </div>
  );
}

export class WorkbenchEntryErrorBoundary extends React.Component {
  constructor(props) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error) {
    return { error };
  }

  componentDidCatch(error, errorInfo) {
    console.error("[RT Native Schedule] mount failed", error, errorInfo?.componentStack || "");
  }

  render() {
    if (this.state.error) {
      return <WorkbenchEntryError error={this.state.error} />;
    }

    return this.props.children;
  }
}
