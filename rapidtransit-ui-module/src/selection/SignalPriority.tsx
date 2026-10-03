import React from "react";
import { COLORS } from "./selectionStyles";
import { PanelData } from "./selectionViewModel";

const STATUS_KEYS: Record<string, string> = {
  cooldown: "signalCooldown", submitted: "signalSubmitted", active: "signalActive",
  tramPriority: "signalTramPriority", otherPriority: "signalOtherPriority",
  noTarget: "signalNoTarget", detected: "signalDetected", progressPaused: "signalPaused",
  unavailable: "signalUnavailable"
};
const LIGHT_COLORS: Record<string, string> = { red: "#ff6464", green: "#70e6a2", transition: "#f1c75b" };
const LIGHT_KEYS: Record<string, string> = { red: "signalRed", green: "signalGreen", transition: "signalTransition" };
const ARROWS: Record<string, string> = {
  straight: "M12 21V3M5 10l7-7 7 7",
  left: "M19 21V11a4 4 0 0 0-4-4H3m6-6L3 7l6 6",
  right: "M5 21V11a4 4 0 0 1 4-4h12m-6-6 6 6-6 6",
  gentleLeft: "M16 21v-6.34a4 4 0 0 0-1.172-2.828L6 3M6 10V3h7",
  gentleRight: "M8 21v-6.34a4 4 0 0 1 1.172-2.828L18 3M18 10V3h-7",
  uTurnLeft: "M18 21V9a6 6 0 0 0-12 0v8M2 13l4 4 4-4",
  uTurnRight: "M6 21V9a6 6 0 0 1 12 0v8m-4-4 4 4 4-4"
};
const MOVE = "left 240ms cubic-bezier(0.16, 1, 0.3, 1), transform 240ms cubic-bezier(0.16, 1, 0.3, 1)";

export function SignalPriority({ data, t }: { data: PanelData; t: (key: string) => string }) {
  const noTarget = data.signalPriorityStatus === "noTarget";
  const primary = noTarget ? undefined : LIGHT_COLORS[data.nextSignalLight || ""];
  const future = noTarget ? [] : [data.secondSignalLight, data.thirdSignalLight].filter(
    (light): light is string => !!light && !!LIGHT_COLORS[light]
  );
  const solo = !!primary && future.length === 0;
  const arrow = ARROWS[data.nextSignalMovement || ""];
  const hasDistance = typeof data.nextSignalDistanceMeters === "number" && data.nextSignalDistanceMeters >= 0;
  const status = t(STATUS_KEYS[data.signalPriorityStatus || ""] || "signalUnavailable");
  const active = data.signalPriorityStatus === "active";

  return (
    <div style={{ boxSizing: "border-box", color: COLORS.text }}>
      {noTarget ? (
        <div style={{ fontSize: "14rem", lineHeight: "24rem", textAlign: "center", color: COLORS.text }}>{status}</div>
      ) : (
        <div style={{ display: "flex", alignItems: "center" }}>
          {arrow ? (
            <svg aria-hidden="true" viewBox="0 0 24 24" style={{ width: "40rem", height: "40rem", flexShrink: 0, marginRight: "12rem" }}>
              <path d={arrow} fill="none" stroke={COLORS.text} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          ) : null}
          <div style={{ flex: "1 1 auto", minWidth: 0 }}>
            {hasDistance ? (
              <div style={{ display: "flex", alignItems: "baseline", color: COLORS.text }}>
                <span style={{ fontSize: "24rem", lineHeight: "28rem", fontWeight: 600 }}>{Math.round(data.nextSignalDistanceMeters!)}</span>
                <span style={{ fontSize: "14rem", marginLeft: "8rem" }}>{t("signalMeters")}</span>
              </div>
            ) : null}
            {data.nextSignalStreetName ? (
              <div style={{ marginTop: hasDistance ? "8rem" : "0", fontSize: "14rem", lineHeight: "20rem", color: COLORS.text, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>{data.nextSignalStreetName}</div>
            ) : null}
          </div>
          <div style={{ maxWidth: "108rem", flexShrink: 0, marginLeft: "12rem", fontSize: active ? "18rem" : "14rem", fontWeight: active ? 600 : 400, lineHeight: active ? "24rem" : "20rem", textAlign: "right", color: active ? LIGHT_COLORS.green : COLORS.text }}>{status}</div>
        </div>
      )}
      {primary || future.length > 0 ? (
        <div style={{ position: "relative", height: "52rem", marginTop: "16rem" }}>
          {primary ? (
            <div style={{ position: "absolute", left: solo ? "50%" : "0%", top: "0rem", transform: solo ? "translateX(-50%)" : "translateX(0%)", transition: MOVE, display: "flex", flexDirection: "column", alignItems: "center" }}>
              <div style={{ fontSize: "12rem", lineHeight: "20rem", color: COLORS.muted, whiteSpace: "nowrap" }}>{t("signalNext")}</div>
              <div role="img" aria-label={t(LIGHT_KEYS[data.nextSignalLight!])} style={{ width: "24rem", height: "24rem", borderRadius: "999rem", marginTop: "8rem", backgroundColor: primary }} />
            </div>
          ) : null}
          {future.length > 0 ? (
            <>
              <div style={{ position: "absolute", right: "0rem", top: "0rem", width: "68rem", textAlign: future.length === 2 ? "center" : "right", fontSize: "12rem", lineHeight: "20rem", color: COLORS.muted }}>{t("signalFuture")}</div>
              <div style={{ position: "absolute", left: primary ? "56rem" : "0rem", right: "0rem", top: "28rem", height: "24rem", display: "flex", alignItems: "center", justifyContent: "flex-end" }}>
                {primary ? <div style={{ flex: "1 1 auto", height: "1rem", marginRight: "8rem", backgroundColor: "rgba(157,178,199,0.3)" }} /> : null}
                {future.map((light, index) => (
                  <React.Fragment key={index}>
                    {index > 0 ? <div style={{ width: "28rem", height: "1rem", margin: "0 8rem", backgroundColor: "rgba(157,178,199,0.3)" }} /> : null}
                    <div role="img" aria-label={t(LIGHT_KEYS[light])} style={{ width: "12rem", height: "12rem", flexShrink: 0, borderRadius: "999rem", backgroundColor: LIGHT_COLORS[light] }} />
                  </React.Fragment>
                ))}
              </div>
            </>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
