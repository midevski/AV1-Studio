using System.Windows;
using AV1Studio.ViewModels;

namespace AV1Studio.Views;

/// <summary>All Manual AV1 settings. Edits the live settings (saved automatically); queued jobs keep their own snapshot.</summary>
public partial class ManualWindow : Window
{
    public ManualWindow(MainViewModel vm)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = vm;
        if (vm.EncodeTarget is null && vm.SelectedItem is { } sel) vm.EncodeTarget = sel;
        else if (vm.EncodeTarget is null && vm.Items.Count > 0) vm.EncodeTarget = vm.Items[0];
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
