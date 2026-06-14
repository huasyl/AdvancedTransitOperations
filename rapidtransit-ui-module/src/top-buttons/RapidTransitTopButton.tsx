import { Button, Tooltip } from "cs2/ui";
import React from "react";
import { setLocalPanelOpen, useLocalPanelOpen } from "../selection/selectionBindings";

export function RapidTransitTopButton() {
  const open = useLocalPanelOpen();

  return (
    <Tooltip tooltip="RapidTransit">
      <Button
        variant="floating"
        onClick={() => setLocalPanelOpen(!open)}
        selected={open}
        style={{
          fontSize: "18px",
          lineHeight: "22px",
          fontWeight: 700,
          letterSpacing: "0.06em"
        }}
      >
        RT
      </Button>
    </Tooltip>
  );
}
