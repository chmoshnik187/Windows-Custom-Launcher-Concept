namespace DotShell.Core.Widgets;

using System.Text.Json;
using DotShell.Core.Settings;

/// <summary>
/// D1.1: именованные наборы/профили раскладок виджетов.
/// Хранится отдельно (widget-layout-profiles.json), чтобы не ломать widgets.json.
/// </summary>
public sealed class WidgetLayoutProfile
{
    public string Name { get; set; } = string.Empty;
    public List<WidgetSpec> Items { get; set; } = new();
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

public static class WidgetLayoutProfiles
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string StorePath => Path.Combine(AppSettings.SettingsDirectory, "widget-layout-profiles.json");

    public static List<WidgetLayoutProfile> LoadAll()
    {
        try
        {
            if (!File.Exists(StorePath)) return new();
            var list = JsonSerializer.Deserialize<List<WidgetLayoutProfile>>(File.ReadAllText(StorePath), JsonOpts);
            return list?.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name)).ToList() ?? new();
        }
        catch { return new(); }
    }

    public static void SaveProfile(string name, IEnumerable<WidgetSpec> items)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return;
        var profiles = LoadAll();
        profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        profiles.Insert(0, new WidgetLayoutProfile
        {
            Name = name,
            Items = items.Select(WidgetStore.CloneSpec).ToList(),
            SavedAt = DateTime.UtcNow,
        });
        SaveAll(profiles);
    }

    public static WidgetLayoutProfile? Get(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return null;
        return LoadAll().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Delete(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return false;
        var profiles = LoadAll();
        int removed = profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        SaveAll(profiles);
        return true;
    }

    private static void SaveAll(List<WidgetLayoutProfile> profiles)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(profiles, JsonOpts));
        }
        catch { }
    }
}
