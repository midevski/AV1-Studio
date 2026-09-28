using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AV1Studio.Mvvm;

/// <summary>
/// ObservableCollection with batch operations. A batch raises a single Reset notification, so bound views
/// (grouped, filtered DataGrids) rebuild once instead of once per item. This replaces DeferRefresh around
/// per-item changes, which lets views be queried while deferred and throws.
/// Must be used from the UI thread, like any collection bound to WPF controls.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Below this size individual notifications are cheaper and keep the selection.</summary>
    private const int ResetThreshold = 16;

    public void AddRange(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        if (list.Count == 0) return;
        if (list.Count < ResetThreshold)
        {
            foreach (var i in list) Add(i);
            return;
        }
        CheckReentrancy();
        foreach (var i in list) Items.Add(i);
        RaiseReset();
    }

    public int RemoveRange(IEnumerable<T> items)
    {
        var set = items.ToHashSet();
        if (set.Count == 0) return 0;
        if (set.Count < ResetThreshold)
        {
            int n = 0;
            foreach (var i in set) if (Remove(i)) n++;
            return n;
        }
        CheckReentrancy();
        int before = Items.Count;
        var keep = Items.Where(i => !set.Contains(i)).ToList();
        Items.Clear();
        foreach (var i in keep) Items.Add(i);
        if (Items.Count != before) RaiseReset();
        return before - Items.Count;
    }

    /// <summary>Removes the first <paramref name="count"/> items (e.g. trimming a log) with one notification.</summary>
    public void RemoveFirst(int count)
    {
        count = Math.Min(count, Items.Count);
        if (count <= 0) return;
        CheckReentrancy();
        var keep = Items.Skip(count).ToList();
        Items.Clear();
        foreach (var i in keep) Items.Add(i);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
