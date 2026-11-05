namespace DotShell.Island.Wpf;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Audio;
using DotShell.Core.Clipboard;
using DotShell.Core.Input;
using DotShell.Core.Media;
using DotShell.Core.Native;
using DotShell.Core.Time;
using DotShell.Core.Settings;
using DotShell.Core.Launcher;
using DotShell.Island.Wpf.Widgets;

public partial class IslandWindow
{
    // ================================================================
    //  Тема: пресеты акцента
    // ================================================================

    private static readonly (string Hex, string Name)[] Presets =
    {
        ("#D71920", "Nothing Red"), ("#FF5A00", "Orange"),
        ("#FFC400", "Amber"),       ("#3DDC84", "Green"),
        ("#00C2FF", "Cyan"),        ("#7B61FF", "Violet"),
        ("#FF4D8D", "Pink"),        ("#E8E8EA", "White"),
    };

    private void BuildAccentPresets()
    {
        // Одна полоса образцов: восемь квадратов в ряд, как палитра на бумаге.
        // Круглые «конфеты» читались как кнопки — а это не кнопки, это цвета.
        AccentPresets.Columns = 8;
        AccentPresets.Rows = 1;
        AccentPresets.Height = 34;
        AccentPresets.HorizontalAlignment = HorizontalAlignment.Left;
        AccentPresets.Width = 8 * 30;

        foreach (var (hex, name) in Presets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Button
            {
                Style = (Style)FindResource("DsButton"),
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(0),
                ToolTip = $"{name} {hex}",
                Cursor = Cursors.Hand,
            };
            swatch.Click += (_, _) => _vm.ApplyAccentPreset(hex);
            AccentPresets.Children.Add(swatch);
        }
    }

    // ================================================================
    //  Плеер: раскладка
    // ================================================================

    private static readonly (string Key, string Label)[] PlayerStyles =
    {
        ("cover", "ОБЛОЖКА"),
        ("dots", "ТОЧКИ"),
        ("row", "СТРОКА"),
    };

    /// <summary>Кнопки скинов: скин меняет акцент, точки, рисунок и плотность разом.</summary>
    private void BuildSkinChips()
    {
        if (SkinChips is null) return;

        SkinChips.Children.Clear();
        foreach (var skin in DotShell.Core.Theming.Skins.All)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("DsButton"),
                Content = skin.Name,
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 9,
                Tag = skin.Id,
                ToolTip = skin.Motto,
                Cursor = Cursors.Hand,
            };
            var captured = skin;
            chip.Click += (_, _) =>
            {
                _vm.ApplySkin(captured);
                ApplyBackdrop();
                ApplyPlayerStyle();
                RebuildPinnedTiles();

                RefreshSkinChips();
            };
            SkinChips.Children.Add(chip);
        }

        RefreshSkinChips();
    }

    /// <summary>Подсветить активный скин.</summary>
    private void RefreshSkinChips()
    {
        if (SkinChips is null) return;

        var current = _services.Settings.Current.Skin;
        foreach (var child in SkinChips.Children.OfType<Button>())
        {
            bool active = string.Equals((string?)child.Tag, current, StringComparison.OrdinalIgnoreCase);
            child.SetResourceReference(Button.BorderBrushProperty, active ? "DsAccent" : "DsBorder");
            child.SetResourceReference(Button.ForegroundProperty, active ? "DsText" : "DsTextMuted");
        }
    }
    private void BuildPlayerChips()
    {
        if (PlayerChips is null) return;

        PlayerChips.Children.Clear();
        foreach (var (key, label) in PlayerStyles)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("DsButton"),
                Content = label,
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 9,
                Tag = key,
                Cursor = Cursors.Hand,
            };
            chip.Click += (_, _) => _vm.PlayerStyle = key;
            PlayerChips.Children.Add(chip);
        }
    }

    /// <summary>Применить выбранную раскладку и подсветить её чип.</summary>
    private void ApplyPlayerStyle()
    {
        if (Player is null) return;

        var style = _services.Settings.Current.PlayerStyle;
        Player.Style = string.IsNullOrWhiteSpace(style) ? "cover" : style;

        if (PlayerChips is null) return;
        foreach (var child in PlayerChips.Children.OfType<Button>())
        {
            bool active = (string?)child.Tag == Player.Style;
            child.SetResourceReference(Button.BorderBrushProperty, active ? "DsAccent" : "DsBorder");
            child.SetResourceReference(Button.ForegroundProperty, active ? "DsText" : "DsTextMuted");
        }
    }
    // ================================================================
    //  Рисунок внутри острова
    // ================================================================

    private static readonly (string Key, string Label)[] Backdrops =
    {
        ("none", "ЧИСТО"),
        ("dots", "ТОЧКИ"),
        ("waves", "ВОЛНЫ"),
        ("topo", "ТОПО"),
        ("rays", "ЛУЧИ"),
        ("aurora", "АВРОРА"),
        ("grid", "СЕТКА"),
    };

    /// <summary>Кнопки выбора рисунка: один ряд чипов, активный — акцентом.</summary>
    private void BuildBackdropChips()
    {
        if (BackdropChips is null) return;

        BackdropChips.Children.Clear();
        foreach (var (key, label) in Backdrops)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("DsButton"),
                Content = label,
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 9,
                Tag = key,
                Cursor = Cursors.Hand,
            };
            chip.Click += (_, _) => _vm.BackdropStyle = key;
            BackdropChips.Children.Add(chip);
        }
    }

    /// <summary>Подсветить выбранный рисунок и применить его к острову.</summary>
    private void ApplyBackdrop()
    {
        if (Backdrop is null) return;

        var s = _services.Settings.Current;
        Backdrop.Style = string.IsNullOrWhiteSpace(s.BackdropStyle) ? "none" : s.BackdropStyle;
        Backdrop.Strength = Math.Clamp(s.BackdropOpacity / 100.0, 0, 1);
        Backdrop.Corner = Math.Min(Pill.ActualHeight / 2, 23);

        if (BackdropChips is null) return;
        foreach (var child in BackdropChips.Children.OfType<Button>())
        {
            bool active = (string?)child.Tag == Backdrop.Style;
            child.SetResourceReference(Button.BorderBrushProperty, active ? "DsAccent" : "DsBorder");
            child.SetResourceReference(Button.ForegroundProperty, active ? "DsText" : "DsTextMuted");
        }
    }
}
