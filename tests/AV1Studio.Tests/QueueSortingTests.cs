using System.ComponentModel;
using System.Windows.Controls;
using AV1Studio.Models;
using AV1Studio.Services;
using AV1Studio.ViewModels;

namespace AV1Studio.Tests;

[Collection("e2e")] // shares AppPaths.Root
public class QueueSortingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av1studio-sort-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previousRoot = AppPaths.Root;

    public QueueSortingTests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
    }

    public void Dispose()
    {
        AppPaths.Root = _previousRoot;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new AggregateException(error);
    }

    [Fact]
    public void Sorting_orders_the_view_by_real_values_inside_each_folder_and_leaves_the_queue_alone()
    {
        OnSta(() =>
        {
            var vm = new MainViewModel();
            var grid = new DataGrid { IsReadOnly = true, CanUserAddRows = false, ItemsSource = vm.ItemsView };
            QueueItem Add(string name, long size, string preset, string? group = null)
            {
                var i = new QueueItem { SourcePath = @"C:\Lib\" + name, SourceSize = size, PresetText = preset };
                if (group != null) { i.SourceRoot = @"C:\Lib"; i.FolderJobId = Guid.NewGuid(); i.SourcePath = $@"C:\Lib\{group}\{name}"; }
                vm.Items.Add(i);
                return i;
            }
            var a = Add("a.mkv", 5_000_000_000, "5");
            var b = Add("b.mkv", 900_000_000, "10");
            var c = Add("c.mkv", 12_000_000_000, "ab-av1 default");   // no number: sorts last
            var queueOrder = vm.Items.ToList();

            vm.SortQueue(nameof(QueueItem.SourceSize), "Size", ListSortDirection.Descending);
            Assert.Equal(new[] { c, a, b }.Select(x => x.FileName), vm.ItemsView.Cast<QueueItem>().Select(x => x.FileName));   // bytes, not "12 GB" < "5 GB" text
            Assert.Equal(queueOrder, vm.Items.ToList());                            // processing order unchanged
            Assert.Contains("Size", vm.SortText);

            Assert.Equal("5|10|ab-av1 default", string.Join("|", new[] { a, b, c }.Select(x => x.PresetText)));
            vm.SortQueue(nameof(QueueItem.SortPreset), "Preset", ListSortDirection.Ascending);
            Assert.Equal(new[] { a, b, c }.Select(x => x.FileName), vm.ItemsView.Cast<QueueItem>().Select(x => x.FileName));   // 5 < 10 (not "10" < "5"), empty last
            vm.SortQueue(nameof(QueueItem.SortPreset), "Preset", ListSortDirection.Descending);
            Assert.Equal(new[] { b, a, c }.Select(x => x.FileName), vm.ItemsView.Cast<QueueItem>().Select(x => x.FileName));   // empty stays last

            // a file added while sorted lands at its sorted position
            var d = Add("d.mkv", 1, "7");
            Assert.Equal(new[] { b, d, a, c }.Select(x => x.FileName), vm.ItemsView.Cast<QueueItem>().Select(x => x.FileName));

            // values changing during an encode do not make rows jump
            a.PresetText = "12";
            Assert.Equal(new[] { b, d, a, c }.Select(x => x.FileName), vm.ItemsView.Cast<QueueItem>().Select(x => x.FileName));

            vm.SortQueue(null, null, null);
            Assert.False(vm.IsSorted);
            Assert.Equal(vm.Items.ToArray(), vm.ItemsView.Cast<QueueItem>().ToArray());
            Assert.Equal(4, grid.Items.Count);
            vm.Shutdown();
        });
    }

    [Fact]
    public void Folder_groups_stay_together_when_sorted()
    {
        OnSta(() =>
        {
            var vm = new MainViewModel();
            var job = Guid.NewGuid();
            QueueItem Add(string rel, long size)
            {
                var i = new QueueItem { SourceRoot = @"C:\Lib", SourcePath = @"C:\Lib\" + rel, SourceSize = size, FolderJobId = job };
                vm.Items.Add(i);
                return i;
            }
            var s1 = Add(@"Series\e1.mkv", 10);
            var m1 = Add(@"Movies\m1.mkv", 100);
            var s2 = Add(@"Series\e2.mkv", 1000);
            var m2 = Add(@"Movies\m2.mkv", 1);
            vm.SortQueue(nameof(QueueItem.SourceSize), "Size", ListSortDirection.Descending);
            var order = vm.ItemsView.Cast<QueueItem>().ToArray();
            Assert.Equal([m1, m2, s2, s1], order); // Movies group, then Series group, each sorted by size
            vm.Shutdown();
        });
    }

    [Fact]
    public void Every_sort_column_points_at_a_real_property()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepo(), "src", "AV1Studio", "Views", "MainWindow.xaml"));
        var queueGrid = xaml[xaml.IndexOf("x:Name=\"QueueGrid\"")..xaml.IndexOf("</DataGrid>", xaml.IndexOf("x:Name=\"QueueGrid\""))];
        var paths = System.Text.RegularExpressions.Regex.Matches(queueGrid, "SortMemberPath=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.True(paths.Count >= 20);
        foreach (var p in paths) Assert.NotNull(typeof(QueueItem).GetProperty(p));
        Assert.Equal(System.Text.RegularExpressions.Regex.Matches(queueGrid, "<DataGrid(Text|Template)Column ").Count, paths.Count); // every column sortable
    }

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "AV1Studio.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
