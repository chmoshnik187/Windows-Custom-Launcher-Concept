namespace DotShell.Island.Wpf;

using System.Windows;
using DotShell.Core.Theming;

/// <summary>
/// Геометрия точек как наблюдаемый статик: ползунки "размер точки" и "зазор"
/// на вкладке ТЕМА меняют эти значения, а все точечные контролы (часы, EQ,
/// мини-EQ) подписаны и перерисовываются сразу.
///
/// Раньше настройки жили только в settings.json и ни на что не влияли на экране —
/// заслуженное замечание тестировщика. Теперь влияют.
/// </summary>
public static class DotStyle
{
    public static int Cell { get; private set; } = 4;
    public static int Gap { get; private set; } = 2;

    public static event Action? Changed;

    public static void Bind(ThemeBus bus)
    {
        bus.SettingsChanged += _ => RunOnUiThread(() => Apply(bus));
        Apply(bus);
    }

    private static void Apply(ThemeBus bus)
    {
        var cell = Math.Clamp(bus.Settings.CellSize, 2, 10);
        var gap = Math.Clamp(bus.Settings.Gap, 0, 8);
        // Форма точек и шахматка живут в ядре (их читает растеризатор текста).
        DotShell.Core.DotMatrix.DotGrid.Shape = bus.Settings.DotShape switch
        {
            "square" => DotShell.Core.DotMatrix.DotShape.Square,
            "diamond" => DotShell.Core.DotMatrix.DotShape.Diamond,
            "dash" => DotShell.Core.DotMatrix.DotShape.Dash,
            _ => DotShell.Core.DotMatrix.DotShape.Circle,
        };
        DotShell.Core.DotMatrix.DotGrid.Stagger = bus.Settings.DotStagger;

        if (cell == Cell && gap == Gap) return;
        Cell = cell;
        Gap = gap;
        Changed?.Invoke();
    }

    private static void RunOnUiThread(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}
