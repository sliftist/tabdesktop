using System.Collections.Concurrent;
using System.Diagnostics;

namespace TabDesktop;

public sealed record WorkerStatus(uint Pid, string State, string LastPoll, double? LastPollMs, double? BusyForMs);

// Queries like PrintWindow block until the target process pumps messages, so a hung process freezes whichever thread asked — permanently, with no way to cancel. Each target process therefore gets its own worker thread, and all direct-ask calls for that process run only there: a hung process kills only its own worker, IsHung starts reporting true so we never hand it work again, and every other process's queries keep flowing. Dead workers are background threads, so they can't keep the app alive at exit.
public sealed class ProcessWorkerPool
{
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<uint, ProcessWorker> workers = new();
    // Completed polls only — a stalled worker never finishes its task, so stalled threads are naturally excluded from the rates.
    private readonly ConcurrentQueue<(long Timestamp, long DurationMs)> completedPolls = new();

    public bool TrySchedule(uint pid, string description, Action work)
    {
        ProcessWorker worker = workers.GetOrAdd(pid, p => new ProcessWorker(p, OnPollCompleted));
        if (worker.IsHung)
        {
            return false;
        }
        return worker.TryEnqueue(description, work);
    }

    public bool IsHung(uint pid)
    {
        return workers.TryGetValue(pid, out ProcessWorker? worker) && worker.IsHung;
    }

    public List<WorkerStatus> SnapshotStatus()
    {
        return workers.Select(pair => pair.Value.GetStatus(pair.Key)).OrderBy(status => status.Pid).ToList();
    }

    public (double PollsPerSecond, double PollMsPerSecond) GetRecentPollRate()
    {
        while (completedPolls.TryPeek(out (long Timestamp, long DurationMs) head) && Stopwatch.GetElapsedTime(head.Timestamp) > RateWindow)
        {
            completedPolls.TryDequeue(out _);
        }
        (long, long DurationMs)[] recent = completedPolls.ToArray();
        double seconds = RateWindow.TotalSeconds;
        return (recent.Length / seconds, recent.Sum(p => p.DurationMs) / seconds);
    }

    private void OnPollCompleted(long durationMs)
    {
        completedPolls.Enqueue((Stopwatch.GetTimestamp(), durationMs));
    }

    public void PruneExcept(IReadOnlySet<uint> livePids)
    {
        foreach (uint pid in workers.Keys)
        {
            if (!livePids.Contains(pid) && workers.TryRemove(pid, out ProcessWorker? removed))
            {
                removed.Shutdown();
            }
        }
    }

    private sealed class ProcessWorker
    {
        // Capacity 1 means at most one task executing plus one waiting; if the worker can't keep up (or is hung), further scans are simply skipped for this process instead of piling up.
        private readonly BlockingCollection<(string Description, Action Work)> queue = new(boundedCapacity: 1);
        private long busySince;
        private volatile string currentDescription = "";
        private volatile string lastDescription = "";
        private long lastDurationMs = -1;
        private readonly Action<long> onPollCompleted;

        public ProcessWorker(uint pid, Action<long> onPollCompleted)
        {
            this.onPollCompleted = onPollCompleted;
            var thread = new Thread(Run) { IsBackground = true, Name = $"window-query-{pid}" };
            thread.Start();
        }

        public bool IsHung
        {
            get
            {
                long since = Volatile.Read(ref busySince);
                return since != 0 && Stopwatch.GetElapsedTime(since) > HangTimeout;
            }
        }

        public WorkerStatus GetStatus(uint pid)
        {
            long since = Volatile.Read(ref busySince);
            if (since != 0)
            {
                TimeSpan busyFor = Stopwatch.GetElapsedTime(since);
                string state = busyFor > HangTimeout ? "Stalled" : "Busy";
                return new WorkerStatus(pid, state, currentDescription, null, busyFor.TotalMilliseconds);
            }
            long lastMs = Volatile.Read(ref lastDurationMs);
            return new WorkerStatus(pid, "Idle", lastDescription, lastMs >= 0 ? lastMs : null, null);
        }

        public bool TryEnqueue(string description, Action work)
        {
            try
            {
                return queue.TryAdd((description, work));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Shutdown()
        {
            queue.CompleteAdding();
        }

        private void Run()
        {
            foreach ((string description, Action work) in queue.GetConsumingEnumerable())
            {
                currentDescription = description;
                long start = Stopwatch.GetTimestamp();
                Volatile.Write(ref busySince, start);
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                }
                Volatile.Write(ref busySince, 0);
                long durationMs = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Volatile.Write(ref lastDurationMs, durationMs);
                lastDescription = description;
                onPollCompleted(durationMs);
            }
        }
    }
}
