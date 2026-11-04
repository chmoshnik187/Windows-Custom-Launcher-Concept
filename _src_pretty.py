import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Core\Media\MediaBridge.cs"
if not __import__("os").path.exists(p):
    import glob
    p = glob.glob(r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Core\**\MediaBridge.cs", recursive=True)[0]

t = io.open(p, encoding="utf-8").read()
old = '            SourceApp = _session.SourceAppUserModelId ?? string.Empty;'
new = '''            SourceApp = PrettySource(_session.SourceAppUserModelId);'''
assert old in t
t = t.replace(old, new, 1)

helper = '''
    /// <summary>
    /// AUMID вида `com.dotify.app-siw` → «Dotify». В интерфейсе это имя источника,
    /// и сырая строка там выглядит как мусор.
    /// </summary>
    private static string PrettySource(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return string.Empty;

        string text = aumid;

        int bang = text.IndexOf('!');
        if (bang > 0) text = text[..bang];

        if (text.StartsWith("com.", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        if (text.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) text = text["Microsoft.".Length..];

        int dot = text.IndexOf('.');
        if (dot > 0) text = text[..dot];

        if (text.Length == 0) return aumid;
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

'''
anchor = '    public async Task StartAsync()'
assert anchor in t
t = t.replace(anchor, helper + anchor, 1)

io.open(p, "w", encoding="utf-8").write(t)
print("источник плеера причёсан:", p)
