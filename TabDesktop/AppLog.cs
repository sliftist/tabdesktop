using System.Collections.ObjectModel;
using System.Diagnostics;

namespace TabDesktop;

// In-app error log shown in the main window's Log tab. Existing code reports failures via Trace.WriteLine, so Install() bridges all Trace output here instead of requiring every call site to change. Entries are newest-first so fresh errors are visible without scrolling; adds marshal to the UI thread because the bound ObservableCollection must only mutate there.
public static class AppLog
{
    private const int MaxEntries = 2000;

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    public static void Install()
    {
        Trace.Listeners.Add(new TraceBridge());
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
