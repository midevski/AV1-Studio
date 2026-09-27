using System.Windows;
using AV1Studio.Services;
using AV1Studio.ViewModels;

namespace AV1Studio.Views;

/// <summary>First-run system check: hardware, dependencies (with download/locate), encoder availability, modes.</summary>
public partial class FirstRunWindow : Window
{
    private readonly MainViewModel _vm;
    private CancellationTokenSource? _download;

    public FirstRunWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = vm;
        Closed += (_, _) =>
        {
            _download?.Cancel();
            _vm.MarkFirstRunDone();
        };
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        var t = _vm.Tools;
        bool needFfmpeg = t.FfmpegPath is null || t.FfprobePath is null || !t.FfmpegHasSvtAv1 || !t.FfmpegHasLibVmaf;
        bool needAbAv1 = t.AbAv1Path is null;
        if (!needFfmpeg && !needAbAv1) { StatusText.Text = "Everything required is already installed."; return; }
        if (needFfmpeg && MessageBox.Show(this,
                "Download the latest FFmpeg GPL build (libsvtav1, libvmaf, hardware encoders; about 150 MB) from github.com/BtbN/FFmpeg-Builds?\n\n" +
                "FFmpeg is licensed under the GPL. It is downloaded directly from its distributor and is not part of this application.",
                "Download FFmpeg", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            needFfmpeg = false;
        if (!needFfmpeg && !needAbAv1) return;

        DownloadButton.IsEnabled = false;
        _download = new CancellationTokenSource();
        var progress = new Progress<string>(s => StatusText.Text = s);
        try
        {
            if (needAbAv1) await ToolDownloader.DownloadAbAv1Async(progress, _download.Token);
            if (needFfmpeg) await ToolDownloader.DownloadFfmpegAsync(progress, _download.Token);
            StatusText.Text = "Downloaded. Checking tools…";
            await _vm.DetectToolsAsync();
            StatusText.Text = _vm.Tools.Ready ? "All required tools are ready." : "Some tools are still missing — see the list above.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Download cancelled."; }
        catch (Exception ex)
        {
            StatusText.Text = "Download failed: " + ex.Message + " — check the internet connection, or download the tools manually and use “locate”.";
            Log.Error("Tool download failed: " + ex.Message);
        }
        finally { DownloadButton.IsEnabled = true; }
    }

    private void OnLocate(object sender, RoutedEventArgs e)
    {
        if (_vm.OpenSettingsCommand.CanExecute(null)) _vm.OpenSettingsCommand.Execute(null);
    }

    private void OnFinish(object sender, RoutedEventArgs e) => Close();
}
