using System.Diagnostics;

namespace LocalSendDrop;

/// <summary>
/// The receive popup, laid out like a Windows 11 notification: app name, a title,
/// details, and action buttons. Asks to accept, shows progress, then offers to open
/// what arrived. Replaces LocalSend's own window.
/// </summary>
sealed class IncomingCard : Form
{
    readonly PillButton _primary = new("", primary: true);
    readonly PillButton _secondary = new("");
    readonly System.Windows.Forms.Timer _autoHide = new();
    readonly Icon? _appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
    Action? _onPrimary, _onSecondary, _onClose;
    string? _session;
    Rectangle _close;
    bool _closeHot;

    // what's painted
    string _title = "", _caption = "";
    List<string> _lines = [];
    double _fraction = -1;
    bool _error, _done;
    readonly List<Image> _thumbs = [];
    DateTime _started;

    public Action? CancelReceive;
    /// <summary>Reply to a message: fingerprint, name.</summary>
    public Action<string, string>? Reply;
    /// <summary>Someone asked for a file and we chose to answer.</summary>
    public Action<FileRequest>? AnswerRequest;

    public IncomingCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _primary.Click += (_, _) => _onPrimary?.Invoke();
        _secondary.Click += (_, _) => _onSecondary?.Invoke();
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); Hide(); };
        var onTop = new System.Windows.Forms.Timer { Interval = 2000 };
        onTop.Tick += (_, _) => { if (IsHandleCreated && Visible) Ui.KeepOnTop(Handle); };
        onTop.Start();
        Controls.AddRange([_primary, _secondary]);
        Ui.OnChange(this, () => { BackColor = Ui.Flyout; Ui.Chrome(Handle); Invalidate(true); });
        BackColor = Ui.Flyout;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000;   // toolwindow, noactivate
            cp.ClassStyle |= 0x20000;          // CS_DROPSHADOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.Chrome(Handle);
    }

    int S(int v) => LogicalToDeviceUnits(v);

    const int W = 364, Pad = 16, HeaderH = 40, LineH = 20, ThumbH = 72;

    /// <summary>Lays out the card for the current state and shows it.</summary>
    void Arrange(string? primary, string? secondary)
    {
        _autoHide.Stop();
        int w = S(W), pad = S(Pad);
        var y = S(HeaderH) + S(22);                       // title
        if (_thumbs.Count > 0) y += S(ThumbH + 8);
        y += _lines.Count * S(LineH);
        if (_caption.Length > 0) y += S(LineH);
        if (_fraction >= 0) y += S(16);
        y += S(16);

        _primary.Visible = primary != null;
        _secondary.Visible = secondary != null;
        if (primary != null || secondary != null)
        {
            var bh = S(32);
            if (primary != null && secondary != null)
            {
                var bw = (w - 2 * pad - S(8)) / 2;
                _primary.SetBounds(pad, y, bw, bh);
                _secondary.SetBounds(pad + bw + S(8), y, bw, bh);
            }
            else (primary != null ? _primary : _secondary).SetBounds(pad, y, w - 2 * pad, bh);
            _primary.Text = primary ?? "";
            _secondary.Text = secondary ?? "";
            y += bh;
        }
        y += pad;

        var wa = Settings.Current.TargetScreen.WorkingArea;
        var st = Settings.Current;
        var m = S(12);
        Bounds = new Rectangle(st.ZoneX(wa, w, m), st.Bottom ? wa.Bottom - y - m : wa.Top + m, w, y);
        _close = new Rectangle(w - S(40), S(8), S(32), S(32));
        if (!Visible)
        {
            // Glass: blur what's behind, with room for the card to grow while it's open.
            var room = S(640);
            Effects.Capture(this, new Rectangle(Left, st.Bottom ? Bottom - room : Top, Width, room));
            Show();
            Effects.Show(this, st.Bottom ? S(12) : -S(12));
        }
        Ui.KeepOnTop(Handle);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Effects.PaintSurface(g, this, Ui.Flyout);
        int w = ClientSize.Width, pad = S(Pad);

        // Notification header: app icon and name, close button.
        if (_appIcon != null) g.DrawIcon(_appIcon, new Rectangle(pad, S(14), S(16), S(16)));
        Ui.DrawText(g, "LocalSend Drop", Ui.Caption, new Rectangle(pad + S(24), S(12), S(200), S(20)), Ui.TextSecondary);
        if (_closeHot) Ui.FillRound(g, Ui.SubtleHover, _close, S(4));
        Ui.DrawIcon(g, Ui.GlyphClose, 9f, _close, Ui.TextSecondary);

        var y = S(HeaderH);
        Ui.DrawText(g, _title, Ui.BodyStrong, new Rectangle(pad, y, w - 2 * pad, S(22)), _error ? Ui.Error : Ui.Text);
        y += S(22);
        if (_thumbs.Count > 0)
        {
            // Previews sent by the other PC, cropped to squares like the Photos app.
            var x = pad;
            foreach (var img in _thumbs)
            {
                var box = new Rectangle(x, y + S(4), S(ThumbH), S(ThumbH));
                using (var path = Ui.Round(box, S(4)))
                {
                    var old = g.Clip;
                    g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Intersect);
                    var scale = Math.Max((double)box.Width / img.Width, (double)box.Height / img.Height);
                    int iw = (int)(img.Width * scale), ih = (int)(img.Height * scale);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, box.X + (box.Width - iw) / 2, box.Y + (box.Height - ih) / 2, iw, ih);
                    g.Clip = old;
                }
                Ui.StrokeRound(g, Ui.CardStroke, box, S(4));
                x += S(ThumbH + 8);
            }
            y += S(ThumbH + 8);
        }
        foreach (var line in _lines)
        {
            Ui.DrawText(g, line, Ui.Body, new Rectangle(pad, y, w - 2 * pad, S(LineH)), Ui.TextSecondary);
            y += S(LineH);
        }
        if (_caption.Length > 0)
        {
            Ui.DrawText(g, _caption, Ui.Caption, new Rectangle(pad, y, w - 2 * pad, S(LineH)),
                _done ? Ui.Success : Ui.TextSecondary);
            y += S(LineH);
        }
        if (_fraction >= 0) Ui.Progress(g, new Rectangle(pad, y + S(4), w - 2 * pad, S(6)), _fraction, DeviceDpi / 96f);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = _close.Contains(e.Location);
        if (hot != _closeHot) { _closeHot = hot; Invalidate(_close); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_closeHot) { _closeHot = false; Invalidate(_close); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_close.Contains(e.Location)) (_onClose ?? Hide)();
    }

    void HideAfter(int ms) { _autoHide.Interval = ms; _autoHide.Start(); }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    static string Count(int n) => n == 1 ? "a file" : $"{n} files";

    void Reset()
    {
        foreach (var t in _thumbs) t.Dispose();
        _thumbs.Clear();
        _error = _done = false;
        _fraction = -1;
        _lines = [];
        _caption = "";
        _onClose = null;
    }

    // ---- states ----

    public Task<bool> Ask(IncomingRequest r, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _session = r.SessionId;
        Reset();
        _title = $"{r.SenderAlias} wants to send you {Count(r.Files.Count)}";
        foreach (var f in r.Files.Take(4))
            if (Thumbs.FromDataUri(f.Thumbnail) is { } img) _thumbs.Add(img);
        _lines = r.Files.Take(_thumbs.Count > 0 ? 2 : 3).Select(f => f.FileName).ToList();
        var shown = _lines.Count;
        if (r.Files.Count > shown) _lines.Add($"and {r.Files.Count - shown} more");
        _caption = FormatSize(r.TotalSize);
        Arrange("Accept", "Decline");
        _onPrimary = () => { tcs.TrySetResult(true); Receiving(r); };
        _onSecondary = _onClose = () => { tcs.TrySetResult(false); Hide(); };
        ct.Register(() => BeginInvoke(() =>
        {
            if (tcs.TrySetResult(false) && _session == r.SessionId) Hide();
        }));
        if (Settings.Current.Sound) System.Media.SystemSounds.Asterisk.Play();
        return tcs.Task;
    }

    public void Receiving(IncomingRequest r)
    {
        _session = r.SessionId;
        Reset();
        _title = $"Receiving from {r.SenderAlias}";
        _lines = r.Files.Take(1).Select(f => r.Files.Count == 1 ? f.FileName : $"{f.FileName} and {r.Files.Count - 1} more").ToList();
        _caption = $"0 of {FormatSize(r.TotalSize)}";
        _fraction = 0;
        _started = DateTime.UtcNow;
        Arrange(null, "Cancel");
        _onSecondary = () => CancelReceive?.Invoke();
        _onClose = Hide; // keeps receiving in the background
    }

    public void OnProgress(string session, long received, long total)
    {
        if (session != _session || _fraction < 0) return;
        _fraction = total > 0 ? Math.Min(1, (double)received / total) : 1;
        _caption = $"{FormatSize(received)} of {FormatSize(total)}";
        var rate = Rate.Describe(received, total, DateTime.UtcNow - _started);
        if (rate.Contains(',')) _caption += rate[rate.IndexOf(',')..];   // ", 11 MB/s, 20 s left"
        Invalidate();
    }

    /// <summary>The sender dropped off; the transfer continues if it reconnects.</summary>
    public void OnReconnecting(string session)
    {
        if (session != _session || _fraction < 0) return;
        _caption = "Connection lost. Waiting for it to come back…";
        Invalidate();
    }

    public void OnFinished(string session, List<string> saved)
    {
        if (session != _session) return;
        Reset();
        _done = true;
        _title = saved.Count == 1 ? $"Received {Path.GetFileName(saved[0])}" : $"Received {saved.Count} files";
        _caption = $"Saved to {ShortDir(saved)}";
        Arrange(saved.Count == 1 ? "Open" : "Open folder", "Show in folder");
        _onPrimary = () =>
        {
            Open(saved.Count == 1 ? saved[0] : CommonDir(saved));
            Hide();
        };
        _onSecondary = () => { Process.Start("explorer.exe", $"/select,\"{saved[0]}\""); Hide(); };
        HideAfter(15000);
    }

    public void OnFailed(string session, string reason)
    {
        if (session != _session) return;
        Reset();
        _error = true;
        _title = "Transfer stopped";
        _lines = [reason];
        Arrange(null, "Dismiss");
        _onSecondary = Hide;
        HideAfter(8000);
    }

    /// <summary>A message that's only a web address.</summary>
    public static bool IsLink(string text, out Uri uri) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https" && !text.Trim().Contains(' ');

    public void OnMessage(string alias, string fingerprint, string text)
    {
        _session = null;
        Reset();
        if (IsLink(text, out var link))
        {
            _title = $"{alias} sent you a link";
            _lines = [link.Host + (link.PathAndQuery.Length > 1 ? link.PathAndQuery : "")];
            Arrange("Open", "Copy");
            _onPrimary = () => { Hide(); Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); };
            _onSecondary = () => { Clipboard.SetText(link.AbsoluteUri); Hide(); };
            if (Settings.Current.Sound) System.Media.SystemSounds.Asterisk.Play();
            return;
        }
        _title = $"Message from {alias}";
        _lines = text.Replace("\r", "").Split('\n').Take(6).ToList();
        Arrange("Reply", "Copy");
        _onPrimary = () => { Hide(); Reply?.Invoke(fingerprint, alias); };
        _onSecondary = () => { Clipboard.SetText(text); Hide(); };
        if (Settings.Current.Sound) System.Media.SystemSounds.Asterisk.Play();
    }

    public void OnFileRequest(FileRequest req)
    {
        _session = null;
        Reset();
        _title = $"{req.Alias} is asking for a file";
        _lines = req.Note != null ? [$"\u201C{req.Note}\u201D"] : [];
        _caption = "Choose what to send. It goes straight to them.";
        Arrange("Choose file", "Decline");
        _onPrimary = () => { Hide(); AnswerRequest?.Invoke(req); };
        _onSecondary = Hide;
        if (Settings.Current.Sound) System.Media.SystemSounds.Asterisk.Play();
    }

    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    static string CommonDir(List<string> paths)
    {
        var dir = Path.GetDirectoryName(paths[0])!;
        while (!paths.All(p => p.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    static string ShortDir(List<string> paths) => Path.GetFileName(CommonDir(paths)) is { Length: > 0 } name ? name : CommonDir(paths);
}

/// <summary>A one-field dialog in the same style (used for PINs).</summary>
sealed class PromptForm : Form
{
    /// <param name="allowEmpty">If true, an empty answer is "" (only Cancel gives null).</param>
    public static string? Ask(string title, string hint, string action, string initial = "", bool allowEmpty = false)
    {
        using var f = new PromptForm(title, hint, action);
        f._box.Text = initial;
        if (f.ShowDialog() != DialogResult.OK) return null;
        return f._box.Text.Length > 0 || allowEmpty ? f._box.Text : null;
    }

    readonly TextField _box = new();
    readonly string _title, _hint;

    PromptForm(string title, string hint, string action)
    {
        _title = title;
        _hint = hint;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Ui.Base;
        int S(int v) => LogicalToDeviceUnits(v);
        ClientSize = new Size(S(360), S(200));
        _box.SetBounds(S(24), S(92), S(312), S(32));
        var ok = new PillButton(action, primary: true);
        var cancel = new PillButton("Cancel");
        ok.SetBounds(S(24), S(148), S(152), S(32));
        cancel.SetBounds(S(184), S(148), S(152), S(32));
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; };
        Controls.AddRange([_box, ok, cancel]);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) DialogResult = DialogResult.OK;
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        };
        Shown += (_, _) => _box.Focus();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.Chrome(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int v) => LogicalToDeviceUnits(v);
        e.Graphics.Clear(Ui.Base);
        Ui.DrawText(e.Graphics, _title, Ui.Subtitle, new Rectangle(S(24), S(20), S(312), S(30)), Ui.Text);
        Ui.DrawText(e.Graphics, _hint, Ui.Body, new Rectangle(S(24), S(54), S(312), S(22)), Ui.TextSecondary);
    }
}
