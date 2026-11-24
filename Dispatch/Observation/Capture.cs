using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal readonly struct ObservedDwellSample
    {
        internal readonly Entity Line;
        internal readonly int WaypointIndex;
        internal readonly float SampleFrames;
        internal readonly uint Frame;
        internal readonly float SampleMinutes;

        internal ObservedDwellSample(Entity line, int waypointIndex, float sampleFrames, uint frame, float sampleMinutes)
        {
            Line = line;
            WaypointIndex = waypointIndex;
            SampleFrames = sampleFrames;
            Frame = frame;
            SampleMinutes = sampleMinutes;
        }
    }

    internal readonly struct StationDwellAnchor
    {
        internal readonly string StationAnchorId;
        internal readonly Entity AnchorEntity;
        internal readonly Entity StopEntity;
        internal readonly Entity BuildingEntity;

        internal StationDwellAnchor(string stationAnchorId, Entity anchorEntity, Entity stopEntity, Entity buildingEntity)
        {
            StationAnchorId = stationAnchorId ?? string.Empty;
            AnchorEntity = anchorEntity;
            StopEntity = stopEntity;
            BuildingEntity = buildingEntity;
        }
    }

    internal sealed class Capture
    {
        private const float MaxObservedDwellMinutes = 30f;

        private readonly LapStore m_Laps;
        private readonly DwellStore m_Dwell;
        private readonly CapturePort m_Port;

        internal Capture(
            LapStore laps,
            DwellStore dwell,
            CapturePort port)
        {
            m_Laps = laps;
            m_Dwell = dwell;
            m_Port = port;
        }

        internal void RecordLapStart(Entity vehicle, string reason = "")
        {
            Entity line = m_Port.LineOf(vehicle);
            if (!m_Port.HasOdo(vehicle))
            {
                m_Laps.EndCurrent(vehicle);
                if (RtLog.VerboseEnabled)
                {
                    string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                    m_Port.Log("[LapStartSkip] " + lineTag + " vehicle" + vehicle.Index
                        + " reason=" + (reason.Length > 0 ? reason : "unspecified")
                        + " no-odometer");
                }
                return;
            }

            float currentOdo = m_Port.Odo(vehicle);
            uint nowFrame = m_Port.Frame();
            m_Laps.Start(vehicle, currentOdo, nowFrame);
            if (RtLog.VerboseEnabled)
            {
                string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                int slot = m_Port.SlotOf(vehicle);
                string curSlot = slot >= 0 ? SlotStr(slot) : "-";
                int cachedWp = m_Port.CachedWp(vehicle);
                m_Port.Log("[LapStart] " + lineTag + " vehicle" + vehicle.Index
                    + " reason=" + (reason.Length > 0 ? reason : "unspecified")
                    + " frame=" + nowFrame
                    + " odo=" + currentOdo.ToString("F1")
                    + " curSlot=" + curSlot
                    + " cachedWp=" + cachedWp);
            }
        }

        internal void UpdateLapStats(Entity vehicle)
        {
            Entity line = m_Port.LineOf(vehicle);
            bool hasStart = m_Laps.TryStart(vehicle, out float startOdo);
            bool hasStartFrame = m_Laps.TryStartFrame(vehicle, out uint startFrame);
            bool hasOdo = m_Port.HasOdo(vehicle);
            float current = hasOdo ? m_Port.Odo(vehicle) : -1f;
            uint nowFrame = m_Port.Frame();
            bool restored = m_Laps.RestoredRunning.Contains(vehicle);
            float lapDist = current - startOdo;
            bool valid = !restored
                && hasStart && hasStartFrame && hasOdo
                && nowFrame > startFrame
                && !float.IsNaN(startOdo) && !float.IsInfinity(startOdo) && startOdo >= 0f
                && !float.IsNaN(current) && !float.IsInfinity(current) && current >= 0f
                && lapDist > 0f;
            m_Laps.EndCurrent(vehicle);
            if (!valid)
            {
                if (RtLog.VerboseEnabled)
                {
                    string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                    m_Port.Log("[LapStatsSkip] " + lineTag + " vehicle" + vehicle.Index
                        + " reason=" + (restored ? "restored-first-lap" : !hasStart || !hasStartFrame ? "missing-start" : !hasOdo ? "no-odometer" : nowFrame <= startFrame ? "invalid-time" : "invalid-distance"));
                }
                return;
            }

            m_Laps.SetDistance(vehicle, lapDist);
            if (RtLog.VerboseEnabled)
            {
                string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                float maintenanceRange = m_Port.Range(vehicle);
                float remaining = maintenanceRange > 0f ? maintenanceRange - current : -1f;
                string maintStr = maintenanceRange > 0f
                    ? " maintenance=" + (maintenanceRange / 1000f).ToString("F1") + "km remaining=" + (remaining / 1000f).ToString("F1") + "km"
                    : " maintenance=none";
                m_Port.Log("[LapDistance] " + lineTag + " vehicle" + vehicle.Index
                    + " lap=" + (lapDist / 1000f).ToString("F2") + "km" + maintStr);
            }

            uint framesDelta = nowFrame - startFrame;
            m_Laps.SetFrames(vehicle, framesDelta);
            float realMinutes = (float)m_Port.ToMinutes(framesDelta);
            if (RtLog.VerboseEnabled)
            {
                string lineTag = line != Entity.Null ? "line" + line.Index : "line?";
                m_Port.Log("[LapStats] " + lineTag + " vehicle" + vehicle.Index
                    + " lap=" + realMinutes.ToString("F1") + "min/" + framesDelta + "frames");
            }

            if (line != Entity.Null)
                m_Port.FlushLap(line);
        }

        internal void BeginObservedDwellSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null || waypointIndex < 0)
                return;

            m_Dwell.Begin(vehicle, line, waypointIndex, nowFrame);
        }

        internal bool TryRecordObservedStopDwellOnBoardingEnd(
            Entity vehicle,
            Entity line,
            int fallbackWaypointIndex,
            uint nowFrame,
            out ObservedDwellSample sample)
        {
            sample = default;
            if (vehicle == Entity.Null || line == Entity.Null)
                return false;
            if (!m_Dwell.End(vehicle, out DwellSession session))
                return false;

            int waypointIndex = session.WaypointIndex >= 0 ? session.WaypointIndex : fallbackWaypointIndex;
            if (waypointIndex < 0 || session.Line != line || nowFrame <= session.StartFrame)
                return false;

            uint sampleFrames = nowFrame - session.StartFrame;
            if (sampleFrames == 0)
                return false;

            uint maxObservedDwellFrames = m_Port.ToFramesCeil(MaxObservedDwellMinutes);
            if (sampleFrames > maxObservedDwellFrames)
                return false;

            float sampleMinutes = (float)m_Port.ToMinutes(sampleFrames);
            sample = new ObservedDwellSample(line, waypointIndex, sampleFrames, nowFrame, sampleMinutes);
            return true;
        }

        internal void RecordStationDwellObservation(string observationKey, float sampleFrames, uint nowFrame)
        {
            if (string.IsNullOrWhiteSpace(observationKey))
                return;

            if (m_Dwell.TryStation(observationKey, out StationDwellObservation existing))
            {
                int sampleCount = math.min(existing.SampleCount + 1, 32);
                float averageFrames = existing.SampleCount <= 0
                    ? sampleFrames
                    : ((existing.AverageFrames * existing.SampleCount) + sampleFrames) / (existing.SampleCount + 1);
                StationDwellObservation updated = new StationDwellObservation
                {
                    AverageFrames = averageFrames,
                    SampleCount = sampleCount,
                    LastObservedFrame = nowFrame
                };
                m_Dwell.RecordStation(observationKey, updated);
                m_Port.FlushStationDwell(observationKey, updated);
                return;
            }

            StationDwellObservation created = new StationDwellObservation
            {
                AverageFrames = sampleFrames,
                SampleCount = 1,
                LastObservedFrame = nowFrame
            };
            m_Dwell.RecordStation(observationKey, created);
            m_Port.FlushStationDwell(observationKey, created);
        }

        internal bool TryGetObservedWaypointStopFrames(Entity line, int waypointIndex, string stationAnchorId, out float dwellFrames)
        {
            dwellFrames = 0f;
            string observationKey = StationDwellKey(line, stationAnchorId);
            if (!string.IsNullOrWhiteSpace(observationKey)
                && m_Dwell.TryStation(observationKey, out StationDwellObservation anchorObservation)
                && anchorObservation.AverageFrames > 0f
                && anchorObservation.SampleCount > 0)
            {
                dwellFrames = anchorObservation.AverageFrames;
                return dwellFrames > 0f;
            }

            return false;
        }

        internal bool TryStationDwellAnchor(Entity line, int waypointIndex, out StationDwellAnchor anchor)
        {
            anchor = default;
            if (line == Entity.Null
                || !m_Port.Exists(line)
                || waypointIndex < 0
                || !m_Port.HasWaypoints(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_Port.Waypoints(line);
            if (waypointIndex >= waypoints.Length)
                return false;

            Entity waypoint = waypoints[waypointIndex].m_Waypoint;
            Entity stopEntity = m_Port.Stop(waypoint);
            if (stopEntity == Entity.Null)
                return false;

            Entity anchorEntity = m_Port.Anchor(waypoint);
            if (anchorEntity == Entity.Null)
                anchorEntity = m_Port.AnchorFromStop(stopEntity);
            if (anchorEntity == Entity.Null)
                return false;

            string stationAnchorId = m_Port.EnsureSak(anchorEntity);
            if (string.IsNullOrWhiteSpace(stationAnchorId) || !RapidTransitMod.Stops.IsKey(stationAnchorId))
                return false;

            Entity buildingEntity = m_Port.StationOf(stopEntity);
            if (buildingEntity == Entity.Null)
                buildingEntity = m_Port.ResolveStation(stopEntity);

            anchor = new StationDwellAnchor(stationAnchorId, anchorEntity, stopEntity, buildingEntity);
            return true;
        }

        internal string StationDwellKey(Entity line, string stationAnchorId)
        {
            if (string.IsNullOrWhiteSpace(stationAnchorId) || !RapidTransitMod.Stops.IsKey(stationAnchorId))
                return string.Empty;

            string lineId = m_Port.LineId(line);
            if (string.IsNullOrWhiteSpace(lineId))
                lineId = line != Entity.Null ? "entity:" + line.Index.ToString() : string.Empty;
            if (string.IsNullOrWhiteSpace(lineId))
                return string.Empty;

            return lineId + "|" + stationAnchorId;
        }

        internal static bool IsStationDwellKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            int separatorIndex = value.IndexOf('|');
            if (separatorIndex <= 0
                || separatorIndex != value.LastIndexOf('|')
                || string.IsNullOrWhiteSpace(value.Substring(0, separatorIndex)))
            {
                return false;
            }

            const int anchorLength = 36;
            int anchorStart = separatorIndex + 1;
            if (value.Length != anchorStart + anchorLength
                || value[anchorStart] != 's'
                || value[anchorStart + 1] != 'a'
                || value[anchorStart + 2] != 'k'
                || value[anchorStart + 3] != ':')
            {
                return false;
            }

            for (int i = anchorStart + 4; i < value.Length; i++)
            {
                char valueChar = value[i];
                bool hexadecimal = (valueChar >= '0' && valueChar <= '9')
                    || (valueChar >= 'a' && valueChar <= 'f')
                    || (valueChar >= 'A' && valueChar <= 'F');
                if (!hexadecimal)
                    return false;
            }

            return true;
        }

        private static string SlotStr(int minutes)
        {
            if (minutes < 0) return "-";
            minutes %= 1440;
            if (minutes < 0) minutes += 1440;
            return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
        }
    }
}
