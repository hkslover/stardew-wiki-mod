using System.Collections.Concurrent;
using StardewModdingAPI;

namespace StardewWikiAgent.Threading;

/// <summary>Queues callbacks from background HTTP work back to the SMAPI update loop.</summary>
internal sealed class MainThreadDispatcher
{
    private readonly ConcurrentQueue<Action> callbacks = new();
    private readonly IMonitor monitor;

    public MainThreadDispatcher(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    public void Enqueue(Action callback) => this.callbacks.Enqueue(callback);

    public Task<T> InvokeAsync<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.Enqueue(() =>
        {
            try { completion.TrySetResult(callback()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    public void Drain(int maxCallbacks)
    {
        for (int i = 0; i < maxCallbacks && this.callbacks.TryDequeue(out Action? callback); i++)
        {
            try { callback(); }
            catch (Exception ex) { this.monitor.Log("Main-thread callback failed: " + ex, LogLevel.Error); }
        }
    }

    public void Clear()
    {
        while (this.callbacks.TryDequeue(out _)) { }
    }
}
