using System;
using Newtonsoft.Json;
using RapidTransitMod.Core;
using RapidTransitMod.SignalPriority;
using RapidTransitMod.ViewTracking;
using Unity.Entities;

namespace RapidTransitMod.FirstPerson
{
    internal sealed class FirstPersonQuery
    {
        internal sealed class Data
        {
            public bool active;
            public string followedEntity = "";
            public string vehicle = "";
            public StopData stop;
            public SignalData signal;
        }

        internal sealed class StopData
        {
            public double dwellMinutes;
            public int? plannedDepartureMinute;
        }

        internal sealed class SignalData
        {
            public string status;
            public string movement;
            public int distanceMeters;
            public string streetName;
            public string firstLight;
            public string secondLight;
            public string thirdLight;
        }

        private readonly FirstPersonAccess m_Access;
        private FollowView m_LastView;
        private Entity m_LastLine;
        private int m_LastWaypoint = -1;
        private uint m_LastArrival;
        private bool m_LastStop;
        private int m_LastTarget = -1;
        private uint m_LastReadFrame;
        private long m_LastClockEpoch;
        private double m_LastFramesPerMinute;
        private Data m_Data;
        private string m_Json = "";

        internal FirstPersonQuery(FirstPersonAccess access) => m_Access = access;

        internal void Clear()
        {
            m_Data = null;
            m_Json = "";
        }

        internal string ReadJson()
        {
            FollowView view = m_Access.ReadContext();
            if (view.Mode != FollowMode.FirstPerson || view.Vehicle == Entity.Null)
            {
                m_Data = null;
                m_Json = "";
                return m_Json;
            }
            uint frame = m_Access.ReadFrame();
            ClockSnapshot clock = m_Access.ReadClock();
            bool stopped = m_Access.TryStopSession(view.Vehicle, out Entity line, out int waypoint, out uint arrival);
            int target = -1;
            if (stopped && waypoint == 0
                && m_Access.TryVehicleState(view.Vehicle, out VehicleState state)
                && state == VehicleState.Holding
                && m_Access.TryTargetMinute(view.Vehicle, out int minute) && minute >= 0)
                target = minute;
            bool changed = m_Data == null || view.FollowedEntity != m_LastView.FollowedEntity
                || view.Vehicle != m_LastView.Vehicle || stopped != m_LastStop
                || line != m_LastLine || waypoint != m_LastWaypoint || arrival != m_LastArrival
                || target != m_LastTarget || clock.ClockEpoch != m_LastClockEpoch
                || clock.FramesPerMinute != m_LastFramesPerMinute;
            if (changed || unchecked(frame - m_LastReadFrame) >= 30u)
            {
                m_LastReadFrame = frame;
                m_LastView = view;
                m_LastStop = stopped;
                m_LastLine = line;
                m_LastWaypoint = waypoint;
                m_LastArrival = arrival;
                m_LastTarget = target;
                m_LastClockEpoch = clock.ClockEpoch;
                m_LastFramesPerMinute = clock.FramesPerMinute;
                m_Data = new Data { active = true, followedEntity = Id(view.FollowedEntity), vehicle = Id(view.Vehicle) };
                if (stopped)
                {
                    int? planned = target >= 0 ? (int?)target : null;
                    if (target < 0 && m_Access.TryVehicleTimes(view.Vehicle, out int current, out _, out _, out _, out _, out int departure)
                        && current == waypoint && departure >= 0)
                        planned = departure;
                    m_Data.stop = new StopData { dwellMinutes = Dwell(clock, frame, arrival), plannedDepartureMinute = planned };
                }
                else if (m_Access.TrySignalDisplay(view.Vehicle, out SignalDisplay display))
                {
                    m_Data.signal = new SignalData
                    {
                        status = display.Status, movement = display.Movement, distanceMeters = display.DistanceMeters,
                        streetName = display.Street == Entity.Null ? "" : m_Access.ReadName(display.Street),
                        firstLight = display.FirstLight, secondLight = display.SecondLight, thirdLight = display.ThirdLight
                    };
                }
                m_Json = JsonConvert.SerializeObject(m_Data);
            }
            else if (stopped)
            {
                double dwell = Dwell(clock, frame, arrival);
                if (dwell != m_Data.stop.dwellMinutes)
                {
                    m_Data.stop.dwellMinutes = dwell;
                    m_Json = JsonConvert.SerializeObject(m_Data);
                }
            }
            return m_Json;
        }

        private static double Dwell(ClockSnapshot clock, uint frame, uint arrival)
            => Math.Round(clock.ToMinutes(unchecked(frame - arrival)), 1);
        private static string Id(Entity entity) => entity == Entity.Null ? "" : entity.Index + ":" + entity.Version;
    }
}
