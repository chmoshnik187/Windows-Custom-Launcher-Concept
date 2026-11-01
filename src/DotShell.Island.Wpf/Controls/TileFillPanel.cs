namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

/// <summary>
/// Плиточная сетка, которая заполняет всё доступное место.
///
/// Зачем свой панель, а не WrapPanel: WrapPanel держит фиксированный размер
/// элемента, поэтому при растягивании виджета иконки остаются мелкими, а справа
/// появляется пустота. Здесь наоборот — подбирается число колонок, при котором
/// клетка получается наибольшей, и каждая клетка занимает свою долю площади.
///
/// Правило выбора сетки простое: перебираем все варианты «сколько колонок»,
/// для каждого считаем размер клетки и берём тот, где клетка больше. Так
/// вытянутый по вертикали виджет получает высокие колонки, а широкий — ряды.
/// </summary>
public sealed class TileFillPanel : Panel
{
    /// <summary>Какую долю клетки занимает сам значок (остальное — воздух).</summary>
    public double IconScale { get; set; } = 0.62;

    private (int Columns, int Rows, double Cell) _layout = (1, 1, 0);

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = InternalChildren.Count;
        if (count == 0) return new Size(0, 0);

        double width = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height;

        _layout = BestGrid(count, width, height);

        var size = new Size(_layout.Cell, _layout.Cell);
        foreach (UIElement child in InternalChildren) child.Measure(size);

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = InternalChildren.Count;
        if (count == 0) return finalSize;

        _layout = BestGrid(count, finalSize.Width, finalSize.Height);
        var (columns, rows, cell) = _layout;

        // Сетку центрируем: если клетки не занимают всю ширину, пусть воздух
        // распределится по краям, а не соберётся справа.
        double totalWidth = columns * cell;
        double totalHeight = rows * cell;
        double offsetX = Math.Max(0, (finalSize.Width - totalWidth) / 2);
        double offsetY = Math.Max(0, (finalSize.Height - totalHeight) / 2);

        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int column = i % columns;

            var rect = new Rect(
                offsetX + column * cell,
                offsetY + row * cell,
                cell,
                cell);

            // Значок внутри клетки: отступ и радиус считаются от её размера,
            // поэтому плитка одинаково аккуратно выглядит и в 40, и в 160 точек.
            if (InternalChildren[i] is Border border)
            {
                double pad = Math.Max(2, cell * (1 - IconScale) / 2);
                border.Padding = new Thickness(pad);
                border.CornerRadius = new CornerRadius(Math.Max(4, cell * 0.22));
            }

            InternalChildren[i].Arrange(rect);
        }

        return finalSize;
    }

    private static (int Columns, int Rows, double Cell) BestGrid(int count, double width, double height)
    {
        double bestCell = 0;
        int bestColumns = count;
        int bestRows = 1;

        for (int columns = 1; columns <= count; columns++)
        {
            int rows = (int)Math.Ceiling(count / (double)columns);
            double cell = Math.Min(width / columns, height / rows);

            if (cell <= bestCell) continue;

            bestCell = cell;
            bestColumns = columns;
            bestRows = rows;
        }

        return (bestColumns, bestRows, bestCell);
    }
}
