namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using DotShell.Core;
using DotShell.Core.Settings;
using DotShell.Core.Updates;

/// <summary>
/// Самообновление: скачать новый exe, проверить его и подменить себя.
///
/// Главная осторожность — подмена живого файла. Порядок такой:
///   1. скачиваем в отдельную папку `update` и **проверяем**: запускаем скачанный
///      exe с `--version-probe` и сверяем версию — если не совпало, файл удаляем;
///   2. при следующем старте текущий exe переименовываем в `.old` (Windows разрешает
///      переименовать работающий файл), кладём на его место новый и запускаем его;
///   3. `.old` удаляется при успешном старте — это и есть точка отката, пока новый
///      файл не подтвердил, что живой.
///
/// Ничего не подменяется без явного действия человека: кнопка в окне обновления.
/// </summary>
public static class SelfUpdate
{
    private static string UpdateDir => Path.Combine(AppSettings.SettingsDirectory, "update");

    private static string PendingPath => Path.Combine(UpdateDir, "pending.txt");

    private static string StagedPath(string version) => Path.Combine(UpdateDir, $"DotShell-{version}.exe");

    /// <summary>Скачать и проверить. true — файл готов, версия совпала.</summary>
    public static async Task<bool> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl)) return false;

        try
        {
            Directory.CreateDirectory(UpdateDir);
            var target = StagedPath(info.Version.ToString(3));

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DotShell-Updater");

            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode) return false;

            long total = response.Content.Headers.ContentLength ?? -1;

            await using (var input = await response.Content.ReadAsStreamAsync(cancel))
            await using (var output = File.Create(target))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;

                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            // Скачали — сразу проверяем, что файл запускается и говорит нужную версию.
            if (!Probe(target, info.Version.ToString(3)))
            {
                Log.Info($"self-update: проверка не прошла, удаляю {Path.GetFileName(target)}");
                try { File.Delete(target); } catch { }
                return false;
            }

            File.WriteAllText(PendingPath, target);
            Log.Info($"self-update: скачано и проверено {Path.GetFileName(target)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"self-update download failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Есть ли уже скачанное и проверенное обновление.</summary>
    public static bool HasPending
    {
        get
        {
            try { return File.Exists(PendingPath) && File.Exists(File.ReadAllText(PendingPath).Trim()); }
            catch { return false; }
        }
    }

    /// <summary>Выполнить отложенное обновление при старте. true — приложение уже перезапущено.</summary>
    public static bool TryApplyPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return false;

            var staged = File.ReadAllText(PendingPath).Trim();
            if (!File.Exists(staged)) { File.Delete(PendingPath); return false; }

            var current = Environment.ProcessPath;
            if (current is null) return false;

            // Копия текущего — точка отката до подтверждения, что новый файл живой.
            var backup = current + ".old";
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }

            File.Move(current, backup);
            File.Copy(staged, current, true);
            File.Delete(PendingPath);

            Process.Start(new ProcessStartInfo(current) { UseShellExecute = true });
            Log.Info("self-update: заменил exe, запускаю новую копию");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"self-update apply failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Применить сейчас и выйти: используется кнопкой «Обновить сейчас».</summary>
    public static bool ApplyNowAndExit()
    {
        if (!TryApplyPending()) return false;

        // Старый процесс должен уйти, иначе останется висеть без окна.
        Environment.Exit(0);
        return true;
    }

    /// <summary>
    /// Удалить `.old` после успешного старта: откат больше не нужен.
    ///
    /// Файл почти всегда ещё держит старый процесс, который как раз выходит, поэтому
    /// одной попытки недостаточно: пробуем сразу, затем повторяем в фоне.
    /// </summary>
    public static void CleanupBackup()
    {
        var current = Environment.ProcessPath;
        if (current is null) return;

        var backup = current + ".old";
        if (!File.Exists(backup)) { PruneStaged(null); return; }

        if (TryDelete(backup))
        {
            Log.Info("self-update: удалил .old");
            PruneStaged(null);
            return;
        }

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(2000);
                if (!File.Exists(backup)) return;
                if (!TryDelete(backup)) continue;

                Log.Info($"self-update: удалил .old (попытка {attempt + 2})");
                PruneStaged(null);
                return;
            }

            Log.Info("self-update: .old остался занятым, уберу при следующем старте");
        });
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; } catch { return false; }
    }

    /// <summary>Убрать из папки обновлений всё, кроме файла из pending.</summary>
    private static void PruneStaged(string? keep)
    {
        try
        {
            if (!Directory.Exists(UpdateDir)) return;

            foreach (var file in Directory.GetFiles(UpdateDir))
            {
                if (keep is not null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(file).Equals("pending.txt", StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(file);
            }
        }
        catch { }
    }

    /// <summary>Запустить exe и прочитать версию, которую он о себе сообщает.</summary>
    private static bool Probe(string exe, string expected)
    {
        try
        {
            var info = new ProcessStartInfo(exe, "--version-probe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(info);
            if (process is null) return false;

            var text = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(20000)) { try { process.Kill(); } catch { } return false; }

            Log.Info($"self-update: проверка версии «{text}», ожидали «{expected}»");
            return string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Info($"self-update probe failed: {ex.Message}");
            return false;
        }
    }
}
