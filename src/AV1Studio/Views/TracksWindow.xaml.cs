using System.Windows;
using AV1Studio.Models;

namespace AV1Studio.Views;

public sealed class TrackChoice
{
    public int TypeIndex { get; init; }
    public string Label { get; init; } = "";
    public bool Keep { get; set; }
}

public partial class TracksWindow : Window
{
    private readonly QueueItem _item;
    private readonly List<TrackChoice> _audio;
    private readonly List<TrackChoice> _subs;

    public TracksWindow(QueueItem item)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        _item = item;
        FileText.Text = item.FileName;
        var probe = item.Probe!;
        _audio = probe.Audio.Select(a => new TrackChoice
        {
            TypeIndex = a.TypeIndex, Label = a.Describe(),
            Keep = item.AudioSelection?.Contains(a.TypeIndex) ?? true,
        }).ToList();
        _subs = probe.Subtitles.Select(s => new TrackChoice
        {
            TypeIndex = s.TypeIndex, Label = s.Describe() + (s.IsBitmapSubtitle ? " (image)" : ""),
            Keep = item.SubtitleSelection?.Contains(s.TypeIndex) ?? true,
        }).ToList();
        AudioList.ItemsSource = _audio;
        SubList.ItemsSource = _subs;
        if (_audio.Count == 0 && _subs.Count == 0) NoneText.Text = "This file has no audio or subtitle tracks.";
        else if (_audio.Count == 0) NoneText.Text = "No audio tracks.";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_audio.Count > 0 && !_audio.Any(a => a.Keep) &&
            MessageBox.Show(this, "No audio track is selected — the output will have no sound. Continue?", "No audio",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _item.AudioSelection = _audio.All(a => a.Keep) ? null : _audio.Where(a => a.Keep).Select(a => a.TypeIndex).ToList();
        _item.SubtitleSelection = _subs.All(s => s.Keep) ? null : _subs.Where(s => s.Keep).Select(s => s.TypeIndex).ToList();
        _item.RefreshMediaSummary();
        DialogResult = true;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _item.AudioSelection = null;
        _item.SubtitleSelection = null;
        _item.RefreshMediaSummary();
        DialogResult = true;
    }
}
