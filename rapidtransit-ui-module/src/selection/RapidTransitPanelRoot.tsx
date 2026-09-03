import { useValue } from "cs2/api";
import React, { useEffect } from "react";
import { RapidTransitPanel } from "./RapidTransitPanel";
import { RapidTransitPanelErrorBoundary } from "./RapidTransitPanelErrorBoundary";
import { autoOpenPanel$, setLocalPanelOpen } from "./selectionBindings";

export function RapidTransitPanelRoot() {
  const autoOpenPanel = useValue<boolean>(autoOpenPanel$) === true;

  useEffect(() => {
    setLocalPanelOpen(autoOpenPanel);
  }, [autoOpenPanel]);

  return (
    <RapidTransitPanelErrorBoundary>
      <RapidTransitPanel />
    </RapidTransitPanelErrorBoundary>
  );
}
