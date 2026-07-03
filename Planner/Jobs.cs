using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RapidTransitMod.Planner
{
    internal sealed class PlannerJobs
    {
        private const int MaxJobs = 24;
        private static readonly TimeSpan KeepTime = TimeSpan.FromMinutes(10);
        private readonly PlannerExport m_Export;
        private readonly DispatchWorkbenchPlannerService m_Service;
        private readonly ConcurrentDictionary<string, PlannerJobState> m_PlannerJobs =
            new ConcurrentDictionary<string, PlannerJobState>(StringComparer.Ordinal);

        internal PlannerJobs(PlannerExport export, DispatchWorkbenchPlannerService service)
        {
            m_Export = export;
            m_Service = service;
        }

        internal DispatchPlannerResult Run(ModeScope scope, DispatchPlannerRequest request)
        {
            DispatchPlannerRequest scopedRequest = ScopeRequest(scope, request);
            DispatchPlannerExportSnapshot snapshot = m_Export.Build(scope);
            return m_Service.Execute(snapshot, scopedRequest);
        }

        internal DispatchPlannerJobStatusDto Start(ModeScope scope, DispatchPlannerRequest request)
        {
            CleanupPlannerJobs();

            DispatchPlannerRequest scopedRequest = ScopeRequest(scope, request);
            DispatchPlannerExportSnapshot snapshot = m_Export.Build(scope);
            string jobId = "planner-job-" + Guid.NewGuid().ToString("N");
            PlannerJobState jobState = new PlannerJobState(jobId, scope);
            m_PlannerJobs[jobId] = jobState;

            Task.Run(() => ExecutePlannerJob(jobState, snapshot, scopedRequest));

            return jobState.CreateStatusCopy();
        }

        internal DispatchPlannerJobStatusDto Status(string jobId)
        {
            CleanupPlannerJobs();

            if (string.IsNullOrWhiteSpace(jobId)
                || !m_PlannerJobs.TryGetValue(jobId, out PlannerJobState jobState))
            {
                return new DispatchPlannerJobStatusDto
                {
                    mode = ModeScope.DefaultWorkbench.Token,
                    success = false,
                    jobId = jobId ?? string.Empty,
                    state = "missing",
                    error = "planner-job-not-found",
                    result = null
                };
            }

            return jobState.CreateStatusCopy();
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
                DispatchPlannerResult result = m_Service.Execute(snapshot, request ?? new DispatchPlannerRequest());
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

                if ((utcNow - jobState.LastUpdatedUtc) > KeepTime)
                {
                    m_PlannerJobs.TryRemove(entry.Key, out _);
                }
            }

            if (m_PlannerJobs.Count <= MaxJobs)
            {
                return;
            }

            foreach (var entry in m_PlannerJobs
                .Where(item => item.Value != null && item.Value.IsTerminal)
                .OrderBy(item => item.Value.LastUpdatedUtc)
                .Take(Math.Max(0, m_PlannerJobs.Count - MaxJobs)))
            {
                m_PlannerJobs.TryRemove(entry.Key, out _);
            }
        }

        private static DispatchPlannerRequest ScopeRequest(ModeScope scope, DispatchPlannerRequest request)
        {
            DispatchPlannerRequest scoped = request ?? new DispatchPlannerRequest();
            ValidateRequestLineId(scope, scoped.draftKey, "draftKey");
            scoped.draftKey = NormalizeRequestLineId(scope, scoped.draftKey);
            scoped.localLineIds = NormalizeRequestLineIds(scope, scoped.localLineIds, "localLineIds");
            scoped.adjustableLineIds = NormalizeRequestLineIds(scope, scoped.adjustableLineIds, "adjustableLineIds");
            ValidateRequestLineId(scope, scoped.expressLineId, "expressLineId");
            scoped.expressLineId = NormalizeRequestLineId(scope, scoped.expressLineId);
            ValidateRequestLineId(scope, scoped.virtualExpressBaseLineId, "virtualExpressBaseLineId");
            scoped.virtualExpressBaseLineId = NormalizeRequestLineId(scope, scoped.virtualExpressBaseLineId);
            scoped.mode = scope.Token;
            return scoped;
        }

        private static string[] NormalizeRequestLineIds(
            ModeScope scope,
            string[] lineIds,
            string fieldName)
        {
            if (lineIds == null)
                return Array.Empty<string>();

            string[] normalized = new string[lineIds.Length];
            for (int i = 0; i < lineIds.Length; i++)
            {
                string itemFieldName = fieldName + "[" + i.ToString() + "]";
                ValidateRequestLineId(scope, lineIds[i], itemFieldName);
                normalized[i] = NormalizeRequestLineId(scope, lineIds[i]);
            }

            return normalized;
        }

        private static string NormalizeRequestLineId(ModeScope scope, string lineId)
        {
            return string.IsNullOrWhiteSpace(lineId)
                ? string.Empty
                : scope.NormalizeLineId(lineId);
        }

        private static void ValidateRequestLineId(
            ModeScope scope,
            string lineId,
            string fieldName)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return;

            List<string> errors = new List<string>();
            scope.ValidateLineId(lineId, fieldName, errors);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join("; ", errors));
        }

        private sealed class PlannerJobState
        {
            private readonly object m_Sync = new object();
            private DispatchPlannerJobStatusDto m_Status;

            public PlannerJobState(string jobId, ModeScope scope)
            {
                JobId = jobId ?? string.Empty;
                Mode = scope.Token;
                LastUpdatedUtc = DateTime.UtcNow;
                m_Status = new DispatchPlannerJobStatusDto
                {
                    mode = Mode,
                    success = true,
                    jobId = JobId,
                    state = "queued",
                    error = string.Empty,
                    result = null
                };
            }

            public string JobId { get; }
            public string Mode { get; }

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
                        mode = m_Status?.mode ?? Mode,
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
                        mode = Mode,
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
