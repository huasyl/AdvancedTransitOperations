using RapidTransitMod.Workbenches;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class HostState
    {
        private DispatchWorkbenchHostStateDto m_State = new DispatchWorkbenchHostStateDto
        {
            phase = "unknown",
            mode = string.Empty,
            activePage = string.Empty,
            selectedLineId = string.Empty,
            selectedEditLine = string.Empty
        };

        internal bool IsParked
            => string.Equals(m_State?.phase, "parked", System.StringComparison.Ordinal);

        internal bool IsOpen
            => IsOpenPhase(m_State?.phase);

        internal string Mode => m_State?.mode ?? string.Empty;
        internal string Phase => m_State?.phase ?? "unknown";
        internal TransitMode TransitMode
        {
            get
            {
                return TransitModeCodec.TryParse(Mode, out TransitMode mode)
                    ? mode
                    : ModeScope.DefaultWorkbench.Mode;
            }
        }
        internal string SelectedLineId => m_State?.selectedLineId ?? string.Empty;
        internal string SelectedEditLine => m_State?.selectedEditLine ?? string.Empty;

        internal string Update(string requestJson)
        {
            Update(requestJson, out _);
            return "{\"ok\":true}";
        }

        internal string Update(string requestJson, out string explicitPhase)
        {
            DispatchWorkbenchHostStateDto state = Json.Read<DispatchWorkbenchHostStateDto>(requestJson);
            bool hasExplicitPhase = !string.IsNullOrWhiteSpace(state?.phase);
            string nextPhase = hasExplicitPhase
                ? NormalizePhase(state.phase)
                : (m_State?.phase ?? "unknown");
            m_State = new DispatchWorkbenchHostStateDto
            {
                phase = nextPhase,
                mode = string.IsNullOrWhiteSpace(state?.mode) ? (m_State?.mode ?? string.Empty) : state.mode,
                activePage = string.IsNullOrWhiteSpace(state?.activePage) ? (m_State?.activePage ?? string.Empty) : state.activePage,
                selectedLineId = string.IsNullOrWhiteSpace(state?.selectedLineId) ? (m_State?.selectedLineId ?? string.Empty) : state.selectedLineId,
                selectedEditLine = string.IsNullOrWhiteSpace(state?.selectedEditLine) ? (m_State?.selectedEditLine ?? string.Empty) : state.selectedEditLine
            };
            explicitPhase = hasExplicitPhase ? nextPhase : string.Empty;
            return "{\"ok\":true}";
        }

        private static string NormalizePhase(string phase)
        {
            string token = (phase ?? string.Empty).Trim().ToLowerInvariant();
            if (token == "opening" || token == "visible" || token == "closing" || token == "parked")
            {
                return token;
            }

            return "unknown";
        }

        private static bool IsOpenPhase(string phase)
        {
            return string.Equals(phase, "opening", System.StringComparison.Ordinal)
                || string.Equals(phase, "visible", System.StringComparison.Ordinal);
        }
    }
}
