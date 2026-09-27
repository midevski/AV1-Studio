using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AV1Studio.Views;

/// <summary>Small "?" help icon: hover shows the explanation, click (or Enter/Space) opens it in a dialog.</summary>
public sealed class HelpTip : Button
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpTip),
            new PropertyMetadata("", (d, e) => ((HelpTip)d).OnTextChanged((string)e.NewValue)));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public HelpTip()
    {
        Content = new TextBlock { Text = "?", FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        Width = 18; Height = 18; Padding = new Thickness(0); MinWidth = 0; MinHeight = 0;
        Margin = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Help;
        Focusable = true;
        IsTabStop = true;
        SetResourceReference(ForegroundProperty, "AccentText");
        SetResourceReference(BorderBrushProperty, "CardBorder");
        Background = Brushes.Transparent;
        Template = BuildTemplate();
        AutomationProperties.SetName(this, "Help");
        Click += (_, _) =>
        {
            var owner = Window.GetWindow(this);
            if (owner != null) MessageBox.Show(owner, Text, "Help — AV1 Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show(Text, "Help — AV1 Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        };
    }

    private void OnTextChanged(string text)
    {
        ToolTip = text;
        AutomationProperties.SetHelpText(this, text);
    }

    private static ControlTemplate BuildTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.2));
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        return t;
    }
}
