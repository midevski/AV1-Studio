using System.Windows;
using System.Windows.Threading;

namespace AV1Studio.Util;

/// <summary>Marshals collection changes to the UI thread (scalar property changes don't need it).</summary>
public static class Ui
{
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action, DispatcherPriority.Background);
    }

    /// <summary>Always queue (never run inline) so that items posted from any thread keep their order.</summary>
    public static void Enqueue(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) action();
        else d.BeginInvoke(action, DispatcherPriority.Background);
    }

    public static Task InvokeAsync(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) { action(); return Task.CompletedTask; }
        return d.InvokeAsync(action).Task;
    }

    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) return Task.FromResult(func());
        return d.InvokeAsync(func).Task;
    }
}
