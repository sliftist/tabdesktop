using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabDesktop;

public sealed class WindowEntry : INotifyPropertyChanged
{
    public required IntPtr Hwnd { get; init; }
    public required uint Pid { get; init; }
    public string? ExePath { get; init; }

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

    // Non-null when the user opted this window's process into directory titles; wins over the title-rule pipeline.
    private string? directoryTitle;
    public string? DirectoryTitle
    {
        get => directoryTitle;
        set
        {
            if (Set(ref directoryTitle, value))
            {
                Raise(nameof(DisplayTitle));
            }
        }
    }

    public string DisplayTitle => directoryTitle ?? TitleRules.Simplify(title);

    // Extension-pushed thumbnails (exact, auth-aware, any site) win over the History-based YouTube fallback; for screenshot-whitelisted executables the focused-window capture stands in.
    public ImageSource? VideoThumbnail => ExtensionThumbnails.TryGet(title) ?? YouTubeThumbnail.TryGet(title, NotifyIconResolved) ?? (IsScreenshotThumbnailEnabled ? thumbnail : null);

    public bool IsBrowserTab => BrowserFavicon.GetPageTitle(title) is not null;

    // Non-null marks this entry as a pseudo-entry for one browser tab of an expanded window; clicking it activates that tab instead of just focusing the window.
    private BrowserTab? browserTabValue;
    public BrowserTab? BrowserTab { get => browserTabValue; set => Set(ref browserTabValue, value); }

    // Per-window (not persisted): show one strip entry per browser tab, fed by the extension's tab reports.
    private bool expandTabs;
    public bool ExpandTabs { get => expandTabs; set => Set(ref expandTabs, value); }

    // Shown on browser windows and on their expanded tab entries alike — toggling from a tab entry collapses the parent window's expansion.
    public bool CanExpandTabs => IsBrowserTab;

    public bool IsVideoThumbnailEnabled => ThumbnailWhitelist.IsDomainWhitelisted(TabDomains.TryGet(title, NotifyIconResolved));

    public bool IsScreenshotThumbnailEnabled => ThumbnailWhitelist.IsScreenshotExe(ExePath);

    // The video thumbnail supersedes the small favicon (which would just be the YouTube logo next to the video's own image).
    public ImageSource? IconImage => VideoThumbnail is not null ? null : BrowserFavicon.TryGet(title, NotifyIconResolved) ?? CursorFavicon.TryGet(title) ?? TitleRules.GetIcon(title) ?? WindowIcon.TryGet(Hwnd, Pid, NotifyIconResolved);

    // Icon/thumbnail resolution finishes on a background task; hop to the UI thread and re-raise so the bindings re-read the now-cached images.
    private void NotifyIconResolved()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Raise(nameof(VideoThumbnail));
            Raise(nameof(IconImage));
            Raise(nameof(IsVideoThumbnailEnabled));
        });
    }

    public void RefreshDerived()
    {
        Raise(nameof(DisplayTitle));
        Raise(nameof(VideoThumbnail));
        Raise(nameof(IconImage));
        Raise(nameof(IsBrowserTab));
        Raise(nameof(CanExpandTabs));
        Raise(nameof(IsVideoThumbnailEnabled));
        Raise(nameof(IsScreenshotThumbnailEnabled));
    }

    private string positionText = "";
    public string PositionText { get => positionText; set => Set(ref positionText, value); }

    private string sizeText = "";
    public string SizeText { get => sizeText; set => Set(ref sizeText, value); }

    private string status = "";
    public string Status { get => status; set => Set(ref status, value); }

    private BitmapSource? thumbnail;
    public BitmapSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            if (Set(ref thumbnail, value))
            {
                Raise(nameof(VideoThumbnail));
            }
        }
    }

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
