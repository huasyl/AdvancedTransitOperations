let fallbackTraceSeq = 0;

function isWorkbenchTraceEnabled() {
  if (typeof window === "undefined") {
    return false;
  }

  return window.__RT_DEBUG_TOOLS__ === true || window.__RT_VERBOSE_LOGS__ === true;
}

function formatTraceDetails(details) {
  if (!details || typeof details !== "object") {
    return "";
  }

  const parts = Object.keys(details)
    .filter((key) => details[key] !== undefined && details[key] !== null)
    .map((key) => `${key}=${String(details[key])}`);
  return parts.length ? ` ${parts.join(" ")}` : "";
}

export function traceWorkbench(eventName, details = null) {
  if (typeof window === "undefined") {
    return;
  }

  if (typeof window.__RTWB_TRACE__ === "function") {
    window.__RTWB_TRACE__(eventName, details || {});
    return;
  }

  if (!isWorkbenchTraceEnabled()) {
    return;
  }

  fallbackTraceSeq += 1;
  console.info(`[RTWB-FE#${fallbackTraceSeq}] ${eventName}${formatTraceDetails(details)}`);
}
