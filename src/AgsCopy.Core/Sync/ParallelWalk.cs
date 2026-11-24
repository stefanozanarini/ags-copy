using System.Runtime.ExceptionServices;

namespace AgsCopy.Core.Sync;

/// <summary>
/// Reads a tree on several threads: over a share each folder costs a round trip, so many folders in
/// flight at once is what makes a walk fast. Deepest folders first, so a run copying while it walks
/// finishes folders instead of opening them all.
/// </summary>
public static class ParallelWalk
{
    /// <param name="open">State of one thread (its own connections), disposed when the walk ends.</param>
    /// <param name="visit">Reads one item; the callback queues the items found below it.</param>
    /// <remarks>The first exception of a visit stops the walk and is thrown here.</remarks>
    public static void Run<TState, T>(T root, int threads, Func<TState> open, Action<TState, T, Action<T>> visit, CancellationToken ct)
        where TState : IDisposable
    {
        var pending = new Stack<T>();
        pending.Push(root);
        var busy = 0;
        Exception? failure = null;
        var gate = new object();

        void Add(T item)
        {
            lock (gate)
            {
                pending.Push(item);
                Monitor.Pulse(gate);
            }
        }

        void Worker()
        {
            using var state = open();
            while (true)
            {
                T item;
                lock (gate)
                {
                    while (pending.Count == 0 && busy > 0 && failure == null && !ct.IsCancellationRequested) Monitor.Wait(gate, 250);
                    if (failure != null || pending.Count == 0 || ct.IsCancellationRequested)
                    {
                        Monitor.PulseAll(gate);
                        return;
                    }
                    item = pending.Pop();
                    busy++;
                }
                try
                {
                    visit(state, item, Add);
                }
                catch (Exception e)
                {
                    lock (gate) failure ??= e;
                }
                finally
                {
                    lock (gate)
                    {
                        busy--;
                        Monitor.PulseAll(gate);
                    }
                }
            }
        }

        var workers = Enumerable.Range(0, Math.Max(1, threads))
            .Select(_ => Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException e)
        {
            // A thread that could not open its connections.
            lock (gate) failure ??= e.InnerExceptions[0];
        }
        ct.ThrowIfCancellationRequested();
        if (failure != null) ExceptionDispatchInfo.Throw(failure);
    }
}
