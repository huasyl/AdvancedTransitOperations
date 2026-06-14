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
          try {
            const closeHandler = typeof window !== "undefined"
              ? window.__RT_NATIVE_WORKBENCH_CLOSE__
              : null;
            if (typeof closeHandler === "function") {
              closeHandler();
              return;
            }
            trigger("game", "togglePanel", NATIVE_WORKBENCH_PANEL_ID);
          } catch (error) {
            console.error("RapidTransit native workbench toggle failed", error);
          }
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
