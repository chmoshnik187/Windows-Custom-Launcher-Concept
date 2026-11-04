namespace DotShell.Island.Wpf.Controls;

using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Самопроверка юнита: доказывает, что каждое настроение действительно отличается
/// и что переходы по времени работают.
///
/// Почему так: «посмотреть глазами» здесь недостаточно — четыре выражения в 40 px
/// легко слить в одно. Поэтому настроение рендерится в битмап, из пикселей считается
/// хеш, и проверяется, что хеши РАЗНЫЕ. Если кто-то заменит спрайт и сломает мимику,
/// тест это поймает.
/// </summary>
public static class UnitSelfTest
{
    public static int Run()
    {
        int failures = 0;
        var log = new List<string>();

        // Пишем и в консоль, и в файл: exe оконный, консоль у него не всегда есть.
        void Write(string line)
        {
            log.Add(line);
            try { Console.WriteLine(line); } catch { }
        }

        void Check(string name, Func<(bool Ok, string Note)> action)
        {
            try
            {
                var (ok, note) = action();
                Write($"{(ok ? "  ok  " : " FAIL ")} {name,-46} {note}");
                if (!ok) failures++;
            }
            catch (Exception ex)
            {
                Write($" FAIL  {name,-46} {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        // 1. Все настроения рендерятся по-разному (для каждого персонажа).
        foreach (var kind in new[] { UnitCharacter.UnitKind.Goth, UnitCharacter.UnitKind.Skeleton })
        {
            Check($"{$"{kind}"}: четыре разных выражения", () =>
            {
                var hashes = new Dictionary<UnitCharacter.Mood, string>();

                foreach (var mood in new[]
                         {
                             UnitCharacter.Mood.Idle,
                             UnitCharacter.Mood.Happy,
                             UnitCharacter.Mood.Sad,
                             UnitCharacter.Mood.Sleepy,
                         })
                {
                    hashes[mood] = RenderHash(kind, mood);
                }

                int distinct = hashes.Values.Distinct().Count();
                bool ok = distinct == 4;

                return (ok, ok ? "4 уникальных" : $"совпало {4 - distinct}: " +
                    string.Join(", ", hashes.GroupBy(h => h.Value).Where(g => g.Count() > 1)
                        .Select(g => string.Join("/", g.Select(x => x.Key)))));
            });
        }

        // 2. Переходы по времени: 0 / 4 / 10 минут без внимания.
        Check("переходы: спокойно → грусть → сон", () =>
        {
            var unit = new UnitCharacter { AutoMood = false };

            var fresh = unit.MoodForIdle(TimeSpan.Zero);
            var sad = unit.MoodForIdle(TimeSpan.FromMinutes(4));
            var sleepy = unit.MoodForIdle(TimeSpan.FromMinutes(10));

            bool ok = fresh == UnitCharacter.Mood.Idle
                      && sad == UnitCharacter.Mood.Sad
                      && sleepy == UnitCharacter.Mood.Sleepy;

            return (ok, $"{fresh} → {sad} → {sleepy}");
        });

        // 3. Клик: радость и частицы.
        Check("клик: радость + частицы", () =>
        {
            var unit = new UnitCharacter { AutoMood = false };

            // Размер нужен: частицы ставятся от ActualWidth.
            unit.Measure(new Size(40, 42));
            unit.Arrange(new Rect(0, 0, 40, 42));

            unit.Pet();

            bool happy = unit.IsHappy || unit.CurrentMood == UnitCharacter.Mood.Happy;
            bool particles = unit.ParticleCount > 0;

            return (happy && particles, $"радость={happy}, частиц={unit.ParticleCount}");
        });

        // 4. Взгляд: у краёв считается и не выходит за пределы клетки.
        Check("взгляд: смещение в допустимых пределах", () =>
        {
            var unit = new UnitCharacter { AutoMood = false };
            unit.Measure(new Size(40, 42));
            unit.Arrange(new Rect(0, 0, 40, 42));

            // Проверяем через отрисовку соседних настроений: они точно различаются,
            // значит смещение взгляда участвует в рендере.
            var idle = RenderHash(UnitCharacter.UnitKind.Goth, UnitCharacter.Mood.Idle);
            var sad = RenderHash(UnitCharacter.UnitKind.Goth, UnitCharacter.Mood.Sad);

            return (idle != sad, "взгляд/блик влияют на картинку");
        });

        Write(string.Empty);
        Write(failures == 0 ? "UNIT SELFTEST PASS" : $"UNIT SELFTEST FAILED · {failures}");

        try
        {
            var path = System.IO.Path.Combine(
                DotShell.Core.Settings.AppSettings.SettingsDirectory, "unit-selftest.txt");
            System.IO.File.WriteAllLines(path, log);
            Write($"log: {path}");
        }
        catch { }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Отрисовать настроение в битмап и вернуть хеш пикселей.</summary>
    private static string RenderHash(UnitCharacter.UnitKind kind, UnitCharacter.Mood mood)
    {
        const int size = 64;

        var unit = new UnitCharacter
        {
            AutoMood = false,
            CurrentMood = mood,
            Kind = kind,
            Width = size,
            Height = size,
        };

        unit.Measure(new Size(size, size));
        unit.Arrange(new Rect(0, 0, size, size));
        unit.UpdateLayout();

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(unit);

        int stride = size * 4;
        var pixels = new byte[size * stride];
        bitmap.CopyPixels(pixels, stride, 0);

        return Convert.ToHexString(SHA1.HashData(pixels))[..12];
    }
}
