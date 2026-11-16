namespace DotShell.Island.Wpf.Controls;

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core;
using DotShell.Core.Native;

/// <summary>
/// Рабочие столы цифрами: «1 2 3 4» в свёрнутой капсуле острова.
///
/// Клик по цифре — переход на этот стол (COM), а не просто «следующий»: владелец
/// просил именно так. Цифры рисуются точками-глифами, текущая — акцентной плашкой.
/// Если стол один, контрол прячется.
/// </summary>
public sealed class WorkspaceChips : FrameworkElement
{
    private int _count;
    private int _index;
    private int _hover = -1;

    public WorkspaceChips()
    {
        Height = 18;
        MinWidth = 18;
        Cursor = Cursors.Hand;
        ToolTip = "рабочие столы · клик — перейти";
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "Рабочие столы");

        MouseMove += (_, e) =>
        {
            int next = HitIndex(e.GetPosition(this).X);
            if (next == _hover) return;
            _hover = next;
            InvalidateVisual();
        };
        MouseLeave += (_, _) => { _hover = -1; InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            int hit = HitIndex(e.GetPosition(this).X);
            if (hit >= 0) VirtualDesktops.SwitchTo(hit);
            e.Handled = true;
            Refresh(force: true);
        };
    }

    private int HitIndex(double x)
    {
        if (_count <= 1) return -1;
        double step = ActualWidth / _count;
        int i = (int)Math.Floor(x / Math.Max(1, step));
        return i >= 0 && i < _count ? i : -1;
    }

    public void Refresh(bool force = false)
    {
        int count = VirtualDesktops.Count;
        int index = VirtualDesktops.CurrentIndex;
        if (!force && count == _count && index == _index) return;

        _count = count;
        _index = index;
        Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Width = Math.Max(18, count * 19);
        ToolTip = count > 1 ? $"рабочий стол {index + 1} из {count} · клик — перейти" : "один рабочий стол";
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WorkspaceChipsPeer(this);

    protected override void OnRender(DrawingContext dc)
    {
        int count = Math.Max(1, _count);
        if (count <= 1) return;

        // прозрачная заливка — чтобы клик работал по всей площади
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double step = ActualWidth / count;
        double h = 18;
        var accent = new SolidColorBrush(ColorPalette.Accent);
        accent.Freeze();
        var hover = new SolidColorBrush(Color.FromArgb(0x30, 0xEC, 0xEC, 0xEE));
        hover.Freeze();
        var muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93));
        muted.Freeze();

        for (int i = 0; i < count; i++)
        {
            var rect = new Rect(i * step + 1, 0, Math.Max(1, step - 2), h);
            bool active = i == _index;
            bool hot = i == _hover;

            if (active) dc.DrawRoundedRectangle(accent, null, rect, 4, 4);
            else if (hot) dc.DrawRoundedRectangle(hover, null, rect, 4, 4);

            var text = new FormattedText(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                10,
                active ? Brushes.Black : muted,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(text, new Point(
                rect.Left + (rect.Width - text.Width) / 2,
                rect.Top + (h - text.Height) / 2));
        }
    }

    private sealed class WorkspaceChipsPeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        public WorkspaceChipsPeer(WorkspaceChips owner) : base(owner) { }
        protected override string GetClassNameCore() => "WorkspaceChips";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;
        protected override string GetNameCore() => "Рабочие столы";

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke()
        {
            var chips = (WorkspaceChips)Owner;
            VirtualDesktops.SwitchNext();
            chips.Refresh(force: true);
        }
    }
}
