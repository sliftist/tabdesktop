using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TabDesktop;

// Per-user install with no elevation anywhere: the exe goes to LocalAppData\Programs (the conventional non-admin install root, same as VS Code's user setup) instead of Program Files, and all registry writes are HKCU. The Add/Remove Programs entry points back at the installed exe with --uninstall, so the app is its own uninstaller.
public static class Installer
{
    private const string AppName = "TabDesktop";
    private const string UninstallArg = "--uninstall";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;

    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    private static readonly string InstalledExe = Path.Combine(InstallDir, AppName + ".exe");
    private static readonly string StartMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk");

    public static void Install()
    {
        string sourceExe = Environment.ProcessPath!;
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(sourceExe, InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            CopyExeOverInstalled(sourceExe);
            CopyExtensionFolder(Path.GetDirectoryName(sourceExe)!);
        }
        CreateStartMenuShortcut();
        RegisterUninstall(sourceExe);
        SetAutoStart(true);
    }

    // A running installed copy locks its exe against deletion/overwrite, but NT still allows renaming a running exe — so the old file is moved aside and cleaned up on the next install.
    private static void CopyExeOverInstalled(string sourceExe)
    {
        string old = InstalledExe + ".old";
        try
        {
            File.Delete(old);
        }
        catch
        {
        }
        if (File.Exists(InstalledExe))
        {
            File.Move(InstalledExe, old, overwrite: false);
        }
        File.Copy(sourceExe, InstalledExe, overwrite: true);
    }

    private static void CopyExtensionFolder(string sourceDir)
    {
        string extensionSource = Path.Combine(sourceDir, "BrowserExtension");
        if (!Directory.Exists(extensionSource))
        {
            return;
        }
        string extensionTarget = Path.Combine(InstallDir, "BrowserExtension");
        Directory.CreateDirectory(extensionTarget);
        foreach (string file in Directory.GetFiles(extensionSource))
        {
            File.Copy(file, Path.Combine(extensionTarget, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void CreateStartMenuShortcut()
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(StartMenuShortcut);
        shortcut.TargetPath = InstalledExe;
        shortcut.WorkingDirectory = InstallDir;
        shortcut.Save();
    }

    private static void RegisterUninstall(string sourceExe)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", FileVersionInfo.GetVersionInfo(sourceExe).ProductVersion ?? "1.0");
        key.SetValue("Publisher", "sliftist");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" {UninstallArg}");
        key.SetValue("NoModify", 1);
        key.SetValue("NoRepair", 1);
        // Add/Remove Programs reads EstimatedSize in KB.
        key.SetValue("EstimatedSize", (int)(new FileInfo(sourceExe).Length / 1024));
    }

    public static bool IsAutoStartEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(AppName) is not null;
    }

    public static void SetAutoStart(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            string target = File.Exists(InstalledExe) ? InstalledExe : Environment.ProcessPath!;
            key.SetValue(AppName, $"\"{target}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    // Returns true when this launch was the uninstaller (Add/Remove Programs runs "TabDesktop.exe --uninstall"); the caller must exit without showing any UI.
    public static bool HandleUninstallArg(string[] args)
    {
        if (!args.Contains(UninstallArg))
        {
            return false;
        }
        try
        {
            Uninstall();
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(Installer), ex.ToString());
        }
        return true;
    }

    private static void Uninstall()
    {
        foreach (Process other in Process.GetProcessesByName(AppName).Where(p => p.Id != Environment.ProcessId))
        {
            try
            {
                other.Kill();
                other.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(Installer), ex.ToString());
            }
        }
        SetAutoStart(false);
        try
        {
            File.Delete(StartMenuShortcut);
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(Installer), ex.ToString());
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        // The uninstaller runs from inside InstallDir, so it can't remove its own exe; a detached cmd waits for this process to exit and then deletes the folder. User data (settings, deployed extension) is left alone.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
