namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        // Рядом с каждым ползунком — его значение. Раньше человек двигал ползунок и не
        // видел числа: «плотность рисунка» или «радиус капсулы» приходилось угадывать.
        Loaded += (_, _) =>
        {
            if (Program.VisualDump) DumpVisualTree();

            // Появление окна: раньше настройки просто «включались» без движения,
            // хотя обзор и док уже умели появляться.
            Motion.DropIn(this, 18);

            // Масштаб интерфейса: содержимое растягивается, окно — пропорционально.
            UiScale.Apply(this, 820, 600);
            AttachSliderValues(this);
        };

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

        FillChoices(UnitStyleBox, new (string Value, string Label)[]
        {
            ("goth", "Готка"),
            ("skeleton", "Скелетик"),
            ("eyes", "Просто глазки"),
        }, vm.UnitStyle);

        UnitStyleBox.SelectionChanged += (_, _) =>
        {
            if (UnitStyleBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string value })
                vm.UnitStyle = value;
        };

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

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;

            // Esc сначала очищает поиск, и только потом закрывает окно:
            // иначе после поиска окно закрывалось вместо сброса.
            if (SearchBox is not null && SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
                e.Handled = true;
                return;
            }

            Close();
        };
    }

    /// <summary>Переключение категорий: показываем ровно одну панель.</summary>
    /// <summary>Какая категория открыта сейчас (нужна поиску, чтобы вернуть её при очистке).</summary>
    private string _category = "look";

    private void OnCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (PanelLook is null) return;   // ещё не загрузились

        _category = tag;

        // Ручное переключение категории снимает фильтр поиска.
        if (SearchBox is not null && SearchBox.Text.Length == 0)
        {
            foreach (var category in Categories())
                foreach (var row in VisualRows(category.Panel))
                    row.Visibility = Visibility.Visible;
        }

        // Новую панель проявляем: мгновенная подмена выглядела как рывок.
        // При активном поиске анимацию не запускаем — там свои правила видимости.
        bool animate = SearchBox is null || SearchBox.Text.Length == 0;

        PanelLook.Visibility = tag == "look" ? Visibility.Visible : Visibility.Collapsed;
        PanelIsland.Visibility = tag == "island" ? Visibility.Visible : Visibility.Collapsed;
        PanelWindows.Visibility = tag == "windows" ? Visibility.Visible : Visibility.Collapsed;
        PanelNotify.Visibility = tag == "notify" ? Visibility.Visible : Visibility.Collapsed;
        PanelUpdates.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        PanelData.Visibility = tag == "data" ? Visibility.Visible : Visibility.Collapsed;

        if (animate)
        {
            var shown = tag switch
            {
                "look" => (FrameworkElement)PanelLook,
                "island" => PanelIsland,
                "windows" => PanelWindows,
                "notify" => PanelNotify,
                "updates" => PanelUpdates,
                _ => PanelData,
            };

            Motion.FadeIn(shown);
        }
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

    /// <summary>Подписать все ползунки их текущим значением.</summary>
    private void AttachSliderValues(DependencyObject root)
    {
        var sliders = new List<Slider>();
        Collect(root, sliders);

        foreach (var slider in sliders)
        {
            // Tolko slaydery vnutri panely kategoriy: inache metka dostavalas skrytym
            // polzunkam vne kategoriy i odinokaya "0,0" visela v shapke.
            if (!InsideCategoryPanel(slider)) continue;

            if (slider.Parent is not Panel panel) continue;

            int index = panel.Children.IndexOf(slider);
            if (index < 0) continue;

            var value = new TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Bahnschrift"),
                FontSize = 9,
                Margin = new Thickness(0, -4, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");

            var current = slider;
            void Update()
            {
                double step = current.Maximum - current.Minimum;
                value.Text = step <= 3 ? current.Value.ToString("0.00")
                    : step <= 12 ? current.Value.ToString("0.0")
                    : current.Value.ToString("0");
            }

            current.ValueChanged += (_, _) => Update();
            Update();

            panel.Children.Insert(index + 1, value);
        }
    }

    private static void Collect(DependencyObject root, List<Slider> result)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is Slider slider) result.Add(slider);
            if (child is DependencyObject node) Collect(node, result);
        }
    }

    // ------------------------------------------------------------------
    //  Поиск по настройкам
    // ------------------------------------------------------------------

    private sealed record Category(string Tag, string Title, StackPanel Panel);

    private Category[] Categories() => new[]
    {
        new Category("look", "ВНЕШНИЙ ВИД", PanelLook),
        new Category("island", "ОСТРОВ", PanelIsland),
        new Category("windows", "ОКНА И WINDOWS", PanelWindows),
        new Category("notify", "УВЕДОМЛЕНИЯ", PanelNotify),
        new Category("updates", "ОБНОВЛЕНИЯ", PanelUpdates),
        new Category("data", "ДАННЫЕ", PanelData),
    };

    /// <summary>
    /// Поиск по настройкам.
    ///
    /// Работает по ВИЗУАЛЬНОМУ дереву и координатам: фильтрация по логическому дереву
    /// оставляла соседние строки видимыми, потому что шаблоны контролов живут отдельно.
    /// Здесь мы собираем сами строки-контролы и подписи, сопоставляем их по вертикали
    /// и прячем только то, что действительно не относится к запросу.
    /// </summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text?.Trim() ?? string.Empty;
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Собираем строки и подписи по всем категориям.
        var rows = new List<FrameworkElement>();
        var labels = new List<TextBlock>();

        foreach (var category in Categories())
        {
            CollectRows(category.Panel, rows);
            CollectLabels(category.Panel, labels);
        }

        // Снимаем прошлую фильтрацию.
        foreach (var row in rows) row.Visibility = Visibility.Visible;
        foreach (var label in labels) label.Visibility = Visibility.Visible;

        if (query.Length == 0)
        {
            foreach (var category in Categories())
                category.Panel.Visibility = category.Tag == _category
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            SearchInfo.Text = string.Empty;
            return;
        }

        string bestTag = string.Empty;
        string bestTitle = string.Empty;
        int bestCount = 0;

        foreach (var category in Categories())
        {
            // Свёрнутая панель не имеет раскладки: координаты строк нулевые, и поиск
            // в ней ничего не находил. Показываем на время замера, прячем после.
            category.Panel.Visibility = Visibility.Visible;
            category.Panel.UpdateLayout();

            var categoryRows = new List<FrameworkElement>();
            var categoryLabels = new List<TextBlock>();
            CollectRows(category.Panel, categoryRows);
            CollectLabels(category.Panel, categoryLabels);

            int count = 0;

            foreach (var row in categoryRows)
            {
                string text = RowTextByPosition(row, categoryLabels);
                bool match = text.Contains(query, StringComparison.OrdinalIgnoreCase);

                row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) count++;
            }

            // Подписи НЕ прячем: геометрический тест «подпись над строкой» ошибается
            // и убирал название найденного пункта. Лишние заголовки лучше, чем пункт
            // без подписи: по одному значению «Только развернутые окна» не понять,
            // что это за настройка.

            if (count > bestCount)
            {
                bestCount = count;
                bestTag = category.Tag;
                bestTitle = category.Title;
            }
        }

        // Строки, которые лежат вне категорий, при поиске тоже прячем.
        var globalRows = new List<FrameworkElement>();
        CollectRows(this, globalRows);
        foreach (var row in globalRows)
        {
            bool belongsToPanel = Categories().Any(category =>
                VisualRows(category.Panel).Contains(row));

            if (!belongsToPanel && !ReferenceEquals(row, SearchBox))
                row.Visibility = Visibility.Collapsed;
        }

        if (bestCount == 0)
        {
            foreach (var category in Categories())
                category.Panel.Visibility = Visibility.Collapsed;

            SearchInfo.Text = $"ничего не нашлось: «{query}»";
            return;
        }

        foreach (var category in Categories())
            category.Panel.Visibility = category.Tag == bestTag
                ? Visibility.Visible
                : Visibility.Collapsed;

        _category = bestTag;
        SearchInfo.Text = $"найдено {bestCount} · {bestTitle}";

        foreach (var button in RadioButtons(this))
        {
            if (button.Tag is string tag && tag == bestTag)
            {
                button.IsChecked = true;
                break;
            }
        }
    }

    /// <summary>Строки-контролы: то, что можно спрятать целиком.</summary>
    private static void CollectRows(DependencyObject root, List<FrameworkElement> rows)
    {
        foreach (var child in VisualChildren(root))
        {
            if (child is CheckBox or ComboBox or Slider or TextBox or Button)
            {
                rows.Add(child);
                continue;   // внутрь шаблона контрола не лезем
            }

            CollectRows(child, rows);
        }
    }

    /// <summary>Подписи: TextBlock'и вне шаблонов контролов.</summary>
    private static void CollectLabels(DependencyObject root, List<TextBlock> labels)
    {
        foreach (var child in VisualChildren(root))
        {
            if (child is CheckBox or ComboBox or Slider or TextBox or Button) continue;

            if (child is TextBlock text && !string.IsNullOrWhiteSpace(text.Text))
            {
                labels.Add(text);
                continue;
            }

            CollectLabels(child, labels);
        }
    }

    private static IEnumerable<FrameworkElement> VisualRows(DependencyObject root)
    {
        var rows = new List<FrameworkElement>();
        CollectRows(root, rows);
        return rows;
    }

    private static IEnumerable<FrameworkElement> VisualChildren(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child)
                yield return child;
    }

    /// <summary>Текст строки: её подпись — ближайший TextBlock выше в пределах 40 px.</summary>
    private static string RowTextByPosition(FrameworkElement row, List<TextBlock> labels)
    {
        if (!row.IsLoaded) return string.Empty;

        Point rowTop;
        try { rowTop = row.TranslatePoint(new Point(0, 0), Window.GetWindow(row)); }
        catch { return string.Empty; }

        TextBlock? closest = null;
        double bestDistance = double.MaxValue;

        foreach (var label in labels)
        {
            if (!label.IsLoaded) continue;

            Point labelTop;
            try { labelTop = label.TranslatePoint(new Point(0, 0), Window.GetWindow(row)); }
            catch { continue; }

            double distance = rowTop.Y - labelTop.Y;
            if (distance < 0 || distance > 40) continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                closest = label;
            }
        }

        string own = row switch
        {
            System.Windows.Controls.ContentControl cc when cc.Content is string s => s,
            TextBox textBox => textBox.Text,
            _ => string.Empty,
        };

        return $"{closest?.Text} {own} {row.ToolTip}".Trim();
    }

    /// <summary>Стоит ли подпись прямо над строкой (в пределах 40 px).</summary>
    private static bool IsLabelAbove(TextBlock label, FrameworkElement row)
    {
        if (!label.IsLoaded || !row.IsLoaded) return false;

        try
        {
            var window = Window.GetWindow(row);
            Point labelTop = label.TranslatePoint(new Point(0, 0), window);
            Point rowTop = row.TranslatePoint(new Point(0, 0), window);

            // Подпись стоит ВЫШЕ строки: расстояние = rowTop − labelTop.
            double distance = rowTop.Y - labelTop.Y;
            return distance >= -4 && distance <= 40;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Текст строки: свой и предыдущего элемента (подписи стоят отдельными TextBlock).</summary>
    private static string RowText(UIElement element, UIElement? previous)
    {
        var parts = new List<string>();

        string label = TextOf(previous);
        if (!string.IsNullOrWhiteSpace(label)) parts.Add(label);

        if (element is FrameworkElement { ToolTip: string tip } && !string.IsNullOrWhiteSpace(tip))
            parts.Add(tip);

        string own = TextOf(element);
        if (!string.IsNullOrWhiteSpace(own)) parts.Add(own);

        return string.Join(" ", parts);
    }

    private static string TextOf(UIElement? element)
    {
        if (element is null) return string.Empty;

        var parts = new List<string>();

        void Walk(DependencyObject node)
        {
            if (node is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            {
                parts.Add(tb.Text);
            }
            else if (node is DotShell.Island.Wpf.Controls.TrackedText tracked
                     && !string.IsNullOrWhiteSpace(tracked.Text))
            {
                parts.Add(tracked.Text);
            }
            else if (node is System.Windows.Controls.ContentControl cc
                     && cc.Content is string content && !string.IsNullOrWhiteSpace(content))
            {
                parts.Add(content);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject dependencyObject)
                    Walk(dependencyObject);
        }

        Walk(element);
        return string.Join(" ", parts);
    }

    /// <summary>Все RadioButton в дереве: нужны поиску, чтобы переключить категорию.</summary>
    private static IEnumerable<RadioButton> RadioButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is RadioButton button) yield return button;

            if (child is DependencyObject node)
                foreach (var nested in RadioButtons(node))
                    yield return nested;
        }
    }

    /// <summary>
    /// Рекурсивная фильтрация: возвращает число найденных строк в поддереве и прячет
    /// всё остальное. Без рекурсии фильтровался только верхний уровень, и внутри
    /// контейнеров оставались лишние поля — поиск выглядел неточным.
    /// </summary>
    private static int Filter(DependencyObject node, string query)
    {
        int count = 0;
        string previous = string.Empty;

        foreach (var child in Enumerable.ToList<object>(
                     Enumerable.Cast<object>(LogicalTreeHelper.GetChildren(node))))
        {
            if (child is not UIElement element)
            {
                if (child is DependencyObject nestedNode)
                    count += Filter(nestedNode, query);
                continue;
            }

            // Подпись раздела: не прячем и запоминаем как «название» следующей строки.
            bool caption = (element is TextBlock captionText && captionText.FontSize <= 10.5)
                           || element is DotShell.Island.Wpf.Controls.TrackedText;

            if (caption)
            {
                previous = TextOf(element);
                continue;
            }

            string text = $"{previous} {TextOf(element)}".Trim();
            previous = string.Empty;

            int inner = Filter(element, query);

            bool isRow = element is CheckBox || element is ComboBox
                         || element is Slider || element is TextBox || element is Button;

            bool match = isRow && text.Contains(query, StringComparison.OrdinalIgnoreCase);

            if (match) count++;
            count += inner;

            // Контейнер оставляем, только если внутри что-то нашлось.
            element.Visibility = match || inner > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        return count;
    }

    /// <summary>
    /// Сохранить визуальное дерево окна с координатами: нужно, чтобы найти источник
    /// «призрачной» строки, которую не видно в логическом дереве.
    /// </summary>
    private void DumpVisualTree()
    {
        try
        {
            var path = System.IO.Path.Combine(AppSettings.SettingsDirectory, "visual-dump.txt");
            var lines = new List<string>();

            void Walk(DependencyObject node, int depth)
            {
                if (depth > 30) return;

                int count = VisualTreeHelper.GetChildrenCount(node);

                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is not FrameworkElement element) { Walk(child, depth + 1); continue; }

                    double x = 0, y = 0;
                    try
                    {
                        var point = element.TranslatePoint(new Point(0, 0), this);
                        x = point.X;
                        y = point.Y;
                    }
                    catch { }

                    string text = element switch
                    {
                        System.Windows.Controls.TextBlock tb => tb.Text,
                        System.Windows.Controls.ContentControl cc when cc.Content is string s => s,
                        _ => string.Empty,
                    };

                    lines.Add($"{depth:00} {element.GetType().Name,-22} x={x,7:0.0} y={y,7:0.0} " +
                              $"w={element.ActualWidth,6:0.0} h={element.ActualHeight,6:0.0} " +
                              $"{element.Visibility,-9} {(string.IsNullOrEmpty(text) ? "" : $"«{text}»")}");

                    Walk(element, depth + 1);
                }
            }

            Walk(this, 0);
            System.IO.File.WriteAllLines(path, lines);
            Log.Info($"visual dump: {lines.Count} строк -> {path}");
        }
        catch (Exception ex)
        {
            Log.Info($"visual dump не удался: {ex.Message}");
        }
    }

    private static bool InsideCategoryPanel(DependencyObject node)
    {
        var current = node;

        while (current is not null)
        {
            if (current is FrameworkElement element
                && element.Name.StartsWith("Panel", StringComparison.Ordinal))
                return true;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
