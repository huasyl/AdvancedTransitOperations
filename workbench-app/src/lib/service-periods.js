export const SERVICE_PERIOD_STEP = 30;
export const DEFAULT_SERVICE_PERIODS = [
  { id: "night-early", labelKey: "nativeSchedule.quick.segment.night", start: 0, end: 390 },
  { id: "morning", labelKey: "nativeSchedule.quick.segment.morning", start: 390, end: 570 },
  { id: "off-peak", labelKey: "nativeSchedule.quick.segment.offPeak", start: 570, end: 990 },
  { id: "evening", labelKey: "nativeSchedule.quick.segment.evening", start: 990, end: 1260 },
  { id: "night-late", labelKey: "nativeSchedule.quick.segment.night", start: 1260, end: 1440 }
];

export function moveServiceBoundary(periods, index, field, minute, join = false) {
  const next = periods.map((period) => ({ ...period }));
  const period = next[index], previous = next[index - 1], following = next[index + 1];
  let value = Math.round(minute / SERVICE_PERIOD_STEP) * SERVICE_PERIOD_STEP;
  if (field === "start") {
    const linked = join && previous && previous.end === period.start;
    const minimum = previous ? linked ? previous.start + SERVICE_PERIOD_STEP
      : join ? previous.end : previous.start + SERVICE_PERIOD_STEP : 0;
    value = Math.max(minimum, Math.min(period.end - SERVICE_PERIOD_STEP, value));
    period.start = value;
    if (previous && (linked || value < previous.end)) previous.end = value;
  } else {
    const linked = join && following && following.start === period.end;
    const maximum = following ? linked ? following.end - SERVICE_PERIOD_STEP
      : join ? following.start : following.end - SERVICE_PERIOD_STEP : 1440;
    value = Math.min(maximum, Math.max(period.start + SERVICE_PERIOD_STEP, value));
    period.end = value;
    if (following && (linked || value > following.start)) following.start = value;
  }
  return next;
}
