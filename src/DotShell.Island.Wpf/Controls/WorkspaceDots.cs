namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core;
using DotShell.Core.Native;

/// <summary>
/// Точечный переключатель рабочих столов: N точек, текущая — акцентом и крупнее.
/// Клик — следующий стол, правый клик — предыдущий. Живёт в свёрнутой капсуле
/// острова и в виджете рабочих столов.
/// </summary>
public sealed class WorkspaceDots : FrameworkElement
{
    private int _count;
    private int _index;

    public WorkspaceDots()
    {
        Height = 12;
        MinWidth = 24;
        Cursor = Cursors.Hand;
        ToolTip = "рабочие столы · клик — следующий";
        SnapsToDevicePixels = true;

        MouseLeftButtonDown += (_, e) =>
        {
            Log.Info($"workspace dots: клик вперёд (count={_count}, index={_index})");
            VirtualDesktops.SwitchNext();
            e.Handled = true;
            Refresh(force: true);
        };
        MouseRightButtonDown += (_, e) =>
        {
            Log.Info($"workspace dots: клик назад (count={_count}, index={_index})");
            VirtualDesktops.SwitchPrev();
            e.Handled = true;
            Refresh(force: true);
        };

        AutomationProperties.SetName(this, "Рабочие столы");
    }

    /// <summary>Автоматизационный пир: без него точки не видны в UIA, и их нечем проверить.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new WorkspaceDotsPeer(this);

    private sealed class WorkspaceDotsPeer : FrameworkElementAutomationPeer
    {
        public WorkspaceDotsPeer(WorkspaceDots owner) : base(owner) { }
        protected override string GetClassNameCore() => "WorkspaceDots";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetNameCore() => "Рабочие столы";
    }

    /// <summary>Прочитать количество и текущий стол из системы.</summary>
    public void Refresh(bool force = false)
    {
        int count = VirtualDesktops.Count;
        int index = VirtualDesktops.CurrentIndex;

        if (!force && count == _count && index == _index) return;

        _count = count;
        _index = index;
        Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Width = Math.Max(24, count * 13);
        ToolTip = count > 1
            ? $"рабочий стол {index + 1} из {count} · клик — следующий"
            : "один рабочий стол";
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int count = Math.Max(1, _count);
        if (count <= 1) return;

        // Прозрачная заливка на всю площадь: иначе WPF считает кликабельными
        // только сами нарисованные точки, и промах между ними улетает в панель задач.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double step = ActualWidth / count;
        double cy = ActualHeight / 2;
        var accent = new SolidColorBrush(ColorPalette.Accent);
        accent.Freeze();
        var muted = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x66));
        muted.Freeze();

        for (int i = 0; i < count; i++)
        {
            bool active = i == _index;
            double cx = i * step + step / 2;
            double r = active ? 3.0 : 2.0;
            dc.DrawEllipse(active ? accent : muted, null, new Point(cx, cy), r, r);
        }

        // Акцентный штрих под текущим столом — «штамп», как в остальном шелле.
        double lineX = _index * step + step / 2;
        dc.DrawRectangle(accent, null, new Rect(lineX - 4, ActualHeight - 1.5, 8, 1.5));
    }
}
