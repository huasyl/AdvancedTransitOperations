export const INITIAL_TIMETABLE_LINES = [
  {
    id: "line-1",
    name: "Northern Coastal Line",
    color: "#74c1c1",
    stations: [
      { id: "s1", name: "Port North", distance: 0 },
      { id: "s2", name: "Bayview", distance: 12 },
      { id: "s3", name: "Central Hub", distance: 28 },
      { id: "s4", name: "Industrial Park", distance: 45 },
      { id: "s5", name: "South Terminus", distance: 60 }
    ],
    trains: [
      {
        id: "t1",
        name: "Express 01",
        scheduleType: "custom",
        stops: [
          { stationId: "s1", arrivalTime: "08:00", departureTime: "08:05" },
          { stationId: "s2", arrivalTime: "08:18", departureTime: "08:20" },
          { stationId: "s3", arrivalTime: "08:38", departureTime: "08:42" },
          { stationId: "s4", arrivalTime: "09:00", departureTime: "09:03" },
          { stationId: "s5", arrivalTime: "09:18", departureTime: "09:18" }
        ]
      },
      {
        id: "t2",
        name: "Local 12",
        stops: [
          { stationId: "s1", arrivalTime: "08:30", departureTime: "08:35" },
          { stationId: "s2", arrivalTime: "08:52", departureTime: "08:55" },
          { stationId: "s3", arrivalTime: "09:18", departureTime: "09:25" },
          { stationId: "s4", arrivalTime: "09:48", departureTime: "09:50" },
          { stationId: "s5", arrivalTime: "10:08", departureTime: "10:08" }
        ]
      }
    ],
    plannedTrains: [
      {
        id: "t1-plan",
        name: "Express 01",
        stops: [
          { stationId: "s1", arrivalTime: "08:00", departureTime: "08:05" },
          { stationId: "s2", arrivalTime: "08:15", departureTime: "08:16" },
          { stationId: "s3", arrivalTime: "08:30", departureTime: "08:32" },
          { stationId: "s4", arrivalTime: "08:48", departureTime: "08:50" },
          { stationId: "s5", arrivalTime: "09:03", departureTime: "09:03" }
        ]
      },
      {
        id: "t2-plan",
        name: "Local 12",
        stops: [
          { stationId: "s1", arrivalTime: "08:30", departureTime: "08:35" },
          { stationId: "s2", arrivalTime: "08:48", departureTime: "08:50" },
          { stationId: "s3", arrivalTime: "09:05", departureTime: "09:08" },
          { stationId: "s4", arrivalTime: "09:28", departureTime: "09:30" },
          { stationId: "s5", arrivalTime: "09:45", departureTime: "09:45" }
        ]
      }
    ]
  },
  {
    id: "line-2",
    name: "Airport Express",
    color: "#b9d7d5",
    stations: [
      { id: "s1", name: "Port North", distance: 0 },
      { id: "s3", name: "Central Hub", distance: 28 },
      { id: "a1", name: "Airport T1", distance: 50 }
    ],
    trains: [
      {
        id: "t3",
        name: "Aero 01",
        stops: [
          { stationId: "s1", arrivalTime: "08:15", departureTime: "08:20" },
          { stationId: "s3", arrivalTime: "08:45", departureTime: "08:50" },
          { stationId: "a1", arrivalTime: "09:10", departureTime: "09:10" }
        ]
      }
    ],
    plannedTrains: [
      {
        id: "t3-plan",
        name: "Aero 01",
        stops: [
          { stationId: "s1", arrivalTime: "08:15", departureTime: "08:20" },
          { stationId: "s3", arrivalTime: "08:42", departureTime: "08:45" },
          { stationId: "a1", arrivalTime: "09:02", departureTime: "09:02" }
        ]
      }
    ]
  },
  {
    id: "line-3",
    name: "Ring Line",
    color: "#d6b878",
    stations: [
      { id: "r1", name: "West Park", distance: 10 },
      { id: "s3", name: "Central Hub", distance: 28 },
      { id: "r2", name: "East Plaza", distance: 40 }
    ],
    trains: [
      {
        id: "t4",
        name: "Ring 01",
        stops: [
          { stationId: "r1", arrivalTime: "08:10", departureTime: "08:12" },
          { stationId: "s3", arrivalTime: "08:35", departureTime: "08:40" },
          { stationId: "r2", arrivalTime: "08:55", departureTime: "08:55" }
        ]
      }
    ]
  }
];

export function timeToMinutes(value) {
  const [hours, minutes] = String(value || "00:00").split(":").map(Number);
  return (hours || 0) * 60 + (minutes || 0);
}

export function minutesToTime(value) {
  const normalized = ((Math.round(value) % 1440) + 1440) % 1440;
  const hours = Math.floor(normalized / 60);
  const minutes = normalized % 60;
  return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}`;
}

export function serviceDayOffset(value) {
  return Number.isFinite(value) && value >= 1440 ? Math.floor(value / 1440) : 0;
}

export function formatServiceMinute(value, formatDayOffset) {
  if (!Number.isFinite(value)) {
    return "--";
  }
  const dayOffset = serviceDayOffset(value);
  const suffix = dayOffset > 0 && typeof formatDayOffset === "function"
    ? formatDayOffset(dayOffset)
    : "";
  return `${minutesToTime(value)}${suffix}`;
}
