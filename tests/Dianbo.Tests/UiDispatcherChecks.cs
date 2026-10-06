using Dianbo.App.Services;
using Microsoft.UI.Dispatching;

// Exercise the real dispatcher wrapper without loading the native XAML runtime.
namespace Microsoft.UI.Dispatching
{
    public enum DispatcherQueuePriority { Low, Normal }
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess { get; set; }
        public Queue<Action> Pending { get; } = new();
        public bool TryEnqueue(DispatcherQueuePriority priority, Action action)
        {
            Pending.Enqueue(action);
            return true;
        }
    }
}

namespace Dianbo.App
{
    internal static class StartupLog
    {
        public static List<string> Messages { get; } = [];
        public static void Write(string message) => Messages.Add(message);
    }
}

namespace Dianbo.Tests
{
    public static class UiDispatcherChecks
    {
        public static Task RunAsync()
        {
            var queue = new DispatcherQueue { HasThreadAccess = true };
            var dispatcher = new UiDispatcher(queue);
            var ran = false;
            dispatcher.PostDeferred(() => ran = true);
            if (ran || queue.Pending.Count != 1)
                throw new Exception("延后滚动不能在布局回调中同步执行");
            queue.Pending.Dequeue()();
            if (!ran) throw new Exception("延后更新未执行");

            queue.HasThreadAccess = false;
            dispatcher.Post(() => throw new ArgumentException("invalid scroll target"));
            // A queued native callback must return normally even if UI work fails.
            queue.Pending.Dequeue()();
            if (!Dianbo.App.StartupLog.Messages.Any(m => m.Contains("invalid scroll target")))
                throw new Exception("回调异常缺少诊断记录");
            ran = false;
            dispatcher.Post(() => ran = true);
            queue.Pending.Dequeue()();
            if (!ran) throw new Exception("异常后界面队列无法继续处理更新");
            return Task.CompletedTask;
        }
    }
}
