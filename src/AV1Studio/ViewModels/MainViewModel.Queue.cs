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
}
