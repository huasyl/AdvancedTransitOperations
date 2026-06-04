import React from "react";

export default function WorkbenchEntryError({ error, title = "RT Dispatch Workbench", body = "Native schedule mount failed." }) {
  return (
    <div className="dw-native-schedule-root">
      <div className="dw-native-schedule-error">
        <div className="dw-native-schedule-error-title">{title}</div>
        <div className="dw-native-schedule-error-text">
          {body}
        </div>
        <div className="dw-native-schedule-error-detail">
          {error?.message || "Unknown error"}
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
