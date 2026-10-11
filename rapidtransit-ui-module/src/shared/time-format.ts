export function formatServiceMinute(value: number | undefined) {
  if (typeof value !== "number" || value < 0) {
    return "-";
  }

  const rounded = Math.round(value);
  const normalized = ((rounded % 1440) + 1440) % 1440;
  const hour = Math.floor(normalized / 60);
  const minute = normalized % 60;
  const time = `${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}`;
  const dayOffset = Math.floor(rounded / 1440);
  return dayOffset > 0 ? `${time}（+${dayOffset}日）` : time;
}
