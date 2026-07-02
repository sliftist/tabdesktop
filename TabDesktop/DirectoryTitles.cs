using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TabDesktop;

// Persisted set of executables whose windows show the process's directory instead of the window title. Middle-clicking a tab toggles its process in and out of the set; disabling removes the entry and rewrites the file immediately, so the preference is genuinely deleted from disk. Keyed by full exe path (not pid) so the choice survives app and process restarts.
public static class DirectoryTitles
{
    private static readonly string StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "directory-titles.json");

    private static readonly HashSet<string> exePaths = Load();

    public static bool IsEnabled(string? exePath)
    {
        return exePath is not null && exePaths.Contains(exePath);
    }

    public static void Toggle(string exePath)
    {
        if (!exePaths.Remove(exePath))
        {
            exePaths.Add(exePath);
        }
        Save();
    }

    private static HashSet<string> Load()
    {
        try
        {
            List<string>? paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath));
            return new HashSet<string>(paths ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(exePaths.ToList()));
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
        }
    }
}
