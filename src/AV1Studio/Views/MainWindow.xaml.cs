using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AV1Studio.Models;
using AV1Studio.Services;
using AV1Studio.ViewModels;

namespace AV1Studio.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        ThemeManager.Apply(_vm.Settings.Theme);
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = _vm;

        _vm.ConfirmDialog = (title, text) =>
            MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        _vm.InfoDialog = (title, text) =>
            MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
        _vm.TracksDialog = item => new TracksWindow(item) { Owner = this }.ShowDialog() == true;
        _vm.SaveFileDialog = (title, name) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = name, Filter = "Text files|*.txt|All files|*.*" };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };
        _vm.SettingsDialog = (settings, tools) =>
        {
            var w = new SettingsWindow(settings, _vm) { Owner = this };
            return w.ShowDialog() == true ? w.Result : null;
        };
        _vm.ManualDialog = () => new ManualWindow(_vm) { Owner = this }.ShowDialog();
        _vm.PreviewDialog = (item, mode) => new PreviewWindow(_vm, item, mode) { Owner = this }.ShowDialog();
        _vm.AboutDialog = () => new AboutWindow(_vm) { Owner = this }.ShowDialog();
        _vm.FirstRunDialog = () => new FirstRunWindow(_vm) { Owner = this }.ShowDialog();
        _vm.CollisionDialog = count =>
        {
            var w = new CollisionWindow(count) { Owner = this };
            return w.ShowDialog() == true ? w.Choice : null;
        };

        Width = Math.Max(MinWidth, _vm.Settings.WindowWidth);
        Height = Math.Max(MinHeight, _vm.Settings.WindowHeight);

        // Auto-scroll the log, deferred and coalesced so the ListBox has processed the change first.
        bool scrollPending = false;
        ((INotifyCollectionChanged)_vm.LogEntries).CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || scrollPending) return;
            scrollPending = true;
            Dispatcher.BeginInvoke(() =>
            {
                scrollPending = false;
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            }, DispatcherPriority.ContextIdle);
        };

        InputBindings.Add(new KeyBinding(new Mvvm.RelayCommand(() => FilterBox.Focus()), Key.F, ModifierKeys.Control));

        Loaded += async (_, _) =>
        {
            await _vm.InitializeAsync();
            if (!_vm.Settings.FirstRunDone) _vm.FirstRunDialog?.Invoke();
        };
        Closing += (_, e) =>
        {
            if (!_vm.ConfirmClose()) { e.Cancel = true; return; }
            _vm.Settings.WindowWidth = ActualWidth;
            _vm.Settings.WindowHeight = ActualHeight;
            _vm.Shutdown();
        };
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SelectedItems = QueueGrid.SelectedItems.OfType<QueueItem>().ToList();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropped files/folders are added in the currently selected mode.</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await _vm.AddPathsAsync(paths);
    }

    private void OnCopyLogLine(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is LogEntry le) Clipboard.SetText($"{le.TimeText} {le.Display}");
    }
}
