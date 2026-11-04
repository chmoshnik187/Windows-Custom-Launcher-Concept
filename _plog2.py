import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Controls\PlayerStrip.cs"
t = io.open(p, encoding="utf-8").read()

broken = '''        Log.Info($"player: live={_media.LivePosition:m\\:ss} smtc={_media.Position:m\\:ss} " +
                 $"dur={_media.Duration:m\\:ss} playing={_media.IsPlaying}");'''
fixed = '''        Log.Info($"player: live={_media.LivePosition.TotalSeconds:F1}s smtc={_media.Position.TotalSeconds:F1}s " +
                 $"dur={_media.Duration.TotalSeconds:F1}s playing={_media.IsPlaying}");'''

assert broken in t
t = t.replace(broken, fixed, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("лог переписан без \\: ")
