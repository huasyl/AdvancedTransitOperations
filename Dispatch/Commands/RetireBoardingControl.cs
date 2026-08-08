using Game.Vehicles;

namespace RapidTransitMod.Dispatch.Commands
{
    internal struct RetireBoardingState
    {
        internal uint WindowEndFrame;
        internal byte WindowExtensions;
        internal bool WindowCompleted;
    }

    internal readonly struct RetireBoardingResult
    {
        internal readonly PublicTransport PublicTransport;
        internal readonly RetireBoardingState State;
        internal readonly bool Changed;
        internal readonly bool WindowActive;

        internal RetireBoardingResult(
            PublicTransport publicTransport,
            RetireBoardingState state,
            bool changed,
            bool windowActive)
        {
            PublicTransport = publicTransport;
            State = state;
            Changed = changed;
            WindowActive = windowActive;
        }
    }

    internal static class RetireBoardingControl
    {
        internal const uint WindowFrames = 60;
        internal const byte MaxWindowExtensions = 5;

        internal static RetireBoardingResult Apply(
            PublicTransport publicTransport,
            RetireBoardingState state,
            bool allowEnRouteClear,
            int passengerCount,
            bool hasCurrentRoute,
            uint nowFrame)
        {
            bool changed = false;
            bool closedThisFrame = false;
            if (state.WindowEndFrame != 0)
            {
                if (nowFrame < state.WindowEndFrame)
                {
                    changed = ProjectWindow(ref publicTransport, state, allowEnRouteClear, nowFrame);
                    return new RetireBoardingResult(publicTransport, state, changed, true);
                }

                if (state.WindowExtensions < MaxWindowExtensions && passengerCount > 0)
                {
                    state.WindowExtensions++;
                    state.WindowEndFrame = nowFrame + WindowFrames;
                    changed = ProjectWindow(ref publicTransport, state, allowEnRouteClear, nowFrame);
                    return new RetireBoardingResult(publicTransport, state, changed, true);
                }

                DispatchActions.ForceOfficialBoardingClose(ref publicTransport, nowFrame);
                state.WindowEndFrame = 0;
                state.WindowCompleted = true;
                changed = true;
                closedThisFrame = true;
            }

            if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0)
                return new RetireBoardingResult(publicTransport, state, changed, false);

            bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            if (!hasCurrentRoute)
            {
                if (boarding && allowEnRouteClear && !state.WindowCompleted)
                {
                    state.WindowEndFrame = nowFrame + WindowFrames;
                    changed |= ProjectWindow(ref publicTransport, state, true, nowFrame);
                    return new RetireBoardingResult(publicTransport, state, changed, true);
                }

                return new RetireBoardingResult(publicTransport, state, changed, false);
            }

            PublicTransportFlags requestedState = publicTransport.m_State | PublicTransportFlags.AbandonRoute;
            if (publicTransport.m_State != requestedState)
            {
                publicTransport.m_State = requestedState;
                changed = true;
            }

            if (boarding && allowEnRouteClear && !state.WindowCompleted && !closedThisFrame)
            {
                state.WindowEndFrame = nowFrame + WindowFrames;
                changed |= ProjectWindow(ref publicTransport, state, true, nowFrame);
                return new RetireBoardingResult(publicTransport, state, changed, true);
            }

            return new RetireBoardingResult(publicTransport, state, changed, false);
        }

        private static bool ProjectWindow(
            ref PublicTransport publicTransport,
            RetireBoardingState state,
            bool allowEnRouteClear,
            uint nowFrame)
        {
            PublicTransportFlags desiredState = publicTransport.m_State | PublicTransportFlags.AbandonRoute;
            if (allowEnRouteClear)
                desiredState &= ~PublicTransportFlags.EnRoute;
            uint desiredDeparture = state.WindowExtensions == 1
                ? nowFrame
                : state.WindowEndFrame + 1u;
            if (publicTransport.m_State == desiredState
                && publicTransport.m_DepartureFrame == desiredDeparture
                && publicTransport.m_MinWaitingDistance == float.MaxValue
                && publicTransport.m_MaxBoardingDistance == 0f)
            {
                return false;
            }

            publicTransport.m_State = desiredState;
            publicTransport.m_DepartureFrame = desiredDeparture;
            publicTransport.m_MinWaitingDistance = float.MaxValue;
            publicTransport.m_MaxBoardingDistance = 0f;
            return true;
        }
    }
}
