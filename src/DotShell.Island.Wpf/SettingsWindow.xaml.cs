namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DotShell.Core;
using DotShell.Core.Settings;
using DotShell.Island.Wpf.Desktop;

/// <summary>
/// Окно настроек. Раньше всё это жило во вкладке СИСТЕМА, и там уже не помещалось:
/// один длинный свиток, в котором оформление Windows, погода и хоткеи лежали вперемешку.
///
/// Теперь категории слева, содержимое справа. Категории — это не украшение: они
/// отвечают на вопрос «где искать», который у длинного списка не отпадает никогда.
///
/// Окно использует те же ресурсы, что остров (тёмные кисти, точечные подписи),
/// поэтому выглядит частью шелла, а не системным диалогом.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly IslandViewModel _vm;
    private readonly ShellServices _services;

    public SettingsWindow(IslandViewModel vm, ShellServices services, Window owner)
    {
        _vm = vm;
        _services = services;

        // Стили кнопок, тумблеров, ползунков и списков живут в окне острова, а не
        // в ресурсах приложения. Забираем их у владельца ДО разбора XAML — иначе
        // разметка падает на StaticResource.
        // Кладём их в ресурсы приложения, а не окна: XAML ниже объявляет свои
        // Window.Resources и перезаписал бы словарь, подготовленный до Initialize.
        foreach (var key in new[] { "DsButton", "DsToggle", "DsSlider", "DsCombo", "DsScroll" })
        {
            if (Application.Current.Resources.Contains(key)) continue;
            if (owner.TryFindResource(key) is Style style) Application.Current.Resources[key] = style;
        }

        InitializeComponent();
        DataContext = vm;

        // Пачка 1 кастомизации: кривая движения и форма точки. Списки собираем
        // кодом — так проще держать значения и подписи рядом.
        FillChoices(CurveBox, new (string Value, string Label)[]
        {
            ("out", "Плавное торможение"),
            ("inout", "Туда-обратно"),
            ("linear", "Линейно"),
            ("spring", "Пружина"),
        }, vm.AnimCurve);

        CurveBox.SelectionChanged += (_, _) =>
        {
            if (CurveBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.AnimCurve = value;
        };

        FillChoices(DotShapeBox, new (string Value, string Label)[]
        {
            ("circle", "Круглые"),
            ("square", "Квадратные"),
            ("diamond", "Ромбы"),
            ("dash", "Штрихи"),
        }, vm.DotShapeName);

        FillChoices(IslandEdgeBox, new (string Value, string Label)[]
        {
            ("top", "Верхняя"),
            ("bottom", "Нижняя"),
        }, vm.IslandEdge);

        IslandEdgeBox.SelectionChanged += (_, _) =>
        {
            if (IslandEdgeBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.IslandEdge = value;
        };

        FillChoices(AccentFocusBox, new (string Value, string Label)[]
        {
            ("all", "Везде"),
            ("clock", "Только часы и трек"),
            ("player", "Только плеер"),
            ("quiet", "Приглушённый везде"),
        }, vm.AccentFocus);

        AccentFocusBox.SelectionChanged += (_, _) =>
        {
            if (AccentFocusBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.AccentFocus = value;
        };

        FillChoices(AccentSecondBox, new (string Value, string Label)[]
        {
            ("", "Выключен"),
            ("#3D7BFF", "Синий"),
            ("#3DDC84", "Зелёный"),
            ("#B26BFF", "Фиолетовый"),
            ("#FFB020", "Янтарный"),
        }, vm.AccentSecond);

        AccentSecondBox.SelectionChanged += (_, _) =>
        {
            if (AccentSecondBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.AccentSecond = value;
        };

        FillChoices(DashboardTabsBox, new (string Value, string Label)[]
        {
            ("overview,media,perf,weather", "Все четыре"),
            ("overview,media", "Обзор и медиа"),
            ("overview,weather", "Обзор и погода"),
            ("media,perf", "Медиа и производительность"),
            ("perf,weather", "Производительность и погода"),
        }, vm.DashboardTabs);

        DashboardTabsBox.SelectionChanged += (_, _) =>
        {
            if (DashboardTabsBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.DashboardTabs = value;
        };

        FillChoices(OpenDirectionBox, new (string Value, string Label)[]
        {
            ("center", "Из центра"),
            ("left", "Слева"),
            ("right", "Справа"),
            ("down", "Сверху вниз"),
        }, vm.OpenDirection);

        OpenDirectionBox.SelectionChanged += (_, _) =>
        {
            if (OpenDirectionBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.OpenDirection = value;
        };

        FillChoices(TabTransitionBox, new (string Value, string Label)[]
        {
            ("fade", "Растворение"),
            ("slide", "Сдвиг"),
            ("curtain", "Шторка"),
            ("none", "Без анимации"),
        }, vm.TabTransition);

        TabTransitionBox.SelectionChanged += (_, _) =>
        {
            if (TabTransitionBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.TabTransition = value;
        };

        FillChoices(CoverShapeBox, new (string Value, string Label)[]
        {
            ("square", "Квадрат"),
            ("rounded", "Скругление"),
            ("circle", "Круг"),
        }, vm.CoverShape);

        CoverShapeBox.SelectionChanged += (_, _) =>
        {
            if (CoverShapeBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.CoverShape = value;
        };

        FillChoices(ClickActionBox, new (string Value, string Label)[]
        {
            ("expand", "Раскрыть остров"),
            ("launcher", "Открыть поиск"),
            ("dashboard", "Открыть обзор"),
        }, vm.ClickAction);

        ClickActionBox.SelectionChanged += (_, _) =>
        {
            if (ClickActionBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.ClickAction = value;
        };

        FillChoices(AutoHideBox, new (string Value, string Label)[]
        {
            ("never", "Не скрывать"),
            ("timer", "Через 30 секунд"),
        }, vm.AutoHide);

        AutoHideBox.SelectionChanged += (_, _) =>
        {
            if (AutoHideBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.AutoHide = value;
        };

        FillChoices(QuietBox, new (string Value, string Label)[]
        {
            ("maximized", "Только развёрнутые окна"),
            ("any", "Любое крупное окно"),
            ("off", "Выключено"),
        }, vm.QuietThreshold);

        QuietBox.SelectionChanged += (_, _) =>
        {
            if (QuietBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.QuietThreshold = value;
        };

        FillChoices(LivelinessBox, new (string Value, string Label)[]
        {
            ("off", "Без движения"),
            ("rare", "Редкое мерцание"),
            ("bass", "Дышит с басом"),
        }, vm.DotLiveliness);

        LivelinessBox.SelectionChanged += (_, _) =>
        {
            if (LivelinessBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.DotLiveliness = value;
        };

        FillChoices(NotifyModeBox, new (string Value, string Label)[]
        {
            ("capsule", "В капсуле острова"),
            ("card", "Карточка в углу"),
            ("toast", "Системный тост Windows"),
            ("dot", "Только точка (тихо)"),
        }, vm.NotifyMode);

        NotifyModeBox.SelectionChanged += (_, _) =>
        {
            if (NotifyModeBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.NotifyMode = value;
        };

        FillChoices(NotifySoundBox, new (string Value, string Label)[]
        {
            ("none", "Без звука"),
            ("tick", "Короткий тик"),
            ("system", "Системный звук"),
        }, vm.NotifySound);

        NotifySoundBox.SelectionChanged += (_, _) =>
        {
            if (NotifySoundBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.NotifySound = value;
        };

        DotShapeBox.SelectionChanged += (_, _) =>
        {
            if (DotShapeBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.DotShapeName = value;
        };

        // Город погоды: список частых + возможность вписать свой.
        foreach (var city in new[] { "Москва", "Санкт-Петербург", "Иркутск", "Новосибирск", "Екатеринбург", "Казань", "Владивосток" })
            CityBox.Items.Add(city);

        CityBox.Text = _services.Settings.Current.WeatherCity;
        VersionLine.Text = $"установлена {DotShell.Core.Updates.UpdateChecker.ActualText}";
        DataPath.Text = AppSettings.SettingsDirectory;
        StatusText.Text = "изменения сохраняются сразу";

        // Рисунок — тот же, что в капсуле: тема острова переносится в настройки.
        var look = _services.Settings.Current;
        Backdrop.Style = string.IsNullOrWhiteSpace(look.BackdropStyle) ? "none" : look.BackdropStyle;
        Backdrop.Strength = Math.Clamp(look.BackdropOpacity / 100.0, 0, 1);
        Backdrop.Corner = 16;

        FillAudioDevices();

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Переключение категорий: показываем ровно одну панель.</summary>
    private void OnCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (PanelLook is null) return;   // ещё не загрузились

        PanelLook.Visibility = tag == "look" ? Visibility.Visible : Visibility.Collapsed;
        PanelIsland.Visibility = tag == "island" ? Visibility.Visible : Visibility.Collapsed;
        PanelWindows.Visibility = tag == "windows" ? Visibility.Visible : Visibility.Collapsed;
        PanelNotify.Visibility = tag == "notify" ? Visibility.Visible : Visibility.Collapsed;
        PanelUpdates.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        PanelData.Visibility = tag == "data" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Список устройств вывода: первым идёт «системное», дальше реальные.
    /// Храним id в Tag, а показываем человеческое имя.
    /// </summary>
    private void FillAudioDevices()
    {
        EqDeviceBox.Items.Clear();
        EqDeviceBox.Items.Add(new ComboBoxItem { Content = "Системное (по умолчанию)", Tag = string.Empty });

        foreach (var (id, name) in DotShell.Core.Audio.AudioSpectrum.OutputDevices())
            EqDeviceBox.Items.Add(new ComboBoxItem { Content = name, Tag = id });

        Log.Info($"audio devices: {EqDeviceBox.Items.Count - 1}");
        var current = _services.Settings.Current.AudioDeviceId;
        foreach (var item in EqDeviceBox.Items)
        {
            if (item is not ComboBoxItem { Tag: string tag }) continue;
            if (!string.Equals(tag, current, StringComparison.OrdinalIgnoreCase)) continue;

            EqDeviceBox.SelectedItem = item;
            return;
        }

        EqDeviceBox.SelectedIndex = 0;
    }

    private void OnApplyEqDevice(object sender, RoutedEventArgs e)
    {
        if (EqDeviceBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;

        _vm.AudioDeviceId = id;
        StatusText.Text = id.Length == 0
            ? "эквалайзер: системное устройство"
            : $"эквалайзер: {(EqDeviceBox.SelectedItem as ComboBoxItem)?.Content}";
    }
    /// <summary>Перечитать рисунок: тема острова могла смениться при открытом окне.</summary>
    public void RefreshBackdrop()
    {
        var look = _services.Settings.Current;
        Backdrop.Style = string.IsNullOrWhiteSpace(look.BackdropStyle) ? "none" : look.BackdropStyle;
        Backdrop.Strength = Math.Clamp(look.BackdropOpacity / 100.0, 0, 1);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSaveCity(object sender, RoutedEventArgs e)
    {
        var city = (CityBox.Text ?? string.Empty).Trim();
        if (city.Length == 0) { StatusText.Text = "город не может быть пустым"; return; }

        _services.Settings.Update(s => s.WeatherCity = city);
        DotShell.Core.Weather.WeatherService.ClearCache();
        DotShell.Island.Wpf.Widgets.WidgetsManager.RefreshAll();
        StatusText.Text = $"погода: {city}";
    }

    private void OnResetPosition(object sender, RoutedEventArgs e)
    {
        _services.Settings.Update(s =>
        {
            s.IslandLeft = -32000;
            s.IslandTop = -32000;
        });

        StatusText.Text = "позиция сброшена — перезапусти остров, чтобы он встал по центру";
    }

    private void OnRestartExplorer(object sender, RoutedEventArgs e)
    {
        bool ok = DotShell.Core.Native.ExplorerRestart.Restart();
        StatusText.Text = ok ? "проводник перезапущен" : "не удалось перезапустить проводник";
    }

    private void OnTestToast(object sender, RoutedEventArgs e)
    {
        bool ok = Toast.Show("DotShell", "Проверка уведомлений: если видишь это справа — тосты работают.");
        StatusText.Text = ok ? "тост отправлен" : "Windows не показала тост (см. crash.log)";
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "проверяю GitHub…";
        var info = await DotShell.Core.Updates.UpdateChecker.CheckAsync();

        if (info is null) { StatusText.Text = "GitHub не ответил"; return; }

        if (info.Version <= DotShell.Core.Updates.UpdateChecker.Current)
        {
            StatusText.Text = $"обновлений нет: {DotShell.Core.Updates.UpdateChecker.ActualText} — последняя";
            return;
        }

        StatusText.Text = $"есть {info.Version.ToString(3)}";
        new UpdateWindow(info, _ => { }).Show();
    }

    /// <summary>
    /// Перекраска иконок рабочего стола. Работа тяжёлая (каждый ярлык — извлечение
    /// иконки и запись .ico), поэтому в фоне, иначе окно замирает на пару секунд.
    /// </summary>
    private async void OnDesktopIconsToggled(object sender, RoutedEventArgs e)
    {
        bool want = DesktopIconsCheck.IsChecked == true;
        StatusText.Text = want ? "перекрашиваю иконки…" : "возвращаю иконки…";

        var accent = ColorPalette.Accent;
        var (done, total) = await Task.Run(() =>
        {
            if (want)
            {
                var links = DesktopIcons.Shortcuts();
                int ok = DesktopIcons.MakeMonochrome(links, accent, out _);
                return (ok, links.Count);
            }

            int restored = DesktopIcons.Restore(out _);
            return (restored, restored);
        });

        DesktopIconsCheck.IsChecked = DesktopIcons.CountManaged() > 0;
        StatusText.Text = want
            ? $"иконки в монохроме: {done} из {total}"
            : $"иконки вернули прежний вид: {done}";
    }

    private async void OnRestoreIcons(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "возвращаю иконки…";

        int restored = await Task.Run(() => DesktopIcons.Restore(out _));

        DesktopIconsCheck.IsChecked = false;
        StatusText.Text = restored > 0
            ? $"иконки вернули прежний вид: {restored}"
            : "возвращать нечего — иконки не менялись";
    }
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppSettings.SettingsDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"не смог открыть: {ex.Message}"; }
    }

    private void OnResetLook(object sender, RoutedEventArgs e)
    {
        _services.Settings.Update(s =>
        {
            s.AccentHex = "#D71920";
            s.AutoAccent = true;
            s.CellSize = 3;
            s.Gap = 2;
            s.IslandOpacity = 100;
            s.BackdropOpacity = 60;
            s.BackdropStyle = "dots";
        });

        _vm.RefreshFromSettings();
        StatusText.Text = "оформление вернулось к значениям по умолчанию";
    }

    private void OnDisableWindowsStyle(object sender, RoutedEventArgs e)
    {
        _vm.FullStyleOn = false;
        _vm.WindowsAccentOn = false;
        _vm.CustomCursorOn = false;
        StatusText.Text = "оформление Windows выключено: заголовки, акцент и курсоры системные";
    }

    /// <summary>Заполнить комбобокс подписями и выбрать текущее значение.</summary>
    private static void FillChoices(System.Windows.Controls.ComboBox box, (string Value, string Label)[] items, string current)
    {
        box.Items.Clear();

        foreach (var (value, label) in items)
        {
            var item = new System.Windows.Controls.ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);

            if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
                box.SelectedItem = item;
        }

        box.SelectedItem ??= box.Items[0];
    }
}
