import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Controls\PlayerStrip.cs"
t = io.open(p, encoding="utf-8").read()
old = '''        _progress.Duration = _media.Duration;'''
new = '''        _progress.Duration = _media.Duration;

        Log.Info($"player: live={_media.LivePosition:m\\:ss} smtc={_media.Position:m\\:ss} " +
                 $"dur={_media.Duration:m\\:ss} playing={_media.IsPlaying}");'''
assert old in t
t = t.replace(old, new, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("лог плеера")
