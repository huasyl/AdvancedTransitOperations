import React from "react";

interface State {
  hasBindingError: boolean;
}

export class RapidTransitPanelErrorBoundary extends React.Component<React.PropsWithChildren, State> {
  private retryTimer: number | null;

  constructor(props: React.PropsWithChildren) {
    super(props);
    this.state = { hasBindingError: false };
    this.retryTimer = null;
  }

  static getDerivedStateFromError(error: any) {
    if (error && typeof error.message === "string" && error.message.indexOf("RapidTransitPanel.visible.update") >= 0) {
      return { hasBindingError: true };
    }
    throw error;
  }

  componentDidCatch(error: any) {
    if (!error || typeof error.message !== "string" || error.message.indexOf("RapidTransitPanel.visible.update") < 0) {
      throw error;
    }

    if (this.retryTimer !== null) {
      window.clearTimeout(this.retryTimer);
    }

    this.retryTimer = window.setTimeout(() => {
      this.retryTimer = null;
      this.setState({ hasBindingError: false });
    }, 250);
  }

  componentWillUnmount() {
    if (this.retryTimer !== null) {
      window.clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
  }

  render() {
    if (this.state.hasBindingError) {
      return null;
    }

    return this.props.children;
  }
}
