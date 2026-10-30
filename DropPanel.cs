namespace LocalSendDrop;

/// <summary>
/// The corner drop zone. Collapsed it's a thin postal-yellow L in the chosen
/// corner; dragging a file (or the mouse) into it opens a panel with one row
/// per device. Dropping on a row sends. Everything is painted on one surface.
/// </summary>
sealed class DropPanel : Form
{
    readonly Identity _id;
    readonly Discovery _discovery;
    readonly Action<string, bool> _notify;
    readonly System.Windows.Forms.Timer _leaveTimer = new() { Interval = 150 };
    readonly Dictionary<string, SendState> _sending = new();

    bool _expanded, _modal, _refreshing;
    DateTime _lastRefresh;
    List<Device> _devices = [];
    string? _dragTitle, _dragSub;   // what's being dragged, shown in the header
    int _hover = -1, _dragOver = -1, _scroll;
    string? _hoverIcon;               // "gear" | "refresh" | "again" | "discard"
    List<string>? _pending;           // files from Explorer's "Send with…", waiting for a device
    List<HistoryEntry> _recent = [];
    int _hoverRecent = -1;

    // layout (device pixels), rebuilt in Measure()
    readonly List<Rectangle> _rows = [];
    readonly List<Rectangle> _recentRows = [];
    Rectangle _gear, _refresh, _again, _list, _discard;
    int _recentLabelY;

    public Action? OpenSettings;

    sealed class SendState
    {
        public string Status = "";
        public double? Fraction;
        public CancellationTokenSource? Cts;
        public bool Done, Ok, Error;
    }

    public DropPanel(Identity id, Discovery discovery, Action<string, bool> notify)
    {
        _id = id;
        _discovery = discovery;
        _notify = notify;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Ink;
        AllowDrop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _leaveTimer.Tick += (_, _) =>
        {
            if (!_expanded || _modal || _pending != null) return;
            var r = Bounds;
            r.Inflate(S(24), S(24));
            if (!r.Contains(Cursor.Position)) Collapse();
        };
        _discovery.Changed += () => { if (IsHandleCreated) BeginInvoke(Reload); };
        History.Changed += () =>
        {
            if (IsHandleCreated) BeginInvoke(() => { _recent = History.Latest(5).ToList(); if (_expanded) { Measure(); Invalidate(); } });
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => BeginInvoke(Reposition);
    }

    int S(int v) => LogicalToDeviceUnits(v);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW (no Alt-Tab), WS_EX_NOACTIVATE (no focus steal)
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Ui.SetBorderColor(Handle, Ui.Line);
        Collapse();
    }

    public void ShowExpanded() => Expand(false);

    /// <summary>True while a send is running (updates wait for this).</summary>
    public bool Busy => _sending.Values.Any(s => !s.Done);

    /// <summary>Explorer's "Send with LocalSend Drop": open with these files in hand.</summary>
    public void ShowShare(List<string> paths)
    {
        _pending = paths;
        SummarizePaths(paths.ToArray());
        Expand(false);
        Measure();
        Invalidate();
    }

    /// <summary>Re-applies the corner after settings change.</summary>
    public void Reposition()
    {
        if (_expanded) Expand(true); else Collapse();
    }

    // ---- collapse / expand ----

    void Collapse()
    {
        _expanded = false;
        _leaveTimer.Stop();
        _dragTitle = _dragSub = null;
        _pending = null;
        _hover = _dragOver = _hoverRecent = -1;
        _scroll = 0;
        var screen = Settings.Current.TargetScreen.Bounds;
        var st = Settings.Current;
        int len = S(140), t = S(4);
        Ui.SetCorners(Handle, false);
        Bounds = new Rectangle(st.Right ? screen.Right - len : screen.Left, st.Bottom ? screen.Bottom - len : screen.Top, len, len);
        // An L hugging the corner: flinging a file at the corner always lands in it.
        var region = new Region(new Rectangle(0, st.Bottom ? len - t : 0, len, t));
        region.Union(new Rectangle(st.Right ? len - t : 0, 0, t, len));
        Region = region;
        Invalidate();
    }

    void Expand(bool force)
    {
        if (_expanded && !force) return;
        var opening = !_expanded;
        _expanded = true;
        Region = null;
        Ui.SetCorners(Handle, true);
        _devices = _discovery.Devices.ToList();
        _recent = History.Latest(5).ToList();
        Measure();
        _leaveTimer.Start();
        Invalidate();

        if (opening && DateTime.UtcNow - _lastRefresh > TimeSpan.FromSeconds(10)) Refresh_();
    }

    async void Refresh_()
    {
        _lastRefresh = DateTime.UtcNow;
        _refreshing = true;
        Invalidate();
        try { await _discovery.RefreshAsync(); } catch { }
        _refreshing = false;
        if (_expanded) Invalidate();
    }

    void Reload()
    {
        if (!_expanded) return;
        // Keep the row order stable while something is dragged over it.
        if (_dragOver >= 0 && _discovery.Devices.Count == _devices.Count) { Invalidate(); return; }
        _devices = _discovery.Devices.ToList();
        Measure();
        Invalidate();
    }

    const int Width_ = 320, HeaderH = 82, RowH = 62, RowGap = 6, FooterH = 46, EmptyH = 132, RecentH = 44, RecentLabelH = 34;

    /// <summary>Recent transfers are hidden while you're holding files, to keep the targets clear.</summary>
    bool ShowRecent => _dragTitle == null && _pending == null && _recent.Count > 0;

    /// <summary>Sizes the window to its content and lays out hit areas.</summary>
    void Measure()
    {
        var wa = Settings.Current.TargetScreen.WorkingArea;
        int m = S(8), w = S(Width_);
        var devicesH = _devices.Count == 0 ? S(EmptyH) : _devices.Count * S(RowH + RowGap) - S(RowGap) + S(4);
        var contentH = devicesH + (ShowRecent ? S(RecentLabelH) + _recent.Count * S(RecentH) + S(4) : 0);
        var maxContent = wa.Height - 2 * m - S(HeaderH + FooterH);
        var listH = Math.Min(contentH, maxContent);
        var h = S(HeaderH) + listH + S(FooterH);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, contentH - listH));

        var st = Settings.Current;
        Bounds = new Rectangle(st.Right ? wa.Right - w - m : wa.Left + m, st.Bottom ? wa.Bottom - h - m : wa.Top + m, w, h);

        _list = new Rectangle(0, S(HeaderH), w, listH);
        _rows.Clear();
        for (var i = 0; i < _devices.Count; i++)
            _rows.Add(new Rectangle(S(10), _list.Top + i * S(RowH + RowGap) - _scroll, w - S(20), S(RowH)));

        _recentRows.Clear();
        if (ShowRecent)
        {
            _recentLabelY = _list.Top + devicesH + S(12) - _scroll;
            for (var i = 0; i < _recent.Count; i++)
                _recentRows.Add(new Rectangle(S(10), _recentLabelY + S(RecentLabelH - 12) + i * S(RecentH), w - S(20), S(RecentH)));
        }
        _discard = _pending != null ? new Rectangle(w - S(52), S(16), S(34), S(34)) : Rectangle.Empty;

        var fy = h - S(FooterH);
        _gear = new Rectangle(w - S(46), fy + S(6), S(34), S(34));
        _refresh = new Rectangle(w - S(82), fy + S(6), S(34), S(34));
        var again = TextRenderer.MeasureText("Look again", Ui.BodyStrong());
        _again = _devices.Count == 0 ? new Rectangle(S(20), _list.Top + S(92), again.Width, S(24)) : Rectangle.Empty;
    }

    int RecentAt(Point p)
    {
        if (!_list.Contains(p)) return -1;
        for (var i = 0; i < _recentRows.Count; i++) if (_recentRows[i].Contains(p)) return i;
        return -1;
    }

    void OnRecentClick(HistoryEntry h, bool reveal)
    {
        if (!h.Received && !h.Ok && !reveal && CanRetry(h))
        {
            StartSend(_devices.First(x => x.Fingerprint == h.PeerFingerprint), Sender.FromPaths(h.Paths), h.Paths);
            return;
        }
        var existing = h.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0) { _notify("Those files have been moved or deleted", true); return; }
        if (h.Received && h.Ok && !reveal && existing.Count == 1)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(existing[0]) { UseShellExecute = true });
        else
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{existing[0]}\"");
        Collapse();
    }

    int RowAt(Point p)
    {
        if (!_list.Contains(p)) return -1;
        for (var i = 0; i < _rows.Count; i++) if (_rows[i].Contains(p)) return i;
        return -1;
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (!_expanded)
        {
            g.Clear(Ui.Postal);
            return;
        }
        g.Clear(Ui.Ink);
        int w = ClientSize.Width, pad = S(20);

        // Header: what you're holding, or what to do.
        var title = _dragTitle ?? "Send to";
        var sub = _dragSub ?? (_devices.Count == 0 ? "Nobody to send to yet" : "Drop on a device, or click one to pick files");
        Ui.DrawText(g, title, Ui.Display(15f), new Rectangle(pad, S(16), w - 2 * pad - (_pending != null ? S(40) : 0), S(30)), Ui.Text);
        if (_pending != null) PaintIcon(g, _discard, "\uE711", "discard");
        Ui.DrawText(g, sub, Ui.Body(9f), new Rectangle(pad, S(46), w - 2 * pad, S(20)), _dragTitle != null ? Ui.Postal : Ui.Muted);

        g.SetClip(_list);
        if (_devices.Count == 0) PaintEmpty(g, pad, w);
        for (var i = 0; i < _devices.Count; i++)
            if (_rows[i].IntersectsWith(_list)) PaintRow(g, _devices[i], _rows[i], i);
        if (ShowRecent)
        {
            Ui.DrawText(g, "Recent", Ui.Body(9f), new Rectangle(pad, _recentLabelY, w - 2 * pad, S(18)), Ui.Muted);
            for (var i = 0; i < _recentRows.Count; i++)
                if (_recentRows[i].IntersectsWith(_list)) PaintRecent(g, _recent[i], _recentRows[i], i == _hoverRecent);
        }
        g.ResetClip();

        // Footer: who you are, refresh, settings.
        var fy = ClientSize.Height - S(FooterH);
        using (var line = new Pen(Ui.Line)) g.DrawLine(line, 0, fy, w, fy);
        Ui.DrawText(g, _refreshing ? "Looking for devices…" : $"You're {_id.Alias}", Ui.Body(9f),
            new Rectangle(pad, fy, _refresh.Left - pad - S(4), S(FooterH)), Ui.Muted);
        PaintIcon(g, _refresh, "", "refresh");
        PaintIcon(g, _gear, "", "gear");
    }

    void PaintRecent(Graphics g, HistoryEntry h, Rectangle r, bool hot)
    {
        if (hot) Ui.FillRound(g, Ui.InkHover, r, S(8));
        var icon = new Rectangle(r.Left + S(10), r.Top, S(24), r.Height);
        var glyph = h.Received ? "\uE896" : "\uE898"; // download / upload arrows
        Ui.DrawText(g, glyph, Ui.Glyph(10f), icon, !h.Ok ? Ui.Error : h.Received ? Ui.Postal : Ui.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var x = icon.Right + S(10);
        var tw = r.Right - x - S(10);
        Ui.DrawText(g, h.Title, Ui.BodyStrong(9.5f), new Rectangle(x, r.Top + S(4), tw, S(20)), Ui.Text);
        var who = $"{(h.Received ? "From" : "To")} {h.Peer}";
        var sub = h.Ok ? $"{who}, {History.Ago(h.Time)}" : $"{who}: {h.Error ?? "failed"}";
        if (hot)
            sub = h.Received && h.Ok ? "Click to open, right-click to show in folder"
                : !h.Received && !h.Ok && CanRetry(h) ? "Click to try again"
                : "Click to show in folder";
        Ui.DrawText(g, sub, Ui.Body(8.5f), new Rectangle(x, r.Top + S(23), tw, S(17)), h.Ok || hot ? Ui.Muted : Ui.Error);
    }

    bool CanRetry(HistoryEntry h) =>
        h.PeerFingerprint != null && _devices.Any(d => d.Fingerprint == h.PeerFingerprint) &&
        h.Paths.All(p => File.Exists(p) || Directory.Exists(p));

    void PaintIcon(Graphics g, Rectangle r, string glyph, string key)
    {
        if (_hoverIcon == key) Ui.FillRound(g, Ui.InkHover, r, r.Height / 2f);
        Ui.DrawText(g, glyph, Ui.Glyph(11f), r, _hoverIcon == key ? Ui.Text : Ui.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void PaintEmpty(Graphics g, int pad, int w)
    {
        var y = _list.Top + S(8);
        Ui.DrawText(g, "No one nearby", Ui.Display(12f), new Rectangle(pad, y, w - 2 * pad, S(24)), Ui.Text);
        TextRenderer.DrawText(g, "Open LocalSend Drop on another PC, or LocalSend on a phone, on the same network.",
            Ui.Body(9f), new Rectangle(pad, y + S(28), w - 2 * pad, S(48)), Ui.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        Ui.DrawText(g, "Look again", Ui.BodyStrong(), _again, _hoverIcon == "again" ? Ui.Text : Ui.Postal);
    }

    void PaintRow(Graphics g, Device d, Rectangle r, int i)
    {
        _sending.TryGetValue(d.Fingerprint, out var st);
        var target = i == _dragOver;
        var bg = target ? Ui.Postal : i == _hover ? Ui.InkHover : Ui.Ink2;
        Ui.FillRound(g, bg, r, S(10));

        var avatar = new Rectangle(r.Left + S(12), r.Top + (r.Height - S(38)) / 2, S(38), S(38));
        var textR = new Rectangle(avatar.Right + S(12), r.Top, r.Right - avatar.Right - S(24), r.Height);
        var name = new Rectangle(textR.Left, r.Top + S(11), textR.Width, S(22));
        var subR = new Rectangle(textR.Left, r.Top + S(33), textR.Width, S(18));

        string sub;
        Color subColor;
        if (target) { sub = "Release to send"; subColor = Ui.Ink; }
        else if (st != null) { sub = st.Status; subColor = st.Error ? Ui.Error : st.Ok ? Ui.Postal : Ui.Muted; }
        else { sub = Ui.DeviceLabel(d); subColor = Ui.Muted; }

        Ui.Monogram(g, avatar, d.Alias, d.Fingerprint, st is { Done: true, Ok: true });
        Ui.DrawText(g, d.Alias, Ui.Display(11.5f), name, target ? Ui.Ink : Ui.Text);
        Ui.DrawText(g, sub, Ui.Body(9f), subR, subColor);

        // Sending: the row fills with yellow like a tube; text over the fill turns ink.
        if (st?.Fraction is double f && f > 0)
        {
            var fill = new Rectangle(r.Left, r.Top, (int)(r.Width * f), r.Height);
            var old = g.Clip;
            g.SetClip(fill, System.Drawing.Drawing2D.CombineMode.Intersect);
            Ui.FillRound(g, Ui.Postal, r, S(10));
            Ui.Monogram(g, avatar, d.Alias, d.Fingerprint);
            Ui.DrawText(g, d.Alias, Ui.Display(11.5f), name, Ui.Ink);
            Ui.DrawText(g, sub, Ui.Body(9f), subR, Ui.Ink);
            g.Clip = old;
        }
    }

    // ---- mouse ----

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!_expanded) Expand(false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_expanded) return;
        var hover = RowAt(e.Location);
        var recent = RecentAt(e.Location);
        var icon = _gear.Contains(e.Location) ? "gear" : _refresh.Contains(e.Location) ? "refresh"
                 : _again.Contains(e.Location) ? "again" : _discard.Contains(e.Location) ? "discard" : null;
        if (hover != _hover || icon != _hoverIcon || recent != _hoverRecent)
        {
            _hover = hover;
            _hoverIcon = icon;
            _hoverRecent = recent;
            Cursor = hover >= 0 || recent >= 0 || icon != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _hoverRecent = -1;
        _hoverIcon = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!_expanded) return;
        if (RecentAt(e.Location) is var ri and >= 0) { OnRecentClick(_recent[ri], e.Button == MouseButtons.Right); return; }
        if (e.Button != MouseButtons.Left) return;
        if (_gear.Contains(e.Location)) { OpenSettings?.Invoke(); return; }
        if (_discard.Contains(e.Location)) { Collapse(); return; }
        if (_refresh.Contains(e.Location) || _again.Contains(e.Location)) { Refresh_(); return; }
        var i = RowAt(e.Location);
        if (i >= 0) OnRowClick(_devices[i]);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_expanded) return;
        _scroll -= Math.Sign(e.Delta) * S(RowH + RowGap);
        Measure();
        Invalidate();
    }

    // ---- drag and drop ----

    static bool Accepts(DragEventArgs e) =>
        e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText));

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        Expand(false);
        Summarize(e);
        OnDragOver(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var i = Accepts(e) ? RowAt(PointToClient(new Point(e.X, e.Y))) : -1;
        e.Effect = i >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if (i != _dragOver) { _dragOver = i; Invalidate(); }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dragOver = -1;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var i = _dragOver;
        _dragOver = -1;
        _dragTitle = _dragSub = null;
        Invalidate();
        if (i < 0 || i >= _devices.Count) return;
        var d = _devices[i];

        List<SendItem> items;
        List<string> sources = [];
        if (e.Data!.GetData(DataFormats.FileDrop) is string[] paths)
        {
            items = Sender.FromPaths(paths);
            sources = paths.ToList();
        }
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
            items = [new SendItem("message.txt", null, text)];
        else return;
        // Let Explorer finish its side of the drop before we do network work.
        BeginInvoke(() => StartSend(d, items, sources));
    }

    /// <summary>Header shows what's in hand: "report.pdf" / "2.1 MB".</summary>
    void Summarize(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
            SummarizePaths(dropped);
        else if (e.Data?.GetDataPresent(DataFormats.UnicodeText) == true)
        {
            _dragTitle = "Text";
            _dragSub = "Arrives as a message";
        }
        Invalidate();
    }

    void SummarizePaths(string[] paths)
    {
        var folders = paths.Count(Directory.Exists);
        var size = paths.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        _dragTitle = paths.Length == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : $"{paths.Length} items";
        _dragSub = folders == paths.Length
            ? (folders == 1 ? "A folder, with everything inside" : $"{folders} folders, with everything inside")
            : folders > 0 ? $"{IncomingCard.FormatSize(size)} plus {folders} folder{(folders == 1 ? "" : "s")}"
            : IncomingCard.FormatSize(size);
        if (_pending != null) _dragSub += ". Click a device to send";
    }

    // ---- sending ----

    void OnRowClick(Device d)
    {
        if (_pending != null)
        {
            var sources = _pending;
            _pending = null;
            _dragTitle = _dragSub = null;
            Measure();
            StartSend(d, Sender.FromPaths(sources), sources);
            return;
        }
        if (_sending.TryGetValue(d.Fingerprint, out var st) && !st.Done)
        {
            st.Cts?.Cancel();
            return;
        }
        _modal = true;
        try
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = $"Send to {d.Alias}" };
            if (dlg.ShowDialog(this) == DialogResult.OK) StartSend(d, Sender.FromPaths(dlg.FileNames), dlg.FileNames.ToList());
        }
        finally { _modal = false; }
    }

    /// <param name="sources">What the user picked (files/folders), kept in Recent for retry.</param>
    async void StartSend(Device d, List<SendItem> items, List<string> sources)
    {
        if (items.Count == 0) return;
        if (_sending.TryGetValue(d.Fingerprint, out var busy) && !busy.Done)
        {
            _notify($"Already sending to {d.Alias}", false);
            return;
        }
        var st = new SendState { Cts = new CancellationTokenSource(), Status = "Connecting…" };
        _sending[d.Fingerprint] = st;
        Invalidate();

        var progress = new Progress<(string text, double? fraction)>(p =>
        {
            st.Status = p.fraction == null ? $"Waiting for {d.Alias} to accept" : $"{p.text}. Click to cancel";
            st.Fraction = p.fraction;
            Invalidate();
        });

        var what = items.Count == 1 ? items[0].Name : $"{items.Count} files";
        try
        {
            var result = await Sender.SendAsync(_id, _discovery.HttpPort, d, items, () => AskPin(d), progress, st.Cts.Token);
            st.Status = result;
            st.Ok = true;
            if (!_expanded) _notify($"Sent {what} to {d.Alias}", false);
        }
        catch (OperationCanceledException) { st.Status = "Cancelled"; }
        catch (SendFailed ex)
        {
            st.Status = ex.Message;
            st.Error = true;
            if (!_expanded) _notify($"Couldn't send {what} to {d.Alias}: {ex.Message}", true);
        }
        catch (Exception ex)
        {
            st.Status = "Something went wrong";
            st.Error = true;
            _notify($"Couldn't send {what} to {d.Alias}: {ex.Message}", true);
        }
        st.Done = true;
        st.Fraction = null;
        Invalidate();
        if (st.Status != "Cancelled")
            History.Add(new HistoryEntry
            {
                Time = DateTime.UtcNow, Received = false, Peer = d.Alias, PeerFingerprint = d.Fingerprint,
                Paths = sources, Title = sources.Count > 0 ? History.TitleFor(sources) : "Message",
                Ok = st.Ok, Error = st.Ok ? null : st.Status,
            });

        // Show the result on the row for a moment, then go back to normal.
        await Task.Delay(6000);
        if (_sending.TryGetValue(d.Fingerprint, out var cur) && cur == st) _sending.Remove(d.Fingerprint);
        Invalidate();
    }

    string? AskPin(Device d)
    {
        _modal = true;
        try { return PromptForm.Ask($"{d.Alias} needs a PIN", "Ask them for the PIN shown in LocalSend.", "Send"); }
        finally { _modal = false; }
    }
}
