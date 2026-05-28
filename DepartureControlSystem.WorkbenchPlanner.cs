using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using RapidTransitMod.Planner;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private const int MaxPlannerJobHistory = 24;
        private static readonly TimeSpan PlannerJobRetention = TimeSpan.FromMinutes(10);
        private readonly ConcurrentDictionary<string, PlannerJobState> m_PlannerJobs =
            new ConcurrentDictionary<string, PlannerJobState>(StringComparer.Ordinal);

        public DispatchPlannerResult RunWorkbenchPlanner(DispatchPlannerRequest request)
        {
            DispatchPlannerExportSnapshot snapshot = BuildPlannerExportSnapshot();
            DispatchWorkbenchPlannerService service = new DispatchWorkbenchPlannerService();
            return service.Execute(snapshot, request ?? new DispatchPlannerRequest());
        }

        public DispatchPlannerJobStatusDto StartWorkbenchPlannerJob(DispatchPlannerRequest request)
        {
            CleanupPlannerJobs();

            DispatchPlannerExportSnapshot snapshot = BuildPlannerExportSnapshot();
            string jobId = "planner-job-" + Guid.NewGuid().ToString("N");
            PlannerJobState jobState = new PlannerJobState(jobId);
            m_PlannerJobs[jobId] = jobState;

            Task.Run(() => ExecutePlannerJob(jobState, snapshot, request ?? new DispatchPlannerRequest()));

            return jobState.CreateStatusCopy();
        }

        public DispatchPlannerJobStatusDto GetWorkbenchPlannerJobStatus(string jobId)
        {
            CleanupPlannerJobs();

            if (string.IsNullOrWhiteSpace(jobId)
                || !m_PlannerJobs.TryGetValue(jobId, out PlannerJobState jobState))
            {
                return new DispatchPlannerJobStatusDto
                {
                    success = false,
                    jobId = jobId ?? string.Empty,
                    state = "missing",
                    error = "planner-job-not-found",
                    result = null
                };
            }

            return jobState.CreateStatusCopy();
        }

        public string RunWorkbenchPlannerJson(string requestJson)
        {
            DispatchPlannerRequest request = DispatchWorkbenchJson.Deserialize<DispatchPlannerRequest>(requestJson);
            return DispatchWorkbenchJson.Serialize(RunWorkbenchPlanner(request));
        }

        public string StartWorkbenchPlannerJobJson(string requestJson)
        {
            try
            {
                DispatchPlannerRequest request = DispatchWorkbenchJson.Deserialize<DispatchPlannerRequest>(requestJson);
                return DispatchWorkbenchJson.Serialize(StartWorkbenchPlannerJob(request));
            }
            catch (Exception ex)
            {
                return DispatchWorkbenchJson.Serialize(new DispatchPlannerJobStatusDto
                {
                    success = false,
                    jobId = string.Empty,
                    state = "failed",
                    error = ex.GetType().Name + ": " + ex.Message,
                    result = null
                });
            }
        }

        public string GetWorkbenchPlannerJobStatusJson(string jobId)
        {
            return DispatchWorkbenchJson.Serialize(GetWorkbenchPlannerJobStatus(jobId));
        }

        private void ExecutePlannerJob(
            PlannerJobState jobState,
            DispatchPlannerExportSnapshot snapshot,
            DispatchPlannerRequest request)
        {
            if (jobState == null)
            {
                return;
            }

            jobState.UpdateStatus("running", true, string.Empty, null);

            try
            {
                DispatchWorkbenchPlannerService service = new DispatchWorkbenchPlannerService();
                DispatchPlannerResult result = service.Execute(snapshot, request ?? new DispatchPlannerRequest());
                jobState.UpdateStatus("completed", true, string.Empty, result);
            }
            catch (Exception ex)
            {
                jobState.UpdateStatus("failed", false, ex.GetType().Name + ": " + ex.Message, null);
                Mod.log.Info("[PlannerJob] failed " + jobState.JobId + ": " + ex);
            }
        }

        private void CleanupPlannerJobs()
        {
            DateTime utcNow = DateTime.UtcNow;
            foreach (var entry in m_PlannerJobs)
            {
                PlannerJobState jobState = entry.Value;
                if (jobState == null)
                {
                    m_PlannerJobs.TryRemove(entry.Key, out _);
                    continue;
                }

                if (!jobState.IsTerminal)
                {
                    continue;
                }

                if ((utcNow - jobState.LastUpdatedUtc) > PlannerJobRetention)
                {
                    m_PlannerJobs.TryRemove(entry.Key, out _);
                }
            }

            if (m_PlannerJobs.Count <= MaxPlannerJobHistory)
            {
                return;
            }

            foreach (var entry in m_PlannerJobs
                .Where(item => item.Value != null && item.Value.IsTerminal)
                .OrderBy(item => item.Value.LastUpdatedUtc)
                .Take(Math.Max(0, m_PlannerJobs.Count - MaxPlannerJobHistory)))
            {
                m_PlannerJobs.TryRemove(entry.Key, out _);
            }
        }

        private sealed class PlannerJobState
        {
            private readonly object m_Sync = new object();
            private DispatchPlannerJobStatusDto m_Status;

            public PlannerJobState(string jobId)
            {
                JobId = jobId ?? string.Empty;
                LastUpdatedUtc = DateTime.UtcNow;
                m_Status = new DispatchPlannerJobStatusDto
                {
                    success = true,
                    jobId = JobId,
                    state = "queued",
                    error = string.Empty,
                    result = null
                };
            }

            public string JobId { get; }

            public DateTime LastUpdatedUtc { get; private set; }

            public bool IsTerminal
            {
                get
                {
                    lock (m_Sync)
                    {
                        return IsTerminalState(m_Status?.state);
                    }
                }
            }

            public DispatchPlannerJobStatusDto CreateStatusCopy()
            {
                lock (m_Sync)
                {
                    return new DispatchPlannerJobStatusDto
                    {
                        success = m_Status?.success ?? false,
                        jobId = m_Status?.jobId ?? string.Empty,
                        state = m_Status?.state ?? string.Empty,
                        error = m_Status?.error ?? string.Empty,
                        result = m_Status?.result
                    };
                }
            }

            public void UpdateStatus(
                string state,
                bool success,
                string error,
                DispatchPlannerResult result)
            {
                lock (m_Sync)
                {
                    m_Status = new DispatchPlannerJobStatusDto
                    {
                        success = success,
                        jobId = JobId,
                        state = state ?? string.Empty,
                        error = error ?? string.Empty,
                        result = result
                    };
                    LastUpdatedUtc = DateTime.UtcNow;
                }
            }

            private static bool IsTerminalState(string state)
            {
                return string.Equals(state, "completed", StringComparison.Ordinal)
                    || string.Equals(state, "failed", StringComparison.Ordinal)
                    || string.Equals(state, "missing", StringComparison.Ordinal);
            }
        }
    }
}
