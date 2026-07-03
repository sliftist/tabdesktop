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
            QueryBox.Focus();
            RefreshResults();
        };
        // A palette that lingers after clicking elsewhere is just clutter; losing focus dismisses it like the Windows search flyout.
        Deactivated += (_, _) => Close();
        SizeChanged += (_, _) => CenterOnVirtualScreen();
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
                Close();
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
        Close();
        activateRequested(entry);
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is SearchResult result)
        {
            Close();
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
