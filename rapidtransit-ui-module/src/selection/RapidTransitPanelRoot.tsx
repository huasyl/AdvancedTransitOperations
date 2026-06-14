import React from "react";
import { RapidTransitPanel } from "./RapidTransitPanel";
import { RapidTransitPanelErrorBoundary } from "./RapidTransitPanelErrorBoundary";

export function RapidTransitPanelRoot() {
  return (
    <RapidTransitPanelErrorBoundary>
      <RapidTransitPanel />
    </RapidTransitPanelErrorBoundary>
  );
}
