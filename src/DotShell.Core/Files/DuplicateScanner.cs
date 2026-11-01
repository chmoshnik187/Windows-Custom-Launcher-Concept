namespace DotShell.Core.Files;

using System.IO;
using System.Security.Cryptography;

/// <summary>Группа дубликатов: один контент под несколькими именами.</summary>
public sealed class DupeGroup
{
    public long Size { get; init; }
    public List<string> Files { get; } = new();

    /// <summary>true = совпали «на глаз» (картинки разного размера), false = байт в байт.</summary>
    public bool Visual { get; init; }

    public string NameLabel => Path.GetFileName(Files[0]);
    public string SizeLabel => Size >= 1L << 20
        ? $"{Size / (1024.0 * 1024):F1} МБ × {Files.Count}"
        : $"{Size / 1024.0:F0} КБ × {Files.Count}";
}

/// <summary>
/// «Радар дубликатов»: находит файлы с одинаковым содержимым (SHA1) и картинки,
/// совпадающие визуально (перцептивный хэш 8×8), даже если имена и размеры разные.
///
/// Принцип безопасности: сканер только НАХОДИТ и предлагает. Удаляет лишние
/// копии только человек и только кнопкой с подтверждением — шелл не имеет
/// права сам решать, что из твоих файлов «мусор».
/// </summary>
public static class DuplicateScanner
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };

    public static List<DupeGroup> Scan(string root, Action<string>? progress = null)
    {
        var groups = new List<DupeGroup>();
        if (!Directory.Exists(root)) return groups;

        // 1. Все файлы, сгруппированные по размеру: разный размер = разный контент.
        var bySize = new Dictionary<long, List<string>>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0 || info.Length > 2L * 1024 * 1024 * 1024) continue;
                    if (!bySize.TryGetValue(info.Length, out var list))
                        bySize[info.Length] = list = new List<string>();
                    list.Add(file);
                }
                catch { }
            }
        }
        catch { }

        // 2. Внутри одинаковых размеров — полный SHA1.
        int done = 0, total = bySize.Values.Count(v => v.Count > 1);
        foreach (var list in bySize.Values.Where(v => v.Count > 1))
        {
            progress?.Invoke($"хэширую… {++done}/{total}");
            var byHash = new Dictionary<string, List<string>>();
            foreach (var file in list)
            {
                var hash = Sha1(file);
                if (hash is null) continue;
                if (!byHash.TryGetValue(hash, out var bucket))
                    byHash[hash] = bucket = new List<string>();
                bucket.Add(file);
            }
            foreach (var bucket in byHash.Values.Where(b => b.Count > 1))
                groups.Add(new DupeGroup
                {
                    Size = new FileInfo(bucket[0]).Length,
                    Visual = false,
                }.WithFiles(bucket));
        }

        // 3. Картинки дополнительно сравниваем «на глаз»: одинаковый кадр в разных
        //    размерах/сжатии SHA1 не совпадёт, а перцептивный хэш — совпадёт.
        var images = bySize.Values.SelectMany(v => v)
            .Where(f => ImageExt.Contains(Path.GetExtension(f))).ToList();
        var byLook = new Dictionary<ulong, List<string>>();
        foreach (var img in images)
        {
            var look = PerceptualHash(img);
            if (look is null) continue;
            if (!byLook.TryGetValue(look.Value, out var bucket))
                byLook[look.Value] = bucket = new List<string>();
            bucket.Add(img);
        }
        foreach (var bucket in byLook.Values.Where(b => b.Count > 1))
        {
            // Исключаем группы, которые уже поймал SHA1 (те же самые файлы).
            var key = string.Join("|", bucket.OrderBy(x => x));
            if (groups.Any(g => string.Join("|", g.Files.OrderBy(x => x)) == key)) continue;
            groups.Add(new DupeGroup
            {
                Size = SafeLength(bucket[0]),
                Visual = true,
            }.WithFiles(bucket));
        }

        return groups.OrderByDescending(g => g.Size * g.Files.Count).ToList();
    }

    /// <summary>Оставляет в группе первый файл (самый старый), остальные удаляет.</summary>
    public static (int Removed, long Freed) RemoveExtras(DupeGroup group)
    {
        var ordered = group.Files
            .Select(f => new FileInfo(f))
            .Where(f => f.Exists)
            .OrderBy(f => f.CreationTimeUtc)
            .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int removed = 0;
        long freed = 0;
        foreach (var file in ordered.Skip(1))
        {
            try
            {
                freed += file.Length;
                file.Delete();
                removed++;
            }
            catch { }
        }
        return (removed, freed);
    }

    // ------------------------------------------------------------------

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string? Sha1(string path)
    {
        try
        {
            using var sha = SHA1.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch { return null; }
    }

    /// <summary>aHash: картинка → 8×8 серого → биты относительно среднего.</summary>
    private static ulong? PerceptualHash(string path)
    {
        try
        {
            using var original = new System.Drawing.Bitmap(path);
            using var tiny = new System.Drawing.Bitmap(8, 8);
            using (var g = System.Drawing.Graphics.FromImage(tiny))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(original, 0, 0, 8, 8);
            }

            var gray = new double[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    var p = tiny.GetPixel(x, y);
                    gray[y * 8 + x] = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                }

            double avg = gray.Average();
            ulong hash = 0;
            for (int i = 0; i < 64; i++)
                if (gray[i] >= avg) hash |= 1UL << i;
            return hash;
        }
        catch { return null; }
    }

    private static DupeGroup WithFiles(this DupeGroup group, IEnumerable<string> files)
    {
        group.Files.AddRange(files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        return group;
    }
}
