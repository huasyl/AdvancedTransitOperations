using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class LinePlan
    {
        internal Entity Line = Entity.Null;
        internal List<RowPlan> Rows = new List<RowPlan>();
    }

    internal sealed class RowPlan
    {
        internal string Id = string.Empty;
        internal string LineId = string.Empty;
        internal string Time = string.Empty;
        internal string Kind = string.Empty;
        internal string Source = string.Empty;
    }
}
