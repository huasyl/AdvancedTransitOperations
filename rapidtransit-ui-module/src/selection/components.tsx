import { trigger } from "cs2/api";
import React, { useState } from "react";
import { GROUP } from "./selectionBindings";
import { COLORS } from "./selectionStyles";
import { formatValue } from "./selectionViewModel";

export function SectionCard(props: React.PropsWithChildren<{ compact?: boolean; dense?: boolean; first?: boolean; alert?: boolean }>) {
  const children = React.Children.toArray(props.children);

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        margin: "0 -12rem",
        padding: props.dense ? "12rem 12rem 10rem" : props.compact ? "14rem 12rem 6rem" : "17rem 12rem 15rem",
        background: "transparent",
        borderTop: props.first ? "none" : "3rem solid rgba(215,233,245,0.28)"
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
  value?: string;
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

export function ActionButton(props: { action: string; label: string; marginLeft?: string }) {
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);

  let background = "rgba(74,149,171,0.88)";
  let boxShadow = "inset 0 1px 0 rgba(255,255,255,0.05)";
  let transform = "translateY(0)";

  if (hovered) {
    background = "rgba(84,163,186,0.90)";
    boxShadow = "inset 0 1px 0 rgba(255,255,255,0.06)";
  }

  if (pressed) {
    background = "rgba(66,136,156,0.92)";
    boxShadow = "inset 0 1px 0 rgba(255,255,255,0.03)";
    transform = "translateY(1px)";
  }

  return (
    <button
      onClick={() => trigger(GROUP, props.action)}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => {
        setHovered(false);
        setPressed(false);
      }}
      onMouseDown={() => setPressed(true)}
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
        cursor: "pointer",
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
