namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core.Input;

/// <summary>
/// Маленькое окно «нажми сочетание»: ловит комбинацию и отдаёт её строкой
/// вида «Ctrl+Alt+1».
///
/// Почему диалог, а не поле в настройках: назначение клавиши должно делаться
/// в одно движение — нажал, увидел, сохранил. Ввод строкой руками («ctrl+alt+1»)
/// люди всё равно пишут с ошибками.
///
/// Fn здесь не появится никогда — Windows её просто не присылает. Об этом
/// честно написано в самом окне, чтобы не выглядело как баг.
/// </summary>
public sealed class HotkeyCaptureWindow : Window
{
    private readonly TextBlock _preview;
    private readonly TextBlock _hint;
    private string _combo = string.Empty;
    private readonly Action<string?> _done;

    public HotkeyCaptureWindow(string appName, string? current, Action<string?> done)
    {
        _done = done;

        Title = "DotShell — назначить клавишу";
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
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(16),
            Width = 460,
        };
        root.SetResourceReference(Border.BackgroundProperty, "DsBackground");
        root.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = appName,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("DsText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        _preview = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(current) ? "нажми сочетание…" : current!,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("DsAccent"),
            Margin = new Thickness(0, 12, 0, 6),
        };
        stack.Children.Add(_preview);

        _hint = new TextBlock
        {
            Text = "нужен модификатор: Ctrl / Alt / Shift / Win + клавиша\n" +
                   "Enter — сохранить · Esc — отмена · Backspace — снять сочетание",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Foreground = (Brush)FindResource("DsTextMuted"),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(_hint);

        stack.Children.Add(new TextBlock
        {
            Text = "Fn Windows не видит — её обрабатывает клавиатура. Для Fn-сочетаний " +
                   "смотри «Клавиши и сочетания» в трее.",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Foreground = (Brush)FindResource("DsTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        root.Child = stack;
        Content = root;

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { };   // не закрываем случайно
        Loaded += (_, _) => Focus();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _done(null); Close(); return; }

        if (e.Key == Key.Back)
        {
            _combo = string.Empty;
            _preview.Text = "сочетание снято";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _done(string.IsNullOrWhiteSpace(_combo) ? "" : _combo);
            Close();
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None)
        {
            e.Handled = true;
            return;
        }

        uint mods = 0;
        var keyboard = Keyboard.Modifiers;
        if ((keyboard & ModifierKeys.Control) != 0) mods |= 0x0002;
        if ((keyboard & ModifierKeys.Alt) != 0) mods |= 0x0001;
        if ((keyboard & ModifierKeys.Shift) != 0) mods |= 0x0004;
        if ((keyboard & ModifierKeys.Windows) != 0) mods |= 0x0008;

        if (mods == 0)
        {
            _preview.Text = "нужен модификатор";
            e.Handled = true;
            return;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk == 0) return;

        _combo = KeyCombo.Format(mods, vk);
        _preview.Text = _combo;
        e.Handled = true;
    }
}
