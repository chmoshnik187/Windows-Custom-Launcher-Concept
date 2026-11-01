namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>
/// Крошечное окно «введи текст»: имя группы, переименование.
///
/// Почему своё, а не InputBox из VisualBasic: тот тянет за собой сборку и
/// выглядит как диалог из Windows 7 — в шелле с плотной типографикой это
/// выбивается сильнее, чем кажется.
/// </summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    public TextPromptWindow(string title, string value, Action<string?> done)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Border
        {
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(14),
            Width = 360,
        };
        root.SetResourceReference(Border.BackgroundProperty, "DsBackground");
        root.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9,
            Margin = new Thickness(0, 0, 0, 8),
        });
        ((TextBlock)stack.Children[0]).SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");

        _input = new TextBox
        {
            Text = value,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 15,
            Padding = new Thickness(8, 6, 8, 6),
            CaretBrush = (Brush)FindResource("DsAccent"),
        };
        _input.SetResourceReference(TextBlock.BackgroundProperty, "DsSurfaceAlt");
        _input.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        stack.Children.Add(_input);

        stack.Children.Add(new TextBlock
        {
            Text = "Enter — сохранить · Esc — отмена",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Margin = new Thickness(0, 8, 0, 0),
        });
        ((TextBlock)stack.Children[^1]).SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");

        root.Child = stack;
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { done(_input.Text); Close(); e.Handled = true; }
            else if (e.Key == Key.Escape) { done(null); Close(); e.Handled = true; }
        };

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }
}
