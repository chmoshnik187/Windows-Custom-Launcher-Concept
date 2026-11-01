namespace DotShell.Core.Native;

using System.Diagnostics;

/// <summary>
/// Перезапуск проводника Windows.
///
/// Зачем это вообще нужно: часть оформления (подсветка «Пуска», цвета акцента в
/// оболочке) применяется только при старте explorer.exe. Без перезапуска человек
/// видит «половину» изменений и думает, что они не сработали.
///
/// Поэтому здесь честный, предсказуемый перезапуск: закрыть процессы и запустить
/// заново. Панель задач исчезает на 1–2 секунды — это нормально и ожидаемо.
/// </summary>
public static class ExplorerRestart
{
    public static bool Restart()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                try { process.Kill(); } catch { }
                finally { process.Dispose(); }
            }

            // Дадим оболочке закрыться, иначе новый процесс может «прилипнуть»
            // к умирающему и не показать панель задач.
            Thread.Sleep(600);

            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
