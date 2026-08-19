using System;
using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class StopPort
    {
        private readonly Func<Entity, bool> m_Exists;
        private readonly Func<Entity, Entity> m_Stop;
        private readonly Func<Entity, StopRef, StopRef> m_Resolve;
        private readonly Func<Entity, bool> m_Live;
        private readonly Func<string> m_Clock;
        private readonly Func<uint> m_Frame;
        private readonly Func<Entity, ResolvedStopKind, string> m_StopName;
        private readonly Func<Entity, string> m_LineId;
        private readonly Func<Entity, string> m_Kind;
        private readonly Func<bool> m_ShouldLog;
        private readonly Action<TraceEvent> m_Log;

        internal StopPort(
            Dictionary<Entity, VehicleTrace> vehicles,
            Func<Entity, bool> exists,
            Func<Entity, Entity> stop,
            Func<Entity, StopRef, StopRef> resolve,
            Func<Entity, bool> live,
            Func<string> clock,
            Func<uint> frame,
            Func<Entity, ResolvedStopKind, string> stopName,
            Func<Entity, string> lineId,
            Func<Entity, string> kind,
            Action<Entity, Entity, Entity, ResolvedStopKind, int, bool, bool, string, uint> recordStop,
            Func<bool> shouldLog,
            Action<TraceEvent> log)
        {
            Vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
            m_Exists = exists ?? throw new ArgumentNullException(nameof(exists));
            m_Stop = stop ?? throw new ArgumentNullException(nameof(stop));
            m_Resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            m_Live = live ?? throw new ArgumentNullException(nameof(live));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            m_StopName = stopName ?? throw new ArgumentNullException(nameof(stopName));
            m_LineId = lineId ?? throw new ArgumentNullException(nameof(lineId));
            m_Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            // 保留旧 Bridge 构造参数；正式 StopFact 只由 Host 事实入口提交。
            _ = recordStop;
            m_ShouldLog = shouldLog ?? throw new ArgumentNullException(nameof(shouldLog));
            m_Log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal Dictionary<Entity, VehicleTrace> Vehicles { get; }

        internal bool Exists(Entity entity) => m_Exists(entity);
        internal Entity Stop(Entity waypoint) => m_Stop(waypoint);
        internal StopRef Resolve(Entity waypoint, StopRef fallback) => m_Resolve(waypoint, fallback);
        internal bool Live(Entity entity) => m_Live(entity);
        internal string Clock() => m_Clock();
        internal uint Frame() => m_Frame();
        internal string StopName(Entity stop, ResolvedStopKind kind) => m_StopName(stop, kind);
        internal string LineId(Entity line) => m_LineId(line);
        internal string Kind(Entity line) => m_Kind(line);
        internal bool ShouldLog() => m_ShouldLog();

        internal void Log(TraceEvent evt) => m_Log(evt);
    }

    internal sealed class Stops
    {
        private const int MaxTrips = 48;
        private readonly StopPort m_Port;

        internal Stops(StopPort port)
        {
            m_Port = port ?? throw new ArgumentNullException(nameof(port));
        }

        internal StopRef Open(VehicleTrace record)
        {
            if (record == null || record.Trips.Count == 0)
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            TripTrace trip = record.Trips[record.Trips.Count - 1];
            if (trip == null || trip.Stops.Count == 0)
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            StopTrace stop = trip.Stops[trip.Stops.Count - 1];
            if (stop == null || !string.IsNullOrEmpty(stop.Departure))
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            return m_Port.Live(stop.Stop)
                ? new StopRef(stop.Stop, stop.Kind)
                : new StopRef(Entity.Null, ResolvedStopKind.Stop);
        }

        internal StopRef Latest(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !m_Port.Vehicles.TryGetValue(vehicle, out VehicleTrace record)
                || record.Trips.Count == 0)
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            TripTrace trip = record.Trips[record.Trips.Count - 1];
            if (trip == null || trip.Stops.Count == 0)
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            StopTrace stop = trip.Stops[trip.Stops.Count - 1];
            return stop != null && m_Port.Live(stop.Stop)
                ? new StopRef(stop.Stop, stop.Kind)
                : new StopRef(Entity.Null, ResolvedStopKind.Stop);
        }

        internal void Record(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding,
            int currentWp,
            int previousWp)
        {
            if (vehicle == Entity.Null || line == Entity.Null || !m_Port.Exists(vehicle))
            {
                return;
            }

            if (!m_Port.Vehicles.TryGetValue(vehicle, out VehicleTrace record))
            {
                record = new VehicleTrace
                {
                    Vehicle = vehicle
                };
                m_Port.Vehicles[vehicle] = record;
            }

            record.Line = line;
            record.Kind = string.Equals(m_Port.Kind(line), "express", StringComparison.Ordinal)
                ? "express"
                : "local";

            int stopWp = boarding ? currentWp : previousWp;
            if (stopWp < 0 || stopWp >= waypoints.Length)
            {
                return;
            }

            StopRef fallback = boarding
                ? new StopRef(Entity.Null, ResolvedStopKind.Stop)
                : Open(record);
            StopRef stop = m_Port.Resolve(waypoints[stopWp].m_Waypoint, fallback);
            if (stop.Ent == Entity.Null)
            {
                return;
            }

            string nowTime = m_Port.Clock();
            uint nowFrame = m_Port.Frame();
            bool origin = stopWp == 0;

            TripTrace trip = record.Trips.Count > 0
                ? record.Trips[record.Trips.Count - 1]
                : null;
            if (trip == null)
            {
                trip = new TripTrace
                {
                    Seq = record.NextSeq++,
                    Frame = nowFrame
                };
                record.Trips.Add(trip);
                Trips.Trim(record.Trips, MaxTrips);
            }

            StopTrace stopTrace = trip.Stops.Count > 0
                ? trip.Stops[trip.Stops.Count - 1]
                : null;
            bool sameStop = stopTrace != null
                && stopTrace.Stop == stop.Ent
                && stopTrace.Kind == stop.Kind;
            if (sameStop)
            {
                if (boarding && !string.IsNullOrEmpty(stopTrace.Arrival))
                    return;
                if (!boarding && !string.IsNullOrEmpty(stopTrace.Departure))
                    return;
            }

            bool reuse = sameStop
                && ((boarding && string.IsNullOrEmpty(stopTrace.Arrival))
                    || (!boarding && string.IsNullOrEmpty(stopTrace.Departure)));
            if (!reuse)
            {
                stopTrace = new StopTrace
                {
                    Stop = stop.Ent,
                    Kind = stop.Kind
                };
                trip.Stops.Add(stopTrace);
            }

            if (boarding)
            {
                stopTrace.Arrival = nowTime;
            }
            else
            {
                stopTrace.Departure = nowTime;
                if (string.IsNullOrEmpty(stopTrace.Arrival))
                {
                    stopTrace.Arrival = nowTime;
                }
            }

            stopTrace.Frame = nowFrame;
            trip.Frame = nowFrame;

            if (m_Port.ShouldLog())
            {
                string stopName = m_Port.StopName(stop.Ent, stop.Kind);
                if (string.IsNullOrEmpty(stopName))
                {
                    stopName = "Stop " + stop.Ent.Index.ToString();
                }

                m_Port.Log(new TraceEvent(
                    line,
                    vehicle,
                    trip.Seq,
                    boarding ? "arrival" : "departure",
                    stopName,
                    stop.Ent,
                    stopWp,
                    nowTime,
                    trip.Stops.Count,
                    origin));
            }
        }

        internal void Start(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (vehicle == Entity.Null || line == Entity.Null || !m_Port.Exists(vehicle) || waypoints.Length == 0)
            {
                return;
            }

            Entity stop = m_Port.Stop(waypoints[0].m_Waypoint);
            if (stop == Entity.Null)
            {
                return;
            }

            if (!m_Port.Vehicles.TryGetValue(vehicle, out VehicleTrace record))
            {
                record = new VehicleTrace
                {
                    Vehicle = vehicle
                };
                m_Port.Vehicles[vehicle] = record;
            }

            string nowTime = m_Port.Clock();
            uint nowFrame = m_Port.Frame();
            TripTrace latest = record.Trips.Count > 0
                ? record.Trips[record.Trips.Count - 1]
                : null;
            if (latest != null
                && latest.Frame == nowFrame
                && latest.Stops.Count == 1
                && latest.Stops[0].Stop == stop
                && latest.Stops[0].Departure == nowTime)
            {
                return;
            }

            TripTrace trip = new TripTrace
            {
                Seq = record.NextSeq++,
                Frame = nowFrame
            };
            trip.Stops.Add(new StopTrace
            {
                Stop = stop,
                Kind = ResolvedStopKind.Stop,
                Arrival = nowTime,
                Departure = nowTime,
                Frame = nowFrame
            });

            record.Vehicle = vehicle;
            record.Line = line;
            record.Kind = string.Equals(m_Port.Kind(line), "express", StringComparison.Ordinal)
                ? "express"
                : "local";
            record.Trips.Add(trip);
            Trips.Trim(record.Trips, MaxTrips);

            if (m_Port.ShouldLog())
            {
                string stopName = m_Port.StopName(stop, ResolvedStopKind.Stop);
                if (string.IsNullOrEmpty(stopName))
                {
                    stopName = "Stop " + stop.Index.ToString();
                }

                m_Port.Log(new TraceEvent(
                    line,
                    vehicle,
                    trip.Seq,
                    "launch-origin",
                    stopName,
                    stop,
                    0,
                    nowTime,
                    1,
                    true));
            }
        }
    }
}
