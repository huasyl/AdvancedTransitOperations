import React from "react";
import { FirstPersonSignalData } from "./firstPersonBindings";
import { ARROWS, LIGHT_COLORS, LIGHT_KEYS, STATUS_KEYS } from "../shared/signal-display";

const TEXT = "#f4f8fc";
const MUTED = "#9db2c7";
const LINE = "rgba(157,178,199,0.5)";
const MOVE = "left 240ms cubic-bezier(0.16, 1, 0.3, 1), transform 240ms cubic-bezier(0.16, 1, 0.3, 1)";
const LABEL_STYLE: React.CSSProperties = { display: "block", color: MUTED, fontFamily: "var(--fontFamily)", fontSize: "14rem", lineHeight: "20rem", whiteSpace: "nowrap" };

export function FirstPersonSignal({ data, t }: { data: FirstPersonSignalData; t: (key: string) => string }) {
  const arrow = ARROWS[data.movement];
  const shownLights = [data.firstLight, data.secondLight, data.thirdLight].map((light, index) => ({ light, index })).filter(({ light }) => !!LIGHT_COLORS[light]);
  const primary = shownLights.find(({ index }) => index === 0);
  const future = shownLights.filter(({ index }) => index > 0);
  const solo = !!primary && future.length === 0;
  const hasDistance = data.distanceMeters >= 0;
  const active = data.status === "active";
  const dot = (light: string, size: string, top?: string) => <div role="img" aria-label={t(LIGHT_KEYS[light])} style={{ width: size, height: size, marginTop: top, flexShrink: 0, borderRadius: "999rem", backgroundColor: LIGHT_COLORS[light] }} />;
  return (
    <div style={{ display: "flex", flexDirection: "column", width: "280rem", padding: "16rem 24rem" }}>
      <div style={{ display: "flex", flexDirection: "row", alignItems: "center" }}>
        {arrow ? <svg viewBox="0 0 24 24" aria-hidden="true" style={{ width: "32rem", height: "32rem", marginRight: "12rem", flexShrink: 0 }}>
          <path d={arrow} fill="none" stroke={TEXT} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
        </svg> : null}
        <div style={{ display: "flex", flexDirection: "column", flex: "1 1 auto", minWidth: 0 }}>
          <div style={{ display: "flex", flexDirection: "row", alignItems: "center" }}>
            {hasDistance ? <div style={{ display: "block", color: TEXT, fontFamily: "var(--fontFamily)", fontSize: "24rem", lineHeight: "28rem", fontWeight: "bold", whiteSpace: "nowrap", flexShrink: 0 }}>{`${Math.round(data.distanceMeters)}${t("signalMeters")}`}</div> : null}
            <div style={{ display: "block", flex: "1 1 auto", minWidth: 0, marginLeft: hasDistance ? "12rem" : "0", textAlign: "right", fontFamily: "var(--fontFamily)", fontSize: active ? "20rem" : "16rem", lineHeight: active ? "24rem" : "20rem", fontWeight: active ? "bold" : undefined, color: active ? LIGHT_COLORS.green : TEXT }}>{t(STATUS_KEYS[data.status] || "signalUnavailable")}</div>
          </div>
          {data.streetName ? <div style={{ display: "block", color: MUTED, fontFamily: "var(--fontFamily)", fontSize: "16rem", lineHeight: "20rem", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>{data.streetName}</div> : null}
        </div>
      </div>
      {shownLights.length > 0 ? <div style={{ position: "relative", height: "48rem", marginTop: "16rem" }}>
        {primary ? <div style={{ position: "absolute", left: solo ? "50%" : "0%", top: "0rem", transform: solo ? "translateX(-50%)" : "translateX(0%)", transition: MOVE, display: "flex", flexDirection: "column", alignItems: "center" }}>
          <div style={LABEL_STYLE}>{t("signalNext")}</div>
          {dot(primary.light, "20rem", "8rem")}
        </div> : null}
        {future.length > 0 ? <>
          <div style={{ ...LABEL_STYLE, position: "absolute", right: "0rem", top: "0rem", width: "72rem", textAlign: future.length === 2 ? "center" : "right" }}>{t("signalFuture")}</div>
          <div style={{ position: "absolute", left: primary ? "64rem" : "0rem", right: "0rem", top: "28rem", height: "20rem", display: "flex", flexDirection: "row", alignItems: "center", justifyContent: "flex-end" }}>
            {primary ? <div style={{ flex: "1 1 auto", height: "2rem", marginRight: "8rem", backgroundColor: LINE }} /> : null}
            {future.map(({ light, index }, order) => <React.Fragment key={index}>
              {order > 0 ? <div style={{ width: "32rem", height: "2rem", margin: "0 8rem", backgroundColor: LINE }} /> : null}
              {dot(light, "12rem")}
            </React.Fragment>)}
          </div>
        </> : null}
      </div> : null}
    </div>
  );
}
