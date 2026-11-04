import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Controls\IslandBackdrop.cs"
t = io.open(p, encoding="utf-8").read()

old = '''    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 8 || ActualHeight < 8) return;'''
new = '''    protected override void OnRender(DrawingContext dc)
    {
        // Яркость рисунка пляшет с басом: 1.0 в тишине, до ~1.8 на ударе.
        double bass = AudioSpectrum.Current?.Bass ?? 0f;
        double strength = Math.Min(1.8, _strength * (1 + bass * 0.75));

        if (ActualWidth < 8 || ActualHeight < 8) return;'''
assert old in t
t = t.replace(old, new, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("strength объявлен в OnRender")
