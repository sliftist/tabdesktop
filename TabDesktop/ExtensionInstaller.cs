using System.IO;
using Microsoft.Win32;

namespace TabDesktop;

// Deploys the bundled unpacked extension to a stable per-user path and locates installed Chromium browsers for the install walkthrough. Chromium remembers the folder an unpacked extension was loaded from, so it must live somewhere that survives rebuilds and bin cleans — not the build output dir; EnsureDeployed re-copies on every launch so a rebuilt extension propagates to already-installed browsers on their next extension reload.
public static class ExtensionInstaller
{
    private static readonly string SourceDir = Path.Combine(AppContext.BaseDirectory, "BrowserExtension");
    public static readonly string DeployDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "BrowserExtension");

    public sealed record BrowserTarget(string Name, string ExeName, string ExtensionsUrl);

    private static readonly BrowserTarget[] Candidates =
    {
        new("Brave", "brave.exe", "brave://extensions/"),
        new("Chrome", "chrome.exe", "chrome://extensions/"),
        new("Edge", "msedge.exe", "edge://extensions/"),
    };

    public static void EnsureDeployed()
    {
        Directory.CreateDirectory(DeployDir);
        foreach (string file in Directory.GetFiles(SourceDir))
        {
            File.Copy(file, Path.Combine(DeployDir, Path.GetFileName(file)), overwrite: true);
        }
    }

    public static List<BrowserTarget> AllBrowsers()
    {
        return Candidates.ToList();
    }

    public static List<BrowserTarget> FindInstalledBrowsers()
    {
        return Candidates.Where(candidate => FindExe(candidate.ExeName) is not null).ToList();
    }

    private static string? FindExe(string exeName)
    {
        foreach (string root in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
        {
            if (Registry.GetValue($@"{root}\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}", "", null) is string path && File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }
}
