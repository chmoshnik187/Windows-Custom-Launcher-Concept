import io
# 1) настройка стиля таймлайна
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Core\Settings\AppSettings.cs"
import os
if not os.path.exists(p):
    p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Core\AppSettings.cs"
t = io.open(p, encoding="utf-8").read()
if "SeekStyle" not in t:
    anchor = '''    public string PlayerStyle { get; set; } = "cover";'''
    assert anchor in t
    t = t.replace(anchor, anchor + '''

    /// <summary>
    /// Стиль полосы перемотки: beads, rail, segments, wave, sand, arc, pulse, cassette.
    /// Владелец выбирал из витрины (--slider-demo).
    /// </summary>
    public string SeekStyle { get; set; } = "beads";

    /// <summary>Стиль ползунков (громкость, яркость, размер точки): capsule, blocks, dots.</summary>
    public string SliderStyle { get; set; } = "capsule";''', 1)
    io.open(p, "w", encoding="utf-8").write(t)
    print("AppSettings: SeekStyle, SliderStyle")

# 2) остров применяет стиль
p2 = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Controls\PlayerStrip.cs"
t2 = io.open(p2, encoding="utf-8").read()
if "Variant =" not in t2:
    anchor2 = "        _timer.Tick += (_, _) => RefreshProgress();"
    assert anchor2 in t2
    t2 = t2.replace(anchor2, anchor2 + '''

        // Стиль таймлайна из настроек: витрина (--slider-demo) показывает варианты.
        if (_progress is not null)
        {
            _progress.Variant = DotShell.Core.ShellServices.Instance.Settings.Current.SeekStyle;
        }''', 1)
    io.open(p2, "w", encoding="utf-8").write(t2)
    print("PlayerStrip: стиль из настроек")
