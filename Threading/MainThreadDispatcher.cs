using System.Collections.Concurrent;
using StardewModdingAPI;

namespace StardewWikiAgent.Threading;

/// <summary>Queues callbacks from background HTTP work back to the SMAPI update loop.</summary>
internal sealed class MainThreadDispatcher
{
    private readonly ConcurrentQueue<QueueItem> callbacks = new();
    private readonly IMonitor monitor;

    public MainThreadDispatcher(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    public void Enqueue(Action callback) => this.callbacks.Enqueue(new QueueItem(callback, static () => { }));

    public Task<T> InvokeAsync<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.callbacks.Enqueue(new QueueItem(
            () =>
            {
                try { completion.TrySetResult(callback()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            },
            () => completion.TrySetCanceled()
        ));
        return completion.Task;
    }

    public void Drain(int maxCallbacks)
    {
        for (int i = 0; i < maxCallbacks && this.callbacks.TryDequeue(out QueueItem? callback); i++)
        {
            try { callback.Run(); }
            catch (Exception ex) { this.monitor.Log("Main-thread callback failed: " + ex, LogLevel.Error); }
        }
    }

    public void Clear()
    {
        while (this.callbacks.TryDequeue(out QueueItem? callback))
        {
            try { callback.Cancel(); }
            catch (Exception ex) { this.monitor.Log("Failed to cancel a queued main-thread callback: " + ex, LogLevel.Error); }
        }
    }

    private sealed record QueueItem(Action Run, Action Cancel);
}
