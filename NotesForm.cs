using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>
/// A small conversation window with one device: your messages on the right in the
/// accent colour, theirs on the left, a text box at the bottom.
/// </summary>
sealed class NotesForm : Form
{
    readonly string _fingerprint, _name;
    readonly Func<string, Task<string?>> _send;
    readonly MessagesView _view;
    readonly TextField _input = new();
    readonly PillButton _sendButton = new("Send", primary: true);
    Rectangle _close;
    bool _closeHot;
    string? _error;

    /// <param name="send">Sends one message; returns null on success, otherwise why it failed.</param>
    public NotesForm(string fingerprint, string name, Func<string, Task<string?>> send)
    {
        _fingerprint = fingerprint;
        _name = name;
        _send = send;
        _view = new MessagesView(fingerprint);
        Text = $"Messages with {name}";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        BackColor = Ui.Base;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _input.MaxLength = 1000;
        _sendButton.Click += async (_, _) => await SendAsync();
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendAsync(); }
        };
        Controls.AddRange([_view, _input, _sendButton]);
        Notes.Changed += OnNotesChanged;
        Ui.OnChange(this, () => { BackColor = Ui.Base; Ui.Chrome(Handle); Invalidate(true); });
    }

    void OnNotesChanged(string fingerprint)
    {
        if (fingerprint != _fingerprint || !IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => { if (!IsDisposed) _view.Reload(); }); } catch (InvalidOperationException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Notes.Changed -= OnNotesChanged;
        base.Dispose(disposing);
    }

    int S(int v) => LogicalToDeviceUnits(v);
    const int W = 400, H = 560, HeaderH = 72, FooterH = 72;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int w = S(W), h = S(H), pad = S(16);
        ClientSize = new Size(w, h);
        _view.SetBounds(0, S(HeaderH), w, h - S(HeaderH) - S(FooterH));
        _input.SetBounds(pad, h - S(FooterH) + S(20), w - 2 * pad - S(88), S(32));
        _sendButton.SetBounds(w - pad - S(80), h - S(FooterH) + S(20), S(80), S(32));
        _close = new Rectangle(w - S(48), S(16), S(32), S(32));
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
        _view.Reload();
        Effects.Show(this, S(10));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _input.Focus();
    }

    async Task SendAsync()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Text = "";
        _error = null;
        var note = Notes.Add(_fingerprint, fromMe: true, text);
        _sendButton.Enabled = false;
        var error = await _send(text);
        if (IsDisposed) return;
        _sendButton.Enabled = true;
        if (error != null)
        {
            Notes.MarkFailed(_fingerprint, note);
            _error = $"Not delivered: {error}";
        }
        Invalidate();
        _input.Focus();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.Chrome(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Base);
        int pad = S(16), w = ClientSize.Width, h = ClientSize.Height;
        Ui.DrawText(g, _name, Ui.Subtitle, new Rectangle(pad, S(14), w - pad - S(56), S(28)), Ui.Text);
        Ui.DrawText(g, _error ?? "Messages are kept on this PC only", Ui.Caption, new Rectangle(pad, S(42), w - 2 * pad, S(18)),
            _error != null ? Ui.Error : Ui.TextSecondary);
        if (_closeHot) Ui.FillRound(g, Ui.SubtleHover, _close, S(4));
        Ui.DrawIcon(g, Ui.GlyphClose, 10f, _close, Ui.Text);
        using var line = new Pen(Ui.Divider);
        g.DrawLine(line, 0, S(HeaderH) - 1, w, S(HeaderH) - 1);
        g.DrawLine(line, 0, h - S(FooterH), w, h - S(FooterH));
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_close.Contains(e.Location)) { Close(); return; }
        if (e.Button == MouseButtons.Left && e.Y < S(HeaderH))
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0); // drag by the header
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = _close.Contains(e.Location);
        if (hot != _closeHot) { _closeHot = hot; Invalidate(_close); }
    }
}

/// <summary>The message bubbles, newest at the bottom; scrolls with the mouse wheel.</summary>
sealed class MessagesView : Drawn
{
    readonly string _fingerprint;
    IReadOnlyList<Note> _notes = [];
    int _scroll;      // pixels scrolled up from the bottom
    int _contentH;

    public MessagesView(string fingerprint) => _fingerprint = fingerprint;

    public void Reload()
    {
        _notes = Notes.For(_fingerprint);
        _scroll = 0;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Clamp(_scroll + Math.Sign(e.Delta) * S(48), 0, Math.Max(0, _contentH - Height));
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Base);
        int pad = S(16), maxW = (int)(Width * 0.75), gap = S(6);
        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

        if (_notes.Count == 0)
        {
            TextRenderer.DrawText(g, "No messages yet. Write one below; it pops up on their screen.", Ui.Caption,
                new Rectangle(pad, S(16), Width - 2 * pad, S(40)), Ui.TextSecondary, wrap);
            return;
        }

        // Lay out bottom-up so the newest message sits just above the text box.
        var layout = new List<(Note Note, Rectangle Bubble, Rectangle Text, bool ShowTime)>();
        var y = 0;
        for (var i = _notes.Count - 1; i >= 0; i--)
        {
            var n = _notes[i];
            var size = TextRenderer.MeasureText(g, n.Text, Ui.Body, new Size(maxW - S(24), int.MaxValue), wrap);
            var bw = size.Width + S(24);
            var bh = size.Height + S(16);
            // A time line under the last message of each run from the same side.
            var showTime = i == _notes.Count - 1 || _notes[i + 1].FromMe != n.FromMe || (_notes[i + 1].Time - n.Time).TotalMinutes > 10;
            y += (showTime ? S(18) : 0) + bh + gap;
            var x = n.FromMe ? Width - pad - bw : pad;
            var bubble = new Rectangle(x, -y, bw, bh);
            layout.Add((n, bubble, new Rectangle(x + S(12), -y + S(8), size.Width, size.Height), showTime));
        }
        _contentH = y + S(16);
        var offset = Height - S(8) + _scroll;

        foreach (var (n, bubble, text, showTime) in layout)
        {
            var b = bubble; b.Offset(0, offset);
            var t = text; t.Offset(0, offset);
            if (b.Bottom < 0 || b.Top > Height) continue;
            if (n.FromMe) Ui.FillRound(g, n.Failed ? Ui.ControlStroke : Ui.Accent, b, S(8));
            else
            {
                Ui.FillRound(g, Ui.Card, b, S(8));
                Ui.StrokeRound(g, Ui.CardStroke, b, S(8));
            }
            TextRenderer.DrawText(g, n.Text, Ui.Body, t, n.FromMe && !n.Failed ? Ui.TextOnAccent : Ui.Text, wrap);
            if (showTime)
            {
                var label = n.Failed ? "Not delivered" : n.Time.ToLocalTime().ToString("t");
                var flags = (n.FromMe ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, label, Ui.Caption, new Rectangle(pad, b.Bottom + S(2), Width - 2 * pad, S(16)),
                    n.Failed ? Ui.Error : Ui.TextTertiary, flags);
            }
        }
    }
}
