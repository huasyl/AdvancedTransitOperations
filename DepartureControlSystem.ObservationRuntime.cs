using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        // Step 9 transition aliases for high-risk runtime observation paths only.
        // These do not own containers; stores own allocation, disposal, clear, and removal.
        private RuntimeObservationSession m_RuntimeObservationSession
        {
            get => m_RuntimeObservations.Session;
            set => m_RuntimeObservations.Session = value;
        }

        private Dictionary<string, List<RuntimeObservedTrip>> m_RuntimeObservedTripsByLineSlot => m_RuntimeObservations.TripsByLineSlot;
        private Dictionary<Entity, List<RuntimeObservedTrip>> m_RuntimeObservedTripsByVehicle => m_RuntimeObservations.TripsByVehicle;
        private Dictionary<Entity, RuntimeObservedBypassEvent> m_RuntimeActiveBypassByVehicle => m_RuntimeObservations.ActiveBypassByVehicle;
    }
}
