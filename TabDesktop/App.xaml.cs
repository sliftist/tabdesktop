using System.Windows;

namespace TabDesktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Install();
        AppLog.InstallCrashHandlers(this);
        // Environment.Exit instead of Shutdown(): StartupUri would still materialize MainWindow (and its tab strips) after OnStartup returns, and the uninstaller must not flash any UI.
        if (Installer.HandleUninstallArg(e.Args))
        {
            Environment.Exit(0);
        }
        ExtensionThumbnails.Start();
        ThumbnailDiskCache.PruneInBackground();
        try
        {
            ExtensionInstaller.EnsureDeployed();
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExtensionInstaller), ex.ToString());
        }
        base.OnStartup(e);
    }
}
