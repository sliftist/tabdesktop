using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace TabDesktop;

public sealed record BrowserTab(int Id, int WindowId, string Title, string? Url, bool Active, int Index);

// Receives thumbnails pushed by the companion browser extension (BrowserExtension/) over loopback. The extension runs inside the page, so it can deliver thumbnails for auth-gated sites (Jellyfin, anything behind a login) that no fetch from this process could reach; reports arrive keyed by document.title, which is exactly the page-title portion of the browser's window title. A raw TcpListener is used instead of HttpListener because http.sys prefix reservations require elevation; the extension only ever sends small JSON POSTs, so a minimal HTTP/1.1 reader suffices.
public static class ExtensionThumbnails
{
    public const int Port = 38472;
    private const int MaxRequestBytes = 8 * 1024 * 1024;
    private const int MaxCachedTitles = 20_000;
    private static readonly TimeSpan SocketTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ImageDownloadTimeout = TimeSpan.FromSeconds(10);
    // Every page reports on a 15 s cycle, so three missed cycles means the extension really is gone (browser closed, extension removed) rather than momentarily quiet.
    private static readonly TimeSpan ConnectedTimeout = TimeSpan.FromSeconds(45);
    // Pings keep the MV3 service worker alive (incoming socket messages reset its idle timer) and double as dead-socket detection.
    private static readonly TimeSpan WebSocketPingInterval = TimeSpan.FromSeconds(20);
    // The title→URL map persists so disk-cached thumbnails resolve at startup, before the extension's first report; saved on a timer because reports mutate it constantly.
    private static readonly string UrlsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "thumbnail-urls.json");
    private static readonly TimeSpan UrlsSaveInterval = TimeSpan.FromMinutes(1);
    // Fixed by RFC 6455 for computing Sec-WebSocket-Accept.
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private static readonly HttpClient http = new() { Timeout = ImageDownloadTimeout };
    private static readonly object gate = new();
    private static readonly Dictionary<string, ImageSource> thumbnailsByTitle = new();
    private static readonly Dictionary<string, string?> urlsByTitle = new();
    private static readonly Queue<string> insertionOrder = new();
    private static Dictionary<int, List<BrowserTab>> tabsByWindowId = new();
    private static readonly object socketsGate = new();
    private static readonly List<WebSocketConnection> sockets = new();
    private static Timer? pingTimer;
    private static Timer? urlSaveTimer;
    private static bool urlsDirty;
    private static readonly HashSet<string> diskLoadPending = new();
    private static readonly HashSet<string> diskLoadMissed = new();

    public static event Action? Updated;

    // Fired only on tab-list reports (activate/move/create/close), so listeners can rebuild group layout without doing that work on every thumbnail report.
    public static event Action? TabsChanged;

    private static long lastReportAt;

    public static bool IsConnected
    {
        get
        {
            long at = Interlocked.Read(ref lastReportAt);
            return at != 0 && Stopwatch.GetElapsedTime(at) < ConnectedTimeout;
        }
    }

    public static ImageSource? TryGet(string windowTitle)
    {
        string? pageTitle = BrowserFavicon.GetPageTitle(windowTitle);
        if (pageTitle is null)
        {
            return null;
        }
        string key = BrowserFavicon.StripNotificationCount(pageTitle);
        string? url;
        lock (gate)
        {
            url = urlsByTitle.GetValueOrDefault(key);
            if (thumbnailsByTitle.TryGetValue(key, out ImageSource? image))
            {
                return image;
            }
            if (url is null || diskLoadMissed.Contains(key) || !diskLoadPending.Add(key))
            {
                return null;
            }
        }
        // Memory miss with a known URL: the disk cache may still have it from a previous run, or failing that a same-domain cached URL sharing the identifying query parameter. Off-thread because this getter runs on the UI thread inside bindings.
        Task.Run(() =>
        {
            ImageSource? loaded = ThumbnailDiskCache.TryLoad(url) ?? ThumbnailFuzzyMatch.TryLoad(url);
            lock (gate)
            {
                diskLoadPending.Remove(key);
                if (loaded is not null)
                {
                    thumbnailsByTitle[key] = loaded;
                }
                else
                {
                    diskLoadMissed.Add(key);
                }
            }
            if (loaded is not null)
            {
                Updated?.Invoke();
            }
        });
        return null;
    }

    // Bumped whenever the title→URL map changes so the Thumbnails tab can tell its snapshot is out of date.
    private static int savedUrlsVersion;
    public static int SavedUrlsVersion => Volatile.Read(ref savedUrlsVersion);

    // Snapshot of the persisted title→URL map, for the Thumbnails browse tab.
    public static List<(string Title, string? Url)> SnapshotSavedUrls()
    {
        lock (gate)
        {
            return urlsByTitle.Select(pair => (pair.Key, pair.Value)).ToList();
        }
    }

    // The URL of the report that carried a title; lets the whitelist toggle learn a tab's domain without touching History.
    public static string? TryGetUrl(string windowTitle)
    {
        string? pageTitle = BrowserFavicon.GetPageTitle(windowTitle);
        if (pageTitle is null)
        {
            return null;
        }
        lock (gate)
        {
            return urlsByTitle.GetValueOrDefault(BrowserFavicon.StripNotificationCount(pageTitle));
        }
    }

    public static void Start()
    {
        LoadUrls();
        pingTimer = new Timer(_ => Broadcast(0x9, Array.Empty<byte>()), null, WebSocketPingInterval, WebSocketPingInterval);
        urlSaveTimer = new Timer(_ => SaveUrlsIfDirty(), null, UrlsSaveInterval, UrlsSaveInterval);
        Task.Run(() =>
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                AppLog.Write(nameof(ExtensionThumbnails), $"Listening for extension reports on 127.0.0.1:{Port}");
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    Task.Run(() => HandleClient(client));
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(ExtensionThumbnails), $"Listener failed (extension thumbnails disabled): {ex}");
            }
        });
    }

    private static void HandleClient(TcpClient client)
    {
        ThumbnailReport? report = null;
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = (int)SocketTimeout.TotalMilliseconds;
                stream.WriteTimeout = (int)SocketTimeout.TotalMilliseconds;
                (string headerText, byte[] body)? request = ReadRequest(stream);
                if (request is null)
                {
                    return;
                }
                string requestLine = request.Value.headerText.Split("\r\n")[0];
                if (requestLine.StartsWith("GET /ws", StringComparison.Ordinal))
                {
                    // Runs the connection's whole lifetime; the enclosing using disposes the client when the socket dies.
                    RunWebSocket(stream, request.Value.headerText);
                    return;
                }
                if (requestLine.StartsWith("OPTIONS ", StringComparison.Ordinal))
                {
                    // Allow-Private-Network satisfies Chromium's Private Network Access preflight for requests targeting localhost.
                    WriteResponse(stream, "204 No Content", "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: POST\r\nAccess-Control-Allow-Headers: Content-Type\r\nAccess-Control-Allow-Private-Network: true\r\n");
                    return;
                }
                if (!requestLine.StartsWith("POST ", StringComparison.Ordinal))
                {
                    WriteResponse(stream, "404 Not Found", "");
                    return;
                }
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                if (requestLine.StartsWith("POST /tabs", StringComparison.Ordinal))
                {
                    TabsReport? tabsReport = JsonSerializer.Deserialize<TabsReport>(request.Value.body, options);
                    Interlocked.Exchange(ref lastReportAt, Stopwatch.GetTimestamp());
                    WriteResponse(stream, "200 OK", "Access-Control-Allow-Origin: *\r\n");
                    if (tabsReport is not null)
                    {
                        StoreTabs(tabsReport);
                    }
                    return;
                }
                report = JsonSerializer.Deserialize<ThumbnailReport>(request.Value.body, options);
                if (report is not null)
                {
                    Interlocked.Exchange(ref lastReportAt, Stopwatch.GetTimestamp());
                }
                WriteResponse(stream, "200 OK", "Access-Control-Allow-Origin: *\r\n");
            }
            if (report is not null)
            {
                ProcessReport(report);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExtensionThumbnails), $"Report failed{(report is null ? "" : $" for \"{report.Title}\"")}: {ex}");
        }
    }

    private static (string headerText, byte[] body)? ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd;
        while ((headerEnd = FindHeaderEnd(buffer)) < 0)
        {
            if (buffer.Length > MaxRequestBytes)
            {
                return null;
            }
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        string headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        int contentLength = ParseContentLength(headerText);
        if (contentLength < 0 || contentLength > MaxRequestBytes)
        {
            return null;
        }
        int bodyStart = headerEnd + 4;
        while (buffer.Length - bodyStart < contentLength)
        {
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }
            buffer.Write(chunk, 0, read);
        }
        var body = new byte[Math.Min(contentLength, buffer.Length - bodyStart)];
        Array.Copy(buffer.GetBuffer(), bodyStart, body, 0, body.Length);
        return (headerText, body);
    }

    private static int FindHeaderEnd(MemoryStream buffer)
    {
        byte[] data = buffer.GetBuffer();
        for (int i = 0; i + 3 < buffer.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static int ParseContentLength(string headerText)
    {
        foreach (string line in headerText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(line["Content-Length:".Length..].Trim(), out int length) ? length : -1;
            }
        }
        return 0;
    }

    private static void WriteResponse(NetworkStream stream, string status, string extraHeaders)
    {
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extraHeaders}Content-Length: 0\r\nConnection: close\r\n\r\n");
        stream.Write(response, 0, response.Length);
    }

    private static void ProcessReport(ThumbnailReport report)
    {
        if (string.IsNullOrEmpty(report.Title))
        {
            return;
        }
        string key = BrowserFavicon.StripNotificationCount(report.Title);
        lock (gate)
        {
            RecordUrlForTitleLocked(key, report.Url);
            // A fresh report may carry an image the disk cache lacked earlier.
            diskLoadMissed.Remove(key);
        }
        byte[]? imageBytes = null;
        if (!string.IsNullOrEmpty(report.ImageDataUrl))
        {
            imageBytes = DecodeDataUrl(report.ImageDataUrl);
        }
        else if (!string.IsNullOrEmpty(report.ImageUrl))
        {
            imageBytes = http.GetByteArrayAsync(report.ImageUrl).GetAwaiter().GetResult();
        }
        if (imageBytes is null)
        {
            return;
        }
        ImageSource image = BrowserFavicon.DecodeImage(imageBytes);
        if (!string.IsNullOrEmpty(report.Url))
        {
            ThumbnailDiskCache.Save(report.Url, imageBytes);
        }
        lock (gate)
        {
            thumbnailsByTitle[key] = image;
        }
        Updated?.Invoke();
    }

    // Caller holds gate. A changed URL also clears the disk-miss marker — the new URL may hit the cache (or fuzzy-match) where the old one didn't.
    private static void RecordUrlForTitleLocked(string key, string? url)
    {
        if (!urlsByTitle.TryGetValue(key, out string? existingUrl))
        {
            insertionOrder.Enqueue(key);
            urlsDirty = true;
            Interlocked.Increment(ref savedUrlsVersion);
            diskLoadMissed.Remove(key);
        }
        else if (existingUrl != url)
        {
            urlsDirty = true;
            Interlocked.Increment(ref savedUrlsVersion);
            diskLoadMissed.Remove(key);
        }
        urlsByTitle[key] = url;
        while (insertionOrder.Count > MaxCachedTitles)
        {
            string evicted = insertionOrder.Dequeue();
            urlsByTitle.Remove(evicted);
            thumbnailsByTitle.Remove(evicted);
            urlsDirty = true;
            Interlocked.Increment(ref savedUrlsVersion);
        }
    }

    // All tabs of the browser window whose active tab matches the window title; falls back to null while no report has arrived.
    public static List<BrowserTab>? TryGetTabsForWindow(string windowTitle)
    {
        string? pageTitle = BrowserFavicon.GetPageTitle(windowTitle);
        if (pageTitle is null)
        {
            return null;
        }
        string key = BrowserFavicon.StripNotificationCount(pageTitle);
        lock (gate)
        {
            foreach (List<BrowserTab> tabs in tabsByWindowId.Values)
            {
                BrowserTab? active = tabs.FirstOrDefault(t => t.Active);
                if (active is not null && BrowserFavicon.StripNotificationCount(active.Title) == key)
                {
                    return tabs;
                }
            }
        }
        return null;
    }

    public static List<BrowserTab>? TryGetTabsByWindowId(int windowId)
    {
        lock (gate)
        {
            return tabsByWindowId.GetValueOrDefault(windowId);
        }
    }

    public static void ActivateTab(BrowserTab tab)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "activateTab", tabId = tab.Id, windowId = tab.WindowId });
        if (!Broadcast(0x1, payload))
        {
            AppLog.Write(nameof(ExtensionThumbnails), $"No extension command socket connected — cannot switch to tab \"{tab.Title}\".");
        }
        // Optimistic local switch, same as MoveTab's reorder: the strip highlights the clicked tab instantly instead of waiting for the browser's confirmation report, which then converges to the actual state.
        lock (gate)
        {
            if (tabsByWindowId.TryGetValue(tab.WindowId, out List<BrowserTab>? tabs))
            {
                tabsByWindowId[tab.WindowId] = tabs.Select(t => t with { Active = t.Id == tab.Id }).ToList();
            }
        }
        TabsChanged?.Invoke();
    }

    public static void MoveTab(BrowserTab tab, int newIndex)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "moveTab", tabId = tab.Id, index = newIndex });
        if (!Broadcast(0x1, payload))
        {
            AppLog.Write(nameof(ExtensionThumbnails), $"No extension command socket connected — cannot move tab \"{tab.Title}\".");
        }
        // Optimistic local reorder so the strip snaps instantly instead of waiting for the browser's confirmation report; that report then converges to the browser's actual result (which may clamp, e.g. around pinned tabs).
        lock (gate)
        {
            if (tabsByWindowId.TryGetValue(tab.WindowId, out List<BrowserTab>? tabs))
            {
                int current = tabs.FindIndex(t => t.Id == tab.Id);
                if (current >= 0)
                {
                    var reordered = new List<BrowserTab>(tabs);
                    BrowserTab moving = reordered[current];
                    reordered.RemoveAt(current);
                    reordered.Insert(Math.Clamp(newIndex, 0, reordered.Count), moving);
                    tabsByWindowId[tab.WindowId] = reordered.Select((t, i) => t with { Index = i }).ToList();
                }
            }
        }
        TabsChanged?.Invoke();
    }

    private static void StoreTabs(TabsReport report)
    {
        var next = new Dictionary<int, List<BrowserTab>>();
        foreach (WindowReport window in report.Windows)
        {
            next[window.WindowId] = window.Tabs.OrderBy(t => t.Index).Select(t => new BrowserTab(t.Id, window.WindowId, t.Title, t.Url, t.Active, t.Index)).ToList();
        }
        lock (gate)
        {
            tabsByWindowId = next;
            // Tab reports arrive on tab events — well before the 15 s thumbnail cycle — so learning title→URL here lets the disk cache and fuzzy matcher resolve a freshly loaded page's thumbnail immediately instead of showing the favicon until the first thumbnail report.
            foreach (List<BrowserTab> tabs in next.Values)
            {
                foreach (BrowserTab tab in tabs)
                {
                    if (tab.Title.Length > 0 && !string.IsNullOrEmpty(tab.Url))
                    {
                        RecordUrlForTitleLocked(BrowserFavicon.StripNotificationCount(tab.Title), tab.Url);
                    }
                }
            }
        }
        Updated?.Invoke();
        TabsChanged?.Invoke();
    }

    private static void RunWebSocket(NetworkStream stream, string headerText)
    {
        string? key = null;
        foreach (string line in headerText.Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                key = line["Sec-WebSocket-Key:".Length..].Trim();
            }
        }
        if (key is null)
        {
            WriteResponse(stream, "400 Bad Request", "");
            return;
        }
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        byte[] handshake = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
        stream.Write(handshake, 0, handshake.Length);
        // The client only speaks when answering our pings, so allow a few missed intervals before declaring the socket dead.
        stream.ReadTimeout = (int)(WebSocketPingInterval.TotalMilliseconds * 3);
        var connection = new WebSocketConnection(stream);
        lock (socketsGate)
        {
            sockets.Add(connection);
        }
        AppLog.Write(nameof(ExtensionThumbnails), "Extension command socket connected.");
        try
        {
            while (true)
            {
                (int Opcode, byte[] Payload)? frame = ReadFrame(stream);
                if (frame is null || frame.Value.Opcode == 0x8)
                {
                    break;
                }
                if (frame.Value.Opcode == 0x9)
                {
                    connection.Send(0xA, frame.Value.Payload);
                }
            }
        }
        finally
        {
            lock (socketsGate)
            {
                sockets.Remove(connection);
            }
        }
    }

    private static bool Broadcast(int opcode, byte[] payload)
    {
        List<WebSocketConnection> snapshot;
        lock (socketsGate)
        {
            snapshot = sockets.ToList();
        }
        bool any = false;
        foreach (WebSocketConnection connection in snapshot)
        {
            try
            {
                connection.Send(opcode, payload);
                any = true;
            }
            catch
            {
                // The reader loop on the connection's own task notices the dead socket and removes it.
            }
        }
        return any;
    }

    private static void LoadUrls()
    {
        try
        {
            if (!File.Exists(UrlsPath))
            {
                return;
            }
            Dictionary<string, string?> loaded = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(UrlsPath)) ?? new Dictionary<string, string?>();
            lock (gate)
            {
                foreach ((string title, string? url) in loaded)
                {
                    if (urlsByTitle.TryAdd(title, url))
                    {
                        insertionOrder.Enqueue(title);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExtensionThumbnails), ex.ToString());
        }
    }

    private static void SaveUrlsIfDirty()
    {
        try
        {
            Dictionary<string, string?> snapshot;
            lock (gate)
            {
                if (!urlsDirty)
                {
                    return;
                }
                urlsDirty = false;
                snapshot = new Dictionary<string, string?>(urlsByTitle);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(UrlsPath)!);
            File.WriteAllText(UrlsPath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExtensionThumbnails), ex.ToString());
        }
    }

    // Data URLs carry either base64 (";base64" in the header) or percent-encoded text after the comma.
    private static byte[]? DecodeDataUrl(string dataUrl)
    {
        int comma = dataUrl.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }
        string header = dataUrl[..comma];
        string payload = dataUrl[(comma + 1)..];
        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.FromBase64String(payload);
        }
        return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
    }

    private static (int Opcode, byte[] Payload)? ReadFrame(NetworkStream stream)
    {
        int b0 = stream.ReadByte();
        int b1 = stream.ReadByte();
        if (b0 < 0 || b1 < 0)
        {
            return null;
        }
        int opcode = b0 & 0x0F;
        bool masked = (b1 & 0x80) != 0;
        long length = b1 & 0x7F;
        if (length == 126)
        {
            var extended = new byte[2];
            if (!ReadExact(stream, extended))
            {
                return null;
            }
            length = (extended[0] << 8) | extended[1];
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            if (!ReadExact(stream, extended))
            {
                return null;
            }
            length = BinaryPrimitives.ReadInt64BigEndian(extended);
        }
        if (length < 0 || length > MaxRequestBytes)
        {
            return null;
        }
        var mask = new byte[4];
        if (masked && !ReadExact(stream, mask))
        {
            return null;
        }
        var payload = new byte[length];
        if (!ReadExact(stream, payload))
        {
            return null;
        }
        if (masked)
        {
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i % 4];
            }
        }
        return (opcode, payload);
    }

    private static bool ReadExact(NetworkStream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private sealed class WebSocketConnection
    {
        private readonly object writeGate = new();
        private readonly NetworkStream stream;

        public WebSocketConnection(NetworkStream stream)
        {
            this.stream = stream;
        }

        // Serialized because the ping timer and tab-activation commands write from different threads.
        public void Send(int opcode, byte[] payload)
        {
            lock (writeGate)
            {
                using var frame = new MemoryStream();
                frame.WriteByte((byte)(0x80 | opcode));
                if (payload.Length < 126)
                {
                    frame.WriteByte((byte)payload.Length);
                }
                else
                {
                    frame.WriteByte(126);
                    frame.WriteByte((byte)(payload.Length >> 8));
                    frame.WriteByte((byte)payload.Length);
                }
                frame.Write(payload, 0, payload.Length);
                byte[] bytes = frame.ToArray();
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }

    private sealed class ThumbnailReport
    {
        public string Title { get; set; } = "";
        public string? Url { get; set; }
        public string? ImageDataUrl { get; set; }
        public string? ImageUrl { get; set; }
    }

    private sealed class TabsReport
    {
        public List<WindowReport> Windows { get; set; } = new();
    }

    private sealed class WindowReport
    {
        public int WindowId { get; set; }
        public List<TabReport> Tabs { get; set; } = new();
    }

    private sealed class TabReport
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string? Url { get; set; }
        public bool Active { get; set; }
        public int Index { get; set; }
    }
}
