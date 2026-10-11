import React from "react";
import { FirstPersonData } from "./firstPersonBindings";
import { formatServiceMinute } from "../shared/time-format";

const TEXT = "#f4f8fc";
const FIELD_STYLE: React.CSSProperties = { display: "flex", flexDirection: "column", alignItems: "center", flexShrink: 0, paddingLeft: "24rem", paddingRight: "24rem" };
const LABEL_STYLE: React.CSSProperties = { display: "block", color: TEXT, fontFamily: "var(--fontFamily)", fontSize: "24rem", lineHeight: "28rem", fontWeight: "bold", whiteSpace: "nowrap", textAlign: "center" };
const VALUE_STYLE: React.CSSProperties = { display: "block", color: TEXT, fontFamily: "var(--fontFamily)", fontSize: "20rem", lineHeight: "24rem", whiteSpace: "nowrap", textAlign: "center" };

export function FirstPersonStop({ data, t }: { data: NonNullable<FirstPersonData["stop"]>; t: (key: string) => string }) {
  return (
    <div style={{ display: "flex", flexDirection: "row", alignItems: "flex-start", padding: "16rem 8rem" }}>
      <div style={{ ...FIELD_STYLE, minWidth: "112rem" }}>
        <div style={LABEL_STYLE}>{t("stopped")}</div>
        <div style={VALUE_STYLE}>{`${data.dwellMinutes.toFixed(1)} ${t("minutes")}`}</div>
      </div>
      {data.plannedDepartureMinute != null ? <div style={FIELD_STYLE}>
        <div style={LABEL_STYLE}>{t("plannedDeparture")}</div>
        <div style={VALUE_STYLE}>{formatServiceMinute(data.plannedDepartureMinute)}</div>
      </div> : null}
    </div>
  );
}
