using System.Windows.Controls;
using System.Windows.Threading;
using AV1Studio.Models;
using AV1Studio.Services;
using AV1Studio.ViewModels;

namespace AV1Studio.Tests;

/// <summary>
/// Add files / add folders / remove / clear against the real view model, with the queue bound to a grouped
/// DataGrid and a ComboBox like in the application. No tools needed (files are never probed or encoded).
/// </summary>
[Collection("e2e")] // shares the global AppPaths.Root
public class QueueWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av1studio-queue-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previousRoot = AppPaths.Root;

    public QueueWorkflowTests()
    {
        AppPaths.Root = Path.Combine(_root, "appdata");
        AppPaths.EnsureCreated();
    }

    public void Dispose()
    {
        AppPaths.Root = _previousRoot;
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Runs an async test body on an STA thread with a WPF dispatcher, like the UI thread.</summary>
    private static void OnUiThread(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var task = body();
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            if (task.IsFaulted) error = task.Exception!.GetBaseException();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new AggregateException(error);
    }

    private string MakeLibrary(int videos)
    {
        var lib = Path.Combine(_root, "Library");
        for (int i = 0; i < videos; i++)
        {
            var dir = Path.Combine(lib, $"Folder {i / 100:00}", $"Sub {i % 7}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"Video {i:0000}.{(i % 2 == 0 ? "mp4" : "mkv")}"), [0, 1, 2]);
        }
        File.WriteAllText(Path.Combine(lib, "notes.txt"), "not a video");
        return lib;
    }

    [Fact]
    public void Adding_removing_and_clearing_keep_the_queue_consistent()
    {
        var lib = MakeLibrary(2000);
        OnUiThread(async () =>
        {
            var vm = new MainViewModel { ConfirmDialog = (_, _) => true, InfoDialog = (t, m) => throw new Xunit.Sdk.XunitException($"{t}: {m}") };
            vm.Settings.DestinationFolder = "";
            vm.Settings.Recursive = true;
            // the same bindings as the main window and the Manual AV1 window
            var grid = new DataGrid { IsReadOnly = true, CanUserAddRows = false, ItemsSource = vm.ItemsView };
            var combo = new ComboBox { ItemsSource = vm.Items };

            await vm.AddPathsAsync([lib]);
            Assert.Equal(2000, vm.Items.Count);
            Assert.Equal(1000, vm.Items.Count(i => i.SourcePath.EndsWith(".mp4")));
            Assert.Equal(vm.Items.Count, vm.ItemsView.Cast<object>().Count());
            Assert.Equal(vm.Items.Count, grid.Items.Count);

            // adding again, concurrently, or via single files never duplicates
            var single = vm.Items.First(i => i.SourcePath.EndsWith(".mp4")).SourcePath;
            await Task.WhenAll(vm.AddPathsAsync([lib]), vm.AddPathsAsync([lib, single]), vm.AddPathsAsync([single.ToUpperInvariant()]));
            Assert.Equal(2000, vm.Items.Count);

            // remove a selection
            vm.SelectedItems = vm.Items.Take(25).ToList();
            vm.SelectedItem = vm.SelectedItems[0];
            vm.RemoveSelectedCommand.Execute(null);
            Assert.Equal(1975, vm.Items.Count);
            Assert.Null(vm.SelectedItem);
            Assert.Empty(vm.SelectedItems);
            Assert.Equal(1975, grid.Items.Count);

            // a running item survives Clear Library Queue; everything else goes; files on disk stay
            var busy = vm.Items[10];
            busy.Status = ItemStatus.Encoding;
            vm.ClearQueueCommand.Execute(null);
            Assert.Same(busy, Assert.Single(vm.Items));
            Assert.Single(grid.Items);
            Assert.Equal(2000, Directory.GetFiles(lib, "Video *", SearchOption.AllDirectories).Length);

            busy.Status = ItemStatus.Waiting;
            vm.ClearQueueCommand.Execute(null);
            Assert.Empty(vm.Items);
            Assert.Empty(grid.Items);

            // the queue can be filled again after clearing
            await vm.AddPathsAsync([lib]);
            Assert.Equal(2000, vm.Items.Count);
            GC.KeepAlive(combo);
            vm.Shutdown();
        });
    }

    [Fact]
    public void Queue_is_restored_after_restart_without_duplicates()
    {
        var lib = MakeLibrary(30);
        OnUiThread(async () =>
        {
            var vm = new MainViewModel { ConfirmDialog = (_, _) => true };
            vm.Settings.DestinationFolder = "";
            await vm.AddPathsAsync([lib]);
            Assert.Equal(30, vm.Items.Count);
            vm.Shutdown(); // saves the queue
        });
        OnUiThread(async () =>
        {
            var vm = new MainViewModel();
            await vm.InitializeAsync();
            Assert.Equal(30, vm.Items.Count);
            Assert.Equal(30, vm.Items.Select(i => i.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            await vm.AddPathsAsync([lib]);
            Assert.Equal(30, vm.Items.Count);
            vm.Shutdown();
        });
    }
}
