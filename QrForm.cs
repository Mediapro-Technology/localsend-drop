using System.Runtime.InteropServices;
using QRCoder;

namespace LocalSendDrop;

/// <summary>
/// Shows the QR code for a share. The link works while this window is open; closing
/// it (or Stop sharing) ends the share.
/// </summary>
sealed class QrForm : Form
{
    readonly QrShare.Share _share;
    readonly string _url;
    readonly System.Collections.BitArray[] _modules;
    readonly PillButton _stop = new("Stop sharing", primary: true);
    readonly PillButton _copy = new("Copy link");
    Rectangle _close;
    bool _closeHot;

    public QrForm(QrShare.Share share)
    {
        _share = share;
        _url = QrShare.Url(share);
        using (var data = QRCodeGenerator.GenerateQrCode(_url, QRCodeGenerator.ECCLevel.M))
            _modules = data.ModuleMatrix.ToArray();
        Text = "Share with a QR code";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        BackColor = Ui.Base;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _stop.Click += (_, _) => Close();
        _copy.Click += (_, _) =>
        {
            if (!_share.Upload) { Clipboard.SetText(_url); _copy.Text = "Copied"; return; }
            string? last;
            lock (_share.Received) last = _share.Received.LastOrDefault();
            if (last != null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{last}\"");
            else System.Diagnostics.Process.Start("explorer.exe", $"\"{Settings.Current.SaveDir ?? Settings.DefaultSaveDir}\"");
        };
        if (share.Upload)
        {
            Text = "Get files from a phone";
            _stop.Text = "Done";
            _copy.Text = "Open folder";
        }
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Controls.AddRange([_stop, _copy]);
        share.Downloaded += OnDownloaded;
        share.FileReceived += _ => OnDownloaded();
        Ui.OnChange(this, () => { BackColor = Ui.Base; Ui.Chrome(Handle); Invalidate(true); });
    }

    void OnDownloaded()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => { if (!IsDisposed) Invalidate(); }); } catch (InvalidOperationException) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _share.Downloaded -= OnDownloaded;
        QrShare.Stop(_share);
        base.OnFormClosed(e);
    }

    int S(int v) => LogicalToDeviceUnits(v);
    const int W = 380, H = 560, Qr = 280;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int w = S(W), h = S(H), pad = S(24);
        ClientSize = new Size(w, h);
        var bw = (w - 2 * pad - S(8)) / 2;
        _copy.SetBounds(pad, h - pad - S(32), bw, S(32));
        _stop.SetBounds(pad + bw + S(8), h - pad - S(32), bw, S(32));
        _close = new Rectangle(w - S(48), S(16), S(32), S(32));
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
        Effects.Show(this, S(10));
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
        int w = ClientSize.Width, pad = S(24);
        Ui.DrawText(g, _share.Upload ? "Scan to send files here" : "Scan to download", Ui.Subtitle, new Rectangle(pad, S(20), w - pad - S(56), S(28)), Ui.Text);
        var what = _share.Items.Count switch
        {
            0 => "Text",
            1 => _share.Items[0].Name,
            var count => $"{count} files",
        };
        if (_share.Items.Count > 0) what += $", {IncomingCard.FormatSize(_share.Items.Sum(i => i.Size))}";
        if (_share.Upload) what = "Their files are saved to this PC without asking";
        Ui.DrawText(g, what, Ui.Caption, new Rectangle(pad, S(50), w - 2 * pad, S(18)), Ui.TextSecondary);
        if (_closeHot) Ui.FillRound(g, Ui.SubtleHover, _close, S(4));
        Ui.DrawIcon(g, Ui.GlyphClose, 10f, _close, Ui.Text);

        // The code itself, always dark on white: phone cameras read it best that way.
        var tile = new Rectangle((w - S(Qr)) / 2, S(84), S(Qr), S(Qr));
        Ui.FillRound(g, Color.White, tile, S(8));
        var n = _modules.Length;
        var cell = (float)tile.Width / n;
        using (var dark = new SolidBrush(Color.FromArgb(0x1B, 0x1B, 0x1B)))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    if (_modules[y][x])
                        g.FillRectangle(dark, tile.X + x * cell, tile.Y + y * cell, cell + 0.5f, cell + 0.5f);
        }

        var y0 = tile.Bottom + S(16);
        TextRenderer.DrawText(g, "Their phone needs to be on the same Wi-Fi. The link stops working when you close this.",
            Ui.Caption, new Rectangle(pad, y0, w - 2 * pad, S(36)), Ui.TextSecondary,
            TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        int received;
        string? last;
        lock (_share.Received) { received = _share.Received.Count; last = _share.Received.LastOrDefault(); }
        var status = _share.Upload
            ? received == 0 ? "Waiting for files" : received == 1 ? $"Received {Path.GetFileName(last)}" : $"Received {received} files"
            : _share.Downloads == 0 ? "Waiting for someone to scan it" : $"Downloaded {_share.Downloads} time{(_share.Downloads == 1 ? "" : "s")}";
        var done = _share.Upload ? received > 0 : _share.Downloads > 0;
        Ui.DrawText(g, status, Ui.Body, new Rectangle(pad, y0 + S(42), w - 2 * pad, S(22)),
            done ? Ui.Success : Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_close.Contains(e.Location)) { Close(); return; }
        if (e.Button == MouseButtons.Left && e.Y < S(76))
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
