import React from "react";
import { useValue } from "cs2/api";
import { FirstPersonData, place$ } from "./firstPersonBindings";
import { FirstPersonSignal } from "./FirstPersonSignal";
import { FirstPersonStop } from "./FirstPersonStop";
import { FirstPersonPanel, PanelContent } from "./FirstPersonPanel";
import { useFirstPersonT } from "./firstPersonI18n";

export function FirstPersonOverlay({ data }: { data: FirstPersonData }) {
  const t = useFirstPersonT();
  const place = useValue(place$);
  const signal = data.signal && data.signal.status !== "noTarget" && data.signal.status !== "unavailable" ? data.signal : null;
  const content: PanelContent | null = data.stop ? { kind: "stop", data: data.stop }
    : signal ? { kind: "signal", data: signal } : null;
  return (
    <FirstPersonPanel content={content} place={place === "left" ? "left" : "center"}>
      {shown => shown.kind === "stop"
        ? <FirstPersonStop data={shown.data} t={t} />
        : <FirstPersonSignal data={shown.data} t={t} />}
    </FirstPersonPanel>
  );
}
