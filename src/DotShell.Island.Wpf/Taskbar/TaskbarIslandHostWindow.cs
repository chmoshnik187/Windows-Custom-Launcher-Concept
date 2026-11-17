namespace DotShell.Island.Wpf.Taskbar;

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Deskband11Lib.Core;
using DotShell.Core.Native;
using Deskband11Lib.Wpf;

/// <summary>
/// Свёрнутый остров, живущий ВНУТРИ панели задач: окно становится
/// WS_CHILD-ребёнком Shell_TrayWnd через Deskband11Lib (MIT). Это не оверлей:
/// панель задач сама двигает и обрезает наше окно, оно переживает смену
/// положения/размера панели, DPI и перезапуск explorer. Клик по острову
/// открывает большую панель (обычное окно над панелью задач).
/// </summary>
internal sealed class TaskbarIslandHostWindow : Window
{
    private readonly TaskbarIslandView _view;
    private TaskbarContentHost? _host;

    public event Action? RecreateRequested;

    public bool Attached { get; private set; }

    public TaskbarIslandHostWindow(Action onClick, Action<string> log)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 440;
        Height = 48;
        Title = "DotShell — остров в панели";

        _view = new TaskbarIslandView(onClick);
        Content = _view;

        _host = new TaskbarContentHost(this, _view, new TaskbarContentHostOptions
        {
            PreferredWidth = 440,
            PreferredHeight = double.MaxValue,
            Placement = TaskbarContentPlacement.LeftEdge,
            TrackTaskbarButtons = true,
            TrackNotificationArea = true,
            AnimateLayoutChanges = true,
        });

        _host.TaskbarWindowRecreationRequired += (_, _) => RecreateRequested?.Invoke();
        _host.TaskbarEdgeChanged += (_, _) => _view.SetVertical(_host?.IsTaskbarVertical == true);
    }

    public async Task<bool> AttachAsync()
    {
        if (_host is null) return false;
        try
        {
            var attach = _host.AttachWhenLayoutReadyAsync();
            var finished = await Task.WhenAny(attach, Task.Delay(TimeSpan.FromSeconds(12)));
            if (finished != attach)
            {
                // Наблюдаем исключение, если подвисший attach всё-таки упадёт:
                // иначе оно всплывёт на finalizer-потоке как FATAL.
                _ = attach.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                Log.Info("taskbar host: attach timeout");
                return false;
            }

            await attach;
            Attached = true;
            if (!IsLoaded && IsVisible == false) { /* окно ещё не показано — покажем ниже */ }
            Show();
            Log.Info("taskbar host: остров встроен в панель задач");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"taskbar host: attach failed: {ex.Message}");
            return false;
        }
    }

    public void Update(string clock, string media, string cpu, bool hasMedia, int workspaceCount, int workspace, Color accent, Color panel, bool light)
    {
        _view.SetState(clock, media, cpu, hasMedia, workspaceCount, workspace, accent, panel, light);
    }

    public void Detach()
    {
        try { _host?.Dispose(); } catch { }
        _host = null;
        Attached = false;
        try { Hide(); } catch { }
    }
}

/// <summary>Рисованная капсула острова внутри панели. Без XAML — один элемент.</summary>
internal sealed class TaskbarIslandView : FrameworkElement
{
    private readonly Action _onClick;

    private string _clock = "";
    private string _media = "";
    private string _cpu = "";
    private bool _hasMedia;
    private int _workspaces = 1;
    private int _workspace;
    private Color _accent = Color.FromRgb(0xB7, 0x61, 0xFF);
    private Color _panel = Color.FromRgb(0x0B, 0x0B, 0x0E);
    private bool _light;
    private bool _vertical;
    private readonly List<(Rect Rect, int Index)> _chips = new();

    public TaskbarIslandView(Action onClick)
    {
        _onClick = onClick;
        Cursor = Cursors.Hand;
        ToolTip = "DotShell";

        MouseLeftButtonUp += (_, _) =>
        {
            // Цифры рабочих столов — самостоятельные кнопки: клик по цифре
            // переключает стол, клик по остальному острову открывает панель.
            var point = Mouse.GetPosition(this);
            foreach (var chip in _chips)
            {
                if (!chip.Rect.Contains(point)) continue;
                VirtualDesktops.SwitchTo(chip.Index);
                return;
            }

            _onClick();
        };
    }

    public void SetState(string clock, string media, string cpu, bool hasMedia, int workspaces, int workspace, Color accent, Color panel, bool light)
    {
        _clock = clock ?? "";
        _media = media ?? "";
        _cpu = cpu ?? "";
        _hasMedia = hasMedia;
        _workspaces = Math.Max(1, workspaces);
        _workspace = Math.Clamp(workspace, 0, Math.Max(0, _workspaces - 1));
        _accent = accent;
        _panel = panel;
        _light = light;
        InvalidateVisual();
    }

    public void SetVertical(bool vertical)
    {
        _vertical = vertical;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 16) return;

        double pad = 10;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _chips.Clear();

        // Фон капсулы: непрозрачный, потому что под нами панель задач.
        var fill = new SolidColorBrush(Color.FromArgb(0xF6, _panel.R, _panel.G, _panel.B));
        fill.Freeze();
        var stroke = new SolidColorBrush(_light ? Color.FromArgb(0x30, 0x00, 0x00, 0x00) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        stroke.Freeze();
        var pen = new Pen(stroke, 1);
        dc.DrawRoundedRectangle(fill, pen, new Rect(0.5, 0.5, Math.Max(1, w - 1), Math.Max(1, h - 1)), 9, 9);

        if (_vertical)
        {
            DrawVertical(dc, w, h, dpi);
            return;
        }

        double x = pad;

        // Юнит из точек: 2×4, акцентные «глаза» как в большой капсуле.
        var accentBrush = new SolidColorBrush(_accent);
        accentBrush.Freeze();
        var dot = new SolidColorBrush(_light ? Color.FromArgb(0x88, 0x10, 0x10, 0x14) : Color.FromArgb(0xAA, 0xF2, 0xF2, 0xF6));
        dot.Freeze();
        for (int col = 0; col < 2; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                bool hot = col == 0 && (row == 1 || row == 2);
                dc.DrawEllipse(hot ? accentBrush : dot, null, new Point(x + 5 + col * 8, h / 2 - 8 + row * 5.4), hot ? 1.9 : 1.5, hot ? 1.9 : 1.5);
            }
        }
        x += 24;

        // Часы.
        var clockText = Text(_clock, 13, _light ? Color.FromRgb(0x14, 0x14, 0x18) : Color.FromRgb(0xF4, 0xF4, 0xF8), dpi, FontWeights.SemiBold);
        dc.DrawText(clockText, new Point(x, (h - clockText.Height) / 2));
        x += clockText.Width + 12;

        // Разделитель.
        dc.DrawEllipse(accentBrush, null, new Point(x, h / 2), 2.1, 2.1);
        x += 12;

        // Медиа/статус: обрезаем по доступной ширине.
        double rightReserve = 26 * _workspaces + (_cpu.Length > 0 ? 66 : 0) + pad + 8;
        double mediaWidth = Math.Max(0, w - x - rightReserve);
        if (mediaWidth > 24)
        {
            string media = _media;
            if (!_hasMedia) media = media.Length == 0 ? "тишина" : media;
            var mediaText = Text(media, 11.5, _light ? Color.FromRgb(0x33, 0x33, 0x3A) : Color.FromRgb(0xA8, 0xA8, 0xB2), dpi, FontWeights.Normal, maxWidth: mediaWidth);
            dc.DrawText(mediaText, new Point(x, (h - mediaText.Height) / 2));
        }

        // Рабочие столы цифрами справа.
        double cx = w - pad - (_cpu.Length > 0 ? 66 : 0);
        var muted = Text("", 10, Colors.Transparent, dpi);
        double chipStep = 19;
        for (int i = _workspaces - 1; i >= 0; i--)
        {
            cx -= chipStep;
            var rect = new Rect(cx, (h - 18) / 2, chipStep - 4, 18);
            _chips.Add((rect, i));
            if (i == _workspace)
                dc.DrawRoundedRectangle(accentBrush, null, rect, 4, 4);
            else
            {
                var hover = new SolidColorBrush(_light ? Color.FromArgb(0x18, 0, 0, 0) : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
                hover.Freeze();
                dc.DrawRoundedRectangle(hover, null, rect, 4, 4);
            }
            var num = Text((i + 1).ToString(CultureInfo.InvariantCulture), 10,
                i == _workspace ? Color.FromRgb(0x0A, 0x0A, 0x0C) : (_light ? Color.FromRgb(0x50, 0x50, 0x58) : Color.FromRgb(0x88, 0x88, 0x94)),
                dpi, FontWeights.SemiBold);
            dc.DrawText(num, new Point(rect.X + (rect.Width - num.Width) / 2, (h - num.Height) / 2));
        }

        // CPU справа.
        if (_cpu.Length > 0)
        {
            string cpu = _cpu.EndsWith('%') ? _cpu : _cpu + "%";
            var cpuText = Text(cpu, 10.5, _light ? Color.FromRgb(0x3A, 0x3A, 0x40) : Color.FromRgb(0xC8, 0xC8, 0xD0), dpi, FontWeights.SemiBold);
            double pillW = cpuText.Width + 16;
            var pill = new Rect(w - pad - pillW, (h - 18) / 2, pillW, 18);
            var pillFill = new SolidColorBrush(_light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            pillFill.Freeze();
            dc.DrawRoundedRectangle(pillFill, null, pill, 9, 9);
            dc.DrawText(cpuText, new Point(pill.X + 8, (h - cpuText.Height) / 2));
        }
    }

    private void DrawVertical(DrawingContext dc, double w, double h, double dpi)
    {
        var text = Text(_clock, 12, _light ? Color.FromRgb(0x14, 0x14, 0x18) : Color.FromRgb(0xF4, 0xF4, 0xF8), dpi, FontWeights.SemiBold);
        dc.DrawText(text, new Point((w - text.Width) / 2, 6));

        var accent = new SolidColorBrush(_accent);
        accent.Freeze();
        dc.DrawEllipse(accent, null, new Point(w / 2, 30), 2.2, 2.2);

        for (int i = 0; i < _workspaces; i++)
        {
            var rect = new Rect((w - 18) / 2, 40 + i * 20, 18, 18);
            dc.DrawRoundedRectangle(i == _workspace ? accent : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), null, rect, 4, 4);
            var num = Text((i + 1).ToString(CultureInfo.InvariantCulture), 9.5, i == _workspace ? Color.FromRgb(0x0A, 0x0A, 0x0C) : Color.FromRgb(0x88, 0x88, 0x94), dpi);
            dc.DrawText(num, new Point(rect.X + (rect.Width - num.Width) / 2, rect.Y + (rect.Height - num.Height) / 2));
        }
    }

    private static FormattedText Text(string text, double size, Color color, double dpi, FontWeight? weight = null, double maxWidth = double.PositiveInfinity)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var formatted = new FormattedText(
            text ?? "",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            dpi)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        // MaxTextWidth = double.MaxValue WPF не принимает (лимит ~3.5e6):
        // не трогаем его вообще, если ширина не ограничена.
        if (!double.IsPositiveInfinity(maxWidth)) formatted.MaxTextWidth = Math.Max(1, maxWidth);
        return formatted;
    }
}
