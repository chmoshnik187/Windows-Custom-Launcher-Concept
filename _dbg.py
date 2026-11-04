import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetWindow.cs"
t = io.open(p, encoding="utf-8").read()

anchor = '''            current = mode;
'''
add = '''            current = mode;

            Dispatcher.BeginInvoke(() => Log.Info(
                $"media: mode={mode} window={ActualWidth:0}x{ActualHeight:0} " +
                $"pill={pill.ActualWidth:0}x{pill.ActualHeight:0} " +
                $"art={pillArtBox.ActualWidth:0} text={pillText.ActualWidth:0} " +
                $"title='{pillTitle.Text}' fg={(pillTitle.Foreground is null ? "null" : pillTitle.Foreground.ToString())} " +
                $"but={pillButtons.ActualWidth:0}"));
'''
t = t.replace(anchor, add, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("диагностика добавлена")
