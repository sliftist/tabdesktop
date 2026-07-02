using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TabDesktop;

// Non-modal walkthrough for loading the unpacked extension. Deliberately shown with Show(), never ShowDialog(): the app spans every monitor and nothing should be blocked while the user flips between this window and their browser. Browsers refuse to open their internal extensions page from a command line, so the URL is presented for copy/paste instead of a launch button.
public partial class ExtensionInstallWindow : Window
{
    private static readonly TimeSpan CopiedFeedbackDuration = TimeSpan.FromSeconds(1.5);

    public ExtensionInstallWindow()
    {
        InitializeComponent();
        try
        {
            ExtensionInstaller.EnsureDeployed();
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExtensionInstaller), ex.ToString());
        }
        FolderPathText.Text = ExtensionInstaller.DeployDir;
        List<ExtensionInstaller.BrowserTarget> browsers = ExtensionInstaller.FindInstalledBrowsers();
        if (browsers.Count == 0)
        {
            browsers = ExtensionInstaller.AllBrowsers();
        }
        foreach (ExtensionInstaller.BrowserTarget browser in browsers)
        {
            ExtensionUrls.Children.Add(BuildUrlRow(browser));
        }
    }

    private static UIElement BuildUrlRow(ExtensionInstaller.BrowserTarget browser)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var name = new TextBlock { Text = browser.Name, Width = 60, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
        DockPanel.SetDock(name, Dock.Left);
        var copy = new Button { Content = "Copy" };
        DockPanel.SetDock(copy, Dock.Right);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(browser.ExtensionsUrl);
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(ExtensionInstallWindow), ex.ToString());
                return;
            }
            copy.Content = "Copied!";
            var revert = new DispatcherTimer { Interval = CopiedFeedbackDuration };
            revert.Tick += (_, _) =>
            {
                revert.Stop();
                copy.Content = "Copy";
            };
            revert.Start();
        };
        var url = new TextBox { Text = browser.ExtensionsUrl };
        row.Children.Add(name);
        row.Children.Add(copy);
        row.Children.Add(url);
        return row;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        // /select opens the parent with the folder highlighted, so the folder itself can be dragged onto the extensions page.
        Process.Start("explorer.exe", $"/select,\"{ExtensionInstaller.DeployDir}\"");
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
