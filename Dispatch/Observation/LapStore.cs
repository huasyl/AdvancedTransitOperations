using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class LapStore
    {
        private NativeHashMap<Entity, float> m_StartOdo;
        private NativeHashMap<Entity, float> m_Distance;
        private NativeHashMap<Entity, uint> m_StartFrame;
        private NativeHashMap<Entity, uint> m_Frames;
        private NativeHashSet<Entity> m_Restored;

        internal ref NativeHashMap<Entity, float> StartOdometer => ref m_StartOdo;
        internal ref NativeHashMap<Entity, float> Distance => ref m_Distance;
        internal ref NativeHashMap<Entity, uint> StartFrame => ref m_StartFrame;
        internal ref NativeHashMap<Entity, uint> Frames => ref m_Frames;
        internal ref NativeHashSet<Entity> RestoredRunning => ref m_Restored;

        internal void Init()
        {
            m_StartOdo = new NativeHashMap<Entity, float>(1024, Allocator.Persistent);
            m_Distance = new NativeHashMap<Entity, float>(1024, Allocator.Persistent);
            m_StartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_Frames = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_Restored = new NativeHashSet<Entity>(64, Allocator.Persistent);
        }

        internal void Dispose()
        {
            if (m_StartOdo.IsCreated) m_StartOdo.Dispose();
            if (m_Distance.IsCreated) m_Distance.Dispose();
            if (m_StartFrame.IsCreated) m_StartFrame.Dispose();
            if (m_Frames.IsCreated) m_Frames.Dispose();
            if (m_Restored.IsCreated) m_Restored.Dispose();
        }

        internal void Clear()
        {
            m_StartOdo.Clear();
            m_Distance.Clear();
            m_StartFrame.Clear();
            m_Frames.Clear();
            m_Restored.Clear();
        }

        internal void Remove(Entity vehicle)
        {
            m_StartOdo.Remove(vehicle);
            m_Distance.Remove(vehicle);
            m_StartFrame.Remove(vehicle);
            m_Frames.Remove(vehicle);
            m_Restored.Remove(vehicle);
        }

        internal void Start(Entity vehicle, float odometer, uint frame)
        {
            m_StartOdo[vehicle] = odometer;
            m_StartFrame[vehicle] = frame;
        }

        internal bool TryStart(Entity vehicle, out float odometer) =>
            m_StartOdo.TryGetValue(vehicle, out odometer);

        internal void SetDistance(Entity vehicle, float distance) =>
            m_Distance[vehicle] = distance;

        internal bool TryDistance(Entity vehicle, out float distance) =>
            m_Distance.TryGetValue(vehicle, out distance);

        internal void SetFrames(Entity vehicle, uint frames) =>
            m_Frames[vehicle] = frames;

        internal bool TryFrames(Entity vehicle, out uint frames) =>
            m_Frames.TryGetValue(vehicle, out frames);

        internal bool TryStartFrame(Entity vehicle, out uint frame) =>
            m_StartFrame.TryGetValue(vehicle, out frame);

        internal void MarkRestored(Entity vehicle) =>
            m_Restored.Add(vehicle);

        internal bool ConsumeRestored(Entity vehicle)
        {
            if (!m_Restored.Contains(vehicle)) return false;
            m_Restored.Remove(vehicle);
            return true;
        }
    }
}
