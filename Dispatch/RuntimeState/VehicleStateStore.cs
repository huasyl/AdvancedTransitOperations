using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleStateStore
    {
        internal NativeHashMap<Entity, VehicleState> State;
        internal NativeHashMap<Entity, int> TargetMinute;
        internal NativeHashMap<Entity, int> CurrentSlotMinute;
        internal NativeHashMap<Entity, Entity> Line;
        internal NativeHashMap<Entity, uint> IdleStartFrame;
        internal NativeHashMap<Entity, uint> PreparingStartFrame;
        internal NativeHashMap<Entity, uint> LastLaunchFrame;
        internal NativeHashMap<Entity, uint> LaunchCooldownUntil;
        internal NativeHashMap<Entity, uint> DispatchRequestStartFrame;
        internal NativeHashSet<Entity> NearingTerminus;
        internal NativeHashMap<Entity, uint> OriginArrivalCandidateSinceFrame;
        internal NativeHashMap<Entity, ReadyClockState> ForcedOriginReadyFrame;
        internal NativeHashMap<Entity, uint> ForcedOriginBoardingGraceUntil;

        public void Init()
        {
            if (State.IsCreated)
                return;

            State = new NativeHashMap<Entity, VehicleState>(1024, Allocator.Persistent);
            TargetMinute = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            CurrentSlotMinute = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            Line = new NativeHashMap<Entity, Entity>(1024, Allocator.Persistent);
            IdleStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            PreparingStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            LastLaunchFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            LaunchCooldownUntil = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            DispatchRequestStartFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            NearingTerminus = new NativeHashSet<Entity>(64, Allocator.Persistent);
            OriginArrivalCandidateSinceFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            ForcedOriginReadyFrame = new NativeHashMap<Entity, ReadyClockState>(256, Allocator.Persistent);
            ForcedOriginBoardingGraceUntil = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (State.IsCreated) State.Dispose();
            if (TargetMinute.IsCreated) TargetMinute.Dispose();
            if (CurrentSlotMinute.IsCreated) CurrentSlotMinute.Dispose();
            if (Line.IsCreated) Line.Dispose();
            if (IdleStartFrame.IsCreated) IdleStartFrame.Dispose();
            if (PreparingStartFrame.IsCreated) PreparingStartFrame.Dispose();
            if (LastLaunchFrame.IsCreated) LastLaunchFrame.Dispose();
            if (LaunchCooldownUntil.IsCreated) LaunchCooldownUntil.Dispose();
            if (DispatchRequestStartFrame.IsCreated) DispatchRequestStartFrame.Dispose();
            if (NearingTerminus.IsCreated) NearingTerminus.Dispose();
            if (OriginArrivalCandidateSinceFrame.IsCreated) OriginArrivalCandidateSinceFrame.Dispose();
            if (ForcedOriginReadyFrame.IsCreated) ForcedOriginReadyFrame.Dispose();
            if (ForcedOriginBoardingGraceUntil.IsCreated) ForcedOriginBoardingGraceUntil.Dispose();
        }

        public void Clear()
        {
            if (State.IsCreated) State.Clear();
            if (TargetMinute.IsCreated) TargetMinute.Clear();
            if (CurrentSlotMinute.IsCreated) CurrentSlotMinute.Clear();
            if (Line.IsCreated) Line.Clear();
            if (IdleStartFrame.IsCreated) IdleStartFrame.Clear();
            if (PreparingStartFrame.IsCreated) PreparingStartFrame.Clear();
            if (LastLaunchFrame.IsCreated) LastLaunchFrame.Clear();
            if (LaunchCooldownUntil.IsCreated) LaunchCooldownUntil.Clear();
            if (DispatchRequestStartFrame.IsCreated) DispatchRequestStartFrame.Clear();
            if (NearingTerminus.IsCreated) NearingTerminus.Clear();
            if (OriginArrivalCandidateSinceFrame.IsCreated) OriginArrivalCandidateSinceFrame.Clear();
            if (ForcedOriginReadyFrame.IsCreated) ForcedOriginReadyFrame.Clear();
            if (ForcedOriginBoardingGraceUntil.IsCreated) ForcedOriginBoardingGraceUntil.Clear();
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            State.Remove(vehicle);
            TargetMinute.Remove(vehicle);
            CurrentSlotMinute.Remove(vehicle);
            Line.Remove(vehicle);
            IdleStartFrame.Remove(vehicle);
            PreparingStartFrame.Remove(vehicle);
            LastLaunchFrame.Remove(vehicle);
            LaunchCooldownUntil.Remove(vehicle);
            DispatchRequestStartFrame.Remove(vehicle);
            NearingTerminus.Remove(vehicle);
            OriginArrivalCandidateSinceFrame.Remove(vehicle);
            ForcedOriginReadyFrame.Remove(vehicle);
            ForcedOriginBoardingGraceUntil.Remove(vehicle);
        }
    }
}
