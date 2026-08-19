import { trigger } from "cs2/api";
import React, { useState } from "react";
import { GROUP } from "./selectionBindings";
import { COLORS } from "./selectionStyles";
import { formatServiceMinute, formatValue } from "./selectionViewModel";

export function SectionCard(props: React.PropsWithChildren<{ compact?: boolean; dense?: boolean; first?: boolean; alert?: boolean; seamless?: boolean }>) {
  const children = React.Children.toArray(props.children);

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        margin: "0 -12rem",
        padding: props.dense ? "12rem 12rem 10rem" : props.compact ? "14rem 12rem 6rem" : "17rem 12rem 15rem",
        background: "transparent",
        borderTop: props.first || props.seamless ? "none" : "3rem solid rgba(215,233,245,0.28)"
      }}
    >
      {children.map((child, index) => (
        <div key={"section-child:" + index} style={{ marginTop: index > 0 ? "12rem" : "0" }}>
          {child}
        </div>
      ))}
    </div>
  );
}

export function DetailRow(props: {
  label?: string;
  value?: string | number;
  valueKind?: string;
  strong?: boolean;
  dense?: boolean;
  t: (key: string) => string;
}) {
  if (!props.label) {
    return null;
  }

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "row",
        justifyContent: "space-between",
        alignItems: "center",
        minHeight: props.strong ? "36rem" : props.dense ? "28rem" : "32rem"
      }}
    >
      <div
        style={{
          minWidth: 0,
          fontSize: props.strong ? "17rem" : "15rem",
          lineHeight: props.strong ? "23rem" : "20rem",
          fontWeight: props.strong ? 600 : 400,
          color: COLORS.text
        }}
      >
        {props.t(props.label)}
      </div>
      <div
        style={{
          fontSize: props.strong ? "18rem" : "16rem",
          lineHeight: props.strong ? "23rem" : "21rem",
          fontWeight: props.strong ? 600 : 500,
          color: COLORS.title,
          textAlign: "right",
          flexShrink: 0,
          marginLeft: "14rem",
          whiteSpace: "nowrap"
        }}
      >
        {formatValue(props.value, props.valueKind, props.t)}
      </div>
    </div>
  );
}

export function VehicleInfoRow(props: {
  label: string;
  value?: string | number;
  valueKind?: string;
  level?: "primary" | "secondary" | "muted";
  t: (key: string) => string;
}) {
  const level = props.level || "primary";
  const secondary = level === "secondary";

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "row",
        justifyContent: "space-between",
        alignItems: "center",
        minHeight: secondary ? "24rem" : "28rem",
        marginLeft: secondary ? "16rem" : "0"
      }}
    >
      <div
        style={{
          minWidth: 0,
          fontSize: "15rem",
          lineHeight: "20rem",
          fontWeight: 400,
          color: COLORS.text
        }}
      >
        {props.t(props.label)}
      </div>
      <div
        style={{
          flexShrink: 0,
          marginLeft: "12rem",
          fontSize: "16rem",
          lineHeight: "21rem",
          fontWeight: 500,
          color: COLORS.title,
          textAlign: "right",
          whiteSpace: "nowrap"
        }}
      >
        {formatValue(props.value, props.valueKind, props.t)}
      </div>
    </div>
  );
}

export function ArrivalTimesRow(props: {
  plannedArrivalMinute?: number;
  actualArrivalMinute?: number;
  t: (key: string) => string;
}) {
  return (
    <div
      style={{
        display: "flex",
        flexDirection: "row",
        justifyContent: "space-between",
        alignItems: "center",
        minHeight: "24rem",
        marginLeft: "16rem"
      }}
    >
      <div style={{ flexShrink: 0, fontSize: "15rem", lineHeight: "20rem", fontWeight: 400, color: COLORS.text }}>
        {props.t("arrival")}
      </div>
      <div style={{ display: "flex", flexDirection: "row", alignItems: "center", flexShrink: 0, marginLeft: "12rem" }}>
        <div style={{ fontSize: "15rem", lineHeight: "20rem", color: COLORS.text }}>
          {props.t("scheduled")}
        </div>
        <div style={{ marginLeft: "8rem", fontSize: "16rem", lineHeight: "21rem", fontWeight: 500, color: COLORS.title, whiteSpace: "nowrap" }}>
          {formatServiceMinute(props.plannedArrivalMinute)}
        </div>
        <div style={{ marginLeft: "12rem", fontSize: "15rem", lineHeight: "20rem", color: COLORS.text }}>
          {props.t("actual")}
        </div>
        <div style={{ marginLeft: "8rem", fontSize: "16rem", lineHeight: "21rem", fontWeight: 500, color: COLORS.title, whiteSpace: "nowrap" }}>
          {formatServiceMinute(props.actualArrivalMinute)}
        </div>
      </div>
    </div>
  );
}

export function ScheduledTimeRow(props: {
  label: string;
  minute?: number;
  t: (key: string) => string;
}) {
  return (
    <div
      style={{
        display: "flex",
        flexDirection: "row",
        justifyContent: "space-between",
        alignItems: "center",
        minHeight: "24rem",
        marginLeft: "16rem"
      }}
    >
      <div style={{ fontSize: "15rem", lineHeight: "20rem", fontWeight: 400, color: COLORS.text }}>
        {props.t(props.label)}
      </div>
      <div style={{ display: "flex", flexDirection: "row", alignItems: "center", flexShrink: 0, marginLeft: "12rem" }}>
        <div style={{ fontSize: "15rem", lineHeight: "20rem", color: COLORS.text }}>
          {props.t("scheduled")}
        </div>
        <div style={{ marginLeft: "8rem", fontSize: "16rem", lineHeight: "21rem", fontWeight: 500, color: COLORS.title, whiteSpace: "nowrap" }}>
          {formatServiceMinute(props.minute)}
        </div>
      </div>
    </div>
  );
}

export function LatinScheduleRows(props: {
  plannedArrivalMinute?: number;
  actualArrivalMinute?: number;
  plannedDepartureMinute?: number;
  stopDwellValue?: string | number;
  t: (key: string) => string;
}) {
  const hasArrival = (typeof props.plannedArrivalMinute === "number" && props.plannedArrivalMinute >= 0)
    || (typeof props.actualArrivalMinute === "number" && props.actualArrivalMinute >= 0);
  const hasDeparture = typeof props.plannedDepartureMinute === "number" && props.plannedDepartureMinute >= 0;
  const columnStyle = {
    width: "82rem",
    flexShrink: 0,
    textAlign: "right" as const,
    whiteSpace: "nowrap" as const
  };
  const rowStyle = {
    display: "flex",
    flexDirection: "row" as const,
    alignItems: "center",
    minHeight: "24rem"
  };
  const labelStyle = {
    flex: "1 1 auto",
    minWidth: 0,
    fontSize: "15rem",
    lineHeight: "20rem",
    fontWeight: 400,
    color: COLORS.text
  };
  const valueStyle = {
    ...columnStyle,
    fontSize: "16rem",
    lineHeight: "21rem",
    fontWeight: 500,
    color: COLORS.title
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", marginLeft: "16rem" }}>
      {hasArrival || hasDeparture ? (
        <div style={rowStyle}>
          <div style={{ flex: "1 1 auto", minWidth: 0 }} />
          <div style={{ ...columnStyle, fontSize: "15rem", lineHeight: "20rem", color: COLORS.text }}>
            {props.t("scheduled")}
          </div>
          <div style={{ ...columnStyle, marginLeft: "12rem", fontSize: "15rem", lineHeight: "20rem", color: COLORS.text }}>
            {props.t("actual")}
          </div>
        </div>
      ) : null}
      {hasArrival ? (
        <div style={rowStyle}>
          <div style={labelStyle}>{props.t("arrival")}</div>
          <div style={valueStyle}>{formatServiceMinute(props.plannedArrivalMinute)}</div>
          <div style={{ ...valueStyle, marginLeft: "12rem" }}>{formatServiceMinute(props.actualArrivalMinute)}</div>
        </div>
      ) : null}
      {hasDeparture ? (
        <div style={rowStyle}>
          <div style={labelStyle}>{props.t("departure")}</div>
          <div style={valueStyle}>{formatServiceMinute(props.plannedDepartureMinute)}</div>
          <div style={{ ...valueStyle, marginLeft: "12rem" }}>—</div>
        </div>
      ) : null}
      <div style={rowStyle}>
        <div style={labelStyle}>{props.t("stopped")}</div>
        <div style={columnStyle} />
        <div style={{ ...valueStyle, marginLeft: "12rem" }}>{props.stopDwellValue || "-"}</div>
      </div>
    </div>
  );
}

export function BypassToggleRow(props: { checked?: boolean; label: string }) {
  const checked = props.checked === true;

  return (
    <button
      onClick={() => trigger(GROUP, "setBypassStation", !checked)}
      style={{
        width: "100%",
        display: "flex",
        flexDirection: "row",
        alignItems: "center",
        justifyContent: "space-between",
        minHeight: "36rem",
        padding: "0",
        border: "none",
        background: "transparent",
        color: COLORS.text,
        cursor: "pointer"
      }}
    >
      <div
        style={{
          fontSize: "15rem",
          lineHeight: "22rem",
          fontWeight: 500
        }}
      >
        {props.label}
      </div>
      <div
        style={{
          width: "24rem",
          height: "24rem",
          paddingTop: "var(--gap1)",
          paddingRight: "var(--gap1)",
          paddingBottom: "var(--gap1)",
          paddingLeft: "var(--gap1)",
          borderRadius: "3rem",
          borderStyle: "solid",
          borderTopWidth: "var(--stroke2)",
          borderLeftWidth: "var(--stroke2)",
          borderBottomWidth: "var(--stroke2)",
          borderRightWidth: "var(--stroke2)",
          borderTopColor: "var(--accentColorLight)",
          borderLeftColor: "var(--accentColorLight)",
          borderBottomColor: "var(--accentColorLight)",
          borderRightColor: "var(--accentColorLight)",
          backgroundColor: "transparent",
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          flexShrink: 0,
          marginLeft: "14rem",
          boxSizing: "border-box"
        }}
      >
        {checked ? (
          <div
            style={{
              width: "100%",
              height: "100%",
              maskImage: "url(Media/Glyphs/Checkmark.svg)",
              maskSize: "100%",
              backgroundColor: "white"
            }}
            aria-hidden="true"
          />
        ) : null}
      </div>
    </button>
  );
}

export function DevSightBlock(props: { first?: boolean; source?: string; summaryText?: string }) {
  return (
    <SectionCard first={props.first} compact={true}>
      <div
        style={{
          display: "flex",
          flexDirection: "column",
        }}
      >
        <div
          style={{
            fontSize: "12rem",
            lineHeight: "15rem",
            fontWeight: 700,
            color: COLORS.titleAccent,
            letterSpacing: "0.04em",
            textTransform: "uppercase"
          }}
        >
          Dev-Sight
        </div>
        {props.source ? (
          <div
            style={{
              fontSize: "14rem",
              lineHeight: "18rem",
            fontWeight: 500,
            color: COLORS.muted,
            marginTop: "9rem"
          }}
        >
            {props.source}
          </div>
        ) : null}
        <div
          style={{
            fontSize: "13rem",
            lineHeight: "18rem",
            color: COLORS.text,
            whiteSpace: "pre-wrap",
            wordBreak: "break-word",
            marginTop: "9rem"
          }}
        >
          {props.summaryText || "-"}
        </div>
      </div>
    </SectionCard>
  );
}

export function ActionButton(props: { action: string; label: string; marginLeft?: string; disabled?: boolean }) {
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);

  let background = "rgba(74,149,171,0.88)";
  let boxShadow = "inset 0 1px 0 rgba(255,255,255,0.05)";
  let transform = "translateY(0)";

  if (hovered && !props.disabled) {
    background = "rgba(84,163,186,0.90)";
    boxShadow = "inset 0 1px 0 rgba(255,255,255,0.06)";
  }

  if (pressed && !props.disabled) {
    background = "rgba(66,136,156,0.92)";
    boxShadow = "inset 0 1px 0 rgba(255,255,255,0.03)";
    transform = "translateY(1px)";
  }

  return (
    <button
      onClick={() => { if (!props.disabled) trigger(GROUP, props.action); }}
      onMouseEnter={() => { if (!props.disabled) setHovered(true); }}
      onMouseLeave={() => {
        setHovered(false);
        setPressed(false);
      }}
      onMouseDown={() => { if (!props.disabled) setPressed(true); }}
      onMouseUp={() => setPressed(false)}
      style={{
        width: "132rem",
        minHeight: "33rem",
        marginLeft: props.marginLeft || "0",
        padding: "0 14rem",
        border: "none",
        borderRadius: "5rem",
        background,
        boxShadow,
        color: "rgba(244,251,255,0.94)",
        fontSize: "12rem",
        lineHeight: "15rem",
        fontWeight: 600,
        letterSpacing: "0",
        cursor: props.disabled ? "default" : "pointer",
        opacity: props.disabled ? 0.55 : 1,
        outline: "none",
        transform,
        transition: "background 0.12s ease, transform 0.08s ease"
      }}
    >
      {props.label}
    </button>
  );
}

export function PanelHeader(props: { title: string }) {
  return (
    <div
      style={{
        width: "100%",
        textAlign: "center",
        fontSize: "17rem",
        lineHeight: "21rem",
        fontWeight: 600,
        color: COLORS.titleAccent
      }}
    >
      {props.title}
    </div>
  );
}
