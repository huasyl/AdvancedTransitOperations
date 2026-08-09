using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace RapidTransitMod.RailEtaHost
{
    public sealed class RailEtaWorker : IDisposable
    {
        private const int MaxQueue = 8;
        private readonly BlockingCollection<Action> m_Queue = new BlockingCollection<Action>(MaxQueue);
        private readonly Thread m_Thread;
        private int m_Lost;
        private Exception m_LastFailure;
        private long m_Heartbeat;
        private long m_ActiveSinceTicks;
        private long m_LastObservedHeartbeat;
        private long m_LastProgressTicks;
        private int m_Active;

        public RailEtaWorker()
        {
            m_Thread = new Thread(Run) { IsBackground = true, Name = "RT Rail ETA Worker" };
            m_Thread.Start();
        }

        public bool TryEnqueue(Action action)
        {
            if (action == null || Volatile.Read(ref m_Lost) != 0 || m_Queue.IsAddingCompleted) return false;
            try { return m_Queue.TryAdd(action); }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public bool WorkerLost => Volatile.Read(ref m_Lost) != 0;
        public Exception LastFailure => Volatile.Read(ref m_LastFailure);
        public bool IsActive => Volatile.Read(ref m_Active) != 0;

        public bool TryMarkLostIfStalled(long nowTicks, long budgetTicks)
        {
            if (WorkerLost || !IsActive) return false;
            long heartbeat = Interlocked.Read(ref m_Heartbeat);
            long observed = Interlocked.Read(ref m_LastObservedHeartbeat);
            if (heartbeat != observed)
            {
                Interlocked.Exchange(ref m_LastObservedHeartbeat, heartbeat);
                Interlocked.Exchange(ref m_LastProgressTicks, nowTicks);
                return false;
            }
            long progress = Interlocked.Read(ref m_LastProgressTicks);
            if (progress == 0) progress = Interlocked.Read(ref m_ActiveSinceTicks);
            if (progress == 0 || nowTicks - progress <= budgetTicks) return false;
            if (Interlocked.CompareExchange(ref m_Lost, 1, 0) != 0) return false;
            Volatile.Write(ref m_LastFailure, new TimeoutException("Rail ETA worker watchdog budget exceeded."));
            return true;
        }

        public void MarkLost(Exception failure)
        {
            if (failure != null) Volatile.Write(ref m_LastFailure, failure);
            Interlocked.Exchange(ref m_Lost, 1);
        }

        private void Run()
        {
            try
            {
                foreach (Action action in m_Queue.GetConsumingEnumerable())
                {
                    long start = Stopwatch.GetTimestamp();
                    Volatile.Write(ref m_Active, 1);
                    Interlocked.Exchange(ref m_ActiveSinceTicks, start);
                    Interlocked.Exchange(ref m_LastProgressTicks, start);
                    long heartbeat = Interlocked.Increment(ref m_Heartbeat);
                    Interlocked.Exchange(ref m_LastObservedHeartbeat, heartbeat);
                    try { action(); }
                    catch (Exception ex) { Volatile.Write(ref m_LastFailure, ex); Interlocked.Exchange(ref m_Lost, 1); break; }
                    finally
                    {
                        Interlocked.Increment(ref m_Heartbeat);
                        Volatile.Write(ref m_Active, 0);
                        Interlocked.Exchange(ref m_LastProgressTicks, Stopwatch.GetTimestamp());
                    }
                }
            }
            catch (Exception ex) { Volatile.Write(ref m_LastFailure, ex); Interlocked.Exchange(ref m_Lost, 1); }
        }

        public void Dispose() => m_Queue.CompleteAdding();
    }
}
