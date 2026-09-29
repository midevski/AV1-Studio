using System.ComponentModel;
using System.Windows.Data;
using AV1Studio.Models;
using AV1Studio.Mvvm;
using AV1Studio.Services;
using AV1Studio.Util;

namespace AV1Studio.ViewModels;

public sealed partial class MainViewModel
{
    public RelayCommand PauseResumeCommand { get; private set; } = null!;
    public RelayCommand CancelSelectedCommand { get; private set; } = null!;
    public RelayCommand SkipSelectedCommand { get; private set; } = null!;
    public RelayCommand MoveUpCommand { get; private set; } = null!;
    public RelayCommand MoveDownCommand { get; private set; } = null!;
    public RelayCommand MoveTopCommand { get; private set; } = null!;
    public RelayCommand MoveBottomCommand { get; private set; } = null!;
    public RelayCommand SetModeAbAv1Command { get; private set; } = null!;
    public RelayCommand SetModeManualCommand { get; private set; } = null!;

    private void InitQueueCommands()
    {
        PauseResumeCommand = new RelayCommand(PauseResume, () => IsRunning);
        CancelSelectedCommand = new RelayCommand(() => StopSelected(ItemStatus.Cancelled), () => SelectedItems.Count > 0);
        SkipSelectedCommand = new RelayCommand(() => StopSelected(ItemStatus.Skipped), () => SelectedItems.Count > 0);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => SelectedItems.Count > 0);
        MoveDownCommand = new RelayCommand(() => Move(+1), () => SelectedItems.Count > 0);
        MoveTopCommand = new RelayCommand(() => Move(int.MinValue), () => SelectedItems.Count > 0);
        MoveBottomCommand = new RelayCommand(() => Move(int.MaxValue), () => SelectedItems.Count > 0);
        SetModeAbAv1Command = new RelayCommand(() => SetMode(EncodeMode.AbAv1), () => SelectedItems.Any(i => !i.IsBusy));
        SetModeManualCommand = new RelayCommand(() => SetMode(EncodeMode.Manual), () => SelectedItems.Any(i => !i.IsBusy));
    }

    // ---------------------------------------------------------------- pause / resume (suspend processes)

    private bool _isSuspended;
    /// <summary>Running encodes are frozen (processes suspended, nothing killed, no progress lost).</summary>
    public bool IsSuspended
    {
        get => _isSuspended;
        private set { if (Set(ref _isSuspended, value)) Notify(nameof(PauseResumeText), nameof(RunStateText)); }
    }

    public string PauseResumeText => IsSuspended ? "Resume" : "Pause";

    private void PauseResume()
    {
        var running = Items.Where(i => i.ActiveProcess != null).ToList();
        if (!IsSuspended)
        {
            foreach (var i in running)
            {
                try { i.ActiveProcess!.Job?.Suspend(); i.IsPaused = true; }
                catch (Exception ex) { Log.Warn($"Could not pause: {ex.Message}", i.FileName); }
            }
            IsSuspended = true;
            Log.Info($"Paused {running.Count} running job(s) — resume to continue exactly where they stopped");
        }
        else
        {
            foreach (var i in Items.Where(i => i.IsPaused))
            {
                try { i.ActiveProcess?.Job?.Resume(); } catch { }
                i.IsPaused = false;
            }
            IsSuspended = false;
            Log.Info("Resumed");
        }
    }

    // ---------------------------------------------------------------- cancel / skip

    private void StopSelected(ItemStatus asStatus)
    {
        string verb = asStatus == ItemStatus.Skipped ? "Skipped" : "Cancelled";
        foreach (var i in SelectedItems.ToList())
        {
            if (i.IsBusy)
            {
                // Running: kill its processes; the pipeline removes the partial output and keeps the source.
                if (_processor?.StopItem(i, asStatus) == true) Log.Warn($"{verb} by user (stopping the running job)", i.FileName);
            }
            else if (!i.Status.IsFinal() && i.Status != ItemStatus.Failed)
            {
                i.Status = asStatus;
                i.StatusDetail = $"{verb} by user";
                Log.Info($"{verb} by user", i.FileName);
            }
        }
        _dirty = true;
        RefreshStorage();
    }

    // ---------------------------------------------------------------- reorder

    private void Move(int delta)
    {
        var selected = SelectedItems.OrderBy(i => Items.IndexOf(i)).ToList();
        if (selected.Count == 0) return;
        if (delta == int.MinValue)
        {
            for (int k = 0; k < selected.Count; k++) Items.Move(Items.IndexOf(selected[k]), k);
        }
        else if (delta == int.MaxValue)
        {
            foreach (var i in selected) Items.Move(Items.IndexOf(i), Items.Count - 1);
        }
        else
        {
            var ordered = delta < 0 ? selected : Enumerable.Reverse(selected).ToList();
            foreach (var i in ordered)
            {
                int idx = Items.IndexOf(i), to = idx + delta;
                if (to < 0 || to >= Items.Count || selected.Contains(Items[to])) continue;
                Items.Move(idx, to);
            }
        }
        _dirty = true;
    }

    // ---------------------------------------------------------------- mode switching

    private void SetMode(EncodeMode mode)
    {
        foreach (var i in SelectedItems.Where(i => !i.IsBusy && i.Mode != mode))
        {
            i.Mode = mode;
            if (i.Status is ItemStatus.CrfFound or ItemStatus.Ready && i.EffectiveCrf is null) i.Status = ItemStatus.Waiting;
            if (i.Status is ItemStatus.CrfFound && mode == EncodeMode.Manual) i.Status = ItemStatus.Ready;
            Log.Info($"Switched to {(mode == EncodeMode.AbAv1 ? "AB-AV1" : "Manual AV1")} mode", i.FileName);
        }
        _dirty = true;
        RefreshPlanned();
        Notify(nameof(SelectedPreviewCommands), nameof(CommandPreview));
    }

    // ---------------------------------------------------------------- planned encoder / CRF / preset columns

    /// <summary>For files not yet encoded, show what their own (queued) settings will use.</summary>
    private void RefreshPlanned()
    {
        foreach (var i in Items)
        {
            if (i.Status.IsActive() || i.Status.IsDone()) continue;
            var s = JobSettings(i);
            i.OutputContainer = i.Kind == ItemKind.Copy ? "" : OutputContainers.Resolve(s.Container, i.SourcePath).Label;
            if (i.Kind == ItemKind.Copy)
            {
                i.EncoderText = "Copy (unchanged)";
                i.PresetText = "";
                i.TargetText = "";
            }
            else if (i.Mode == EncodeMode.Manual)
            {
                var spec = ManualCommands.Spec(s.Manual.Encoder);
                i.EncoderText = spec.Name.Split(" — ")[0];
                i.PresetText = s.Manual.Preset;
                i.ManualQuality = s.Manual.Quality;
                i.TargetText = $"{spec.QualityName} {Fmt.Num(i.CrfOverride ?? s.Manual.Quality)}";
            }
            else
            {
                i.EncoderText = AbAv1Commands.EncoderDescription(s);
                i.PresetText = s.HardwareEncoding
                    ? (string.IsNullOrWhiteSpace(s.HardwarePreset) ? "default" : s.HardwarePreset)
                    : s.Preset?.ToString() ?? "ab-av1 default";
                i.TargetText = $"VMAF {Fmt.Num(s.TargetVmaf)}";
            }
        }
    }

    // =================================================================== sorting (view only)

    private string? _sortHeader;
    private ListSortDirection? _sortDirection;

    /// <summary>"Sorted by Size ↓", or empty when the queue is shown in processing order.</summary>
    public string SortText => _sortHeader is null ? "" :
        $"Sorted by {_sortHeader} {(_sortDirection == ListSortDirection.Descending ? "↓" : "↑")} — files are still processed in queue order";

    public bool IsSorted => _sortHeader != null;

    public RelayCommand ResetSortCommand => _resetSort ??= new RelayCommand(() => SortQueue(null, null, null));
    private RelayCommand? _resetSort;

    /// <summary>Raised when the sort is reset from the view model, so the grid can clear its header arrows.</summary>
    public event Action? SortReset;

    /// <summary>
    /// Sorts the queue view by a property of <see cref="QueueItem"/>, inside each folder group. Only the view is
    /// sorted: the queue itself (and the order files are processed in) is unchanged. One comparer with cached
    /// property getters means a single refresh per click, and rows do not move while files are encoding.
    /// </summary>
    public void SortQueue(string? property, string? header, ListSortDirection? direction)
    {
        var view = (ListCollectionView)ItemsView;
        if (property is null || direction is null)
        {
            view.CustomSort = null;
            _sortHeader = null;
            _sortDirection = null;
            SortReset?.Invoke();
        }
        else
        {
            view.CustomSort = new QueueSorter(property, direction.Value);
            _sortHeader = header;
            _sortDirection = direction;
        }
        view.Refresh(); // replacing CustomSort alone is not always re-applied while a DataGrid holds the view
        Notify(nameof(SortText), nameof(IsSorted));
    }

    private sealed class QueueSorter : System.Collections.IComparer
    {
        private static readonly Dictionary<string, Func<QueueItem, object?>> Getters = new();
        private readonly Func<QueueItem, object?> _get;
        private readonly int _sign;

        public QueueSorter(string property, ListSortDirection direction)
        {
            lock (Getters)
            {
                if (!Getters.TryGetValue(property, out var g))
                {
                    var pi = typeof(QueueItem).GetProperty(property) ?? throw new ArgumentException($"Unknown column {property}");
                    g = i => pi.GetValue(i);
                    Getters[property] = g;
                }
                _get = g;
            }
            _sign = direction == ListSortDirection.Descending ? -1 : 1;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not QueueItem a || y is not QueueItem b) return 0;
            // folder groups stay together, in name order
            int g = string.Compare(a.GroupKey, b.GroupKey, StringComparison.CurrentCultureIgnoreCase);
            if (g != 0) return g;
            object? va = _get(a), vb = _get(b);
            // empty values always go last, whatever the direction
            if (va is null || vb is null) return va is null ? (vb is null ? 0 : 1) : -1;
            int c = va is string sa && vb is string sb
                ? string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase)
                : Comparer<object>.Default.Compare(va, vb);
            return c * _sign;
        }
    }
}
