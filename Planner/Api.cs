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

        internal string Load(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "loadPlannerContext");
            return Workbenches.Json.Write(m_Export.Load(scope));
        }

        internal void Dump()
        {
            m_Export.Dump();
        }

        internal string Run(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "runPlanner");
            DispatchPlannerRequest request = Workbenches.Json.Read<DispatchPlannerRequest>(requestJson);
            return Workbenches.Json.Write(m_Jobs.Run(scope, request));
        }

        internal string Start(string requestJson)
        {
            try
            {
                ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "startPlannerJob");
                DispatchPlannerRequest request = Workbenches.Json.Read<DispatchPlannerRequest>(requestJson);
                return Workbenches.Json.Write(m_Jobs.Start(scope, request));
            }
            catch (Exception ex)
            {
                ModeScope scope = ModeScope.DefaultWorkbench;
                try
                {
                    scope = Workbenches.ModeRequest.ReadScope(requestJson, "startPlannerJob", allowLegacyDefault: true);
                }
                catch
                {
                }

                return Workbenches.Json.Write(new DispatchPlannerJobStatusDto
                {
                    mode = scope.Token,
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
