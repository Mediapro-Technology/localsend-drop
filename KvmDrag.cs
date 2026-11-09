using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LocalSendDrop;

/// <summary>
/// Dragging files from one paired PC to the other, like a local drag. The PC
/// with the files catches its own drag at the shared edge (<see cref="DropStrip"/>),
/// takes the image Windows is drawing, and offers the files. The other PC starts
/// copying them into a staging folder at once and runs a real Windows drag of its
/// own with the same image, so any folder or app can take the drop. If the drop
/// comes before the copy is done, letting go waits for it.
/// </summary>
static class KvmDrag
{
    static Identity? _id;
    static Discovery? _discovery;
    public static Action<Action> Ui = a => a();

    public static void Init(Identity id, Discovery discovery, Action<Action> ui)
    {
        _id = id;
        _discovery = discovery;
        Ui = ui;
        CleanUp();
    }

    // ---- on the wire ----

    public sealed record Entry(string Name, bool Folder);

    /// <summary>What's being carried: names at the top of the drag, and where the pointer sits in the image.</summary>
    public sealed record Meta(string Token, List<Entry> Entries, int OffsetX, int OffsetY);

    public static byte[] MetaMessage(Meta m, byte[]? png)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(m, Json.Options);
        png ??= [];
        var msg = new byte[1 + 4 + json.Length + png.Length];
        msg[0] = (byte)KvmMsg.DragMeta;
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(1), json.Length);
        json.CopyTo(msg, 5);
        png.CopyTo(msg, 5 + json.Length);
        return msg;
    }

    public static (Meta meta, Bitmap? image)? ReadMeta(byte[] m)
    {
        try
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(1));
            var meta = JsonSerializer.Deserialize<Meta>(m.AsSpan(5, len), Json.Options);
            if (meta == null || meta.Token.Length < 16 || meta.Entries.Count == 0) return null;
            var png = m.AsSpan(5 + len);
            Bitmap? image = null;
            if (png.Length > 0)
            {
                using var ms = new MemoryStream(png.ToArray());
                image = new Bitmap(ms);
            }
            return (meta, image);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or IndexOutOfRangeException) { return null; }
    }

    public static byte[] TokenMessage(KvmMsg type, string token) =>
        KvmWire.WithPayload(type, System.Text.Encoding.ASCII.GetBytes(token));

    public static string ReadToken(byte[] m) => System.Text.Encoding.ASCII.GetString(m, 1, m.Length - 1);

    // ---- the PC with the files ----

    sealed record Outgoing(List<string> Paths, string To, Bitmap? Image, Point Offset, CancellationTokenSource Cts);
    static readonly ConcurrentDictionary<string, Outgoing> Out = new();

    /// <summary>Offers files to a paired PC; returns what to send it.</summary>
    public static byte[] Offer(List<string> paths, string toFingerprint, Bitmap? image, Point offset, out string token)
    {
        token = Guid.NewGuid().ToString("N");
        Out[token] = new Outgoing(paths, toFingerprint, image, offset, new CancellationTokenSource());
        var entries = paths.Select(p => new Entry(Path.GetFileName(p.TrimEnd('\\')), Directory.Exists(p))).ToList();
        KvmLog.Write($"carrying {paths.Count} item(s) over: {string.Join(", ", entries.Select(e => e.Name).Take(3))}");
        return MetaMessage(new Meta(token, entries, offset.X, offset.Y), Png(image));
    }

    /// <summary>They want the files now (they're copying while you drag): send them, marked for that drag.</summary>
    public static void OnWant(string token, string fromFingerprint)
    {
        if (!Out.TryGetValue(token, out var o) || !string.Equals(o.To, fromFingerprint, StringComparison.OrdinalIgnoreCase)) return;
        var device = _discovery?.Devices.FirstOrDefault(d => string.Equals(d.Fingerprint, fromFingerprint, StringComparison.OrdinalIgnoreCase));
        if (device == null || _id == null || _discovery == null) { KvmLog.Write("drag: they asked for the files, but aren't in the device list"); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await Sender.SendAsync(_id, _discovery.HttpPort, device, Sender.FromPaths(o.Paths), () => null,
                    new Progress<(string, double?)>(), o.Cts.Token, kvmDrop: token);
                KvmLog.Write($"drag: files sent ({result})");
            }
            catch (OperationCanceledException) { KvmLog.Write("drag: sending cancelled"); }
            catch (Exception e) { KvmLog.Write($"drag: sending failed ({e.Message})"); }
            finally { Out.TryRemove(token, out _); }
        });
    }

    /// <summary>The drag was cancelled over there: stop sending.</summary>
    public static void OnCancel(string token)
    {
        if (Out.TryRemove(token, out var o)) o.Cts.Cancel();
    }

    /// <summary>Our own files, if this drag is ours (it came back).</summary>
    public static (List<string> paths, Bitmap? image, Point offset)? Mine(string token) =>
        Out.TryGetValue(token, out var o) ? (o.Paths, o.Image, o.Offset) : null;

    // ---- the PC where they'll be dropped ----

    public sealed class Staging
    {
        public required string Token { get; init; }
        public required string From { get; init; }
        public required string Dir { get; init; }
        public required List<string> Top { get; init; }
        /// <summary>Null when every file arrived, otherwise why not.</summary>
        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    static readonly ConcurrentDictionary<string, Staging> In = new();
    static string Root => Path.Combine(Path.GetTempPath(), "LocalSendDrop", "drag");

    /// <summary>A staging folder with a placeholder per item, so the drag has real paths from the start.</summary>
    public static Staging Stage(Meta m, string fromFingerprint)
    {
        var dir = Path.Combine(Root, m.Token);
        Directory.CreateDirectory(dir);
        var top = new List<string>();
        foreach (var e in m.Entries)
        {
            var path = Path.Combine(dir, Server.CleanPart(e.Name));
            if (e.Folder) Directory.CreateDirectory(path);
            else if (!File.Exists(path)) File.WriteAllBytes(path, []);
            top.Add(path);
        }
        var st = new Staging { Token = m.Token, From = fromFingerprint, Dir = dir, Top = top };
        In[m.Token] = st;
        return st;
    }

    /// <summary>Server side: where files for this drag go, if it's one we're expecting from this PC.</summary>
    public static string? StagingFor(string token, string senderFingerprint) =>
        In.TryGetValue(token, out var st) && string.Equals(st.From, senderFingerprint, StringComparison.OrdinalIgnoreCase) ? st.Dir : null;

    public static void Arrived(string token, string? error)
    {
        if (In.TryGetValue(token, out var st)) st.Done.TrySetResult(error);
        KvmLog.Write(error == null ? "drag: files arrived" : $"drag: files didn't all arrive ({error})");
    }

    public static void Discard(string token)
    {
        if (!In.TryRemove(token, out var st)) return;
        st.Done.TrySetResult("cancelled");
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => { try { Directory.Delete(st.Dir, true); } catch { } });
    }

    /// <summary>Explorer copies after the drop, so staging folders are kept a while and cleared later.</summary>
    static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var d in Directory.EnumerateDirectories(Root))
                if (DateTime.Now - Directory.GetCreationTime(d) > TimeSpan.FromDays(1)) Directory.Delete(d, true);
        }
        catch { }
    }

    // ---- a real drag on this PC ----

    /// <summary>
    /// UI thread. Starts a Windows drag of <paramref name="paths"/> under the pointer.
    /// With <paramref name="pressButton"/>, the button is pressed by injection first (the
    /// pointer is driven from the other PC); without, it's already held by hand.
    /// <paramref name="ended"/> gets whether something took the drop.
    /// </summary>
    public static void RunDrag(IReadOnlyList<string> paths, Bitmap? image, Point offset, Action? pressButton, Action<bool> ended)
    {
        var grab = new GrabWindow();
        var started = false;
        void Start()
        {
            if (started) return;
            started = true;
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths.ToArray());
            DragDropEffects effect;
            try
            {
                effect = image != null
                    ? grab.DoDragDrop(data, DragDropEffects.Copy, image, offset, false)
                    : grab.DoDragDrop(data, DragDropEffects.Copy);
            }
            catch (Exception e) { KvmLog.Write($"drag: couldn't start ({e.Message})"); effect = DragDropEffects.None; }
            grab.Close();
            KvmLog.Write($"drag: ended ({effect})");
            ended(effect != DragDropEffects.None);
        }
        grab.PlaceAt(Cursor.Position);
        if (pressButton == null)
        {
            grab.Shown += (_, _) => grab.BeginInvoke(Start);
            grab.Show();
            return;
        }
        grab.MouseDown += (_, _) => Start();
        grab.Show();
        pressButton();
        // If the press didn't land on it (it moved), start anyway: the button is down.
        var fallback = new System.Windows.Forms.Timer { Interval = 400 };
        fallback.Tick += (_, _) => { fallback.Dispose(); if (!grab.IsDisposed) Start(); };
        fallback.Start();
    }

    // ---- the drag image ----

    /// <summary>The image Windows is drawing for this drag (Explorer's own), or one made like it.</summary>
    public static (Bitmap? image, Point offset) ImageOf(IDataObject data, List<string> paths)
    {
        try
        {
            if (data.GetData("DragImageBits") is MemoryStream ms && FromDragImageBits(ms.ToArray()) is { } shell) return shell;
        }
        catch { }
        var made = Made(paths);
        return (made, new Point(made.Width / 2, made.Height - 16));
    }

    /// <summary>"DragImageBits": SHDRAGIMAGE (size, offset, bitmap handle, colour key), then 32-bit premultiplied pixels, bottom row first.</summary>
    static (Bitmap, Point)? FromDragImageBits(byte[] b)
    {
        if (b.Length < 24) return null;
        int w = BitConverter.ToInt32(b, 0), h = BitConverter.ToInt32(b, 4);
        int ox = BitConverter.ToInt32(b, 8), oy = BitConverter.ToInt32(b, 12);
        if (w <= 0 || h <= 0 || w > 2048 || h > 2048) return null;
        var pixels = w * h * 4;
        var header = b.Length - pixels;
        if (header < 24) return null;
        using var pre = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var bits = pre.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < h; y++)
                Marshal.Copy(b, header + (h - 1 - y) * w * 4, bits.Scan0 + y * bits.Stride, w * 4);
        }
        finally { pre.UnlockBits(bits); }
        var image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(image)) g.DrawImage(pre, 0, 0, w, h);
        return (image, new Point(Math.Clamp(ox, 0, w), Math.Clamp(oy, 0, h)));
    }

    /// <summary>Like Explorer's: the first item's preview or icon, with a count when there are more.</summary>
    static Bitmap Made(List<string> paths)
    {
        const int size = 112;
        var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Image? picture = paths.Count > 0 ? Thumbs.FromDataUri(Thumbs.DataUri(paths[0])) : null;
        if (picture == null && paths.Count > 0)
        {
            try { picture = (Directory.Exists(paths[0]) ? SystemIcons.Application : Icon.ExtractAssociatedIcon(paths[0]))?.ToBitmap(); }
            catch { }
        }
        if (picture != null)
        {
            var scale = Math.Min(80.0 / picture.Width, 80.0 / picture.Height);
            int w = (int)(picture.Width * scale), h = (int)(picture.Height * scale);
            using var faded = new ImageAttributes();
            faded.SetColorMatrix(new ColorMatrix { Matrix33 = 0.85f });
            g.DrawImage(picture, new Rectangle((size - w) / 2, (size - h) / 2 - 8, w, h), 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, faded);
            picture.Dispose();
        }
        if (paths.Count > 1)
        {
            var badge = new Rectangle(size - 40, size - 44, 30, 30);
            using var fill = new SolidBrush(Color.FromArgb(255, 0, 103, 192));
            g.FillEllipse(fill, badge);
            using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
            TextRenderer.DrawText(g, paths.Count > 99 ? "99+" : paths.Count.ToString(), font, badge, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return image;
    }

    static byte[]? Png(Bitmap? image)
    {
        if (image == null) return null;
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

/// <summary>
/// A thin invisible strip along a shared edge, shown while the left button is held
/// there. When a file drag reaches it, it holds the file list and the drag image,
/// so the drag can be carried to the PC on the other side.
/// </summary>
sealed class DropStrip : Form
{
    public sealed record Held(List<string> Paths, Bitmap? Image, Point Offset);

    /// <summary>The file drag resting on the strip, if any. Safe to read from any thread.</summary>
    public Held? Holding { get => _holding; private set => _holding = value; }
    volatile Held? _holding;
    long _since;

    public DropStrip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 1 / 255.0;
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy;
            var list = files.ToList();
            var (image, offset) = KvmDrag.ImageOf(e.Data, list);
            Holding = new Held(list, image, offset);
            _since = Environment.TickCount64;
        };
        DragOver += (_, e) => e.Effect = Holding != null ? DragDropEffects.Copy : DragDropEffects.None;
        DragLeave += (_, _) => Holding = null;
        DragDrop += (_, _) => Holding = null;
    }

    /// <summary>How long a file drag has rested on the strip, in ms (0 if none).</summary>
    public long HeldFor => Holding != null ? Environment.TickCount64 - _since : 0;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }   // toolwindow, noactivate
    }

    /// <summary>Along <paramref name="edge"/> of the desktop, 2 px deep, where the pointer is.</summary>
    public void ShowOn(Edge edge, Rectangle desk)
    {
        var b = edge switch
        {
            Edge.Left => new Rectangle(desk.Left, desk.Top, 2, desk.Height),
            Edge.Right => new Rectangle(desk.Right - 2, desk.Top, 2, desk.Height),
            Edge.Top => new Rectangle(desk.Left, desk.Top, desk.Width, 2),
            _ => new Rectangle(desk.Left, desk.Bottom - 2, desk.Width, 2),
        };
        if (Visible && Bounds == b) return;
        Bounds = b;
        if (!Visible) Show();
        Ui.KeepOnTop(Handle);
    }

    public void Away()
    {
        if (!Visible) return;
        Hide();
        Holding = null;
    }
}

/// <summary>A tiny invisible window under the pointer that a drag starts from.</summary>
sealed class GrabWindow : Form
{
    public GrabWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 1 / 255.0;
        Size = new Size(32, 32);
    }

    public void PlaceAt(Point p) => Bounds = new Rectangle(p.X - 16, p.Y - 16, 32, 32);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }
    }
}

/// <summary>
/// Diagnostics: --kvm-dragtest. On this PC alone: stages a drag, sends a file to
/// itself for it over the real connection, then runs a real Windows drag of the
/// staged file (pressed by injection, like on the controlled PC) onto a test window.
/// Moves the pointer for about two seconds, then puts it back.
/// </summary>
static class DragTest
{
    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        var id = Identity.LoadOrCreate();
        var (discovery, server) = Program.StartNetwork(id);
        var failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        var start = Inject.CursorPos;
        var source = Path.Combine(Path.GetTempPath(), $"lsdrop drag test {Guid.NewGuid():N}.txt");
        File.WriteAllText(source, "carried across by drag " + DateTime.Now.ToString("o"));

        var target = new Form { Text = "drop here", TopMost = true, AllowDrop = true, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
        var area = Screen.PrimaryScreen!.WorkingArea;
        target.Bounds = new Rectangle(area.Left + area.Width / 2 - 150, area.Top + area.Height / 2 - 150, 300, 300);
        string[]? dropped = null;
        target.DragEnter += (_, e) => e.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        target.DragDrop += (_, e) => dropped = e.Data!.GetData(DataFormats.FileDrop) as string[];
        KvmDrag.Init(id, discovery, a => target.BeginInvoke(a));
        server.AskAccept = (r, _) => Task.FromResult(r.KvmDrop != null);
        server.Ended += (r, _, error) => { if (r.KvmDrop is { } t) KvmDrag.Arrived(t, error); };
        var self = new Device { Fingerprint = id.Fingerprint, Alias = "self", Ip = "127.0.0.1", Port = server.Port, Protocol = "https" };

        target.Shown += async (_, _) =>
        {
            try
            {
                // 1. Staging, and the files arriving for it
                var token = Guid.NewGuid().ToString("N");
                var meta = new KvmDrag.Meta(token, [new KvmDrag.Entry(Path.GetFileName(source), false)], 10, 10);
                var st = KvmDrag.Stage(meta, id.Fingerprint);
                Check(File.Exists(st.Top[0]) && new FileInfo(st.Top[0]).Length == 0, "staging has a placeholder at the drag's path from the start");
                await Sender.SendAsync(id, discovery.HttpPort, self, Sender.FromPaths([source]), () => null, new Progress<(string, double?)>(), CancellationToken.None, kvmDrop: token);
                var error = await st.Done.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(error == null && File.ReadAllText(st.Top[0]) == File.ReadAllText(source), "the files arrive into the placeholder, same content, no question asked");

                // 2. A token nobody is expecting
                var refused = false;
                try { await Sender.SendAsync(id, discovery.HttpPort, self, Sender.FromPaths([source]), () => null, new Progress<(string, double?)>(), CancellationToken.None, kvmDrop: Guid.NewGuid().ToString("N")); }
                catch (Exception) { refused = true; }
                Check(refused, "files for a drag nobody is expecting are refused");

                // 3. A real drag of the staged file, pressed and let go by injection
                var from = new Point(target.Left - 120, target.Top + 150);
                Inject.MoveTo(from.X, from.Y);
                await Task.Delay(150);
                var ended = new TaskCompletionSource<bool>();
                KvmDrag.RunDrag(st.Top, null, Point.Empty, () => Inject.Button(MouseKind.LeftDown), ok => ended.TrySetResult(ok));
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    var to = new Point(target.Left + 150, target.Top + 150);
                    for (var i = 1; i <= 20; i++)
                    {
                        Inject.MoveTo(from.X + (to.X - from.X) * i / 20, from.Y + (to.Y - from.Y) * i / 20);
                        await Task.Delay(25);
                    }
                    await Task.Delay(300);
                    Inject.Button(MouseKind.LeftUp);
                });
                var took = await ended.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await Task.Delay(100);
                Check(took, "the drag started from an injected press and something took the drop");
                Check(dropped is [var p] && string.Equals(p, st.Top[0], StringComparison.OrdinalIgnoreCase), $"the drop got the staged file ({dropped?.FirstOrDefault() ?? "nothing"})");
            }
            catch (Exception e) { Check(false, $"error: {e.GetType().Name}: {e.Message}"); }
            finally { target.Close(); }
        };
        try { Application.Run(target); }
        finally
        {
            Inject.Button(MouseKind.LeftUp);
            Inject.MoveTo(start.X, start.Y);
            try { File.Delete(source); } catch { }
            discovery.Dispose();
            server.DisposeAsync().AsTask().Wait(2000);
        }
        Console.WriteLine(failures == 0 ? "RESULT: all passed" : $"RESULT: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
