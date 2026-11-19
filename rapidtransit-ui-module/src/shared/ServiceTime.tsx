import React from "react";

export function ServiceTime(props: { minute?: number; markSize: string }) {
  if (typeof props.minute !== "number" || props.minute < 0) {
    return <>-</>;
  }

  const rounded = Math.round(props.minute);
  const normalized = ((rounded % 1440) + 1440) % 1440;
  const hour = Math.floor(normalized / 60);
  const minute = normalized % 60;
  const dayOffset = Math.floor(rounded / 1440);
  return (
    <div style={{ display: "flex", flexDirection: "row", alignItems: "flex-start", whiteSpace: "nowrap" }}>
      <span>{`${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}`}</span>
      {dayOffset > 0 ? (
        <span style={{ marginLeft: "2rem", fontSize: props.markSize, lineHeight: props.markSize, fontWeight: 600 }}>{`+${dayOffset}`}</span>
      ) : null}
    </div>
  );
}
