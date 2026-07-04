using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace TabDesktop;

// In-app error log shown in the main window's Log tab. Existing code reports failures via Trace.WriteLine, so Install() bridges all Trace output here instead of requiring every call site to change. Entries are newest-first so fresh errors are visible without scrolling; adds marshal to the UI thread because the bound ObservableCollection must only mutate there.
public static class AppLog
{
    private const int MaxEntries = 2000;

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    private static readonly string ErrorLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "errors.log");

    public static void Install()
    {
        Trace.Listeners.Add(new TraceBridge());
    }

    // One bad event handler must not take down every strip on the desktop, so dispatcher exceptions are swallowed after reporting. AppDomain exceptions can't be swallowed — the runtime terminates regardless — so the point there is getting the report onto disk before dying.
    public static void InstallCrashHandlers(System.Windows.Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            ReportCrash("DispatcherUnhandledException", e.Exception.ToString());
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ReportCrash("UnhandledException", e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "(no exception object)");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportCrash("UnobservedTaskException", e.Exception.ToString());
            e.SetObserved();
        };
    }

    private static void ReportCrash(string source, string message)
    {
        Write(source, message);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void Write(string source, string message)
    {
        var entry = new LogEntry { Time = DateTime.Now, Source = source, Message = message };
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        });
    }

    private sealed class TraceBridge : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                AppLog.Write("Trace", message);
            }
        }
    }
}

public sealed class LogEntry
{
    public required DateTime Time { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");

    public string FirstLine
    {
        get
        {
            int newline = Message.IndexOf('\n');
            return newline < 0 ? Message : Message[..newline].TrimEnd('\r');
        }
    }
}
