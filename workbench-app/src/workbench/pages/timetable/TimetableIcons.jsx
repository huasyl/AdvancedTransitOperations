export default function TimetableIcon({ name, className = "" }) {
  const common = {
    viewBox: "0 0 24 24",
    className: `rtw-timetable-icon ${className}`.trim(),
    fill: "none",
    stroke: "currentColor",
    strokeWidth: "2",
    strokeLinecap: "round",
    strokeLinejoin: "round",
    "aria-hidden": "true"
  };

  if (name === "map") {
    return <svg {...common}><path d="M20 10c0 5-8 11-8 11S4 15 4 10a8 8 0 1 1 16 0Z" /><circle cx="12" cy="10" r="2.5" /></svg>;
  }

  if (name === "route") {
    return <svg {...common}><circle cx="6" cy="5" r="2" /><circle cx="18" cy="19" r="2" /><path d="M8 5h5a3 3 0 0 1 0 6h-2a3 3 0 0 0 0 6h5" /></svg>;
  }

  if (name === "sliders") {
    return <svg {...common}><path d="M4 6h10M18 6h2M4 12h2M10 12h10M4 18h7M15 18h5" /><circle cx="16" cy="6" r="2" /><circle cx="8" cy="12" r="2" /><circle cx="13" cy="18" r="2" /></svg>;
  }

  if (name === "chart") {
    return <svg {...common}><path d="M4 19V5M4 19h16M7 15l4-5 3 2 5-7" /></svg>;
  }

  if (name === "calendar") {
    return <svg {...common}><rect x="3" y="5" width="18" height="16" rx="2" /><path d="M7 3v4M17 3v4M3 10h18" /></svg>;
  }

  if (name === "check") {
    return <svg {...common}><path d="m5 12 4 4L19 6" /></svg>;
  }

  if (name === "arrow-down") {
    return <svg {...common}><path d="M12 4v16M6 14l6 6 6-6" /></svg>;
  }

  if (name === "arrow-right") {
    return <svg {...common}><path d="M5 12h14M13 6l6 6-6 6" /></svg>;
  }

  if (name === "clock") {
    return <svg {...common}><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></svg>;
  }

  if (name === "chevron-up") {
    return <svg {...common}><path d="m6 15 6-6 6 6" /></svg>;
  }

  if (name === "chevron-down") {
    return <svg {...common}><path d="m6 9 6 6 6-6" /></svg>;
  }

  return null;
}
