using Unity.Collections;
using Unity.Entities;
using System.Collections.Generic;
using RapidTransitMod.Dispatch.Scheduling;

namespace RapidTransitMod
{
    internal sealed class VehicleView
    {
        private readonly VehicleStateStore m_Store;
        private readonly SlotBook m_Slots;
        private readonly VehicleWorksets m_Worksets;

        public VehicleView(VehicleStateStore store, SlotBook slots, VehicleWorksets worksets)
        {
            m_Store = store;
            m_Slots = slots;
            m_Worksets = worksets;
        }

        public int Count => m_Store.State.Count;

        internal NativeHashMap<Entity, VehicleState> StateMap => m_Store.State;

        public bool Contains(Entity vehicle) => m_Store.State.ContainsKey(vehicle);

        public VehicleState GetState(Entity vehicle) => m_Store.State[vehicle];

        public NativeArray<Entity> Keys(Allocator allocator) => m_Store.State.GetKeyArray(allocator);

        public bool TryGetState(Entity vehicle, out VehicleState state) => m_Store.State.TryGetValue(vehicle, out state);

        public bool TryGetTarget(Entity vehicle, out int targetMinute)
        {
            bool found = m_Slots.TryGetHeld(vehicle, out SlotRow row);
            targetMinute = found ? row.Slot.Minute : -1;
            return found;
        }

        public bool TryGetSlot(Entity vehicle, out int slotMinute)
        {
            bool found = m_Slots.TryGetTrip(vehicle, out SlotRow trip);
            slotMinute = found ? trip.Slot.Minute : -1;
            return found;
        }

        public IReadOnlyCollection<Entity> OnLine(Entity line) => m_Worksets.OnLine(line);

        public bool TryGetLine(Entity vehicle, out Entity line) => m_Store.Line.TryGetValue(vehicle, out line);

        public bool TryGetIdle(Entity vehicle, out uint frame) => m_Store.IdleStartFrame.TryGetValue(vehicle, out frame);

        public bool TryGetPreparingStartFrame(Entity vehicle, out uint frame) => m_Store.PreparingStartFrame.TryGetValue(vehicle, out frame);

        public bool TryGetEstimatedOriginArrivalFrame(Entity vehicle, out uint frame) => m_Store.EstimatedOriginArrivalFrame.TryGetValue(vehicle, out frame);

        public bool TryGetTheoryWait(Entity vehicle, out uint frame) => m_Store.WaitingForTheory.TryGetValue(vehicle, out frame);

        public bool TryGetLaunch(Entity vehicle, out uint frame) => m_Store.LastLaunchFrame.TryGetValue(vehicle, out frame);

        public bool TryGetSpawnOrderRequestFrame(Entity vehicle, out uint frame) => m_Store.SpawnOrderRequestFrame.TryGetValue(vehicle, out frame);


        public bool IsInbound(Entity vehicle) => m_Store.NearingTerminus.Contains(vehicle);

        public bool IsFreshPreparing(Entity vehicle, uint nowFrame, uint graceFrames)
        {
            if (vehicle == Entity.Null || !TryGetSpawnOrderRequestFrame(vehicle, out uint spawnOrderRequestFrame))
                return false;

            return nowFrame >= spawnOrderRequestFrame
                && (nowFrame - spawnOrderRequestFrame) <= graceFrames;
        }
    }
}
