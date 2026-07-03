using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Saves
    {
        private const int MaxWorkbenchSaveOperationHistory = 24;
        private static readonly TimeSpan WorkbenchSaveOperationRetention = TimeSpan.FromMinutes(5);
        private readonly Commands m_CommandHandler;
        private readonly Persist m_Persistence;
        private readonly Func<ModeScope, string, DispatchWorkbenchSnapshot> m_BuildSnapshot;
        private readonly Func<ulong> m_GetSnapshotVersion;
        private readonly Func<Exception, string> m_DescribeException;
        private readonly Action<string, Exception> m_LogException;
        private readonly Action<Action> m_RunOnMainThread;
        private readonly object m_Sync = new object();
        private readonly Dictionary<string, WorkbenchSaveOperationState> m_Operations =
            new Dictionary<string, WorkbenchSaveOperationState>(StringComparer.Ordinal);
        private int m_Generation;

        internal Saves(
            Commands commandHandler,
            Persist persistence,
            Func<ModeScope, string, DispatchWorkbenchSnapshot> buildSnapshot,
            Func<ulong> getSnapshotVersion,
            Func<Exception, string> describeException,
            Action<string, Exception> logException,
            Action<Action> runOnMainThread)
        {
            m_CommandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
            m_Persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            m_BuildSnapshot = buildSnapshot ?? throw new ArgumentNullException(nameof(buildSnapshot));
            m_GetSnapshotVersion = getSnapshotVersion ?? throw new ArgumentNullException(nameof(getSnapshotVersion));
            m_DescribeException = describeException ?? throw new ArgumentNullException(nameof(describeException));
            m_LogException = logException ?? throw new ArgumentNullException(nameof(logException));
            m_RunOnMainThread = runOnMainThread ?? throw new ArgumentNullException(nameof(runOnMainThread));
        }

        internal void Reset()
        {
            lock (m_Sync)
            {
                m_Generation++;
                foreach (WorkbenchSaveOperationState operation in m_Operations.Values.ToArray())
                {
                    operation?.UpdateStatus("superseded", true, string.Empty, null);
                }
                m_Operations.Clear();
            }
        }

        internal string Save(string requestJson)
        {
            try
            {
                WorkbenchSavePrepareContext context = m_CommandHandler.Capture(requestJson);
                PreparedWorkbenchSave prepared = m_CommandHandler.Prep(context);
                DispatchWorkbenchSaveResult result = m_CommandHandler.Commit(prepared, persistImmediately: true);
                return Workbenches.Json.Write(result);
            }
            catch (Exception ex)
            {
                m_LogException("Save", ex);
                ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "saveNativeWorkbenchDraft", allowLegacyDefault: true);
                DispatchWorkbenchSaveResult result = CreateWorkbenchSaveFailureResult(
                    scope,
                    m_GetSnapshotVersion(),
                    m_DescribeException(ex));
                result.snapshot = m_BuildSnapshot(scope, null);
                return Workbenches.Json.Write(result);
            }
        }

        internal string Start(string requestJson)
        {
            Clean();

            string operationId = "save-operation-" + Guid.NewGuid().ToString("N");
            int generation = GetGeneration();
            WorkbenchSaveOperationState operation =
                new WorkbenchSaveOperationState(operationId, requestJson ?? string.Empty, generation);
            WorkbenchSavePrepareContext context;
            try
            {
                context = m_CommandHandler.Capture(operation.RequestJson);
            }
            catch (Exception ex)
            {
                m_LogException("Start.Capture", ex);
                operation.UpdateStatus("failed", false, m_DescribeException(ex), null);
                lock (m_Sync)
                {
                    m_Operations[operationId] = operation;
                }

                return Workbenches.Json.Write(operation.CreateStatusCopy());
            }

            lock (m_Sync)
            {
                if (generation != m_Generation)
                {
                    operation.UpdateStatus("superseded", true, string.Empty, null);
                    return Workbenches.Json.Write(operation.CreateStatusCopy());
                }

                m_Operations[operationId] = operation;
                SupersedeOlderDraftSaveOperations(operation);
            }

            System.Threading.Tasks.Task.Run(() => RunWorkbenchSaveOperation(operation, context));

            return Workbenches.Json.Write(operation.CreateStatusCopy());
        }

        internal string Status(string operationId)
        {
            Clean();

            lock (m_Sync)
            {
                if (string.IsNullOrWhiteSpace(operationId)
                    || !m_Operations.TryGetValue(operationId, out WorkbenchSaveOperationState operation))
                {
                    Mod.log.Info($"[WorkbenchSaveOperation] status missing id={operationId ?? string.Empty}");
                    return Workbenches.Json.Write(new DispatchWorkbenchSaveOperationStatusDto
                    {
                        mode = ModeScope.DefaultWorkbench.Token,
                        success = false,
                        operationId = operationId ?? string.Empty,
                        state = "missing",
                        error = "save-operation-not-found",
                        result = null
                    });
                }

                return Workbenches.Json.Write(operation.CreateStatusCopy());
            }
        }

        private void RunWorkbenchSaveOperation(
            WorkbenchSaveOperationState operation,
            WorkbenchSavePrepareContext context)
        {
            if (operation == null)
                return;

            operation.UpdateStatus("running", true, string.Empty, null);
            try
            {
                if (!IsCurrent(operation))
                    return;

                PreparedWorkbenchSave prepared = m_CommandHandler.Prep(context);
                if (!IsCurrent(operation) || operation.IsTerminal)
                    return;

                if (prepared == null)
                {
                    DispatchWorkbenchSaveResult result = CreateWorkbenchSaveFailureResult(
                        context?.Scope ?? ModeScope.DefaultWorkbench,
                        context?.SnapshotVersion ?? m_GetSnapshotVersion(),
                        "save-operation-prepare-failed");
                    operation.UpdateStatus("completed", result?.success == true, string.Empty, result);
                    return;
                }

                if (prepared.HasErrors)
                {
                    m_RunOnMainThread(() =>
                    {
                        if (operation.IsTerminal || !IsCurrent(operation))
                            return;

                        DispatchWorkbenchSaveResult result = m_CommandHandler.Commit(
                            prepared,
                            persistImmediately: false);
                        operation.UpdateStatus("completed", result?.success == true, string.Empty, result);
                    });
                    return;
                }

                operation.UpdateStatus("committing", true, string.Empty, null);
            m_RunOnMainThread(() => Commit(operation, prepared));
            }
            catch (Exception ex)
            {
                operation.UpdateStatus("failed", false, ex.GetType().Name + ": " + ex.Message, null);
                m_LogException("RunWorkbenchSaveOperation", ex);
            }
        }

        private void Commit(
            WorkbenchSaveOperationState operation,
            PreparedWorkbenchSave prepared)
        {
            if (operation == null || prepared == null || operation.IsTerminal || !IsCurrent(operation))
                return;

            try
            {
                DispatchWorkbenchSaveResult result = m_CommandHandler.Commit(
                    prepared,
                    persistImmediately: false);
                if (result?.success == true)
                {
                    WorkbenchSavePersistencePayload payload = m_Persistence.Capture();
                    System.Threading.Tasks.Task.Run(() => PrepPersist(operation, result, payload));
                    return;
                }

                operation.UpdateStatus("completed", result?.success == true, string.Empty, result);
            }
            catch (Exception ex)
            {
                operation.UpdateStatus("failed", false, m_DescribeException(ex), null);
                m_LogException("Commit", ex);
            }
        }

        private void PrepPersist(
            WorkbenchSaveOperationState operation,
            DispatchWorkbenchSaveResult result,
            WorkbenchSavePersistencePayload payload)
        {
            try
            {
                if (operation == null || operation.IsTerminal || !IsCurrent(operation))
                    return;

                PreparedWorkbenchSavePersistence prepared = m_Persistence.Prep(payload);
                m_RunOnMainThread(() =>
                {
                    try
                    {
                        if (operation.IsTerminal || !IsCurrent(operation))
                            return;

                        m_Persistence.Commit(prepared);
                        operation.UpdateStatus("completed", result?.success == true, string.Empty, result);
                    }
                    catch (Exception ex)
                    {
                        operation.UpdateStatus("failed", false, m_DescribeException(ex), null);
                        m_LogException("PrepPersist.Commit", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                operation.UpdateStatus("failed", false, m_DescribeException(ex), null);
                m_LogException("PrepPersist", ex);
            }
        }

        private void Clean()
        {
            DateTime utcNow = DateTime.UtcNow;
            lock (m_Sync)
            {
                foreach (string operationId in m_Operations
                    .Where(entry => entry.Value == null
                        || (entry.Value.IsTerminal && (utcNow - entry.Value.LastUpdatedUtc) > WorkbenchSaveOperationRetention))
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    m_Operations.Remove(operationId);
                }

                if (m_Operations.Count <= MaxWorkbenchSaveOperationHistory)
                    return;

                foreach (string operationId in m_Operations
                    .Where(entry => entry.Value != null && entry.Value.IsTerminal)
                    .OrderBy(entry => entry.Value.LastUpdatedUtc)
                    .Take(Math.Max(0, m_Operations.Count - MaxWorkbenchSaveOperationHistory))
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    m_Operations.Remove(operationId);
                }
            }
        }

        private int GetGeneration()
        {
            lock (m_Sync)
            {
                return m_Generation;
            }
        }

        private bool IsCurrent(WorkbenchSaveOperationState operation)
        {
            if (operation == null)
                return false;

            lock (m_Sync)
            {
                return operation.Generation == m_Generation
                    && m_Operations.TryGetValue(operation.OperationId, out WorkbenchSaveOperationState current)
                    && ReferenceEquals(current, operation);
            }
        }

        private void SupersedeOlderDraftSaveOperations(WorkbenchSaveOperationState nextOperation)
        {
            if (nextOperation == null)
                return;

            foreach (WorkbenchSaveOperationState pending in m_Operations.Values.ToArray())
            {
                if (pending == null || ReferenceEquals(pending, nextOperation))
                    continue;

                if (pending.IsApplyDraft || pending.IsTerminal)
                    continue;

                pending.UpdateStatus("superseded", true, string.Empty, null);
            }
        }

        private static DispatchWorkbenchSaveResult CreateWorkbenchSaveFailureResult(
            ModeScope scope,
            ulong version,
            string error)
        {
            return new DispatchWorkbenchSaveResult
            {
                mode = scope.Token,
                success = false,
                errors = new[] { error ?? string.Empty },
                warnings = Array.Empty<string>(),
                version = version.ToString(),
                appliedLineIds = Array.Empty<string>(),
                snapshot = null
            };
        }
    }
}
