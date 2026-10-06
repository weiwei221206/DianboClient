using Microsoft.UI.Dispatching;

namespace Dianbo.App.Services;

public sealed class UiDispatcher
{
    private readonly DispatcherQueue _queue;

    public UiDispatcher(DispatcherQueue queue) => _queue = queue;

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess) action();
        else PostDeferred(action);
    }

    public void PostDeferred(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        _queue.TryEnqueue(priority, () =>
        {
            // DispatcherQueue callbacks are a native boundary: exceptions here
            // can fail-fast in CoreMessaging without reaching XAML's handler.
            try { action(); }
            catch (Exception error)
            {
                StartupLog.Write($"UI callback failed ({action.Method.Name}): {error}");
            }
        });
    }
}
