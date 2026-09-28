using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AV1Studio.Models;
using AV1Studio.Services;
using AV1Studio.ViewModels;

namespace AV1Studio.Views;

public sealed record Option<T>(T Value, string Label);

/// <summary>Settings window. Edits a working copy of the settings; nothing changes until Save.</summary>
public partial class SettingsWindow : Window
{
    private AppSettings? _edit;
    private CancellationTokenSource? _download;
    private readonly MainViewModel _vm;

    private MainViewModel? Vm => _vm;

    /// <summary>The saved settings (null when cancelled).</summary>
    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings working, MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = vm;
        Closed += (_, _) => _download?.Cancel();

        var presets = new List<Option<int?>> { new(null, "ab-av1 default (preset 8)") };
        presets.AddRange(Enumerable.Range(0, 14).Select(i =>
            new Option<int?>(i, $"Preset {i} — {(i == 0 ? "research-grade, extremely slow" : MainViewModel.PresetDescription(i))}")));
        PresetCombo.ItemsSource = presets;
        PixCombo.ItemsSource = new List<Option<string>>
        {
            new("", "Default — 10-bit yuv420p10le (ab-av1 default)"),
            new("yuv420p10le", "10-bit 4:2:0 (yuv420p10le)"),
            new("yuv420p", "8-bit 4:2:0 (yuv420p)"),
            new("yuv422p10le", "10-bit 4:2:2 (yuv422p10le)"),
            new("yuv444p10le", "10-bit 4:4:4 (yuv444p10le)"),
        };
        ResCombo.ItemsSource = new List<Option<int>>
        {
            new(0, "Same as source (default)"), new(2160, "Max 2160p (4K)"), new(1440, "Max 1440p"),
            new(1080, "Max 1080p"), new(720, "Max 720p"), new(480, "Max 480p"),
        };
        DataFolderRun.Text = AppPaths.Root;
        CpuDetected.Text = $"This PC: {Environment.ProcessorCount} logical processors.";
        Load(working);
    }

    private void Load(AppSettings working)
    {
        if (Vm is null) return;
        _edit = working;
        Tabs.DataContext = _edit;
        ErrorText.Text = "";
        var tools = Vm.Tools;
        HwEncoderCombo.ItemsSource = ToolLocator.SupportedHardwareEncoders
            .Select(x => new Option<string>(x.Id, $"{x.Name} — {(tools.HardwareAvailable(x.Id) ? "available" : "not available here")}"))
            .ToList();
        HwPresetCombo.ItemsSource = HwPresets(_edit.HardwareEncoder);
        HwCheck.IsEnabled = tools.HardwareAv1Encoders.Count > 0 || _edit.HardwareEncoding;
        HwStatusText.Text = tools.HardwareAv1Encoders.Count > 0
            ? $"Working GPU encoder(s) for AB-AV1: {string.Join(", ", tools.HardwareAv1Encoders)}. GPU encoding is faster; files are usually larger than SVT-AV1 at the same VMAF."
            : "No GPU AV1 encoder usable by AB-AV1 on this PC — SVT-AV1 on the CPU is used.";
        ShowTools(tools);
        UpdateCpuPlans();
    }

    private void OnCpuChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // Switching the CPU usage mode selects the matching priority (it can still be changed afterwards).
        if (e.OriginalSource is ComboBox { Tag: "mode", DataContext: CpuProfile profile } && e is SelectionChangedEventArgs { AddedItems.Count: > 0 })
        {
            profile.Priority = ResourcePlanner.SuggestedPriority(profile.Mode);
            if (((ComboBox)e.OriginalSource).Parent is DockPanel row && row.Parent is StackPanel panel)
                foreach (var cb in panel.Children.OfType<DockPanel>().SelectMany(d => d.Children.OfType<ComboBox>()))
                    cb.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.UpdateTarget();
        }
        Dispatcher.BeginInvoke(UpdateCpuPlans, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Shows exactly what each CPU profile resolves to on this PC.</summary>
    private void UpdateCpuPlans()
    {
        if (_edit is null) return;
        if (Keyboard.FocusedElement is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        SearchPlanText.Text = "→ " + ResourcePlanner.Plan(_edit.SearchCpu).Description;
        EncodePlanText.Text = "→ " + ResourcePlanner.Plan(_edit.EncodeCpu).Description;
    }

    private void OnFirstRun(object sender, RoutedEventArgs e) => Vm?.FirstRunDialog?.Invoke();

    private void ShowTools(ToolStatus t)
    {
        var lines = new List<string>
        {
            $"ab-av1   {t.AbAv1Version ?? "—"}   {t.AbAv1Path ?? "NOT FOUND"}",
            $"ffmpeg   {t.FfmpegVersion ?? "—"}   {t.FfmpegPath ?? "NOT FOUND"}",
            $"ffprobe  {t.FfprobePath ?? "NOT FOUND"}",
            $"libsvtav1: {(t.FfmpegHasSvtAv1 ? "yes" : "NO")}   SVT-AV1: {t.SvtAv1Version ?? "?"}   libvmaf: {(t.FfmpegHasLibVmaf ? "yes" : "NO")}   zscale: {(t.FfmpegHasZscale ? "yes" : "no")}",
            $"Manual AV1 encoders: {(t.ManualEncoders.Count > 0 ? string.Join(", ", t.ManualEncoders) : "none")}",
            $"ab-av1 features: json={(t.CrfSearchJson ? "yes" : "no")} verify={(t.EncodeVerify ? "yes" : "no")} fail-fast={(t.EncodeFailFast ? "yes" : "no")}",
        };
        lines.AddRange(t.Problems.Select(p => "✘ " + p));
        lines.AddRange(t.Notes.Select(n => "• " + n));
        ToolsStatusText.Text = string.Join("\n", lines);
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        TabItem? first = null;
        foreach (TabItem tab in Tabs.Items)
        {
            bool match = q.Length == 0
                || (tab.Header as string ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (tab.Tag as string ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
            tab.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match) first ??= tab;
        }
        if (Tabs.SelectedItem is TabItem sel && sel.Visibility != Visibility.Visible && first != null) Tabs.SelectedItem = first;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_edit is null || Vm is null) return;
        if (Keyboard.FocusedElement is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (_edit.HardwareEncoding && !Vm.Tools.HardwareAvailable(_edit.HardwareEncoder))
        {
            ErrorText.Text = $"{_edit.HardwareEncoder} does not work on this PC — choose an available GPU encoder or turn GPU encoding off (AB-AV1).";
            return;
        }
        var errors = AbAv1Commands.Validate(_edit);
        foreach (var (name, p) in new[] { ("CRF search", _edit.SearchCpu), ("Final encode", _edit.EncodeCpu) })
        {
            if (p.Mode != CpuUsageMode.Custom) continue;
            if (p.Threads < 0) errors.Add($"{name}: the number of processors cannot be negative.");
            if (!string.IsNullOrWhiteSpace(p.Affinity) && !ResourcePlanner.TryParseAffinity(p.Affinity, Environment.ProcessorCount, out _))
                errors.Add($"{name}: \"{p.Affinity}\" is not a valid processor list (use e.g. 0-7,12; this PC has processors 0–{Environment.ProcessorCount - 1}).");
        }
        if (errors.Count > 0) { ErrorText.Text = string.Join("  ", errors); return; }
        Result = _edit;
        DialogResult = true;
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => DialogResult = false;

    private static List<Option<string>> HwPresets(string encoder) =>
        AbAv1Commands.HardwarePresets(encoder).Select(p => new Option<string>(p.Value, p.Label)).ToList();

    private void OnHwEncoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _edit is null) return;
        var presets = HwPresets(_edit.HardwareEncoder);
        if (!presets.Any(p => p.Value == _edit.HardwarePreset)) _edit.HardwarePreset = "";
        HwPresetCombo.ItemsSource = presets;
        HwPresetCombo.SelectedValue = _edit.HardwarePreset;
    }

    private void OnOverwriteChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _edit is null) return;
        var ok = MessageBox.Show(Window.GetWindow(this)!,
            "Existing OUTPUT files with the same name will be replaced by new encodes.\n\nSource files are never overwritten by this option.\n\nAllow overwriting existing output files?",
            "Overwrite existing files?", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!ok) { _edit.Collision = CollisionPolicy.ReuseIfValid; Tabs.DataContext = null; Tabs.DataContext = _edit; }
    }

    private void OnBrowseTool(object sender, RoutedEventArgs e)
    {
        if (_edit is null) return;
        var which = (string)((Button)sender).Tag;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = $"{which}.exe|{which}.exe|Programs|*.exe", Title = $"Locate {which}.exe" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        switch (which)
        {
            case "ab-av1": _edit.AbAv1Path = dlg.FileName; AbAv1Box.Text = dlg.FileName; break;
            case "ffmpeg":
                _edit.FfmpegPath = dlg.FileName; FfmpegBox.Text = dlg.FileName;
                var probe = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, "ffprobe.exe");
                if (string.IsNullOrWhiteSpace(_edit.FfprobePath) && File.Exists(probe)) { _edit.FfprobePath = probe; FfprobeBox.Text = probe; }
                break;
            case "ffprobe": _edit.FfprobePath = dlg.FileName; FfprobeBox.Text = dlg.FileName; break;
        }
        HintText.Text = "Press Save to use the new tool paths.";
    }

    private async void OnRedetect(object sender, RoutedEventArgs e)
    {
        if (_edit is null) return;
        ToolsStatusText.Text = "Detecting…";
        ShowTools(await ToolLocator.DetectAsync(_edit));
    }

    private async void OnDownloadAbAv1(object sender, RoutedEventArgs e) =>
        await RunDownload(async (p, ct) =>
        {
            await ToolDownloader.DownloadAbAv1Async(p, ct);
            if (_edit != null) { _edit.AbAv1Path = ""; AbAv1Box.Text = ""; }
        });

    private async void OnDownloadFfmpeg(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!,
                "Download the latest FFmpeg GPL build (libsvtav1, libvmaf, hardware encoders) from github.com/BtbN/FFmpeg-Builds?\n\n" +
                "FFmpeg is licensed under the GPL. It is downloaded directly from its distributor and is not part of this application.",
                "Download FFmpeg", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunDownload(async (p, ct) =>
        {
            await ToolDownloader.DownloadFfmpegAsync(p, ct);
            if (_edit != null) { _edit.FfmpegPath = ""; _edit.FfprobePath = ""; FfmpegBox.Text = ""; FfprobeBox.Text = ""; }
        });
    }

    private async Task RunDownload(Func<IProgress<string>, CancellationToken, Task> action)
    {
        DlAbAv1.IsEnabled = DlFfmpeg.IsEnabled = false;
        _download = new CancellationTokenSource();
        var progress = new Progress<string>(s => DownloadText.Text = s);
        try
        {
            await action(progress, _download.Token);
            DownloadText.Text = "Done. Detecting tools…";
            if (Vm != null) await Vm.DetectToolsAsync();
            if (Vm != null) ShowTools(Vm.Tools);
            DownloadText.Text = "Done — tools detected.";
        }
        catch (OperationCanceledException) { DownloadText.Text = "Cancelled."; }
        catch (Exception ex)
        {
            DownloadText.Text = "Download failed: " + ex.Message;
            Log.Error("Tool download failed: " + ex.Message);
        }
        finally { DlAbAv1.IsEnabled = DlFfmpeg.IsEnabled = true; }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", [AppPaths.Root]); } catch { }
    }

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (MessageBox.Show(this,
                $"Clear all saved CRF search results?\n\nThis also deletes ab-av1's sample cache ({Util.Fmt.Bytes(AbAv1Cache.SizeBytes())}). " +
                "Queued AB-AV1 files will run a new CRF search. Your videos are not affected.",
                "Clear analysis cache", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var summary = Vm.ClearAnalysisCache();
        MessageBox.Show(this, summary, "Clear analysis cache", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
