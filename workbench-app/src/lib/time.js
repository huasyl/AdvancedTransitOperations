export function timeToMinutes(value) {
  if (!/^\d{2}:\d{2}$/.test(value)) {
    return null;
  }

  const [hours, minutes] = value.split(":").map(Number);
  if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59) {
    return null;
  }

  return hours * 60 + minutes;
}

export function normalizeCompactTimeInput(value) {
  const digits = String(value).replace(/\D/g, "").slice(0, 4);
  if (digits.length <= 2) {
    return digits;
  }

  return `${digits.slice(0, 2)}:${digits.slice(2)}`;
}

export function minutesToTime(minutes) {
  if (!Number.isFinite(minutes)) {
    return "--:--";
  }

  const normalized = ((Math.round(minutes) % 1440) + 1440) % 1440;
  const hours = String(Math.floor(normalized / 60)).padStart(2, "0");
  const mins = String(normalized % 60).padStart(2, "0");
  return `${hours}:${mins}`;
}

export function minutesToX(minutes, startMinutes, endMinutes, x0, x1) {
  const range = endMinutes - startMinutes;
  if (range <= 0) {
    return x0;
  }

  return x0 + ((minutes - startMinutes) / range) * (x1 - x0);
}

export function getStationY(index, count, y0, y1) {
  if (count <= 1) {
    return y0;
  }

  return y0 + (index / (count - 1)) * (y1 - y0);
}

export function formatDirection(direction, t) {
  return direction === "up" ? t("direction.up") : t("direction.down");
}
