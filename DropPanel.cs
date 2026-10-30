namespace LocalSendDrop;

/// <summary>
/// The corner drop zone. Collapsed it's a thin accent-coloured L in the chosen
/// corner; dragging a file (or the mouse) into it opens a flyout listing the
/// devices nearby, styled like Windows 11's Quick Settings. Dropping on a row sends.
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
        AllowDrop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _leaveTimer.Tick += (_, _) =>
        {
            if (!_expanded || _modal || _pending != null) return;
            var r = Bounds;
            r.Inflate(S(24), S(24));
            if (!r.Contains(Cursor.Position)) Collapse();
        };
        // Stay above other always-on-top windows (Chrome's fullscreen video, picture-in-picture).
        _onTop.Tick += (_, _) => { if (IsHandleCreated && Visible) Ui.KeepOnTop(Handle); };
        _onTop.Start();
        _discovery.Changed += OnDevicesChanged;
        History.Changed += OnHistoryChanged;
        Ui.Changed += OnThemeChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    readonly System.Windows.Forms.Timer _onTop = new() { Interval = 2000 };

    // These come from other threads or outlive the window: run them on the UI
    // thread, and never after the panel has been closed.
    void Post(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => { if (!IsDisposed) action(); }); }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { }
    }

    void OnDevicesChanged() => Post(Reload);
    void OnHistoryChanged() => Post(() => { _recent = History.Latest(5).ToList(); if (_expanded) { Measure(); Invalidate(); } });
    void OnThemeChanged() => Post(() => { Ui.Chrome(Handle, _expanded); Invalidate(); });
    void OnDisplayChanged(object? sender, EventArgs e) => Post(Reposition);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _onTop.Dispose();
            _leaveTimer.Dispose();
            _discovery.Changed -= OnDevicesChanged;
            History.Changed -= OnHistoryChanged;
            Ui.Changed -= OnThemeChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }
        base.Dispose(disposing);
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
        int len = S(140), t = S(3);
        Ui.Chrome(Handle, round: false);
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
        Ui.Chrome(Handle);
        _devices = _discovery.Devices.ToList();
        _recent = History.Latest(5).ToList();
        Measure();
        Ui.KeepOnTop(Handle);
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
        if (IsDisposed) return; // closed while searching
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

    // Windows 11 flyout metrics: 4 px list inset, 4 px item radius, 48 px footer.
    const int Width_ = 340, HeaderH = 72, RowH = 56, FooterH = 48, EmptyH = 112, RecentH = 48, RecentLabelH = 40, Inset = 4;

    /// <summary>Recent transfers are hidden while you're holding files, to keep the targets clear.</summary>
    bool ShowRecent => _dragTitle == null && _pending == null && _recent.Count > 0;

    /// <summary>Sizes the window to its content and lays out hit areas.</summary>
    void Measure()
    {
        var wa = Settings.Current.TargetScreen.WorkingArea;
        int m = S(12), w = S(Width_);
        var devicesH = _devices.Count == 0 ? S(EmptyH) : _devices.Count * S(RowH) + S(8);
        var contentH = devicesH + (ShowRecent ? S(RecentLabelH) + _recent.Count * S(RecentH) + S(8) : 0);
        var maxContent = wa.Height - 2 * m - S(HeaderH + FooterH);
        var listH = Math.Min(contentH, maxContent);
        var h = S(HeaderH) + listH + S(FooterH);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, contentH - listH));

        var st = Settings.Current;
        Bounds = new Rectangle(st.Right ? wa.Right - w - m : wa.Left + m, st.Bottom ? wa.Bottom - h - m : wa.Top + m, w, h);

        _list = new Rectangle(0, S(HeaderH), w, listH);
        _rows.Clear();
        for (var i = 0; i < _devices.Count; i++)
            _rows.Add(new Rectangle(S(Inset), _list.Top + i * S(RowH) - _scroll, w - S(2 * Inset), S(RowH)));

        _recentRows.Clear();
        if (ShowRecent)
        {
            _recentLabelY = _list.Top + devicesH - _scroll;
            for (var i = 0; i < _recent.Count; i++)
                _recentRows.Add(new Rectangle(S(Inset), _recentLabelY + S(RecentLabelH) + i * S(RecentH), w - S(2 * Inset), S(RecentH)));
        }
        _discard = _pending != null ? new Rectangle(w - S(48), S(16), S(32), S(32)) : Rectangle.Empty;

        var fy = h - S(FooterH);
        _gear = new Rectangle(w - S(44), fy + S(8), S(32), S(32));
        _refresh = new Rectangle(w - S(80), fy + S(8), S(32), S(32));
        var again = TextRenderer.MeasureText("Search again", Ui.Body, Size.Empty, TextFormatFlags.NoPadding);
        _again = _devices.Count == 0 ? new Rectangle(S(16), _list.Top + S(70), again.Width, S(24)) : Rectangle.Empty;
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
            g.Clear(Ui.Accent);
            return;
        }
        g.Clear(Ui.Flyout);
        int w = ClientSize.Width, pad = S(16);

        // Header: what you're holding, or what to do.
        var title = _dragTitle != null ? $"Send {_dragTitle}" : "Send to";
        var sub = _dragSub ?? (_devices.Count == 0 ? "No devices found yet" : "Drop files on a device, or select one to choose files");
        var titleW = w - 2 * pad - (_pending != null ? S(40) : 0);
        Ui.DrawText(g, title, Ui.BodyStrong, new Rectangle(pad, S(16), titleW, S(20)), Ui.Text);
        Ui.DrawText(g, sub, Ui.Caption, new Rectangle(pad, S(38), titleW, S(18)), Ui.TextSecondary);
        if (_pending != null) PaintIcon(g, _discard, Ui.GlyphClose, "discard");

        g.SetClip(_list);
        if (_devices.Count == 0) PaintEmpty(g, pad, w);
        for (var i = 0; i < _devices.Count; i++)
            if (_rows[i].IntersectsWith(_list)) PaintRow(g, _devices[i], _rows[i], i);
        if (ShowRecent)
        {
            using (var line = new Pen(Ui.Divider)) g.DrawLine(line, pad, _recentLabelY, w - pad, _recentLabelY);
            Ui.DrawText(g, "Recent", Ui.BodyStrong, new Rectangle(pad, _recentLabelY + S(12), w - 2 * pad, S(20)), Ui.Text);
            for (var i = 0; i < _recentRows.Count; i++)
                if (_recentRows[i].IntersectsWith(_list)) PaintRecent(g, _recent[i], _recentRows[i], i == _hoverRecent);
        }
        g.ResetClip();

        // Footer bar, like Quick Settings: who you are, refresh, settings.
        var fy = ClientSize.Height - S(FooterH);
        using (var b = new SolidBrush(Ui.Base)) g.FillRectangle(b, 0, fy, w, S(FooterH));
        using (var line = new Pen(Ui.Divider)) g.DrawLine(line, 0, fy, w, fy);
        Ui.DrawText(g, _refreshing ? "Searching for devices…" : _id.Alias, Ui.Caption,
            new Rectangle(pad, fy, _refresh.Left - pad - S(4), S(FooterH)), Ui.TextSecondary);
        PaintIcon(g, _refresh, Ui.GlyphRefresh, "refresh");
        PaintIcon(g, _gear, Ui.GlyphSettings, "gear");
    }

    void PaintRecent(Graphics g, HistoryEntry h, Rectangle r, bool hot)
    {
        if (hot) Ui.FillRound(g, Ui.SubtleHover, r, S(4));
        var icon = new Rectangle(r.Left + S(12), r.Top, S(20), r.Height);
        Ui.DrawIcon(g, h.Received ? Ui.GlyphDown : Ui.GlyphUp, 11f, icon, h.Ok ? Ui.TextSecondary : Ui.Error);
        var x = icon.Right + S(12);
        var tw = r.Right - x - S(12);
        var when = History.Ago(h.Time);
        var whenW = TextRenderer.MeasureText(when, Ui.Caption, Size.Empty, TextFormatFlags.NoPadding).Width;
        Ui.DrawText(g, h.Title, Ui.Body, new Rectangle(x, r.Top + S(5), tw - whenW - S(8), S(20)), Ui.Text);
        Ui.DrawText(g, when, Ui.Caption, new Rectangle(r.Right - S(12) - whenW, r.Top + S(5), whenW, S(20)), Ui.TextTertiary);
        var who = $"{(h.Received ? "From" : "To")} {h.Peer}";
        var sub = h.Ok ? who : $"{who}: {h.Error ?? "failed"}";
        if (hot)
            sub = h.Received && h.Ok ? "Select to open, right-click to show in folder"
                : !h.Received && !h.Ok && CanRetry(h) ? "Select to try again"
                : "Select to show in folder";
        Ui.DrawText(g, sub, Ui.Caption, new Rectangle(x, r.Top + S(25), tw, S(18)), h.Ok || hot ? Ui.TextSecondary : Ui.Error);
    }

    bool CanRetry(HistoryEntry h) =>
        h.PeerFingerprint != null && _devices.Any(d => d.Fingerprint == h.PeerFingerprint) &&
        h.Paths.All(p => File.Exists(p) || Directory.Exists(p));

    void PaintIcon(Graphics g, Rectangle r, string glyph, string key)
    {
        if (_hoverIcon == key) Ui.FillRound(g, Ui.SubtleHover, r, S(4));
        Ui.DrawIcon(g, glyph, 12f, r, Ui.Text);
    }

    void PaintEmpty(Graphics g, int pad, int w)
    {
        var y = _list.Top + S(8);
        Ui.DrawText(g, "No devices nearby", Ui.Body, new Rectangle(pad, y, w - 2 * pad, S(20)), Ui.Text);
        TextRenderer.DrawText(g, "Other PCs need LocalSend Drop open, phones need LocalSend, and both need to be on this network.",
            Ui.Caption, new Rectangle(pad, y + S(24), w - 2 * pad, S(36)), Ui.TextSecondary,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        Ui.DrawText(g, "Search again", Ui.Body, _again, _hoverIcon == "again" ? Ui.TextSecondary : Ui.AccentText);
    }

    void PaintRow(Graphics g, Device d, Rectangle r, int i)
    {
        _sending.TryGetValue(d.Fingerprint, out var st);
        var target = i == _dragOver;
        if (target || i == _hover) Ui.FillRound(g, Ui.SubtleHover, r, S(4));
        // Windows 11 selection indicator: a short accent pill on the left edge.
        if (target) Ui.FillRound(g, Ui.Accent, new RectangleF(r.Left, r.Top + (r.Height - S(16)) / 2f, S(3), S(16)), S(2) / 2f + 0.5f);

        var icon = new Rectangle(r.Left + S(12), r.Top, S(24), r.Height);
        Ui.DrawIcon(g, Ui.DeviceGlyph(d), 14f, icon, Ui.Text);

        var x = icon.Right + S(12);
        var tw = r.Right - x - S(12);
        string sub;
        Color subColor = Ui.TextSecondary;
        if (target) { sub = "Release to send"; subColor = Ui.AccentText; }
        else if (st != null) { sub = st.Status; if (st.Error) subColor = Ui.Error; else if (st.Ok) subColor = Ui.Success; }
        else sub = Ui.DeviceLabel(d);

        Ui.DrawText(g, d.Alias, Ui.Body, new Rectangle(x, r.Top + S(9), tw, S(20)), Ui.Text);
        Ui.DrawText(g, sub, Ui.Caption, new Rectangle(x, r.Top + S(29), tw, S(18)), subColor);
        if (st?.Fraction is double f)
            Ui.Progress(g, new Rectangle(x, r.Bottom - S(8), tw, S(4)), f, DeviceDpi / 96f);
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
        _scroll -= Math.Sign(e.Delta) * S(RowH);
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
            st.Status = p.fraction == null ? $"Waiting for {d.Alias} to accept" : $"{p.text}. Select to cancel";
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
        if (IsDisposed) return;
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
