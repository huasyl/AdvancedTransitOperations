using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleView
    {
        private readonly VehicleStateStore m_Store;

        public VehicleView(VehicleStateStore store)
        {
            m_Store = store;
        }

        public int Count => m_Store.State.Count;

        internal NativeHashMap<Entity, VehicleState> StateMap => m_Store.StateMap;

        public bool Contains(Entity vehicle) => m_Store.State.ContainsKey(vehicle);

        public VehicleState GetState(Entity vehicle) => m_Store.State[vehicle];

        public NativeArray<Entity> Keys(Allocator allocator) => m_Store.State.GetKeyArray(allocator);

        public bool TryGetState(Entity vehicle, out VehicleState state) => m_Store.State.TryGetValue(vehicle, out state);

        public bool TryGetTarget(Entity vehicle, out int targetMinute) => m_Store.TargetMinute.TryGetValue(vehicle, out targetMinute);

        public bool TryGetSlot(Entity vehicle, out int slotMinute) => m_Store.CurrentSlotMinute.TryGetValue(vehicle, out slotMinute);

        public bool TryGetLine(Entity vehicle, out Entity line) => m_Store.Line.TryGetValue(vehicle, out line);

        public bool TryGetIdle(Entity vehicle, out uint frame) => m_Store.IdleStartFrame.TryGetValue(vehicle, out frame);

        public bool TryGetPreparing(Entity vehicle, out uint frame) => m_Store.PreparingStartFrame.TryGetValue(vehicle, out frame);

        public bool TryGetLaunch(Entity vehicle, out uint frame) => m_Store.LastLaunchFrame.TryGetValue(vehicle, out frame);

        public bool TryGetCooldown(Entity vehicle, out uint frame) => m_Store.LaunchCooldownUntil.TryGetValue(vehicle, out frame);

        public bool TryGetDispatch(Entity vehicle, out uint frame) => m_Store.DispatchRequestStartFrame.TryGetValue(vehicle, out frame);

        public bool TryGetOrigin(Entity vehicle, out uint frame) => m_Store.OriginArrivalCandidateSinceFrame.TryGetValue(vehicle, out frame);

        public bool TryGetReady(Entity vehicle, out uint readyFrame)
        {
            if (m_Store.ForcedOriginReadyFrame.TryGetValue(vehicle, out ReadyClockState readyState))
            {
                readyFrame = readyState.ReadyFrame;
                return true;
            }

            readyFrame = 0u;
            return false;
        }

        public bool TryGetBoardingGrace(Entity vehicle, out uint frame) => m_Store.ForcedOriginBoardingGraceUntil.TryGetValue(vehicle, out frame);

        public bool IsInbound(Entity vehicle) => m_Store.NearingTerminus.Contains(vehicle);

        public bool IsFreshPreparing(Entity vehicle, uint nowFrame, uint graceFrames)
        {
            if (vehicle == Entity.Null || !TryGetDispatch(vehicle, out uint dispatchStartFrame))
                return false;

            return nowFrame >= dispatchStartFrame
                && (nowFrame - dispatchStartFrame) <= graceFrames;
        }
    }
}
