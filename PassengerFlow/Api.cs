namespace RapidTransitMod.PassengerFlow
{
    internal static class Api
    {
        internal static string Load(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "loadPassengerFlowSnapshot");
            Port port = Runtime.Current;
            uint frame = port != null ? port.Frame() : 0u;
            State state = SamplingSystem.CurrentState;
            return Workbenches.Json.Write(Snapshot.Build(scope, state, frame, port));
        }
    }
}
