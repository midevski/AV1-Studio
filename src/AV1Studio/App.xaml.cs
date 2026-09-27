using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AV1Studio.Services;
using AV1Studio.Views;

namespace AV1Studio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Hosted by another executable (UI tests / snapshot tools): resources only, no window.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly) return;
        AppPaths.MigrateLegacyFolder();
        AppPaths.EnsureCreated();
        Log.Start();

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            Log.Error($"Fatal: {a.ExceptionObject}");
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error($"Background error: {a.Exception.GetBaseException().Message}");
            a.SetObserved();
        };

        // One taskbar identity (icon, grouping, pinning) for every window of the app.
        Native.Win32.SetAppUserModelId(AppInfo.AppUserModelId);
        // Tooltips: short delay, long enough to read.
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(500));
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(30000));

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A UI bug must never take the queue down silently; running child processes are bound to a
        // job object and are terminated if the app really exits, leaving sources untouched.
        Log.Error($"Unexpected UI error: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
