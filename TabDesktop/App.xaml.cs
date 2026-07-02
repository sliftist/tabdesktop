using System.Windows;

namespace TabDesktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Install();
        ExtensionThumbnails.Start();
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
