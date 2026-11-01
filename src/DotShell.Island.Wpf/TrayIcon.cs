namespace DotShell.Island.Wpf;

using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// Иконка в системном трее: страховка и точка входа, когда остров не видно
/// (спрятан, задвинут за экран или просто потерялся среди окон).
///
/// Левый клик — раскрыть/свернуть остров, правый — меню. Иконку берём прямо
/// из исполняемого файла, чтобы не таскать отдельный .ico рядом с single-file.
///
/// NotifyIcon — из WinForms: да, это "чужой" фреймворк в WPF-проекте, но своя
/// обвязка Shell_NotifyIconIcon — сотни строк Win32 ради того же результата.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action onToggle, Action onResetPosition, Action onOpenFolder, Action onExit,
        Action<ContextMenuStrip>? extend = null)
    {
        _icon = new NotifyIcon
        {
            Text = "DotShell — остров",
            Icon = LoadIcon(),
            Visible = true,
        };

        // Меню трея живёт в WinForms и по умолчанию светлое. Один раз ставим
        // тёмный рендерер — он применяется и к выпадающим подменю (виджеты).
        ToolStripManager.Renderer = new DarkMenuRenderer();

        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(0x12, 0x12, 0x14),
            ForeColor = Color.FromArgb(0xE6, 0xE6, 0xEA),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9f),
        };
        menu.Items.Add("Раскрыть / свернуть остров", null, (_, _) => onToggle());
        menu.Items.Add("Показать остров (сброс позиции)", null, (_, _) => onResetPosition());
        menu.Items.Add("Папка настроек", null, (_, _) => onOpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выйти", null, (_, _) => onExit());
        extend?.Invoke(menu);
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) onToggle();
        };
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    /// <summary>Пузырь над трее: напоминания и таймер доходят до человека даже
    /// из-под полноэкранных окон.</summary>
    public void Notify(string title, string text)
    {
        try { _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info); }
        catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
