namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core.Updates;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Окно «доступно обновление»: версия, пара строк о том, что нового, кнопка на GitHub.
///
/// Почему окно, а не шарик трея: шарик живёт две секунды и его легко пропустить.
/// Обновление — редкое событие, ради которого не стыдно показать отдельную карточку
/// в стиле острова: точечная подложка, крупная версия, акцентная кнопка.
/// </summary>
public sealed class UpdateWindow : Window
{
    /// <summary>
    /// Скачать обновление, проверить его и предложить перезапуск.
    ///
    /// Проверка обязательна: если скачанный файл не запускается или сообщает не ту
    /// версию, мы его удаляем и говорим об этом — подменять себя непроверенным
    /// файлом нельзя.
    /// </summary>
    private static async Task RunUpdateAsync(UpdateInfo info, Button button, ProgressBar progress,
        TextBlock state, Action<bool> done)
    {
        button.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        state.Visibility = Visibility.Visible;
        state.Text = "скачиваю…";

        var reporter = new Progress<double>(value =>
        {
            progress.Value = value * 100;
            state.Text = $"скачиваю… {value:P0}";
        });

        bool ok = await SelfUpdate.DownloadAsync(info, reporter);
        if (!ok)
        {
            progress.Visibility = Visibility.Collapsed;
            state.Text = "не получилось скачать или проверить файл — попробуй кнопку на GitHub";
            button.IsEnabled = true;
            return;
        }

        progress.Value = 100;
        state.Text = "обновление скачано и проверено. Перезапустить остров?";
        button.Content = "ПЕРЕЗАПУСТИТЬ";

        // Перевешиваем обработчик: второй клик уже применяет обновление.
        button.IsEnabled = true;
        button.Click += (_, _) =>
        {
            done(false);
            if (!SelfUpdate.ApplyNowAndExit())
                state.Text = "не удалось заменить файл — закрой остров и запусти установку заново";
        };
    }
    private static ControlTemplate FlatTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(BackgroundProperty, Brushes.Transparent);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    /// <summary>
    /// Markdown релиза → человеческий текст. GitHub присылает «## заголовок»,
    /// звёздочки и обратные кавычки — в окне это выглядит как сырой исходник.
    /// </summary>
    private static string CleanNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var lines = markdown.Replace("\r", string.Empty).Split('\n');
        var result = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith("![", StringComparison.Ordinal)) continue;      // картинки
            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.Contains("]:", StringComparison.Ordinal)) continue;       // сноски

            line = System.Text.RegularExpressions.Regex.Replace(line, @"^#{1,6}\s*", string.Empty);
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^[-*+]\s+", "· ");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"!\[[^\]]*\]\([^)]*\)", string.Empty);
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");
            line = line.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*-{3,}\s*$", string.Empty);

            if (line == "·") continue;
            result.Add(line);
        }

        // Пустые строки схлопываем: иначе в окне полосы воздуха вместо текста.
        var compact = new List<string>();
        foreach (var line in result)
        {
            if (line.Length == 0 && (compact.Count == 0 || compact[^1].Length == 0)) continue;
            compact.Add(line);
        }

        return string.Join("\n", compact).Trim();
    }
    public UpdateWindow(UpdateInfo info, Action<bool> done)
    {
        Title = "DotShell — обновление";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            Width = 520,
            ClipToBounds = true,
        };
        root.SetResourceReference(Border.BackgroundProperty, "DsBackground");
        root.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var layer = new Grid();
        layer.Children.Add(new IslandBackdrop { Style = "dots", Strength = 0.5, Corner = 16, IsHitTestVisible = false });

        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = new TextBlock
        {
            Text = "\uE895",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        head.Children.Add(mark);

        var micro = new TrackedText
        {
            Text = "ДОСТУПНО ОБНОВЛЕНИЕ",
            FontSize = 8,
            Tracking = 0.34,
            Margin = new Thickness(10, 3, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        micro.SetResourceReference(TrackedText.BrushProperty, "DsTextMuted");
        head.Children.Add(micro);
        content.Children.Add(head);

        var version = new TextBlock
        {
            Text = info.Version.ToString(3),
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
        };
        version.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        content.Children.Add(version);

        var from = new TextBlock
        {
            Text = $"у тебя {UpdateChecker.ActualText}",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
        };
        from.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        content.Children.Add(from);

        // Что нового: чистим Markdown и обрезаем, чтобы окно не превращалось в статью.
        var notes = CleanNotes(info.Notes);
        if (notes.Length > 0)
        {
            var cut = notes.Length > 420 ? notes[..420].TrimEnd() + "…" : notes;
            content.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(14, 12, 14, 12),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock
                {
                    Text = cut,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    LineHeight = 17,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("DsText"),
                },
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 18, 0, 0),
        };

        // Кнопки собираем вручную: окно создаётся кодом и не видит window-scoped
        // стили острова (DsButton живёт в IslandWindow.xaml).
        Button ActionButton(string text, bool accent, Action onClick)
        {
            var frame = new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 7, 14, 7),
            };
            frame.SetResourceReference(Border.BorderBrushProperty, accent ? "DsAccent" : "DsBorder");

            var label = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, accent ? "DsAccent" : "DsTextMuted");
            frame.Child = label;

            var button = new Button
            {
                Content = frame,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Template = FlatTemplate(),
                Margin = new Thickness(0, 0, 8, 0),
            };
            button.Click += (_, _) => onClick();
            return button;
        }

        // Обновление «в один клик»: скачать, проверить, подменить при перезапуске.
        var progress = new ProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = 100,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
        };
        progress.SetResourceReference(ProgressBar.ForegroundProperty, "DsAccent");
        progress.SetResourceReference(ProgressBar.BackgroundProperty, "DsBorder");
        content.Children.Add(progress);

        var state = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        state.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        content.Children.Add(state);

        Button update = ActionButton("ОБНОВИТЬ САМ", true, () => { });
        if (string.IsNullOrEmpty(info.DownloadUrl))
            update.IsEnabled = false;
        else
            update.Click += async (_, _) => await RunUpdateAsync(info, update, progress, state, done);

        buttons.Children.Add(update);

        buttons.Children.Add(ActionButton("ОТКРЫТЬ НА GITHUB", false, () =>
        {
            try { Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true }); }
            catch { }
            done(false);
            Close();
        }));

        buttons.Children.Add(ActionButton("ПОЗЖЕ", false, () => { done(false); Close(); }));

        buttons.Children.Add(ActionButton("НЕ НАПОМИНАТЬ", false, () =>
        {
            done(true);
            Close();
        }));

        content.Children.Add(buttons);

        layer.Children.Add(content);
        root.Child = layer;
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { done(false); Close(); } };
        Deactivated += (_, _) => { };
    }
}
