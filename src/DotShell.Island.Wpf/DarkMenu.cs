namespace DotShell.Island.Wpf;

using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// Тёмное оформление для WinForms-меню трея.
///
/// Почему вообще WinForms: иконка в трее — это NotifyIcon, а его меню живёт
/// в мире WinForms и по умолчанию рисуется системным светлым. На тёмном шелле
/// это выглядело как чужая программа: белый прямоугольник с чёрным текстом.
///
/// Здесь мы подменяем рендерер: тёмная подложка, акцентная подсветка пункта,
/// тонкие разделители, скругления выключены (жёсткая геометрия, как в острове).
/// </summary>
public sealed class DarkMenuColors : ProfessionalColorTable
{
    private static readonly Color Surface = Color.FromArgb(0x12, 0x12, 0x14);
    private static readonly Color SurfaceAlt = Color.FromArgb(0x1B, 0x1B, 0x1F);
    private static readonly Color Border = Color.FromArgb(0x2A, 0x2A, 0x30);

    public static Color Accent { get; set; } = Color.FromArgb(0xD7, 0x19, 0x20);

    public override Color ToolStripDropDownBackground => Surface;
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;
    public override Color MenuBorder => Border;
    public override Color MenuItemBorder => Accent;
    public override Color MenuItemSelected => SurfaceAlt;
    public override Color MenuItemSelectedGradientBegin => SurfaceAlt;
    public override Color MenuItemSelectedGradientEnd => SurfaceAlt;
    public override Color MenuItemPressedGradientBegin => SurfaceAlt;
    public override Color MenuItemPressedGradientEnd => SurfaceAlt;
    public override Color SeparatorDark => Border;
    public override Color SeparatorLight => Surface;
    public override Color CheckBackground => Accent;
    public override Color CheckSelectedBackground => Accent;
    public override Color CheckPressedBackground => Accent;
}

/// <summary>Рендерер: тёмный текст, плоские пункты, без градиентов и объёма.</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? (e.Item.Selected ? Color.White : Color.FromArgb(0xE6, 0xE6, 0xEA))
            : Color.FromArgb(0x77, 0x77, 0x80);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(DarkMenuColors_Background());
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    private static Color DarkMenuColors_Background() => Color.FromArgb(0x12, 0x12, 0x14);
}
