using System.Windows;
using AV1Studio.Models;
using AV1Studio.ViewModels;

namespace AV1Studio.Views;

/// <summary>Encodes a short sample of one queued file with the mode and settings that file was queued with.</summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow(MainViewModel vm, QueueItem item, EncodeMode mode)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = vm;
        vm.EncodeTarget = item;
        SettingsText.Text = mode == EncodeMode.Manual
            ? $"MANUAL AV1 · {item.EncoderText} · {item.TargetText} · preset {item.PresetText}"
            : $"AB-AV1 · {item.EncoderText} · target {item.TargetText} · preset {item.PresetText} — the preview runs a CRF search on samples first";
        Closed += (_, _) => { if (vm.CancelPreviewCommand.CanExecute(null)) vm.CancelPreviewCommand.Execute(null); };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
