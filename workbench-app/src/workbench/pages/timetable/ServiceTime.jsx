import { minutesToTime, serviceDayOffset } from "./timetable-data";

export default function ServiceTime({ value }) {
  if (!Number.isFinite(value)) {
    return "--";
  }
  const dayOffset = serviceDayOffset(value);
  return (
    <span className="rtw-timetable-service-time">
      <span>{minutesToTime(value)}</span>
      {dayOffset > 0 ? <span className="rtw-timetable-day-mark">{`+${dayOffset}`}</span> : null}
    </span>
  );
}
