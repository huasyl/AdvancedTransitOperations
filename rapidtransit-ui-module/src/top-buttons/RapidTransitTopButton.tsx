import { trigger } from "cs2/api";
import { Button, Tooltip } from "cs2/ui";
import React from "react";
import { useT } from "../selection/selectionI18n";
import { setLocalPanelOpen, useLocalPanelOpen } from "../selection/selectionBindings";
import { NATIVE_WORKBENCH_PANEL_ID } from "../workbench-host/workbenchHost";

export function RapidTransitTopButton() {
  const open = useLocalPanelOpen();
  const t = useT();
  const toggleWorkbench = () => {
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
  };

  return (
    <Tooltip tooltip={(
      <div>
        <div
          style={{
            fontWeight: 700
          }}
        >
          {t("topButtonTitle")}
        </div>
        <div
          style={{
            marginTop: "4px"
          }}
        >
          {t("topButtonDescription")}
        </div>
      </div>
    )}>
      <div
        onAuxClick={(event) => {
          if (event.button !== 2) {
            return;
          }
          event.preventDefault();
          event.stopPropagation();
          setLocalPanelOpen(!open);
        }}
        style={{ display: "inline-block" }}
      >
        <Button
          variant="floating"
          onClick={toggleWorkbench}
          selected={open}
          style={{
            fontSize: "18px",
            lineHeight: "22px",
            fontWeight: 700,
            letterSpacing: "0.06em"
          }}
        >
          ATO
        </Button>
      </div>
    </Tooltip>
  );
}
