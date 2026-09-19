using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.TrackProjection
{
    internal sealed class VehicleTrackCursorCache
    {
        private readonly Dictionary<Entity, VehicleTrackCursor> m_Cursors = new Dictionary<Entity, VehicleTrackCursor>();
        private readonly Dictionary<Entity, VehicleTrackCursorFrameSnapshot> m_Snapshots = new Dictionary<Entity, VehicleTrackCursorFrameSnapshot>();

        public bool TryCursor(Entity vehicle, out VehicleTrackCursor cursor)
        {
            return m_Cursors.TryGetValue(vehicle, out cursor);
        }

        public bool TryGetFramePosition(
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            uint frame,
            out bool available,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            available = false;
            if (vehicle == Entity.Null || line == Entity.Null)
                return false;

            if (TryFrameSnapshot(vehicle, line, chainSignature, frame, out VehicleTrackCursorFrameSnapshot snapshot))
            {
                if (!snapshot.FinalKnown)
                    return false;
                cursor = snapshot.Cursor;
                available = snapshot.Available;
                return true;
            }

            return false;
        }

        public void StoreFramePosition(
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            uint frame,
            bool available,
            VehicleTrackCursor cursor)
        {
            if (vehicle == Entity.Null || line == Entity.Null)
                return;

            bool exactKnown = TryFrameSnapshot(vehicle, line, chainSignature, frame, out VehicleTrackCursorFrameSnapshot existing)
                && existing.ExactKnown;
            bool exactAvailable = exactKnown && existing.ExactAvailable;
            VehicleTrackCursor exactCursor = exactAvailable ? existing.ExactCursor : default;
            if (available)
            {
                m_Cursors[vehicle] = cursor;
                m_Snapshots[vehicle] = new VehicleTrackCursorFrameSnapshot(
                    line,
                    chainSignature,
                    frame,
                    true,
                    cursor,
                    true,
                    exactKnown,
                    exactAvailable,
                    exactCursor);
            }
            else
            {
                m_Snapshots[vehicle] = new VehicleTrackCursorFrameSnapshot(
                    line,
                    chainSignature,
                    frame,
                    false,
                    default,
                    true,
                    exactKnown,
                    exactAvailable,
                    exactCursor);
            }
        }

        public bool TryGetFrameExact(
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            uint frame,
            out bool available,
            out VehicleTrackCursor cursor)
        {
            available = false;
            cursor = default;
            if (!TryFrameSnapshot(vehicle, line, chainSignature, frame, out VehicleTrackCursorFrameSnapshot snapshot)
                || !snapshot.ExactKnown)
            {
                return false;
            }
            available = snapshot.ExactAvailable;
            cursor = snapshot.ExactCursor;
            return true;
        }

        public void StoreFrameExact(
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            uint frame,
            bool available,
            VehicleTrackCursor cursor)
        {
            if (vehicle == Entity.Null || line == Entity.Null)
                return;
            if (available)
                m_Cursors[vehicle] = cursor;
            bool finalKnown = TryFrameSnapshot(vehicle, line, chainSignature, frame, out VehicleTrackCursorFrameSnapshot existing)
                && existing.FinalKnown;
            m_Snapshots[vehicle] = new VehicleTrackCursorFrameSnapshot(
                line,
                chainSignature,
                frame,
                finalKnown && existing.Available,
                finalKnown ? existing.Cursor : default,
                finalKnown,
                true,
                available,
                available ? cursor : default);
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

            if (!TryFrameSnapshot(vehicle, line, chainSignature, frame, out VehicleTrackCursorFrameSnapshot snapshot))
                return false;

            cursor = snapshot.Cursor;
            return snapshot.Available;
        }

        private bool TryFrameSnapshot(
            Entity vehicle,
            Entity line,
            ulong chainSignature,
            uint frame,
            out VehicleTrackCursorFrameSnapshot snapshot)
        {
            return m_Snapshots.TryGetValue(vehicle, out snapshot)
                && snapshot.Frame == frame
                && snapshot.LineEntity == line
                && snapshot.ChainSignature == chainSignature;
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

        public void RemoveWaypointDependent(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            if (m_Cursors.TryGetValue(vehicle, out VehicleTrackCursor cursor)
                && cursor.Source != VehicleTrackCursorSource.CurrentLane)
            {
                m_Cursors.Remove(vehicle);
            }

            if (m_Snapshots.TryGetValue(vehicle, out VehicleTrackCursorFrameSnapshot snapshot)
                && (!snapshot.Available || snapshot.Cursor.Source != VehicleTrackCursorSource.CurrentLane))
            {
                m_Snapshots.Remove(vehicle);
            }
        }
    }
}
