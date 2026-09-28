using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using AV1Studio.Services;
using AV1Studio.ViewModels;

namespace AV1Studio.Views;

public partial class AboutWindow : Window
{
    private readonly MainViewModel _vm;

    public AboutWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        DataContext = vm;
        NameText.Text = AppInfo.NameAndVersion;
        DescText.Text = AppInfo.Description;
        RepoLink.NavigateUri = new Uri(AppInfo.RepositoryUrl);
        DiagBox.Text = vm.DiagnosticsText;
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    private void OnDataFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", [AppPaths.Root]); } catch { }
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!await Util.ClipboardHelper.TrySetTextAsync(DiagBox.Text))
            MessageBox.Show(this, "The clipboard is currently in use by another application. Use \"Save as…\" instead.",
                "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => DiagBox.Text = _vm.DiagnosticsText;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
