using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TabDesktop.Interop;

namespace TabDesktop;

// The global-hotkey search popup: type to filter every tab, Enter (or click) to jump to the selected one. Non-modal singleton owned by MainWindow, like every other helper window.
public partial class SearchWindow : Window
{
    private const int MaxResults = 10;

    private readonly Func<List<WindowEntry>> getEntries;
    private readonly Action<WindowEntry> activateRequested;
    private List<SearchResult> results = new();
    private int selectedIndex = -1;
    private bool dismissed;
    private bool everActivated;

    public SearchWindow(Func<List<WindowEntry>> getEntries, Action<WindowEntry> activateRequested)
    {
        this.getEntries = getEntries;
        this.activateRequested = activateRequested;
        InitializeComponent();
        // Tool-window ex-style keeps the popup out of alt-tab and out of our own scanner, so it never shows up as a searchable tab itself.
        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            long exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW));
        };
        Loaded += (_, _) =>
        {
            ForceForeground();
            QueryBox.Focus();
            RefreshResults();
        };
        Activated += (_, _) => everActivated = true;
        // A palette that lingers after clicking elsewhere is just clutter; losing focus dismisses it like the Windows search flyout. Gated on having actually been activated: opened from the keyboard hook the process has no foreground-activation grant (unlike RegisterHotKey), and without the gate a failed activation would fire Deactivated and close the window the instant it appears.
        Deactivated += (_, _) =>
        {
            if (everActivated)
            {
                Dismiss();
            }
        };
        // Closing itself deactivates the window, so the Deactivated handler re-enters Close mid-close and WPF throws; Closing marks the window dismissed first so that re-entry no-ops. Covers external Close() calls (the hotkey toggle) too.
        Closing += (_, _) => dismissed = true;
        SizeChanged += (_, _) => CenterOnVirtualScreen();
    }

    private void Dismiss()
    {
        if (dismissed)
        {
            return;
        }
        dismissed = true;
        Close();
    }

    // SetForegroundWindow refuses callers that didn't receive the last input, and a keyboard-hook callback doesn't count as receiving input; temporarily attaching to the current foreground window's input queue borrows its right to take focus — the standard launcher-palette trick.
    private void ForceForeground()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint ourThread = NativeMethods.GetCurrentThreadId();
        uint foregroundThread = foreground != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(foreground, out _) : 0;
        bool attached = foregroundThread != 0 && foregroundThread != ourThread && NativeMethods.AttachThreadInput(foregroundThread, ourThread, true);
        try
        {
            NativeMethods.BringWindowToTop(handle);
            NativeMethods.SetForegroundWindow(handle);
            Activate();
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(foregroundThread, ourThread, false);
            }
        }
    }

    private void CenterOnVirtualScreen()
    {
        Left = SystemParameters.VirtualScreenLeft + (SystemParameters.VirtualScreenWidth - ActualWidth) / 2;
        Top = SystemParameters.VirtualScreenTop + (SystemParameters.VirtualScreenHeight - ActualHeight) / 2;
    }

    private void OnQueryChanged(object sender, RoutedEventArgs e)
    {
        RefreshResults();
    }

    private void RefreshResults()
    {
        results = TabSearch.Rank(getEntries(), QueryBox.Text, MaxResults).Select(entry => new SearchResult(entry)).ToList();
        Results.ItemsSource = results;
        Select(results.Count > 0 ? 0 : -1);
    }

    private void Select(int index)
    {
        if (selectedIndex >= 0 && selectedIndex < results.Count)
        {
            results[selectedIndex].IsSelected = false;
        }
        selectedIndex = index;
        if (selectedIndex >= 0 && selectedIndex < results.Count)
        {
            results[selectedIndex].IsSelected = true;
        }
    }

    private void Move(int delta)
    {
        if (results.Count == 0)
        {
            return;
        }
        Select(((selectedIndex + delta) % results.Count + results.Count) % results.Count);
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                Dismiss();
                e.Handled = true;
                break;
            case Key.Enter:
                ActivateSelected();
                e.Handled = true;
                break;
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Tab:
                Move(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    private void ActivateSelected()
    {
        if (selectedIndex < 0 || selectedIndex >= results.Count)
        {
            return;
        }
        WindowEntry entry = results[selectedIndex].Entry;
        Dismiss();
        activateRequested(entry);
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is SearchResult result)
        {
            Dismiss();
            activateRequested(result.Entry);
            e.Handled = true;
        }
    }

    // Selection lives here rather than on the shared WindowEntry so highlighting a result can't leak state into the strips.
    private sealed class SearchResult : INotifyPropertyChanged
    {
        public SearchResult(WindowEntry entry)
        {
            Entry = entry;
        }

        public WindowEntry Entry { get; }

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected != value)
                {
                    isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
