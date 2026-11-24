import { trigger } from "cs2/api";
import React, { useState } from "react";
import { GROUP } from "./selectionBindings";
import { ServiceTime } from "../shared/ServiceTime";
import { COLORS } from "./selectionStyles";
import { formatValue } from "./selectionViewModel";

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

  const padding = props.strong ? "6.5rem" : props.dense ? "3.5rem" : "5.5rem";

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "row",
        justifyContent: "space-between",
        alignItems: "flex-start",
        paddingTop: padding,
        paddingBottom: padding
      }}
    >
      <div
        style={{
          flexShrink: 0,
          whiteSpace: "nowrap",
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
          flexShrink: 1,
          minWidth: 0,
          marginLeft: "14rem",
          whiteSpace: "normal",
          overflowWrap: "break-word"
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
  const padding = secondary ? "1.5rem" : "3.5rem";

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "row",
        justifyContent: "space-between",
        alignItems: "flex-start",
        paddingTop: padding,
        paddingBottom: padding,
        marginLeft: secondary ? "16rem" : "0"
      }}
    >
      <div
        style={{
          flexShrink: 0,
          whiteSpace: "nowrap",
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
          flexShrink: 1,
          minWidth: 0,
          marginLeft: "12rem",
          fontSize: "16rem",
          lineHeight: "21rem",
          fontWeight: 500,
          color: COLORS.title,
          textAlign: "right",
          whiteSpace: "normal",
          overflowWrap: "break-word"
        }}
      >
        {props.valueKind === "serviceMinute"
          ? <ServiceTime minute={typeof props.value === "number" ? props.value : undefined} markSize="11rem" />
          : formatValue(props.value, props.valueKind, props.t)}
      </div>
    </div>
  );
}

export function TimeRow(props: {
  label: string;
  minute?: number;
  actualMinute?: number;
  t: (key: string) => string;
}) {
  const delay = typeof props.minute === "number" && props.minute >= 0
    && typeof props.actualMinute === "number" && props.actualMinute >= 0
    ? props.actualMinute - props.minute
    : null;

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "row",
        alignItems: "center",
        minHeight: "24rem",
        marginLeft: "16rem"
      }}
    >
      <div style={{ flex: "1 1 auto", minWidth: 0, fontSize: "15rem", lineHeight: "20rem", fontWeight: 400, color: COLORS.text }}>
        {props.t(props.label)}
      </div>
      {delay === null ? null : (
        <div
          style={{
            flexShrink: 0,
            marginLeft: "12rem",
            fontSize: "15rem",
            lineHeight: "20rem",
            fontWeight: 400,
            color: delay > 0 ? "var(--warningColor)" : delay < 0 ? "var(--accentColorNormal)" : "var(--positiveColor)",
            whiteSpace: "nowrap"
          }}
        >
          {delay === 0
            ? props.t("onTime")
            : props.t(delay > 0 ? "minutesLate" : "minutesEarly").replace("{minutes}", String(Math.abs(delay)))}
        </div>
      )}
      <div style={{ flexShrink: 0, marginLeft: "12rem", fontSize: "16rem", lineHeight: "21rem", fontWeight: 500, color: COLORS.title, whiteSpace: "nowrap" }}>
        <ServiceTime minute={props.minute} markSize="11rem" />
      </div>
    </div>
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
