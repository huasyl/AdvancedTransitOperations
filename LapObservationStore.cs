using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class LapObservationStore
    {
        private NativeHashMap<Entity, float> m_VehicleLapStartOdometer;
        private NativeHashMap<Entity, float> m_VehicleLapDistance;
        private NativeHashMap<Entity, uint> m_VehicleLapStartFrame;
        private NativeHashMap<Entity, uint> m_VehicleLapFrames;
        private NativeHashSet<Entity> m_RestoredRunning;

        internal ref NativeHashMap<Entity, float> StartOdometer => ref m_VehicleLapStartOdometer;
        internal ref NativeHashMap<Entity, float> Distance => ref m_VehicleLapDistance;
        internal ref NativeHashMap<Entity, uint> StartFrame => ref m_VehicleLapStartFrame;
        internal ref NativeHashMap<Entity, uint> Frames => ref m_VehicleLapFrames;
        internal ref NativeHashSet<Entity> RestoredRunning => ref m_RestoredRunning;

        internal void Init()
        {
            m_VehicleLapStartOdometer = new NativeHashMap<Entity, float>(1024, Allocator.Persistent);
            m_VehicleLapDistance = new NativeHashMap<Entity, float>(1024, Allocator.Persistent);
            m_VehicleLapStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_VehicleLapFrames = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_RestoredRunning = new NativeHashSet<Entity>(64, Allocator.Persistent);
        }

        internal void Dispose()
        {
            if (m_VehicleLapStartOdometer.IsCreated) m_VehicleLapStartOdometer.Dispose();
            if (m_VehicleLapDistance.IsCreated) m_VehicleLapDistance.Dispose();
            if (m_VehicleLapStartFrame.IsCreated) m_VehicleLapStartFrame.Dispose();
            if (m_VehicleLapFrames.IsCreated) m_VehicleLapFrames.Dispose();
            if (m_RestoredRunning.IsCreated) m_RestoredRunning.Dispose();
        }

        internal void Clear()
        {
            m_VehicleLapStartOdometer.Clear();
            m_VehicleLapDistance.Clear();
            m_VehicleLapStartFrame.Clear();
            m_VehicleLapFrames.Clear();
            m_RestoredRunning.Clear();
        }

        internal void Remove(Entity vehicle)
        {
            m_VehicleLapStartOdometer.Remove(vehicle);
            m_VehicleLapDistance.Remove(vehicle);
            m_VehicleLapStartFrame.Remove(vehicle);
            m_VehicleLapFrames.Remove(vehicle);
            m_RestoredRunning.Remove(vehicle);
        }

        internal void Start(Entity vehicle, float odometer, uint frame)
        {
            m_VehicleLapStartOdometer[vehicle] = odometer;
            m_VehicleLapStartFrame[vehicle] = frame;
        }

        internal bool TryStart(Entity vehicle, out float odometer) =>
            m_VehicleLapStartOdometer.TryGetValue(vehicle, out odometer);

        internal void SetDistance(Entity vehicle, float distance) =>
            m_VehicleLapDistance[vehicle] = distance;

        internal bool TryDistance(Entity vehicle, out float distance) =>
            m_VehicleLapDistance.TryGetValue(vehicle, out distance);

        internal void SetFrames(Entity vehicle, uint frames) =>
            m_VehicleLapFrames[vehicle] = frames;

        internal bool TryFrames(Entity vehicle, out uint frames) =>
            m_VehicleLapFrames.TryGetValue(vehicle, out frames);

        internal bool TryStartFrame(Entity vehicle, out uint frame) =>
            m_VehicleLapStartFrame.TryGetValue(vehicle, out frame);

        internal void MarkRestored(Entity vehicle) =>
            m_RestoredRunning.Add(vehicle);

        internal bool ConsumeRestored(Entity vehicle)
        {
            if (!m_RestoredRunning.Contains(vehicle)) return false;
            m_RestoredRunning.Remove(vehicle);
            return true;
        }
    }
}
