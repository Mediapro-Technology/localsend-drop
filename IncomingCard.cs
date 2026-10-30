using System.Diagnostics;

namespace LocalSendDrop;

/// <summary>
/// The receive popup in the drop corner: asks to accept, shows progress, then
/// offers to open what arrived. Replaces LocalSend's own window.
/// </summary>
sealed class IncomingCard : Form
{
    readonly PillButton _primary = new("", primary: true);
    readonly PillButton _secondary = new("");
    readonly System.Windows.Forms.Timer _autoHide = new();
    Action? _onPrimary, _onSecondary;
    string? _session;

    // what's painted
    string _who = "", _whoKey = "", _title = "", _detail = "";
    List<string> _lines = [];
    double _fraction = -1;
    bool _done, _error, _message;

    public Action? CancelReceive;

    public IncomingCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Ink;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _primary.Click += (_, _) => _onPrimary?.Invoke();
        _secondary.Click += (_, _) => _onSecondary?.Invoke();
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); Hide(); };
        Controls.AddRange([_primary, _secondary]);
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
        Ui.SetCorners(Handle, true);
        Ui.SetBorderColor(Handle, Ui.Line);
    }

    int S(int v) => LogicalToDeviceUnits(v);

    const int W = 344, Pad = 20, Avatar = 44, LineH = 21;

    /// <summary>Lays out the card for the current state and shows it.</summary>
    void Arrange(string? primary, string? secondary)
    {
        _autoHide.Stop();
        int w = S(W), pad = S(Pad);
        var y = pad + S(Avatar) + S(14);
        y += _lines.Count * S(LineH) + (_lines.Count > 0 ? S(8) : 0);
        if (_fraction >= 0) y += S(18);

        _primary.Visible = primary != null;
        _secondary.Visible = secondary != null;
        if (primary != null || secondary != null)
        {
            var bh = S(38);
            if (primary != null && secondary != null)
            {
                var bw = (w - 2 * pad - S(10)) / 2;
                _secondary.SetBounds(pad, y, bw, bh);
                _primary.SetBounds(pad + bw + S(10), y, bw, bh);
            }
            else (primary != null ? _primary : _secondary).SetBounds(pad, y, w - 2 * pad, bh);
            _primary.Text = primary ?? "";
            _secondary.Text = secondary ?? "";
            y += bh;
        }
        y += pad;

        var wa = Settings.Current.TargetScreen.WorkingArea;
        var st = Settings.Current;
        var m = S(8);
        Bounds = new Rectangle(st.Right ? wa.Right - w - m : wa.Left + m, st.Bottom ? wa.Bottom - y - m : wa.Top + m, w, y);
        if (!Visible) Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Ink);
        int w = ClientSize.Width, pad = S(Pad);

        var avatar = new Rectangle(pad, pad, S(Avatar), S(Avatar));
        if (_message)
        {
            Ui.FillRound(g, Ui.Ink2, avatar, avatar.Width / 2f);
            Ui.DrawText(g, "", Ui.Glyph(15f), avatar, Ui.Postal, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else Ui.Monogram(g, avatar, _who, _whoKey, _done);

        var tx = avatar.Right + S(14);
        Ui.DrawText(g, _title, Ui.Display(13f), new Rectangle(tx, pad + S(1), w - tx - pad, S(24)), Ui.Text);
        Ui.DrawText(g, _detail, Ui.Body(9f), new Rectangle(tx, pad + S(25), w - tx - pad, S(19)), _error ? Ui.Error : Ui.Muted);

        var y = avatar.Bottom + S(14);
        foreach (var line in _lines)
        {
            Ui.DrawText(g, line, Ui.Body(9.5f), new Rectangle(pad, y, w - 2 * pad, S(LineH)),
                line.StartsWith("and ") ? Ui.Muted : Ui.Text);
            y += S(LineH);
        }
        if (_lines.Count > 0) y += S(8);

        if (_fraction >= 0)
        {
            var track = new Rectangle(pad, y + S(4), w - 2 * pad, S(6));
            Ui.FillRound(g, Ui.Ink2, track, track.Height / 2f);
            if (_fraction > 0)
                Ui.FillRound(g, Ui.Postal, new Rectangle(track.X, track.Y, Math.Max(track.Height, (int)(track.Width * _fraction)), track.Height), track.Height / 2f);
        }
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

    void Reset(string who, string key)
    {
        _who = who; _whoKey = key;
        _done = _error = _message = false;
        _fraction = -1;
        _lines = [];
    }

    // ---- states ----

    public Task<bool> Ask(IncomingRequest r, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _session = r.SessionId;
        Reset(r.SenderAlias, r.SenderFingerprint.Length > 0 ? r.SenderFingerprint : r.SenderAlias);
        _title = r.SenderAlias;
        _detail = $"wants to send you {Count(r.Files.Count)}, {FormatSize(r.TotalSize)}";
        _lines = r.Files.Take(4).Select(f => f.FileName).ToList();
        if (r.Files.Count > 4) _lines.Add($"and {r.Files.Count - 4} more");
        Arrange("Accept", "Decline");
        _onPrimary = () => { tcs.TrySetResult(true); Receiving(r); };
        _onSecondary = () => { tcs.TrySetResult(false); Hide(); };
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
        Reset(r.SenderAlias, r.SenderFingerprint.Length > 0 ? r.SenderFingerprint : r.SenderAlias);
        _title = $"Receiving from {r.SenderAlias}";
        _detail = $"0 of {FormatSize(r.TotalSize)}";
        _fraction = 0;
        Arrange(null, "Cancel");
        _onSecondary = () => CancelReceive?.Invoke();
    }

    public void OnProgress(string session, long received, long total)
    {
        if (session != _session || _fraction < 0) return;
        _fraction = total > 0 ? Math.Min(1, (double)received / total) : 1;
        _detail = $"{FormatSize(received)} of {FormatSize(total)}";
        Invalidate();
    }

    public void OnFinished(string session, List<string> saved)
    {
        if (session != _session) return;
        _fraction = -1;
        _done = true;
        _title = saved.Count == 1 ? Path.GetFileName(saved[0]) : $"{saved.Count} files received";
        _detail = $"Saved in {ShortDir(saved)}";
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
        _fraction = -1;
        _error = true;
        _title = "Transfer stopped";
        _detail = reason;
        Arrange(null, "Close");
        _onSecondary = Hide;
        HideAfter(6000);
    }

    public void OnMessage(string alias, string text)
    {
        _session = null;
        Reset(alias, alias);
        _message = true;
        _title = alias;
        _detail = "sent you a message";
        _lines = text.Replace("\r", "").Split('\n').Take(6).ToList();
        Arrange("Copy", "Close");
        _onPrimary = () => { Clipboard.SetText(text); Hide(); };
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

/// <summary>A one-field prompt in the same style (used for PINs).</summary>
sealed class PromptForm : Form
{
    public static string? Ask(string title, string hint, string action)
    {
        using var f = new PromptForm(title, hint, action);
        return f.ShowDialog() == DialogResult.OK && f._box.Text.Length > 0 ? f._box.Text : null;
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
        BackColor = Ui.Ink;
        int S(int v) => LogicalToDeviceUnits(v);
        ClientSize = new Size(S(320), S(176));
        _box.SetBounds(S(20), S(72), S(280), S(38));
        var ok = new PillButton(action, primary: true);
        var cancel = new PillButton("Cancel");
        cancel.SetBounds(S(20), S(122), S(135), S(36));
        ok.SetBounds(S(165), S(122), S(135), S(36));
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
        Ui.SetCorners(Handle, true);
        Ui.SetBorderColor(Handle, Ui.Line);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int v) => LogicalToDeviceUnits(v);
        e.Graphics.Clear(Ui.Ink);
        Ui.DrawText(e.Graphics, _title, Ui.Display(13f), new Rectangle(S(20), S(16), S(280), S(26)), Ui.Text);
        Ui.DrawText(e.Graphics, _hint, Ui.Body(9f), new Rectangle(S(20), S(42), S(280), S(20)), Ui.Muted);
    }
}
