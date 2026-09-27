using System.Windows;
using AV1Studio.Models;

namespace AV1Studio.Views;

/// <summary>"Ask" collision policy: one decision for this run, applied to every existing output.</summary>
public partial class CollisionWindow : Window
{
    public CollisionPolicy? Choice { get; private set; }

    public CollisionWindow(int count)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        Heading.Text = count == 1
            ? "1 output file already exists."
            : $"{count} output files already exist.";
    }

    private void Pick(CollisionPolicy p)
    {
        Choice = p;
        DialogResult = true;
    }

    private void OnReuse(object sender, RoutedEventArgs e) => Pick(CollisionPolicy.ReuseIfValid);
    private void OnSkip(object sender, RoutedEventArgs e) => Pick(CollisionPolicy.Skip);
    private void OnNumber(object sender, RoutedEventArgs e) => Pick(CollisionPolicy.AppendNumber);

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Existing OUTPUT files will be replaced by new encodes (only after the new file is verified).\n\nSource files are never overwritten.\n\nContinue?",
                "Replace existing outputs?", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Pick(CollisionPolicy.Overwrite);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
