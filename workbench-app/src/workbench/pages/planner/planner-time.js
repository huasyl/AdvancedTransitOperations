export function normalizeTimeInput(rawValue) {
  const rawText = String(rawValue || "");
  const digitsOnly = rawText.replace(/\D/g, "").slice(0, 4);
  if (digitsOnly.length < 2) {
    return digitsOnly;
  }
  if (digitsOnly.length === 2) {
    return rawText.indexOf(":") >= 0 ? `${digitsOnly}:` : digitsOnly;
  }

  return `${digitsOnly.slice(0, 2)}:${digitsOnly.slice(2)}`;
}

export function isValidTimeValue(value) {
  const match = /^(\d{2}):(\d{2})$/.exec(String(value || "").trim());
  if (!match) {
    return false;
  }

  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  return Number.isFinite(hours) && Number.isFinite(minutes) && hours >= 0 && hours <= 23 && minutes >= 0 && minutes <= 59;
}

export function timeToMinutes(value) {
  if (!isValidTimeValue(value)) {
    return null;
  }

  const [hours, minutes] = String(value).split(":").map((part) => Number(part));
  return hours * 60 + minutes;
}

export function parsePositiveInt(value, fallbackValue = 0) {
  const parsed = Number.parseInt(String(value || "").trim(), 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallbackValue;
}

export function waitForUiPaint() {
  return new Promise((resolve) => {
    if (typeof window !== "undefined" && typeof window.requestAnimationFrame === "function") {
      window.requestAnimationFrame(() => {
        window.requestAnimationFrame(() => {
          window.setTimeout(resolve, 50);
        });
      });
      return;
    }
    setTimeout(resolve, 50);
  });
}

export function waitForMinimumDuration(startedAt, minimumMs) {
  const elapsedMs = Date.now() - startedAt;
  const remainingMs = Math.max(0, minimumMs - elapsedMs);
  if (remainingMs <= 0) {
    return Promise.resolve();
  }
  return new Promise((resolve) => setTimeout(resolve, remainingMs));
}

export function waitForDelay(delayMs) {
  return new Promise((resolve) => setTimeout(resolve, delayMs));
}

export function isTerminalPlannerJobState(state) {
  return state === "completed" || state === "failed" || state === "missing";
}
