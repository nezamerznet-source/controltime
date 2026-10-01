using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FamilyTime.Windows;

public sealed class HelpLabel : StackPanel
{
    private readonly TextBlock label = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly HelpTip tip = new();
    public string Label { get => label.Text; set => label.Text = value; }
    public string Help { get => tip.Text; set => tip.Text = value; }
    public HelpLabel()
    {
        Orientation = Orientation.Horizontal; Margin = new Thickness(0, 12, 0, 7);
        Children.Add(label); Children.Add(tip);
    }
}
/// <summary>Hover, keyboard focus or click exposes the same accessible help.</summary>
public sealed class HelpTip : Button
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpTip),
        new PropertyMetadata("", (d, _) => ((HelpTip)d).UpdateTip()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public HelpTip()
    {
        if (Application.Current?.TryFindResource(typeof(Button)) is Style buttonStyle) Style = buttonStyle;
        Content = "?"; Width = 24; Height = 24; Padding = new Thickness(0); Margin = new Thickness(6, 0, 0, 0);
        FontWeight = FontWeights.SemiBold; Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        VerticalAlignment = VerticalAlignment.Center; HorizontalAlignment = HorizontalAlignment.Left;
        ToolTipService.SetInitialShowDelay(this, 150); ToolTipService.SetShowDuration(this, 60000);
        Click += (_, _) => OpenTip(); GotKeyboardFocus += (_, _) => OpenTip();
        LostKeyboardFocus += (_, _) => { if (ToolTip is ToolTip tip) tip.IsOpen = false; };
    }
    private void UpdateTip()
    {
        ToolTip = new ToolTip { Content = new TextBlock { Text = Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }, PlacementTarget = this };
        System.Windows.Automation.AutomationProperties.SetName(this, "Подсказка: " + Text);
    }
    private void OpenTip() { if (ToolTip is ToolTip tip) tip.IsOpen = true; }
}
