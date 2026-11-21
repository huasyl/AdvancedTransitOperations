using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleStateStore
    {
        internal NativeHashMap<Entity, VehicleState> State;
        internal NativeHashMap<Entity, Entity> Line;
        internal NativeHashMap<Entity, uint> IdleStartFrame;
        internal NativeHashMap<Entity, uint> PreparingStartFrame;
        internal NativeHashMap<Entity, uint> EstimatedOriginArrivalFrame;
        internal NativeHashMap<Entity, uint> WaitingForTheory;
        internal NativeHashMap<Entity, uint> LastLaunchFrame;
        internal NativeHashMap<Entity, uint> SpawnOrderRequestFrame;
        internal NativeHashSet<Entity> NearingTerminus;

        public void Init()
        {
            if (State.IsCreated)
                return;

            State = new NativeHashMap<Entity, VehicleState>(1024, Allocator.Persistent);
            Line = new NativeHashMap<Entity, Entity>(1024, Allocator.Persistent);
            IdleStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            PreparingStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            EstimatedOriginArrivalFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            WaitingForTheory = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            LastLaunchFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            SpawnOrderRequestFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            NearingTerminus = new NativeHashSet<Entity>(64, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (State.IsCreated) State.Dispose();
            if (Line.IsCreated) Line.Dispose();
            if (IdleStartFrame.IsCreated) IdleStartFrame.Dispose();
            if (PreparingStartFrame.IsCreated) PreparingStartFrame.Dispose();
            if (EstimatedOriginArrivalFrame.IsCreated) EstimatedOriginArrivalFrame.Dispose();
            if (WaitingForTheory.IsCreated) WaitingForTheory.Dispose();
            if (LastLaunchFrame.IsCreated) LastLaunchFrame.Dispose();
            if (SpawnOrderRequestFrame.IsCreated) SpawnOrderRequestFrame.Dispose();
            if (NearingTerminus.IsCreated) NearingTerminus.Dispose();
        }

        public void Clear()
        {
            if (State.IsCreated) State.Clear();
            if (Line.IsCreated) Line.Clear();
            if (IdleStartFrame.IsCreated) IdleStartFrame.Clear();
            if (PreparingStartFrame.IsCreated) PreparingStartFrame.Clear();
            if (EstimatedOriginArrivalFrame.IsCreated) EstimatedOriginArrivalFrame.Clear();
            if (WaitingForTheory.IsCreated) WaitingForTheory.Clear();
            if (LastLaunchFrame.IsCreated) LastLaunchFrame.Clear();
            if (SpawnOrderRequestFrame.IsCreated) SpawnOrderRequestFrame.Clear();
            if (NearingTerminus.IsCreated) NearingTerminus.Clear();
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            State.Remove(vehicle);
            Line.Remove(vehicle);
            IdleStartFrame.Remove(vehicle);
            PreparingStartFrame.Remove(vehicle);
            EstimatedOriginArrivalFrame.Remove(vehicle);
            WaitingForTheory.Remove(vehicle);
            LastLaunchFrame.Remove(vehicle);
            SpawnOrderRequestFrame.Remove(vehicle);
            NearingTerminus.Remove(vehicle);
        }
    }
}
