using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class SaveOperations : ModuleBase
    {
        private const int MaxOperations = 12;
        private readonly object m_Sync = new object();
        private readonly Dictionary<string, OperationState> m_Operations =
            new Dictionary<string, OperationState>(StringComparer.Ordinal);
        private string m_CurrentOperationId = string.Empty;
        private int m_Generation;

        internal SaveOperations(Context context) : base(context) { }

        internal void Reset()
        {
            lock (m_Sync)
            {
                m_Generation++;
                m_CurrentOperationId = string.Empty;
                foreach (OperationState operation in m_Operations.Values.ToArray())
                {
                    operation?.Update("superseded", true, string.Empty, null);
                }
                m_Operations.Clear();
            }
        }

        internal string Start(string requestJson)
        {
            Clean();

            string operationId = "broadcast-apply-operation-" + Guid.NewGuid().ToString("N");
            OperationState operation;
            lock (m_Sync)
            {
                if (HasActiveOperationLocked())
                {
                    return global::RapidTransitMod.Workbenches.Json.Write(new ApplyOperationStatusDto
                    {
                        mode = ModeScope.DefaultWorkbench.Token,
                        success = false,
                        operationId = string.Empty,
                        state = "busy",
                        error = "broadcast-apply-operation-busy",
                        result = null
                    });
                }

                m_Generation++;
                operation = new OperationState(operationId, requestJson ?? string.Empty, m_Generation);
                m_Operations[operationId] = operation;
                m_CurrentOperationId = operationId;
            }

            Task.Run(() => Run(operation));
            return global::RapidTransitMod.Workbenches.Json.Write(operation.Copy());
        }

        internal string Status(string operationId)
        {
            Clean();

            lock (m_Sync)
            {
                if (string.IsNullOrWhiteSpace(operationId)
                    || !m_Operations.TryGetValue(operationId, out OperationState operation))
                {
                    return global::RapidTransitMod.Workbenches.Json.Write(new ApplyOperationStatusDto
                    {
                        mode = ModeScope.DefaultWorkbench.Token,
                        success = false,
                        operationId = operationId ?? string.Empty,
                        state = "missing",
                        error = "broadcast-apply-operation-not-found",
                        result = null
                    });
                }

                return global::RapidTransitMod.Workbenches.Json.Write(operation.Copy());
            }
        }

        private void Run(OperationState operation)
        {
            if (operation == null)
            {
                return;
            }

            if (!TryUpdateCurrent(operation, "running", true, string.Empty, null))
            {
                return;
            }

            try
            {
                Apply.PreparedApply prepared = m_Ctx.Apply.Prepare(operation.RequestJson);
                m_Access.Run(() => Commit(operation, prepared));
            }
            catch (Exception ex)
            {
                TryUpdateCurrent(operation, "failed", false, m_Access.Error(ex), null);
                LogException("SaveOperations.Run", ex);
            }
        }

        private void Commit(OperationState operation, Apply.PreparedApply prepared)
        {
            if (operation == null || prepared == null)
            {
                return;
            }

            try
            {
                if (!TryUpdateCurrent(operation, "applying", true, string.Empty, null))
                {
                    return;
                }

                ApplyResult result = m_Ctx.Apply.Commit(prepared);
                TryUpdateCurrent(operation, "completed", result?.success == true, result?.error ?? string.Empty, result);
            }
            catch (Exception ex)
            {
                TryUpdateCurrent(operation, "failed", false, m_Access.Error(ex), null);
                LogException("SaveOperations.Commit", ex);
            }
        }

        private bool TryUpdateCurrent(
            OperationState operation,
            string state,
            bool success,
            string error,
            ApplyResult result)
        {
            lock (m_Sync)
            {
                if (!IsCurrentLocked(operation))
                {
                    return false;
                }

                operation.Update(state, success, error, result);
                return true;
            }
        }

        private bool IsCurrentLocked(OperationState operation)
        {
            return operation != null
                && !operation.IsTerminal
                && operation.Generation == m_Generation
                && string.Equals(m_CurrentOperationId, operation.OperationId, StringComparison.Ordinal)
                && m_Operations.TryGetValue(operation.OperationId, out OperationState current)
                && ReferenceEquals(current, operation);
        }

        private bool HasActiveOperationLocked()
        {
            if (string.IsNullOrWhiteSpace(m_CurrentOperationId)
                || !m_Operations.TryGetValue(m_CurrentOperationId, out OperationState operation))
            {
                return false;
            }

            return !operation.IsTerminal;
        }

        private void Clean()
        {
            lock (m_Sync)
            {
                if (m_Operations.Count <= MaxOperations)
                {
                    return;
                }

                foreach (string operationId in m_Operations
                    .Where(entry => entry.Value.IsTerminal)
                    .OrderBy(entry => entry.Value.UpdatedAtUtc)
                    .Select(entry => entry.Key)
                    .Take(Math.Max(0, m_Operations.Count - MaxOperations))
                    .ToArray())
                {
                    m_Operations.Remove(operationId);
                }
            }
        }

        private sealed class OperationState
        {
            private readonly object m_StateSync = new object();
            private ApplyOperationStatusDto m_Status;

            internal OperationState(string operationId, string requestJson, int generation)
            {
                OperationId = operationId ?? string.Empty;
                RequestJson = requestJson ?? string.Empty;
                Generation = generation;
                Mode = ResolveModeToken(RequestJson);
                UpdatedAtUtc = DateTime.UtcNow;
                m_Status = new ApplyOperationStatusDto
                {
                    mode = Mode,
                    success = true,
                    operationId = OperationId,
                    state = "queued",
                    error = string.Empty,
                    result = null
                };
            }

            internal string OperationId { get; }
            internal string RequestJson { get; }
            internal string Mode { get; }
            internal int Generation { get; }
            internal DateTime UpdatedAtUtc { get; private set; }

            internal bool IsTerminal
            {
                get
                {
                    lock (m_StateSync)
                    {
                        string state = m_Status?.state ?? string.Empty;
                        return state == "completed"
                            || state == "failed"
                            || state == "missing"
                            || state == "superseded";
                    }
                }
            }

            internal void Update(
                string state,
                bool success,
                string error,
                ApplyResult result)
            {
                lock (m_StateSync)
                {
                    UpdatedAtUtc = DateTime.UtcNow;
                    m_Status = new ApplyOperationStatusDto
                    {
                        mode = Mode,
                        success = success,
                        operationId = OperationId,
                        state = state ?? string.Empty,
                        error = error ?? string.Empty,
                        result = result
                    };
                }
            }

            internal ApplyOperationStatusDto Copy()
            {
                lock (m_StateSync)
                {
                    return new ApplyOperationStatusDto
                    {
                        mode = m_Status?.mode ?? Mode,
                        success = m_Status?.success == true,
                        operationId = OperationId,
                        state = m_Status?.state ?? string.Empty,
                        error = m_Status?.error ?? string.Empty,
                        result = m_Status?.result
                    };
                }
            }

            private static string ResolveModeToken(string requestJson)
            {
                try
                {
                    return Workbenches.ModeRequest
                        .ReadBroadcastScope(requestJson, "startBroadcastApplyOperation", allowLegacyDefault: true)
                        .Token;
                }
                catch
                {
                    return ModeScope.DefaultWorkbench.Token;
                }
            }
        }
    }
}
