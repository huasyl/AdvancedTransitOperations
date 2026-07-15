#if RT_DEBUG_TOOLS
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RapidTransitMod.RailEta.Contracts;

namespace RapidTransitMod.RailEtaHost
{
    internal sealed class RailEtaHotRuntime : IDisposable
    {
        internal sealed class Selection
        {
            public Selection(IRailEtaPredictor predictor, string buildId, long generation)
            {
                Predictor = predictor;
                BuildId = buildId ?? string.Empty;
                Generation = generation;
            }

            public IRailEtaPredictor Predictor { get; }
            public string BuildId { get; }
            public long Generation { get; }
        }

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

        private readonly RailEtaWorker m_Worker;
        private readonly object m_Gate = new object();
        private Selection m_Current;
        private Selection m_Previous;
        private long m_NextGeneration;
        private int m_Busy;
        private int m_Disposed;
        private int m_LoadedAssemblies;
        private StatusSnapshot m_Status = new StatusSnapshot(false, string.Empty, 0, string.Empty, "idle", 0, string.Empty, string.Empty, 0);

        public RailEtaHotRuntime(RailEtaWorker worker) => m_Worker = worker ?? throw new ArgumentNullException(nameof(worker));

        public StatusSnapshot Status => Volatile.Read(ref m_Status);
        public bool IsDisposed => Volatile.Read(ref m_Disposed) != 0;
        public bool WorkerLost => m_Worker.WorkerLost;
        public Selection Current => Volatile.Read(ref m_Current);

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
                RailEtaHotSmokeResult smoke = RailEtaHotSmoke.Run(selection.Predictor);
                Finish("smoke", smoke.Success ? "completed" : "failed", smoke.Value, smoke.Summary, smoke.Error);
                if (smoke.Success) completion.TrySetResult(unchecked((uint)smoke.Value));
                else completion.TrySetException(new InvalidOperationException(smoke.Error));
            }))
            {
                Finish("smoke", "failed", 0, string.Empty, "Rail ETA worker queue is unavailable.");
                completion.TrySetResult(0);
            }
            return completion.Task;
        }

        public bool Rollback()
        {
            if (IsDisposed || Interlocked.CompareExchange(ref m_Busy, 1, 0) != 0) return false;
            bool changed;
            lock (m_Gate)
            {
                Selection previous = m_Previous;
                if (previous != null)
                {
                    Volatile.Write(ref m_Current, previous);
                    m_Previous = null;
                    changed = true;
                }
                else
                {
                    changed = Volatile.Read(ref m_Current) != null;
                    Volatile.Write(ref m_Current, null);
                }
            }
            Finish("rollback", changed ? "completed" : "empty", 0, string.Empty, changed ? string.Empty : "No hot Rail ETA predictor is loaded.");
            return changed;
        }

        private void ReloadOnWorker(string dllPath, TaskCompletionSource<bool> completion, string action = "reload")
        {
            try
            {
                Selection next = Load(dllPath);
                RailEtaHotSmokeResult smoke = RailEtaHotSmoke.Run(next.Predictor);
                if (!smoke.Success) throw new InvalidDataException(smoke.Error);
                lock (m_Gate)
                {
                    m_Previous = Volatile.Read(ref m_Current);
                    Volatile.Write(ref m_Current, next);
                }
                Finish(action, "completed", smoke.Value, smoke.Summary, string.Empty);
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
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
                if (type.IsAbstract || !typeof(IRailEtaPredictor).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null) continue;
                if (selected == null) selected = type;
                if (String.Equals(type.Namespace, "RapidTransitMod.RailEta.Hot", StringComparison.Ordinal)) { selected = type; break; }
            }
            if (selected == null) throw new InvalidDataException("RailEta.Hot DLL has no IRailEtaPredictor implementation.");
            var predictor = (IRailEtaPredictor)Activator.CreateInstance(selected);
            string buildId = Path.GetFileNameWithoutExtension(dllPath) + "@" + File.GetLastWriteTimeUtc(dllPath).Ticks;
            return new Selection(predictor, buildId, Interlocked.Increment(ref m_NextGeneration));
        }

        private bool TryBegin(string action)
        {
            if (IsDisposed) return false;
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

        public void Dispose() => Interlocked.Exchange(ref m_Disposed, 1);
    }
}
#endif
