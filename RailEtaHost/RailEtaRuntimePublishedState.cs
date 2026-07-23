using System.Threading;
using RapidTransitMod.RailEtaHost;

namespace RapidTransitMod
{
    public partial class ModRuntimeHostSystem
    {
        private RailEtaPublicResult m_LastRailEtaPublicResult;
        internal RailEtaPublicResult LastRailEtaPublicResult => Volatile.Read(ref m_LastRailEtaPublicResult);

        internal void PublishRailEtaPublicResult(RailEtaPublicResult result)
        {
            if (result != null) Volatile.Write(ref m_LastRailEtaPublicResult, result);
        }
    }
}
