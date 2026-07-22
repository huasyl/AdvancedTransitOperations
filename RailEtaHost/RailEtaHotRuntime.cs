using System;
#if RT_DEBUG_TOOLS
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
#endif
using System.Threading;
#if RT_DEBUG_TOOLS
using System.Threading.Tasks;
#endif
using Unity.Jobs;

namespace RapidTransitMod.RailEtaHost
{
    internal sealed class RailEtaHotRuntime : IDisposable
    {
        internal sealed class Selection
        {
            public Selection(IRailEtaHotModule module, string buildId, long generation)
            {
                Module = module;
                BuildId = buildId ?? string.Empty;
                Generation = generation;
            }

            public IRailEtaHotModule Module { get; }
            public string BuildId { get; }
            public long Generation { get; }
        }

#if RT_DEBUG_TOOLS
        internal sealed class StatusSnapshot
        {
            public StatusSnapshot(bool busy, string currentBuildId, long generation, string lastAction, string status,
                long lastSmokeValue, string lastSmokeSummary, string lastError, int loadedAssemblies)
            {
                Busy = busy;
                CurrentBuildId = currentBuildId ?? string.Empty;
                Generation = generation;
                LastAction = lastAction ?? string.Empty;
                Status = status ?? string.Empty;
                LastSmokeValue = lastSmokeValue;
                LastSmokeSummary = lastSmokeSummary ?? string.Empty;
                LastError = lastError ?? string.Empty;
                LoadedAssemblies = loadedAssemblies;
            }

            public bool Busy { get; }
            public string CurrentBuildId { get; }
            public long Generation { get; }
            public string LastAction { get; }
            public string Status { get; }
            public long LastSmokeValue { get; }
            public string LastSmokeSummary { get; }
            public string LastError { get; }
            public int LoadedAssemblies { get; }
        }

        private sealed class PendingSwap
        {
            public Selection Next;
            public TaskCompletionSource<bool> Completion;
            public string Action;
            public bool Rollback;
        }
#endif

        private readonly RailEtaWorker m_Worker;
        private Selection m_Current;
        private int m_Disposed;
        private RailEtaHotContext m_Context;
        private JobHandle m_LastHandle;
        private int m_PendingClearGeneration = -1;
#if RT_DEBUG_TOOLS
        private readonly object m_Gate = new object();
        private readonly ConcurrentDictionary<long, string> m_ComparisonSummaries = new ConcurrentDictionary<long, string>();
        private Selection m_Previous;
        private PendingSwap m_PendingSwap;
        private long m_NextGeneration;
        private int m_Busy;
        private int m_LoadedAssemblies;
        private StatusSnapshot m_Status = new StatusSnapshot(false, string.Empty, 0, string.Empty, "idle", 0, string.Empty, string.Empty, 0);
#endif

        public RailEtaHotRuntime(RailEtaWorker worker, IRailEtaHotModule builtIn)
        {
            m_Worker = worker ?? throw new ArgumentNullException(nameof(worker));
            if (builtIn == null) throw new ArgumentNullException(nameof(builtIn));
            m_Current = new Selection(builtIn, builtIn.BuildId, 1);
#if RT_DEBUG_TOOLS
            m_NextGeneration = 1;
            m_Status = new StatusSnapshot(false, m_Current.BuildId, m_Current.Generation, string.Empty, "idle", 0, string.Empty, string.Empty, 0);
#endif
        }

#if RT_DEBUG_TOOLS
        public StatusSnapshot Status => Volatile.Read(ref m_Status);
#endif
        public bool IsDisposed => Volatile.Read(ref m_Disposed) != 0;
        public bool WorkerLost => m_Worker.WorkerLost;
        public Selection Current => Volatile.Read(ref m_Current);

        public bool ModuleBusy => Current?.Module.Busy ?? false;
        public bool NeedsTick
        {
            get
            {
                if (Volatile.Read(ref m_PendingClearGeneration) >= 0) return true;
#if RT_DEBUG_TOOLS
                lock (m_Gate)
                {
                    if (m_PendingSwap != null) return true;
                }
#endif
                return Current?.Module.NeedsTick ?? false;
            }
        }

        public void Attach(RailEtaHotContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            m_Context = context;
            Current?.Module.Attach(context);
        }

        public bool Submit(RailEtaHotCommand command)
        {
#if RT_DEBUG_TOOLS
            if (IsDisposed || Volatile.Read(ref m_Busy) != 0) return false;
            lock (m_Gate)
            {
                if (Volatile.Read(ref m_Busy) != 0 || m_PendingSwap != null) return false;
                Selection selection = Current;
                if (selection == null || selection.Generation != command.Generation) return false;
                selection.Module.Submit(command);
                return true;
            }
#else
            Selection selection = Current;
            if (IsDisposed || selection == null || selection.Generation != command.Generation) return false;
            selection.Module.Submit(command);
            return true;
#endif
        }

        public JobHandle Tick(uint simulationFrame, JobHandle inputDependency)
        {
#if RT_DEBUG_TOOLS
            ApplyPendingSwap();
#endif
            int clearGeneration = Interlocked.Exchange(ref m_PendingClearGeneration, -1);
            if (clearGeneration >= 0)
            {
                CompleteLastHandle();
                Current?.Module.Clear(clearGeneration);
#if RT_DEBUG_TOOLS
                m_Previous?.Module.Clear(clearGeneration);
#endif
            }
            Selection selection = Current;
            if (selection == null) return inputDependency;
            JobHandle output = selection.Module.Tick(simulationFrame, inputDependency);
            m_LastHandle = JobHandle.CombineDependencies(m_LastHandle, output);
            return output;
        }

        public void Cancel(long ticket) => Current?.Module.Cancel(ticket);

        public bool TryGetComparisonSummary(long ticket, out string summary)
        {
#if RT_DEBUG_TOOLS
            Selection selection = Current;
            if (selection != null && selection.Module.TryGetComparisonSummary(ticket, out summary)) return true;
            return m_ComparisonSummaries.TryGetValue(ticket, out summary);
#else
            summary = string.Empty;
            return false;
#endif
        }

        public void Clear(int generation)
        {
#if RT_DEBUG_TOOLS
            m_ComparisonSummaries.Clear();
#endif
            Interlocked.Exchange(ref m_PendingClearGeneration, generation);
        }

        private void CompleteLastHandle()
        {
            m_LastHandle.Complete();
            m_LastHandle = default;
        }

#if RT_DEBUG_TOOLS
        public Task<bool> ReloadAsync(string dllPath)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryBegin("reload"))
            {
                completion.TrySetResult(false);
                return completion.Task;
            }
            if (!m_Worker.TryEnqueue(() => ReloadOnWorker(dllPath, completion)))
            {
                Finish("reload", "failed", 0, string.Empty, "Rail ETA worker queue is unavailable.");
                completion.TrySetResult(false);
            }
            return completion.Task;
        }

        public Task<bool> ReloadLatestAsync(string hotDirectory)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryBegin("reload-latest"))
            {
                completion.TrySetResult(false);
                return completion.Task;
            }
            if (!m_Worker.TryEnqueue(() =>
            {
                string latest = FindLatest(hotDirectory);
                if (latest.Length == 0)
                {
                    Finish("reload-latest", "no-module", 0, string.Empty, "Hot directory has no RailEta.Hot DLL.");
                    completion.TrySetResult(false);
                    return;
                }
                ReloadOnWorker(latest, completion, "reload-latest");
            }))
            {
                Finish("reload-latest", "failed", 0, string.Empty, "Rail ETA worker queue is unavailable.");
                completion.TrySetResult(false);
            }
            return completion.Task;
        }

        public Task<uint> SmokeAsync()
        {
            var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryBegin("smoke"))
            {
                completion.TrySetResult(0);
                return completion.Task;
            }
            Selection selection = Current;
            if (selection == null)
            {
                Finish("smoke", "no-module", 0, string.Empty, "No hot Rail ETA predictor is loaded.");
                completion.TrySetResult(0);
                return completion.Task;
            }
            if (!m_Worker.TryEnqueue(() =>
            {
                Finish("smoke", "completed", 1, selection.Module.BuildId, string.Empty);
                completion.TrySetResult(1);
            }))
            {
                Finish("smoke", "failed", 0, string.Empty, "Rail ETA worker queue is unavailable.");
                completion.TrySetResult(0);
            }
            return completion.Task;
        }

        public bool Rollback()
        {
            if (IsDisposed || ModuleBusy || Interlocked.CompareExchange(ref m_Busy, 1, 0) != 0) return false;
            lock (m_Gate)
            {
                if (m_PendingSwap != null || m_Previous == null)
                {
                    Interlocked.Exchange(ref m_Busy, 0);
                    return false;
                }
                m_PendingSwap = new PendingSwap { Next = m_Previous, Action = "rollback", Rollback = true };
            }
            SetStatus(true, "rollback", "pending-swap", 0, string.Empty, string.Empty);
            return true;
        }

        private void ReloadOnWorker(string dllPath, TaskCompletionSource<bool> completion, string action = "reload")
        {
            Selection next = null;
            try
            {
                next = Load(dllPath);
                bool disposed;
                lock (m_Gate)
                {
                    disposed = IsDisposed;
                    if (!disposed)
                    {
                        if (m_PendingSwap != null) throw new InvalidOperationException("A Rail ETA module swap is already pending.");
                        m_PendingSwap = new PendingSwap { Next = next, Completion = completion, Action = action };
                        SetStatus(true, action, "pending-swap", 0, next.Module.BuildId, string.Empty);
                    }
                }
                if (!disposed) return;
                DisposeRejected(next.Module, action);
                next = null;
                Finish(action, "failed", 0, string.Empty, "Rail ETA hot runtime is disposed.");
                completion.TrySetResult(false);
            }
            catch (Exception ex)
            {
                LogFailure(action, ex);
                DisposeRejected(next?.Module, action);
                Finish(action, "failed", 0, string.Empty, ex.GetType().Name + ": " + ex.Message);
                completion.TrySetResult(false);
            }
        }

        private Selection Load(string dllPath)
        {
            if (String.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath)) throw new FileNotFoundException("RailEta.Hot DLL is missing.", dllPath);
            byte[] bytes = File.ReadAllBytes(dllPath);
            Assembly assembly = Assembly.Load(bytes);
            Interlocked.Increment(ref m_LoadedAssemblies);
            Type selected = null;
            foreach (Type type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IRailEtaHotModule).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null) continue;
                if (selected == null) selected = type;
                if (String.Equals(type.Namespace, "RapidTransitMod.RailEta.Hot", StringComparison.Ordinal)) { selected = type; break; }
            }
            if (selected == null) throw new InvalidDataException("RailEta.Hot DLL has no IRailEtaHotModule implementation.");
            IRailEtaHotModule module = null;
            try
            {
                module = (IRailEtaHotModule)Activator.CreateInstance(selected);
                string buildId = String.IsNullOrWhiteSpace(module.BuildId)
                    ? Path.GetFileNameWithoutExtension(dllPath) + "@" + File.GetLastWriteTimeUtc(dllPath).Ticks
                    : module.BuildId;
                return new Selection(module, buildId, Interlocked.Increment(ref m_NextGeneration));
            }
            catch
            {
                DisposeRejected(module, "load");
                throw;
            }
        }

        private bool TryBegin(string action)
        {
            if (IsDisposed) return false;
            if (ModuleBusy)
            {
                SetStatus(false, action, "busy", 0, string.Empty, "Rail ETA module is active.");
                return false;
            }
            if (WorkerLost)
            {
                SetStatus(false, action, "worker-lost", 0, string.Empty, "Rail ETA worker is lost; restart the game.");
                return false;
            }
            if (Interlocked.CompareExchange(ref m_Busy, 1, 0) != 0) return false;
            SetStatus(true, action, "queued", 0, string.Empty, string.Empty);
            return true;
        }

        private void Finish(string action, string status, long smokeValue, string summary, string error)
        {
            Interlocked.Exchange(ref m_Busy, 0);
            if (!IsDisposed) SetStatus(false, action, status, smokeValue, summary, error);
        }

        private void SetStatus(bool busy, string action, string status, long smokeValue, string summary, string error)
        {
            Selection current = Current;
            Interlocked.Exchange(ref m_Status, new StatusSnapshot(
                busy,
                current?.BuildId ?? string.Empty,
                current?.Generation ?? 0,
                action,
                status,
                smokeValue,
                summary,
                error,
                Volatile.Read(ref m_LoadedAssemblies)));
        }

        private static string FindLatest(string hotDirectory)
        {
            if (String.IsNullOrWhiteSpace(hotDirectory) || !Directory.Exists(hotDirectory)) return string.Empty;
            string latest = string.Empty;
            DateTime latestWrite = DateTime.MinValue;
            foreach (string path in Directory.GetFiles(hotDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith("RapidTransitMod.RailEta.Hot", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("RailEta.Hot", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.EndsWith(".staging.dll", StringComparison.OrdinalIgnoreCase)) continue;
                DateTime write = File.GetLastWriteTimeUtc(path);
                if (write > latestWrite) { latestWrite = write; latest = path; }
            }
            return latest;
        }

        private void ApplyPendingSwap()
        {
            PendingSwap pending;
            lock (m_Gate)
            {
                pending = m_PendingSwap;
                if (pending == null) return;
                m_PendingSwap = null;
            }

            Selection current;
            try
            {
                CompleteLastHandle();
                current = Volatile.Read(ref m_Current);
            }
            catch (Exception ex)
            {
                LogFailure(pending.Action, ex);
                if (!pending.Rollback) DisposeRejected(pending.Next?.Module, pending.Action);
                Finish(pending.Action, "failed", 0, string.Empty, ex.GetType().Name + ": " + ex.Message);
                pending.Completion?.TrySetResult(false);
                return;
            }
            if (current?.Module.Busy == true)
            {
                if (!pending.Rollback) DisposeRejected(pending.Next?.Module, pending.Action);
                Finish(pending.Action, "busy", 0, string.Empty, "ModuleBusy");
                pending.Completion?.TrySetResult(false);
                return;
            }

            Selection retired;
            try
            {
                if (!pending.Rollback) pending.Next.Module.Attach(m_Context);
                if (current != null && current.Module.PrepareForReload(out long ticket, out string summary)
                    && ticket != 0 && !String.IsNullOrEmpty(summary))
                    m_ComparisonSummaries[ticket] = summary;

                if (pending.Rollback)
                {
                    Volatile.Write(ref m_Current, pending.Next);
                    m_Previous = null;
                    retired = current;
                }
                else
                {
                    retired = m_Previous;
                    m_Previous = current;
                    Volatile.Write(ref m_Current, pending.Next);
                }
            }
            catch (Exception ex)
            {
                LogFailure(pending.Action, ex);
                if (!pending.Rollback) DisposeRejected(pending.Next?.Module, pending.Action);
                Finish(pending.Action, "failed", 0, string.Empty, ex.GetType().Name + ": " + ex.Message);
                pending.Completion?.TrySetResult(false);
                return;
            }

            if (retired != null && !ReferenceEquals(retired.Module, pending.Next?.Module)) DisposeRetired(retired.Module, pending.Action);
            Finish(pending.Action, "completed", 1, pending.Next?.BuildId ?? string.Empty, string.Empty);
            pending.Completion?.TrySetResult(true);
        }

        private void LogFailure(string action, Exception exception) =>
            m_Context?.Log("[RailEtaHotRuntime] " + action + " failed: " + exception);

        private void DisposeRejected(IRailEtaHotModule module, string action)
        {
            if (module == null) return;
            try { module.Dispose(); }
            catch (Exception ex) { m_Context?.Log("[RailEtaHotRuntime] " + action + " rejected module dispose failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private void DisposeRetired(IRailEtaHotModule module, string action)
        {
            try { module.Dispose(); }
            catch (Exception ex) { m_Context?.Log("[RailEtaHotRuntime] " + action + " retired module dispose failed: " + ex.GetType().Name + ": " + ex.Message); }
        }
#endif

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_Disposed, 1) != 0) return;
            CompleteLastHandle();
#if RT_DEBUG_TOOLS
            lock (m_Gate)
            {
                m_PendingSwap?.Next?.Module.Dispose();
                m_PendingSwap?.Completion?.TrySetResult(false);
                m_PendingSwap = null;
                m_Current?.Module.Dispose();
                if (!ReferenceEquals(m_Previous?.Module, m_Current?.Module)) m_Previous?.Module.Dispose();
                m_Current = null;
                m_Previous = null;
            }
#else
            m_Current?.Module.Dispose();
            m_Current = null;
#endif
        }
    }
}
