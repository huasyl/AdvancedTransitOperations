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
            display: "flex",
            alignItems: "center",
            justifyContent: "center"
          }}
        >
          <svg
            aria-hidden="true"
            viewBox="2 4 44 40"
            style={{
              width: "32rem",
              height: "32rem",
              display: "block"
            }}
          >
            <path d="M4 20h40M4 28h40" fill="none" stroke="white" strokeWidth="2.25" strokeLinecap="round" strokeLinejoin="round" />
            <path d="M12 20l6-8h12l6 8M12 28l6 8h12l6-8" fill="none" stroke="white" strokeWidth="2.25" strokeLinecap="round" strokeLinejoin="round" />
            <rect x="18" y="6" width="12" height="4" rx="1" fill="white" opacity="0.2" />
            <rect x="18" y="38" width="12" height="4" rx="1" fill="white" opacity="0.2" />
            <circle cx="12" cy="20" r="1.5" fill="white" />
            <circle cx="12" cy="28" r="1.5" fill="white" />
            <circle cx="36" cy="20" r="1.5" fill="white" />
            <circle cx="36" cy="28" r="1.5" fill="white" />
          </svg>
        </Button>
      </div>
    </Tooltip>
  );
}
