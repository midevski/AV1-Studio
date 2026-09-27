using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AV1Studio.Mvvm;

/// <summary>Minimal INotifyPropertyChanged base. WPF marshals scalar property
/// notifications from worker threads, so setters may be called off the UI thread.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Raise change notifications for several dependent properties.</summary>
    protected void Notify(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}
