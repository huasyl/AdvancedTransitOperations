using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Overview
{
    internal sealed class FeatureSettingsOperations
    {
        private const int MaxOperationHistory = 24;
        private static readonly TimeSpan OperationRetention = TimeSpan.FromMinutes(5);
        private readonly FeatureSettingsService m_Service;
        private readonly Action<Action> m_RunOnMainThread;
        private readonly object m_Sync = new object();
        private readonly Dictionary<string, OperationState> m_Operations =
            new Dictionary<string, OperationState>(StringComparer.Ordinal);

        internal FeatureSettingsOperations(
            FeatureSettingsService service,
            Action<Action> runOnMainThread)
        {
            m_Service = service ?? throw new ArgumentNullException(nameof(service));
            m_RunOnMainThread = runOnMainThread ?? throw new ArgumentNullException(nameof(runOnMainThread));
        }

        internal string Start(string requestJson)
        {
            Clean();

            OverviewFeatureSettingsRequestDto request;
            try
            {
                request = global::RapidTransitMod.Workbenches.Json.Read<OverviewFeatureSettingsRequestDto>(requestJson ?? string.Empty);
            }
            catch (Exception ex)
            {
                return global::RapidTransitMod.Workbenches.Json.Write(new OverviewFeatureSettingsOperationStatusDto
                {
                    success = false,
                    operationId = string.Empty,
                    state = "failed",
                    error = ex.GetType().Name + ": " + ex.Message,
                    result = null
                });
            }

            string operationId = "overview-feature-settings-" + Guid.NewGuid().ToString("N");
            OperationState operation = new OperationState(operationId);
            lock (m_Sync)
            {
                m_Operations[operationId] = operation;
                SupersedeOlderOperations(operation);
            }

            System.Threading.Tasks.Task.Run(() => Run(operation, request));
            return global::RapidTransitMod.Workbenches.Json.Write(operation.CreateStatusCopy());
        }

        internal string Status(string operationId)
        {
            Clean();

            lock (m_Sync)
            {
                if (string.IsNullOrWhiteSpace(operationId)
                    || !m_Operations.TryGetValue(operationId, out OperationState operation))
                {
                    return global::RapidTransitMod.Workbenches.Json.Write(new OverviewFeatureSettingsOperationStatusDto
                    {
                        success = false,
                        operationId = operationId ?? string.Empty,
                        state = "missing",
                        error = "overview-feature-settings-operation-not-found",
                        result = null
                    });
                }

                return global::RapidTransitMod.Workbenches.Json.Write(operation.CreateStatusCopy());
            }
        }

        private void Run(OperationState operation, OverviewFeatureSettingsRequestDto request)
        {
            if (operation == null || operation.IsTerminal)
            {
                return;
            }

            operation.UpdateStatus("running", true, string.Empty, null);
            m_RunOnMainThread(() =>
            {
                if (operation.IsTerminal)
                {
                    return;
                }

                try
                {
                    OverviewFeatureSettingsResultDto result = m_Service.Apply(request);
                    string error = result?.success == true || result?.errors == null || result.errors.Length == 0
                        ? string.Empty
                        : string.Join("; ", result.errors);
                    operation.UpdateStatus("completed", result?.success == true, error, result);
                }
                catch (Exception ex)
                {
                    operation.UpdateStatus("failed", false, ex.GetType().Name + ": " + ex.Message, null);
                }
            });
        }

        private void SupersedeOlderOperations(OperationState nextOperation)
        {
            foreach (OperationState pending in m_Operations.Values.ToArray())
            {
                if (pending == null || ReferenceEquals(pending, nextOperation) || pending.IsTerminal)
                {
                    continue;
                }

                pending.UpdateStatus("superseded", true, string.Empty, null);
            }
        }

        private void Clean()
        {
            DateTime utcNow = DateTime.UtcNow;
            lock (m_Sync)
            {
                foreach (string operationId in m_Operations
                    .Where(entry => entry.Value == null
                        || (entry.Value.IsTerminal && (utcNow - entry.Value.LastUpdatedUtc) > OperationRetention))
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    m_Operations.Remove(operationId);
                }

                if (m_Operations.Count <= MaxOperationHistory)
                {
                    return;
                }

                foreach (string operationId in m_Operations
                    .Where(entry => entry.Value != null && entry.Value.IsTerminal)
                    .OrderBy(entry => entry.Value.LastUpdatedUtc)
                    .Take(Math.Max(0, m_Operations.Count - MaxOperationHistory))
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    m_Operations.Remove(operationId);
                }
            }
        }

        private sealed class OperationState
        {
            private readonly object m_StatusSync = new object();
            private OverviewFeatureSettingsOperationStatusDto m_Status;

            internal OperationState(string operationId)
            {
                m_Status = new OverviewFeatureSettingsOperationStatusDto
                {
                    success = true,
                    operationId = operationId ?? string.Empty,
                    state = "queued",
                    error = string.Empty,
                    result = null
                };
                LastUpdatedUtc = DateTime.UtcNow;
            }

            internal DateTime LastUpdatedUtc { get; private set; }

            internal bool IsTerminal
            {
                get
                {
                    lock (m_StatusSync)
                    {
                        return m_Status.state == "completed"
                            || m_Status.state == "failed"
                            || m_Status.state == "missing"
                            || m_Status.state == "superseded";
                    }
                }
            }

            internal OverviewFeatureSettingsOperationStatusDto CreateStatusCopy()
            {
                lock (m_StatusSync)
                {
                    return new OverviewFeatureSettingsOperationStatusDto
                    {
                        success = m_Status.success,
                        operationId = m_Status.operationId ?? string.Empty,
                        state = m_Status.state ?? string.Empty,
                        error = m_Status.error ?? string.Empty,
                        result = m_Status.result
                    };
                }
            }

            internal void UpdateStatus(
                string state,
                bool success,
                string error,
                OverviewFeatureSettingsResultDto result)
            {
                lock (m_StatusSync)
                {
                    m_Status = new OverviewFeatureSettingsOperationStatusDto
                    {
                        success = success,
                        operationId = m_Status?.operationId ?? string.Empty,
                        state = state ?? string.Empty,
                        error = error ?? string.Empty,
                        result = result
                    };
                    LastUpdatedUtc = DateTime.UtcNow;
                }
            }
        }
    }
}
