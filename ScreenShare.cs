using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace LocalSendDrop;

/// <summary>"Show my screen" offered by another PC: where to watch it.</summary>
sealed record ScreenOffer(string Alias, string Fingerprint, string Ip, int Port, string Token);

/// <summary>
/// LocalSend Drop extension: show your screen to a colleague, view only.
/// The sharer offers (POST /screen/offer on the viewer); the viewer then pulls
/// JPEG frames from the sharer (GET /screen?token=), length-prefixed, about 8 a second.
/// </summary>
static class ScreenShare
{
    sealed class Share
    {
        public required string Token;
        public required string Viewer;       // their fingerprint
        public required string ViewerName;
        public required Rectangle Area;      // the monitor being shown
        public int Watching;
        public bool Watched;
        public readonly CancellationTokenSource Cts = new();
    }

    static Share? _current;

    /// <summary>On the UI thread's side: sharing started/stopped, or the viewer came or went.</summary>
    public static event Action? Changed;

    public static bool Active => _current != null;
    public static string? ViewerName => _current?.ViewerName;
    public static bool Watching => _current is { Watching: > 0 };
    public static bool WasWatched => _current is { Watched: true };

    public static string Start(string viewerFingerprint, string viewerName, Rectangle area)
    {
        Stop();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _current = new Share { Token = token, Viewer = viewerFingerprint, ViewerName = viewerName, Area = area };
        Changed?.Invoke();
        return token;
    }

    public static void Stop()
    {
        if (Interlocked.Exchange(ref _current, null) is not { } s) return;
        s.Cts.Cancel();
        Changed?.Invoke();
    }

    /// <returns>null if they were asked, otherwise why not.</returns>
    public static async Task<string?> OfferAsync(Identity id, int ourPort, Device target, string token)
    {
        try
        {
            using var client = LsHttp.Create(id, LsHttp.PinFor(target), TimeSpan.FromSeconds(8));
            var resp = await client.PostAsJsonAsync(target.Root + "/api/localsend-drop/v1/screen/offer",
                new { alias = id.Alias, fingerprint = id.Fingerprint, port = ourPort, token }, Json.Options);
            return (int)resp.StatusCode switch
            {
                200 => null,
                423 => "they have Do not disturb on",
                404 or 405 => "they need the latest LocalSend Drop",
                var code => $"their PC answered {code}",
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return "their PC didn't answer"; }
    }

    /// <summary>Serves the frames to the viewer until either side stops.</summary>
    public static async Task Stream(HttpContext ctx)
    {
        var s = _current;
        var token = ctx.Request.Query["token"].ToString();
        if (s == null || token.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(s.Token)))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        // If they proved who they are, it has to be the one we offered it to.
        if (ctx.Connection.ClientCertificate is { } cc && s.Viewer.Length > 0 &&
            !string.Equals(Identity.FingerprintOf(cc), s.Viewer, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.CacheControl = "no-store";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, s.Cts.Token);
        Interlocked.Increment(ref s.Watching);
        s.Watched = true;
        Changed?.Invoke();
        try
        {
            var length = new byte[4];
            while (!stop.IsCancellationRequested)
            {
                var started = Environment.TickCount64;
                var frame = Capture(s.Area);
                BitConverter.TryWriteBytes(length, frame.Length);
                await ctx.Response.Body.WriteAsync(length, stop.Token);
                await ctx.Response.Body.WriteAsync(frame, stop.Token);
                await ctx.Response.Body.FlushAsync(stop.Token);
                var wait = 120 - (int)(Environment.TickCount64 - started);
                if (wait > 0) await Task.Delay(wait, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            Interlocked.Decrement(ref s.Watching);
            // They closed the window: nobody's watching, so stop sharing.
            if (_current == s && s.Watching == 0) Stop();
            else Changed?.Invoke();
        }
    }

    /// <summary>The monitor as a JPEG, cursor included, at most 1920 px wide.</summary>
    static byte[] Capture(Rectangle area)
    {
        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref ci) && (ci.flags & 1) != 0 && area.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y))
            {
                var hdc = g.GetHdc();
                try
                {
                    var hotspot = GetIconInfo(ci.hCursor, out var info) ? new Point(info.xHotspot, info.yHotspot) : Point.Empty;
                    if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                    if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                    DrawIconEx(hdc, ci.ptScreenPos.X - area.Left - hotspot.X, ci.ptScreenPos.Y - area.Top - hotspot.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
                }
                finally { g.ReleaseHdc(hdc); }
            }
        }
        var scale = Math.Min(1.0, 1920.0 / area.Width);
        using var ms = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 65L) } };
        if (scale >= 1) full.Save(ms, jpeg, quality);
        else
        {
            using var small = new Bitmap((int)(area.Width * scale), (int)(area.Height * scale), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, new Rectangle(Point.Empty, small.Size));
            }
            small.Save(ms, jpeg, quality);
        }
        return ms.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}

/// <summary>While you're sharing: a small bar at the top of the screen with Stop. Left out of what they see.</summary>
sealed class SharingBar : Form
{
    readonly PillButton _stop = new("Stop");
    readonly System.Windows.Forms.Timer _noShow = new() { Interval = 60_000 };

    public SharingBar(Rectangle area)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(196, 43, 28);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _stop.Click += (_, _) => ScreenShare.Stop();
        Controls.Add(_stop);
        int w = S(380), h = S(44);
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + S(8), w, h);
        _stop.SetBounds(w - S(96), S(6), S(88), S(32));
        ScreenShare.Changed += OnChanged;
        // Nobody opened it within a minute: stop offering.
        _noShow.Tick += (_, _) => { _noShow.Stop(); if (!ScreenShare.WasWatched) ScreenShare.Stop(); };
        _noShow.Start();
    }

    int S(int v) => LogicalToDeviceUnits(v);
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.Chrome(Handle);
        SetWindowDisplayAffinity(Handle, 0x11);   // WDA_EXCLUDEFROMCAPTURE: not in the picture they see
    }

    void OnChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() =>
        {
            if (!ScreenShare.Active) { Close(); return; }
            Invalidate();
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillEllipse(Brushes.White, S(14), (Height - S(10)) / 2, S(10), S(10));
        var who = ScreenShare.ViewerName ?? "them";
        var text = ScreenShare.Watching ? $"{who} is watching your screen" : $"Waiting for {who} to watch…";
        Ui.DrawText(g, text, Ui.BodyStrong, new Rectangle(S(34), 0, _stop.Left - S(40), Height), Color.White);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ScreenShare.Changed -= OnChanged;
        _noShow.Dispose();
        base.OnFormClosed(e);
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}

/// <summary>Watching a colleague's screen: frames pulled from their PC, fitted to the window.</summary>
sealed class ScreenViewer : Form
{
    readonly ScreenOffer _offer;
    readonly Identity _id;
    readonly CancellationTokenSource _cts = new();
    Image? _frame;
    string _status = "Connecting…";
    FormWindowState _before;
    bool _sized;

    public ScreenViewer(ScreenOffer offer, Identity id)
    {
        _offer = offer;
        _id = id;
        Text = $"{offer.Alias}'s screen";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Bounds = new Rectangle(wa.Left + wa.Width / 8, wa.Top + wa.Height / 8, wa.Width * 3 / 4, wa.Height * 3 / 4);
        KeyPreview = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.Chrome(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = Watch();
    }

    async Task Watch()
    {
        try
        {
            using var client = LsHttp.Create(_id, _offer.Fingerprint.Length > 0 ? _offer.Fingerprint : null, Timeout.InfiniteTimeSpan);
            var url = $"https://{_offer.Ip}:{_offer.Port}/api/localsend-drop/v1/screen?token={Uri.EscapeDataString(_offer.Token)}";
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            if (!resp.IsSuccessStatusCode) { Show_($"{_offer.Alias} has stopped sharing"); return; }
            await using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token);
            var length = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(length, _cts.Token);
                var size = BitConverter.ToInt32(length);
                if (size <= 0 || size > 32 << 20) break;
                var data = new byte[size];
                await stream.ReadExactlyAsync(data, _cts.Token);
                var image = Image.FromStream(new MemoryStream(data));
                if (IsDisposed) { image.Dispose(); return; }
                BeginInvoke(() =>
                {
                    var old = _frame;
                    _frame = image;
                    old?.Dispose();
                    if (!_sized) FitTo(image.Size);
                    Invalidate();
                });
            }
            Show_($"{_offer.Alias} has stopped sharing");
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (!_cts.IsCancellationRequested) { Show_($"{_offer.Alias} has stopped sharing"); }
    }

    void Show_(string status)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { _status = status; var old = _frame; _frame = null; old?.Dispose(); Invalidate(); });
    }

    /// <summary>First frame: match their screen's shape, within the same space.</summary>
    void FitTo(Size picture)
    {
        _sized = true;
        if (WindowState != FormWindowState.Normal) return;
        var area = ClientSize;
        var scale = Math.Min((double)area.Width / picture.Width, (double)area.Height / picture.Height);
        var fit = new Size((int)(picture.Width * scale), (int)(picture.Height * scale));
        var dw = area.Width - fit.Width;
        var dh = area.Height - fit.Height;
        Bounds = new Rectangle(Left + dw / 2, Top + dh / 2, Width - dw, Height - dh);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        if (_frame == null)
        {
            Ui.DrawText(g, _status, Ui.Subtitle, ClientRectangle, Color.FromArgb(200, 200, 200),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var area = ClientRectangle;
        var scale = Math.Min((double)area.Width / _frame.Width, (double)area.Height / _frame.Height);
        var w = (int)(_frame.Width * scale);
        var h = (int)(_frame.Height * scale);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(_frame, new Rectangle((area.Width - w) / 2, (area.Height - h) / 2, w, h));
    }

    // Double-click or F11: full screen; Esc leaves it.
    protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); ToggleFull(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F11 || (e.KeyCode == Keys.Escape && FormBorderStyle == FormBorderStyle.None)) ToggleFull();
    }

    void ToggleFull()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _before;
        }
        else
        {
            _before = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _frame?.Dispose();
        base.OnFormClosed(e);
    }
}
