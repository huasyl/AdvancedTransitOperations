import { trigger } from "cs2/api";
import { Button, Tooltip } from "cs2/ui";
import React from "react";
import { NATIVE_WORKBENCH_PANEL_ID } from "../workbench-host/workbenchHost";

export function RapidTransitWorkbenchTopButton() {
  return (
    <Tooltip tooltip="RT Workbench">
      <Button
        variant="floating"
        onClick={() => {
          trigger("game", "togglePanel", NATIVE_WORKBENCH_PANEL_ID);
        }}
        style={{
          fontSize: "18px",
          lineHeight: "22px",
          fontWeight: 700,
          letterSpacing: "0.04em"
        }}
      >
        WB
      </Button>
    </Tooltip>
  );
}
