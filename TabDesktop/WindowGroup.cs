namespace TabDesktop;

public sealed class WindowGroup
{
    public required double CanvasLeft { get; init; }
    public required double CanvasTop { get; init; }
    public required double ScreenLeft { get; init; }
    public required double ScreenTop { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required string CountText { get; init; }
    public required List<WindowEntry> Members { get; init; }
    public required int ZIndex { get; init; }
}
