using System;

namespace RapidTransitMod.Planner
{
    internal sealed class PlannerApi
    {
        private readonly PlannerExport m_Export;
        private readonly PlannerJobs m_Jobs;

        internal PlannerApi(PlannerExport export, PlannerJobs jobs)
        {
            m_Export = export;
            m_Jobs = jobs;
        }

        internal string Load()
        {
            return Workbenches.Json.Write(m_Export.Load());
        }

        internal void Dump()
        {
            m_Export.Dump();
        }

        internal string Run(string requestJson)
        {
            DispatchPlannerRequest request = Workbenches.Json.Read<DispatchPlannerRequest>(requestJson);
            return Workbenches.Json.Write(m_Jobs.Run(request));
        }

        internal string Start(string requestJson)
        {
            try
            {
                DispatchPlannerRequest request = Workbenches.Json.Read<DispatchPlannerRequest>(requestJson);
                return Workbenches.Json.Write(m_Jobs.Start(request));
            }
            catch (Exception ex)
            {
                return Workbenches.Json.Write(new DispatchPlannerJobStatusDto
                {
                    success = false,
                    jobId = string.Empty,
                    state = "failed",
                    error = ex.GetType().Name + ": " + ex.Message,
                    result = null
                });
            }
        }

        internal string Status(string jobId)
        {
            return Workbenches.Json.Write(m_Jobs.Status(jobId));
        }
    }
}
