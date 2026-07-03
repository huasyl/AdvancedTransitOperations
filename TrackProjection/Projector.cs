using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.TrackProjection
{
    internal delegate bool TrackPositionProject(out VehicleTrackCursor cursor);

    internal sealed class VehicleTrackCursorCache
    {
        private readonly Dictionary<Entity, VehicleTrackCursor> m_Cursors = new Dictionary<Entity, VehicleTrackCursor>();
        private readonly Dictionary<Entity, VehicleTrackCursorFrameSnapshot> m_Snapshots = new Dictionary<Entity, VehicleTrackCursorFrameSnapshot>();

        public bool TryCursor(Entity vehicle, out VehicleTrackCursor cursor)
        {
            return m_Cursors.TryGetValue(vehicle, out cursor);
        }

        public bool TryPosition(
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            uint frame,
            TrackPositionProject project,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (vehicle == Entity.Null || line == Entity.Null || project == null)
                return false;

            if (TrySnapshot(vehicle, line, chainSignature, frame, out cursor))
                return true;

            bool available = project(out cursor);
            if (available)
            {
                m_Cursors[vehicle] = cursor;
                m_Snapshots[vehicle] = new VehicleTrackCursorFrameSnapshot(
                    line,
                    chainSignature,
                    frame,
                    true,
                    cursor);
            }
            else
            {
                m_Snapshots.Remove(vehicle);
            }

            return available;
        }

        public bool TrySnapshot(
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            uint frame,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (vehicle == Entity.Null || line == Entity.Null)
                return false;

            if (!m_Snapshots.TryGetValue(vehicle, out VehicleTrackCursorFrameSnapshot snapshot)
                || snapshot.Frame != frame
                || snapshot.LineEntity != line
                || snapshot.ChainSignature != chainSignature)
            {
                return false;
            }

            cursor = snapshot.Cursor;
            return snapshot.Available;
        }

        public void Clear()
        {
            m_Cursors.Clear();
            m_Snapshots.Clear();
        }

        public void Remove(Entity vehicle, bool keepCursor = false)
        {
            if (vehicle == Entity.Null)
                return;

            m_Snapshots.Remove(vehicle);
            if (!keepCursor)
                m_Cursors.Remove(vehicle);
        }
    }
}
