using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabDesktop;

public sealed class WindowEntry : INotifyPropertyChanged
{
    public required IntPtr Hwnd { get; init; }
    public required uint Pid { get; init; }

    private string title = "";
    public string Title
    {
        get => title;
        set
        {
            if (Set(ref title, value))
            {
                RefreshDerived();
            }
        }
    }

    public string DisplayTitle => TitleRules.Simplify(title);

    public ImageSource? IconImage => CursorFavicon.TryGet(title) ?? TitleRules.GetIcon(title);

    public void RefreshDerived()
    {
        Raise(nameof(DisplayTitle));
        Raise(nameof(IconImage));
    }

    private string positionText = "";
    public string PositionText { get => positionText; set => Set(ref positionText, value); }

    private string sizeText = "";
    public string SizeText { get => sizeText; set => Set(ref sizeText, value); }

    private string status = "";
    public string Status { get => status; set => Set(ref status, value); }

    private BitmapSource? thumbnail;
    public BitmapSource? Thumbnail { get => thumbnail; set => Set(ref thumbnail, value); }

    private double canvasLeft;
    public double CanvasLeft { get => canvasLeft; set => Set(ref canvasLeft, value); }

    private double canvasTop;
    public double CanvasTop { get => canvasTop; set => Set(ref canvasTop, value); }

    private double layoutWidth;
    public double LayoutWidth { get => layoutWidth; set => Set(ref layoutWidth, value); }

    private double layoutHeight;
    public double LayoutHeight { get => layoutHeight; set => Set(ref layoutHeight, value); }

    private int zIndex;
    public int ZIndex { get => zIndex; set => Set(ref zIndex, value); }

    private bool showInLayout;
    public bool ShowInLayout { get => showInLayout; set => Set(ref showInLayout, value); }

    private bool isForeground;
    public bool IsForeground { get => isForeground; set => Set(ref isForeground, value); }

    private bool isGroupLastFocused;
    public bool IsGroupLastFocused { get => isGroupLastFocused; set => Set(ref isGroupLastFocused, value); }

    private bool isDragging;
    public bool IsDragging { get => isDragging; set => Set(ref isDragging, value); }

    // Tab sort key: defaults to the detection timestamp (spaced at least 1 apart), replaced by a neighbor-midpoint value on manual reorder. Not bound in XAML, so no INPC.
    public double OrderKey { get; set; }

    public long LastFocusedAt { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
