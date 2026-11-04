import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Core\Media\MediaBridge.cs"
t = io.open(p, encoding="utf-8").read()

if "_lastReported" not in t:
    t = t.replace('''    private TimeSpan _anchorPosition;''',
                  '''    private TimeSpan _anchorPosition;
    private TimeSpan _lastReported;''', 1)

    old_tick = '''        // Плеер, который не двигает таймлайн, не должен отбрасывать наш счётчик
        // назад: переякориваемся только если он реально ушёл от расчётного места.
        var live = LivePosition;
        if ((timeline.Position - live).Duration() > TimeSpan.FromMilliseconds(1200))
            Anchor(timeline.Position);'''

    new_tick = '''        // Часть плееров (например, Dotify) отдаёт позицию 0 всё время, пока играет.
        // Сравнивать с расчётной позицией нельзя: тогда мы будем обнулять счётчик
        // каждую секунду и ползунок будет вечно стоять на нуле. Поэтому реагируем
        // только на ИЗМЕНЕНИЕ того, что прислал плеер.
        if ((timeline.Position - _lastReported).Duration() > TimeSpan.FromMilliseconds(500))
            Anchor(timeline.Position);'''

    assert old_tick in t
    t = t.replace(old_tick, new_tick, 1)

    old_anchor = '''    private void Anchor(TimeSpan position)
    {
        _anchorPosition = position;
        _anchorStamp = DateTime.UtcNow;
        Position = position;
    }'''
    new_anchor = '''    private void Anchor(TimeSpan position)
    {
        _anchorPosition = position;
        _lastReported = position;
        _anchorStamp = DateTime.UtcNow;
        Position = position;
    }'''
    assert old_anchor in t
    t = t.replace(old_anchor, new_anchor, 1)

    io.open(p, "w", encoding="utf-8").write(t)
    print("Tick: реагируем только на изменение позиции от плеера")
