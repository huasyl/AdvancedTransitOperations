using System;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleStateStore
    {
        private NativeHashMap<Entity, VehicleState> m_State;
        private NativeHashMap<Entity, int> m_TargetMinute;
        private NativeHashMap<Entity, int> m_CurrentSlotMinute;
        private NativeHashMap<Entity, Entity> m_Line;
        private NativeHashMap<Entity, uint> m_IdleStartFrame;
        private NativeHashMap<Entity, uint> m_PreparingStartFrame;
        private NativeHashMap<Entity, uint> m_LastLaunchFrame;
        private NativeHashMap<Entity, uint> m_LaunchCooldownUntil;
        private NativeHashMap<Entity, uint> m_DispatchRequestStartFrame;
        private NativeHashSet<Entity> m_NearingTerminus;
        private NativeHashMap<Entity, uint> m_OriginArrivalCandidateSinceFrame;
        private NativeHashMap<Entity, ReadyClockState> m_ForcedOriginReadyFrame;
        private NativeHashMap<Entity, uint> m_ForcedOriginBoardingGraceUntil;

        public VehicleStateStore()
        {
            State = new MapRef<VehicleState>(() => m_State);
            TargetMinute = new MapRef<int>(() => m_TargetMinute);
            CurrentSlotMinute = new MapRef<int>(() => m_CurrentSlotMinute);
            Line = new MapRef<Entity>(() => m_Line);
            IdleStartFrame = new MapRef<uint>(() => m_IdleStartFrame);
            PreparingStartFrame = new MapRef<uint>(() => m_PreparingStartFrame);
            LastLaunchFrame = new MapRef<uint>(() => m_LastLaunchFrame);
            LaunchCooldownUntil = new MapRef<uint>(() => m_LaunchCooldownUntil);
            DispatchRequestStartFrame = new MapRef<uint>(() => m_DispatchRequestStartFrame);
            NearingTerminus = new SetRef(() => m_NearingTerminus);
            OriginArrivalCandidateSinceFrame = new MapRef<uint>(() => m_OriginArrivalCandidateSinceFrame);
            ForcedOriginReadyFrame = new MapRef<ReadyClockState>(() => m_ForcedOriginReadyFrame);
            ForcedOriginBoardingGraceUntil = new MapRef<uint>(() => m_ForcedOriginBoardingGraceUntil);
        }

        public MapRef<VehicleState> State { get; }
        public MapRef<int> TargetMinute { get; }
        public MapRef<int> CurrentSlotMinute { get; }
        public MapRef<Entity> Line { get; }
        public MapRef<uint> IdleStartFrame { get; }
        public MapRef<uint> PreparingStartFrame { get; }
        public MapRef<uint> LastLaunchFrame { get; }
        public MapRef<uint> LaunchCooldownUntil { get; }
        public MapRef<uint> DispatchRequestStartFrame { get; }
        public SetRef NearingTerminus { get; }
        public MapRef<uint> OriginArrivalCandidateSinceFrame { get; }
        public MapRef<ReadyClockState> ForcedOriginReadyFrame { get; }
        public MapRef<uint> ForcedOriginBoardingGraceUntil { get; }

        internal NativeHashMap<Entity, VehicleState> StateMap => m_State;

        public void Init()
        {
            if (m_State.IsCreated)
                return;

            m_State = new NativeHashMap<Entity, VehicleState>(1024, Allocator.Persistent);
            m_TargetMinute = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            m_CurrentSlotMinute = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            m_Line = new NativeHashMap<Entity, Entity>(1024, Allocator.Persistent);
            m_IdleStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_PreparingStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_LastLaunchFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_LaunchCooldownUntil = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_DispatchRequestStartFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            m_NearingTerminus = new NativeHashSet<Entity>(64, Allocator.Persistent);
            m_OriginArrivalCandidateSinceFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            m_ForcedOriginReadyFrame = new NativeHashMap<Entity, ReadyClockState>(256, Allocator.Persistent);
            m_ForcedOriginBoardingGraceUntil = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (m_State.IsCreated) m_State.Dispose();
            if (m_TargetMinute.IsCreated) m_TargetMinute.Dispose();
            if (m_CurrentSlotMinute.IsCreated) m_CurrentSlotMinute.Dispose();
            if (m_Line.IsCreated) m_Line.Dispose();
            if (m_IdleStartFrame.IsCreated) m_IdleStartFrame.Dispose();
            if (m_PreparingStartFrame.IsCreated) m_PreparingStartFrame.Dispose();
            if (m_LastLaunchFrame.IsCreated) m_LastLaunchFrame.Dispose();
            if (m_LaunchCooldownUntil.IsCreated) m_LaunchCooldownUntil.Dispose();
            if (m_DispatchRequestStartFrame.IsCreated) m_DispatchRequestStartFrame.Dispose();
            if (m_NearingTerminus.IsCreated) m_NearingTerminus.Dispose();
            if (m_OriginArrivalCandidateSinceFrame.IsCreated) m_OriginArrivalCandidateSinceFrame.Dispose();
            if (m_ForcedOriginReadyFrame.IsCreated) m_ForcedOriginReadyFrame.Dispose();
            if (m_ForcedOriginBoardingGraceUntil.IsCreated) m_ForcedOriginBoardingGraceUntil.Dispose();
        }

        public void Clear()
        {
            if (m_State.IsCreated) m_State.Clear();
            if (m_TargetMinute.IsCreated) m_TargetMinute.Clear();
            if (m_CurrentSlotMinute.IsCreated) m_CurrentSlotMinute.Clear();
            if (m_Line.IsCreated) m_Line.Clear();
            if (m_IdleStartFrame.IsCreated) m_IdleStartFrame.Clear();
            if (m_PreparingStartFrame.IsCreated) m_PreparingStartFrame.Clear();
            if (m_LastLaunchFrame.IsCreated) m_LastLaunchFrame.Clear();
            if (m_LaunchCooldownUntil.IsCreated) m_LaunchCooldownUntil.Clear();
            if (m_DispatchRequestStartFrame.IsCreated) m_DispatchRequestStartFrame.Clear();
            if (m_NearingTerminus.IsCreated) m_NearingTerminus.Clear();
            if (m_OriginArrivalCandidateSinceFrame.IsCreated) m_OriginArrivalCandidateSinceFrame.Clear();
            if (m_ForcedOriginReadyFrame.IsCreated) m_ForcedOriginReadyFrame.Clear();
            if (m_ForcedOriginBoardingGraceUntil.IsCreated) m_ForcedOriginBoardingGraceUntil.Clear();
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_State.Remove(vehicle);
            m_TargetMinute.Remove(vehicle);
            m_CurrentSlotMinute.Remove(vehicle);
            m_Line.Remove(vehicle);
            m_IdleStartFrame.Remove(vehicle);
            m_PreparingStartFrame.Remove(vehicle);
            m_LastLaunchFrame.Remove(vehicle);
            m_LaunchCooldownUntil.Remove(vehicle);
            m_DispatchRequestStartFrame.Remove(vehicle);
            m_NearingTerminus.Remove(vehicle);
            m_OriginArrivalCandidateSinceFrame.Remove(vehicle);
            m_ForcedOriginReadyFrame.Remove(vehicle);
            m_ForcedOriginBoardingGraceUntil.Remove(vehicle);
        }

        internal sealed class MapRef<TValue> where TValue : unmanaged
        {
            private readonly Func<NativeHashMap<Entity, TValue>> m_Get;

            public MapRef(Func<NativeHashMap<Entity, TValue>> get)
            {
                m_Get = get;
            }

            public TValue this[Entity key]
            {
                get
                {
                    NativeHashMap<Entity, TValue> map = m_Get();
                    return map[key];
                }
                set
                {
                    NativeHashMap<Entity, TValue> map = m_Get();
                    map[key] = value;
                }
            }

            public bool IsCreated
            {
                get
                {
                    NativeHashMap<Entity, TValue> map = m_Get();
                    return map.IsCreated;
                }
            }

            public int Count
            {
                get
                {
                    NativeHashMap<Entity, TValue> map = m_Get();
                    return map.Count;
                }
            }

            public bool ContainsKey(Entity key)
            {
                NativeHashMap<Entity, TValue> map = m_Get();
                return map.ContainsKey(key);
            }

            public bool TryGetValue(Entity key, out TValue value)
            {
                NativeHashMap<Entity, TValue> map = m_Get();
                return map.TryGetValue(key, out value);
            }

            public void Clear()
            {
                NativeHashMap<Entity, TValue> map = m_Get();
                if (map.IsCreated)
                    map.Clear();
            }

            public void Remove(Entity key)
            {
                NativeHashMap<Entity, TValue> map = m_Get();
                map.Remove(key);
            }

            public NativeArray<Entity> GetKeyArray(Allocator allocator)
            {
                NativeHashMap<Entity, TValue> map = m_Get();
                return map.GetKeyArray(allocator);
            }

            public NativeHashMap<Entity, TValue>.Enumerator GetEnumerator()
            {
                NativeHashMap<Entity, TValue> map = m_Get();
                return map.GetEnumerator();
            }
        }

        internal sealed class SetRef
        {
            private readonly Func<NativeHashSet<Entity>> m_Get;

            public SetRef(Func<NativeHashSet<Entity>> get)
            {
                m_Get = get;
            }

            public bool IsCreated
            {
                get
                {
                    NativeHashSet<Entity> set = m_Get();
                    return set.IsCreated;
                }
            }

            public int Count
            {
                get
                {
                    NativeHashSet<Entity> set = m_Get();
                    return set.Count;
                }
            }

            public bool Contains(Entity key)
            {
                NativeHashSet<Entity> set = m_Get();
                return set.Contains(key);
            }

            public bool Add(Entity key)
            {
                NativeHashSet<Entity> set = m_Get();
                return set.Add(key);
            }

            public void Clear()
            {
                NativeHashSet<Entity> set = m_Get();
                if (set.IsCreated)
                    set.Clear();
            }

            public void Remove(Entity key)
            {
                NativeHashSet<Entity> set = m_Get();
                set.Remove(key);
            }

            public NativeArray<Entity> GetKeyArray(Allocator allocator)
            {
                NativeHashSet<Entity> set = m_Get();
                return set.ToNativeArray(allocator);
            }

            public NativeHashSet<Entity>.Enumerator GetEnumerator()
            {
                NativeHashSet<Entity> set = m_Get();
                return set.GetEnumerator();
            }
        }
    }
}
