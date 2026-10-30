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
    string? _hoverIcon;               // "gear" | "refresh" | "again" | "discard" | "clear"
    List<string>? _pending;           // files from Explorer's "Send with…", waiting for a device
    string? _pendingText;             // copied text (Ctrl+Alt+S), waiting for a device
    bool Holding => _pending != null || _pendingText != null;
    const int Everyone = -2;          // RowAt() result for the "Everyone nearby" row
    const int QrRow = -3;             // RowAt() result for "Phone without the app"
    const int ShelfRow = -4;          // RowAt() result for the shelf box (top right) and the files on it
    bool _dragging, _dragHasFiles;
    int _trayH, _hoverTile = -1;
    readonly List<Rectangle> _tiles = [];   // the files on the shelf, as icons under the header
    List<HistoryEntry> _recent = [];
    Mouth? _mouth;                    // the "Little mouth" corner, when chosen
    string? _ateFrom;                 // something was sent while open: the mouth gulps it on close
    bool _ateText;
    int _hoverRecent = -1;

    // layout (device pixels), rebuilt in Measure()
    readonly List<Rectangle> _rows = [];
    readonly List<Rectangle> _recentRows = [];
    Rectangle _gear, _refresh, _again, _list, _discard, _clear, _everyone, _quiet, _qr, _shelf, _tray;
    int _recentLabelY;

    public Action? OpenSettings;
    /// <summary>Footer button: turns Do not disturb on or off.</summary>
    public Action? ToggleQuiet;
    /// <summary>"Phone without the app": files (with paths) and/or text to share by QR code.</summary>
    public Action<List<SendItem>, string?>? ShareByQr;
    /// <summary>"Get files from a phone": show an upload QR code.</summary>
    public Action? ReceiveByQr;
    /// <summary>Device menu: open the conversation, or ask for a file.</summary>
    public Action<Device>? OpenNotes, AskForFile;

    /// <summary>The device with this fingerprint, if it's nearby.</summary>
    public Device? Find(string fingerprint) => _discovery.Devices.FirstOrDefault(d => d.Fingerprint == fingerprint);

    /// <summary>Sends files to a device (e.g. answering its request).</summary>
    public void SendFiles(Device d, List<string> paths) => StartSend(d, Sender.FromPaths(paths), paths);

    public static string DisplayName(Device d) => NameOf(d);

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
            if (!_expanded || _modal || Holding) return;
            var r = Bounds;
            r.Inflate(S(24), S(24));
            if (!r.Contains(Cursor.Position) && !(_mouth is { Visible: true } && _mouth.Bounds.Contains(Cursor.Position))) Collapse();
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    /// <summary>1 = Ctrl+Alt+S (send what you copied), 2 = Ctrl+Alt+Shift+S (screenshot).</summary>
    public event Action<int>? Hotkey;

    /// <summary>Registers the shortcuts; false for any another app already owns.</summary>
    public (bool Clipboard, bool Screenshot) RegisterHotkeys()
    {
        const uint Alt = 1, Ctrl = 2, Shift = 4, NoRepeat = 0x4000;
        return (RegisterHotKey(Handle, 1, Ctrl | Alt | NoRepeat, 'S'), RegisterHotKey(Handle, 2, Ctrl | Alt | Shift | NoRepeat, 'S'));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) Hotkey?.Invoke((int)m.WParam); // WM_HOTKEY
        base.WndProc(ref m);
    }

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
        _pendingText = null;
        SummarizePaths(paths.ToArray());
        Expand(false);
        Measure();
        Invalidate();
    }

    /// <summary>Copied text (Ctrl+Alt+S): open with it in hand; it arrives as a message.</summary>
    public void ShowShareText(string text)
    {
        _pending = null;
        _pendingText = text;
        var firstLine = text.Replace("\r", "").Split('\n')[0].Trim();
        _dragTitle = firstLine.Length > 0 ? $"\u201C{firstLine}\u201D" : "text";
        _dragSub = "Arrives as a message. Click a device to send";
        Expand(false);
        Measure();
        Invalidate();
    }

    /// <summary>The name you gave a device, or its own.</summary>
    static string NameOf(Device d) => Settings.Current.Nicknames.GetValueOrDefault(d.Fingerprint) ?? d.Alias;

    /// <summary>Pinned devices first, then by name.</summary>
    List<Device> Ordered() => _discovery.Devices
        .OrderByDescending(d => Settings.Current.Favorites.Contains(d.Fingerprint))
        .ThenBy(NameOf, StringComparer.CurrentCultureIgnoreCase).ToList();

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
        _pendingText = null;
        _hover = _dragOver = _hoverRecent = -1;
        _scroll = 0;
        var screen = Settings.Current.TargetScreen.Bounds;
        var st = Settings.Current;
        int len = S(140), t = S(3);
        Ui.Chrome(Handle, round: false);
        Bounds = new Rectangle(st.Right ? screen.Right - len : screen.Left, st.Bottom ? screen.Bottom - len : screen.Top, len, len);
        if (st.ZoneStyle == "Mouth")
        {
            // The mouth is its own window; this one steps aside until it's needed.
            Region = new Region(Rectangle.Empty);
            if (_mouth == null)
            {
                _mouth = new Mouth();
                _mouth.Wanted += () => Expand(false);
                _mouth.Ate += Shelve;
            }
            _mouth.Place();
            if (_ateFrom != null || _ateText) _mouth.Gulp(_ateFrom == null ? null : Thumbs.Preview(_ateFrom, () => { })?.Image);
            else _mouth.Waiting(false);
        }
        else
        {
            _mouth?.Hide();
            // An L hugging the corner: flinging a file at the corner always lands in it.
            var region = new Region(new Rectangle(0, st.Bottom ? len - t : 0, len, t));
            region.Union(new Rectangle(st.Right ? len - t : 0, 0, t, len));
            Region = region;
        }
        _ateFrom = null;
        _ateText = false;
        Invalidate();
    }

    void Expand(bool force)
    {
        if (_expanded && !force) return;
        var opening = !_expanded;
        _expanded = true;
        Region = null;
        Ui.Chrome(Handle);
        _devices = Ordered();
        _recent = History.Latest(5).ToList();
        if (opening)
        {
            // Glass: blur whatever the panel is about to cover (its whole column, it may grow).
            var wa = Settings.Current.TargetScreen.WorkingArea;
            var w = S(Width_);
            var x = Settings.Current.Right ? wa.Right - w - S(12) - MouthRoom : wa.Left + S(12) + MouthRoom;
            Effects.Capture(this, new Rectangle(x, wa.Top, w, wa.Height));
        }
        Measure();
        Ui.KeepOnTop(Handle);
        if (opening && _mouth is { Visible: true }) _mouth.Waiting(true);
        if (opening) Effects.Show(this, Settings.Current.Bottom ? S(12) : -S(12));
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
        _devices = Ordered();
        Measure();
        Invalidate();
    }

    // Windows 11 flyout metrics: 4 px list inset, 4 px item radius, 48 px footer.
    const int TrayH = 60, TileSize = 48, ShelfW = 76;
    /// <summary>With the little mouth, the panel opens beside it so the mouth stays in sight.</summary>
    int MouthRoom => Settings.Current.ZoneStyle == "Mouth" ? S(52) : 0;
    const int Width_ = 340, HeaderH = 72, RowH = 56, FooterH = 48, EmptyH = 112, RecentH = 48, RecentLabelH = 40, Inset = 4;

    /// <summary>Recent transfers are hidden while you're holding files, to keep the targets clear.</summary>
    bool ShowRecent => _dragTitle == null && !Holding && _recent.Count > 0;

    /// <summary>Sizes the window to its content and lays out hit areas.</summary>
    void Measure()
    {
        var wa = Settings.Current.TargetScreen.WorkingArea;
        int m = S(12), w = S(Width_);
        var everyoneH = _devices.Count >= 2 ? S(RowH) : 0;
        _trayH = _pending is { Count: > 0 } ? S(TrayH) : 0;
        var listedH = _trayH + (_devices.Count == 0 ? S(EmptyH) : everyoneH + _devices.Count * S(RowH));
        var devicesH = listedH + S(RowH) + S(8);   // + "Phone without the app"
        var contentH = devicesH + (ShowRecent ? S(RecentLabelH) + _recent.Count * S(RecentH) + S(8) : 0);
        var maxContent = wa.Height - 2 * m - S(HeaderH + FooterH);
        var listH = Math.Min(contentH, maxContent);
        var h = S(HeaderH) + listH + S(FooterH);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, contentH - listH));

        var st = Settings.Current;
        Bounds = new Rectangle(st.Right ? wa.Right - w - m - MouthRoom : wa.Left + m + MouthRoom, st.Bottom ? wa.Bottom - h - m : wa.Top + m, w, h);

        _list = new Rectangle(0, S(HeaderH), w, listH);
        _rows.Clear();
        _tray = new Rectangle(S(Inset), _list.Top - _scroll, w - S(2 * Inset), _trayH);
        _tiles.Clear();
        if (_trayH > 0)
        {
            int tile = S(TileSize), gap = S(8), x0 = S(16);
            var fit = Math.Max(1, (w - 2 * x0 + gap) / (tile + gap));
            for (var i = 0; i < Math.Min(_pending!.Count, fit); i++)
                _tiles.Add(new Rectangle(x0 + i * (tile + gap), _tray.Top + S(4), tile, tile));
        }
        _shelf = _pendingText == null ? new Rectangle(w - S(16 + ShelfW), S(12), S(ShelfW), S(48)) : Rectangle.Empty;
        _everyone = everyoneH > 0 ? new Rectangle(S(Inset), _list.Top + _trayH - _scroll, w - S(2 * Inset), S(RowH)) : Rectangle.Empty;
        for (var i = 0; i < _devices.Count; i++)
            _rows.Add(new Rectangle(S(Inset), _list.Top + _trayH + everyoneH + i * S(RowH) - _scroll, w - S(2 * Inset), S(RowH)));

        _qr = new Rectangle(S(Inset), _list.Top + listedH - _scroll, w - S(2 * Inset), S(RowH));
        _recentRows.Clear();
        _clear = Rectangle.Empty;
        if (ShowRecent)
        {
            _recentLabelY = _list.Top + devicesH - _scroll;
            var cw = TextRenderer.MeasureText("Clear", Ui.Body, Size.Empty, TextFormatFlags.NoPadding).Width;
            _clear = new Rectangle(w - S(16) - cw, _recentLabelY + S(12), cw, S(20));
            for (var i = 0; i < _recent.Count; i++)
                _recentRows.Add(new Rectangle(S(Inset), _recentLabelY + S(RecentLabelH) + i * S(RecentH), w - S(2 * Inset), S(RecentH)));
        }
        _discard = !Holding ? Rectangle.Empty
            : _shelf.IsEmpty ? new Rectangle(w - S(48), S(16), S(32), S(32))
            : new Rectangle(_shelf.Left - S(40), S(20), S(32), S(32));

        var fy = h - S(FooterH);
        _gear = new Rectangle(w - S(44), fy + S(8), S(32), S(32));
        _refresh = new Rectangle(w - S(80), fy + S(8), S(32), S(32));
        _quiet = new Rectangle(w - S(116), fy + S(8), S(32), S(32));
        var again = TextRenderer.MeasureText("Search again", Ui.Body, Size.Empty, TextFormatFlags.NoPadding);
        _again = _devices.Count == 0 ? new Rectangle(S(16), _list.Top + _trayH + S(70), again.Width, S(24)) : Rectangle.Empty;
    }

    int RecentAt(Point p)
    {
        if (!_list.Contains(p)) return -1;
        for (var i = 0; i < _recentRows.Count; i++) if (_recentRows[i].Contains(p)) return i;
        return -1;
    }

    void OnRecentClick(HistoryEntry h, bool reveal)
    {
        if (h.Text != null)
        {
            Clipboard.SetText(h.Text);
            _notify("Message copied", false);
            return;
        }
        if (h.Received && !h.Ok) return; // nothing was saved
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

    int TileAt(Point p)
    {
        if (!_list.Contains(p)) return -1;
        for (var i = 0; i < _tiles.Count; i++) if (_tiles[i].Contains(p)) return i;
        return -1;
    }

    void TakeOffShelf(int i)
    {
        _pending!.RemoveAt(i);
        _hoverTile = -1;
        if (_pending.Count == 0) { _pending = null; _dragTitle = _dragSub = null; }
        else SummarizePaths(_pending.ToArray());
        Measure();
        Invalidate();
    }

    int RowAt(Point p)
    {
        if (_shelf.Contains(p)) return ShelfRow;
        if (!_list.Contains(p)) return -1;
        if (_dragging && _tray.Contains(p)) return ShelfRow;
        if (_everyone.Contains(p)) return Everyone;
        if (_qr.Contains(p)) return QrRow;
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
        Effects.PaintSurface(g, this, Ui.Flyout);
        int w = ClientSize.Width, pad = S(16);

        // Header: what you're holding, or what to do.
        var title = _dragTitle != null ? $"Send {_dragTitle}" : "Send to";
        var sub = _hoverTile >= 0 && _hoverTile < TileFiles
            ? $"{Path.GetFileName(_pending![_hoverTile].TrimEnd('\\'))}. Click to take it off"
            : _dragSub ?? (_devices.Count == 0 ? "No devices found yet" : "Drop files on a device, or click one");
        var titleW = (_discard.IsEmpty ? _shelf.IsEmpty ? w - pad : _shelf.Left : _discard.Left) - pad - S(8);
        Ui.DrawText(g, title, Ui.BodyStrong, new Rectangle(pad, S(16), titleW, S(20)), Ui.Text);
        Ui.DrawText(g, sub, Ui.Caption, new Rectangle(pad, S(38), titleW, S(18)), Ui.TextSecondary);
        if (Holding) PaintIcon(g, _discard, Ui.GlyphClose, "discard");
        if (!_shelf.IsEmpty) PaintShelf(g, _shelf);

        g.SetClip(_list);
        if (_devices.Count == 0) PaintEmpty(g, pad, w);
        if (!_everyone.IsEmpty) PaintEveryone(g, _everyone);
        PaintQrRow(g, _qr);
        if (_tiles.Count > 0) PaintTray(g);
        for (var i = 0; i < _devices.Count; i++)
            if (_rows[i].IntersectsWith(_list)) PaintRow(g, _devices[i], _rows[i], i);
        if (ShowRecent)
        {
            using (var line = new Pen(Ui.Divider)) g.DrawLine(line, pad, _recentLabelY, w - pad, _recentLabelY);
            Ui.DrawText(g, "Recent", Ui.BodyStrong, new Rectangle(pad, _recentLabelY + S(12), w - 2 * pad, S(20)), Ui.Text);
            Ui.DrawText(g, "Clear", Ui.Body, _clear, _hoverIcon == "clear" ? Ui.TextSecondary : Ui.AccentText);
            for (var i = 0; i < _recentRows.Count; i++)
                if (_recentRows[i].IntersectsWith(_list)) PaintRecent(g, _recent[i], _recentRows[i], i == _hoverRecent);
        }
        g.ResetClip();

        // Footer bar, like Quick Settings: who you are, refresh, settings.
        var fy = ClientSize.Height - S(FooterH);
        using (var b = new SolidBrush(Ui.Footer)) g.FillRectangle(b, 0, fy, w, S(FooterH));
        using (var line = new Pen(Ui.Divider)) g.DrawLine(line, 0, fy, w, fy);
        var footer = _refreshing ? "Searching for devices…"
            : Settings.Current.DoNotDisturb ? "Do not disturb is on"
            : Quiet.Auto ? "Quiet while you're presenting"
            : _id.Alias;
        Ui.DrawText(g, footer, Ui.Caption, new Rectangle(pad, fy, _quiet.Left - pad - S(4), S(FooterH)), Ui.TextSecondary);
        // Like a Quick Settings toggle: accent-filled while on.
        if (Settings.Current.DoNotDisturb)
        {
            Ui.FillRound(g, _hoverIcon == "quiet" ? Ui.AccentHover : Ui.Accent, _quiet, S(4));
            Ui.DrawIcon(g, Ui.GlyphQuiet, 12f, _quiet, Ui.TextOnAccent);
        }
        else PaintIcon(g, _quiet, Ui.GlyphQuiet, "quiet");
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
            sub = h.Text != null ? "Select to copy"
                : h.Received && h.Ok ? "Select to open, or drag it somewhere"
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
        var y = _list.Top + _trayH + S(8);
        Ui.DrawText(g, "No devices nearby", Ui.Body, new Rectangle(pad, y, w - 2 * pad, S(20)), Ui.Text);
        TextRenderer.DrawText(g, "Other PCs need LocalSend Drop open, phones need LocalSend, and both need to be on this network.",
            Ui.Caption, new Rectangle(pad, y + S(24), w - 2 * pad, S(36)), Ui.TextSecondary,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        Ui.DrawText(g, "Search again", Ui.Body, _again, _hoverIcon == "again" ? Ui.TextSecondary : Ui.AccentText);
    }

    void PaintEveryone(Graphics g, Rectangle r)
    {
        var target = _dragOver == Everyone;
        if (target || _hover == Everyone) Ui.FillRound(g, Ui.SubtleHover, r, S(4));
        if (target) Ui.FillRound(g, Ui.Accent, new RectangleF(r.Left, r.Top + (r.Height - S(16)) / 2f, S(3), S(16)), S(2) / 2f + 0.5f);
        var icon = new Rectangle(r.Left + S(12), r.Top, S(24), r.Height);
        Ui.DrawIcon(g, Ui.GlyphPeople, 14f, icon, Ui.Text);
        var x = icon.Right + S(12);
        Ui.DrawText(g, "Everyone nearby", Ui.Body, new Rectangle(x, r.Top + S(9), r.Right - x - S(12), S(20)), Ui.Text);
        Ui.DrawText(g, target ? $"Release to send to all {_devices.Count}" : $"Send to all {_devices.Count} devices at once", Ui.Caption,
            new Rectangle(x, r.Top + S(29), r.Right - x - S(12), S(18)), target ? Ui.AccentText : Ui.TextSecondary);
    }

    /// <summary>Tiles that stand for one file each; the last one may be "+3" instead.</summary>
    int TileFiles => _pending == null ? 0 : _pending.Count > _tiles.Count ? _tiles.Count - 1 : _tiles.Count;

    void PaintTray(Graphics g)
    {
        var radius = S(6);
        for (var i = 0; i < _tiles.Count; i++)
        {
            var r = _tiles[i];
            var hover = i == _hoverTile;
            Ui.FillRound(g, hover ? Ui.ControlHover : Ui.ControlFill, r, radius);
            if (i >= TileFiles)
            {
                Ui.DrawText(g, $"+{_pending!.Count - TileFiles}", Ui.BodyStrong, r, Ui.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Ui.StrokeRound(g, Ui.ControlStroke, r, radius);
                continue;
            }
            var path = _pending![i];
            var preview = Thumbs.Preview(path, () => { try { BeginInvoke(Invalidate); } catch { } });
            var image = preview?.Image;
            if (preview is { Photo: true })
            {
                // A real preview fills the tile, cropped to a square.
                var side = Math.Min(image!.Width, image.Height);
                var src = new Rectangle((image.Width - side) / 2, (image.Height - side) / 2, side, side);
                using var clip = Ui.Round(r, Ui.Radius(r, radius));
                var state = g.Save();
                g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, r, src, GraphicsUnit.Pixel);
                g.Restore(state);
            }
            else if (image != null)
            {
                var s = S(32);
                g.DrawImage(image, new Rectangle(r.Left + (r.Width - s) / 2, r.Top + (r.Height - s) / 2, s, s));
            }
            else
                Ui.DrawIcon(g, Directory.Exists(path) ? Ui.GlyphFolder : Ui.GlyphShelf, 16f, r, Ui.TextSecondary);
            Ui.StrokeRound(g, hover ? Ui.Accent : Ui.ControlStroke, r, radius);
            if (hover)
            {
                // Remove badge in the corner.
                var b = new Rectangle(r.Right - S(18), r.Top + S(2), S(16), S(16));
                using (var fill = new SolidBrush(Ui.Solid(Ui.Flyout))) g.FillEllipse(fill, b);
                Ui.DrawIcon(g, Ui.GlyphClose, 6.5f, b, Ui.Text);
            }
        }
    }

    /// <summary>The shelf: a drop box in the top right. Files dropped here wait until you pick a device.</summary>
    void PaintShelf(Graphics g, Rectangle r)
    {
        var target = _dragOver == ShelfRow;
        var inviting = _dragging && _dragHasFiles;
        var radius = Ui.Radius(r, S(6));
        if (target) Ui.FillRound(g, Ui.Accent, r, radius);
        else if (_hover == ShelfRow || inviting) Ui.FillRound(g, Ui.SubtleHover, r, radius);
        if (!target)
        {
            using var pen = new Pen(inviting ? Ui.Accent : Ui.StrongStroke, Math.Max(1, S(1)))
                { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var path = Ui.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius);
            var smooth = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawPath(pen, path);
            g.SmoothingMode = smooth;
        }
        var fore = target ? Ui.TextOnAccent : inviting ? Ui.AccentText : Ui.TextSecondary;
        Ui.DrawIcon(g, Ui.GlyphShelf, 13f, new Rectangle(r.Left, r.Top + S(5), r.Width, S(20)), fore);
        var onShelf = _pending?.Count ?? 0;
        var label = target ? "Keep" : onShelf > 0 ? $"{onShelf} kept" : "Shelf";
        Ui.DrawText(g, label, Ui.Caption, new Rectangle(r.Left, r.Top + S(26), r.Width, S(16)), fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Files fed to the mouth: they go on the shelf.</summary>
    void Shelve(string[] files)
    {
        _pending = (_pending ?? []).Concat(files).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _pendingText = null;
        SummarizePaths(_pending.ToArray());
        Expand(false);
        Measure();
        Invalidate();
    }

    /// <summary>Clicking the shelf: choose files to put on it.</summary>
    void AddToShelf()
    {
        _modal = true;
        try
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Put on the shelf" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _pending = (_pending ?? []).Concat(dlg.FileNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingText = null;
            SummarizePaths(_pending.ToArray());
            Measure();
            Invalidate();
        }
        finally { _modal = false; }
    }

    void PaintQrRow(Graphics g, Rectangle r)
    {
        var target = _dragOver == QrRow;
        if (target || _hover == QrRow) Ui.FillRound(g, Ui.SubtleHover, r, S(4));
        if (target) Ui.FillRound(g, Ui.Accent, new RectangleF(r.Left, r.Top + (r.Height - S(16)) / 2f, S(3), S(16)), S(2) / 2f + 0.5f);
        var icon = new Rectangle(r.Left + S(12), r.Top, S(24), r.Height);
        Ui.DrawIcon(g, Ui.GlyphQr, 14f, icon, Ui.Text);
        var x = icon.Right + S(12);
        Ui.DrawText(g, "Phone without the app", Ui.Body, new Rectangle(x, r.Top + S(9), r.Right - x - S(12), S(20)), Ui.Text);
        Ui.DrawText(g, target ? "Release to show a QR code" : "Send or receive with a QR code", Ui.Caption,
            new Rectangle(x, r.Top + S(29), r.Right - x - S(12), S(18)), target ? Ui.AccentText : Ui.TextSecondary);
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
        else if (st is { Done: false } && i == _hover) sub = "Select to cancel";
        else if (st != null) { sub = st.Status; if (st.Error) subColor = Ui.Error; else if (st.Ok) subColor = Ui.Success; }
        else sub = Settings.Current.Nicknames.ContainsKey(d.Fingerprint) ? d.Alias : Ui.DeviceLabel(d);

        if (Settings.Current.Favorites.Contains(d.Fingerprint))
        {
            var pin = new Rectangle(r.Right - S(32), r.Top, S(20), r.Height);
            Ui.DrawIcon(g, Ui.GlyphPin, 10f, pin, Ui.TextTertiary);
            tw -= S(24);
        }
        Ui.DrawText(g, NameOf(d), Ui.Body, new Rectangle(x, r.Top + S(9), tw, S(20)), Ui.Text);
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

    Point _pressAt;
    int _pressRecent = -1;
    bool _draggedOut;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressAt = e.Location;
        _pressRecent = e.Button == MouseButtons.Left ? RecentAt(e.Location) : -1;
        _draggedOut = false;
    }

    /// <summary>Files in Recent can be dragged into email, chat or a folder.</summary>
    bool TryDragOut(MouseEventArgs e)
    {
        if (_pressRecent < 0 || e.Button != MouseButtons.Left) return false;
        var drag = SystemInformation.DragSize;
        if (Math.Abs(e.X - _pressAt.X) < drag.Width && Math.Abs(e.Y - _pressAt.Y) < drag.Height) return false;
        var h = _recent[_pressRecent];
        _pressRecent = -1;
        var paths = h.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (paths.Length == 0) return false;
        _draggedOut = true;
        _modal = true; // keep the panel open while dragging
        try { DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy); }
        finally { _modal = false; }
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_expanded) return;
        if (TryDragOut(e)) return;
        var hover = RowAt(e.Location);
        var recent = RecentAt(e.Location);
        var tile = TileAt(e.Location);
        var icon = _gear.Contains(e.Location) ? "gear" : _refresh.Contains(e.Location) ? "refresh"
                 : _again.Contains(e.Location) ? "again" : _discard.Contains(e.Location) ? "discard"
                 : _clear.Contains(e.Location) ? "clear" : _quiet.Contains(e.Location) ? "quiet" : null;
        if (hover != _hover || icon != _hoverIcon || recent != _hoverRecent || tile != _hoverTile)
        {
            _hover = hover;
            _hoverTile = tile;
            _hoverIcon = icon;
            _hoverRecent = recent;
            Cursor = hover != -1 || recent >= 0 || icon != null || (tile >= 0 && tile < TileFiles) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _hoverRecent = _hoverTile = -1;
        _hoverIcon = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!_expanded) return;
        if (_draggedOut) { _draggedOut = false; return; }
        if (RecentAt(e.Location) is var ri and >= 0) { OnRecentClick(_recent[ri], e.Button == MouseButtons.Right); return; }
        if (e.Button == MouseButtons.Right)
        {
            if (RowAt(e.Location) is var di and >= 0) ShowDeviceMenu(_devices[di], e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_gear.Contains(e.Location)) { OpenSettings?.Invoke(); return; }
        if (_quiet.Contains(e.Location)) { ToggleQuiet?.Invoke(); Invalidate(); return; }
        if (_discard.Contains(e.Location)) { Collapse(); return; }
        if (TileAt(e.Location) is var ti and >= 0) { if (ti < TileFiles) TakeOffShelf(ti); return; }
        if (_clear.Contains(e.Location)) { History.Clear(); return; }
        if (_refresh.Contains(e.Location) || _again.Contains(e.Location)) { Refresh_(); return; }
        var i = RowAt(e.Location);
        if (i == ShelfRow) AddToShelf();
        else if (i == Everyone) OnEveryoneClick();
        else if (i == QrRow) OnQrClick();
        else if (i >= 0) OnRowClick(_devices[i]);
    }

    // ---- nicknames and favourites ----

    void ShowDeviceMenu(Device d, Point at)
    {
        var s = Settings.Current;
        var menu = Ui.Menu();
        menu.Items.Add("Message…", null, (_, _) => BeginInvoke(() => OpenNotes?.Invoke(d)));
        menu.Items.Add("Ask for a file…", null, (_, _) => BeginInvoke(() => AskForFile?.Invoke(d)));
        menu.Items.Add("Send a link…", null, (_, _) => BeginInvoke(() => SendLink(d)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Rename…", null, (_, _) => BeginInvoke(() => Rename(d)));
        if (s.Nicknames.ContainsKey(d.Fingerprint))
            menu.Items.Add($"Use its own name ({d.Alias})", null, (_, _) => { s.Nicknames.Remove(d.Fingerprint); s.Save(); Reorder(); });
        var pinned = s.Favorites.Contains(d.Fingerprint);
        menu.Items.Add(pinned ? "Unpin" : "Pin to top", null, (_, _) =>
        {
            if (pinned) s.Favorites.Remove(d.Fingerprint); else s.Favorites.Add(d.Fingerprint);
            s.Save();
            Reorder();
        });
        _modal = true;
        menu.Closed += (_, _) => _modal = false;
        menu.Show(this, at);
    }

    void SendLink(Device d)
    {
        _modal = true;
        try
        {
            var copied = "";
            try { if (Clipboard.ContainsText() && IncomingCard.IsLink(Clipboard.GetText(), out var u)) copied = u.AbsoluteUri; } catch { }
            var link = PromptForm.Ask($"Send a link to {NameOf(d)}", "It opens in their browser when they accept", "Send", copied);
            if (link == null) return;
            if (!IncomingCard.IsLink(link, out var uri)) { _notify("That isn't a web address (it should start with https://)", true); return; }
            StartSend(d, [new SendItem("message.txt", null, uri.AbsoluteUri)], []);
        }
        finally { _modal = false; }
    }

    /// <summary>Taskbar "Send to …": pick files for that device.</summary>
    public void PickAndSend(string fingerprint)
    {
        if (Find(fingerprint) is not { } d) { _notify("That device isn't nearby right now", true); return; }
        _modal = true;
        try
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = $"Send to {NameOf(d)}" };
            if (dlg.ShowDialog() == DialogResult.OK) StartSend(d, Sender.FromPaths(dlg.FileNames), dlg.FileNames.ToList());
        }
        finally { _modal = false; }
    }

    void Rename(Device d)
    {
        _modal = true;
        try
        {
            var s = Settings.Current;
            var name = PromptForm.Ask($"Rename {d.Alias}", "Only you see this name", "Save",
                s.Nicknames.GetValueOrDefault(d.Fingerprint) ?? d.Alias);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || name == d.Alias) s.Nicknames.Remove(d.Fingerprint);
            else s.Nicknames[d.Fingerprint] = name;
            s.Save();
            Reorder();
        }
        finally { _modal = false; }
    }

    void Reorder()
    {
        _devices = Ordered();
        Measure();
        Invalidate();
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
        _dragging = true;
        _dragHasFiles = e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        Expand(false);
        Measure();
        Summarize(e);
        OnDragOver(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var i = Accepts(e) ? RowAt(PointToClient(new Point(e.X, e.Y))) : -1;
        if (i == ShelfRow && !_dragHasFiles) i = -1;   // only files go on the shelf
        e.Effect = i != -1 ? DragDropEffects.Copy : DragDropEffects.None;
        if (i != _dragOver) { _dragOver = i; Invalidate(); }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        EndDrag();
        _dragOver = -1;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        var i = _dragOver;
        _dragOver = -1;
        _dragTitle = _dragSub = null;
        if (i == ShelfRow && e.Data!.GetData(DataFormats.FileDrop) is string[] kept)
        {
            _pending = (_pending ?? []).Concat(kept).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingText = null;
            EndDrag();
            return;
        }
        EndDrag();
        Invalidate();
        if (i == QrRow)
        {
            var dropped = e.Data!.GetData(DataFormats.FileDrop) as string[];
            var droppedText = dropped == null ? e.Data.GetData(DataFormats.UnicodeText) as string : null;
            BeginInvoke(() => { ShareByQr?.Invoke(dropped != null ? Sender.FromPaths(dropped) : [], droppedText); Collapse(); });
            return;
        }
        var targets = i == Everyone ? _devices.ToList() : i >= 0 && i < _devices.Count ? [_devices[i]] : new List<Device>();
        if (targets.Count == 0) return;

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
        BeginInvoke(() => { foreach (var d in targets) StartSend(d, items, sources); });
    }

    /// <summary>Drag over: drop the shelf row, and show the shelf again if there is one.</summary>
    void EndDrag()
    {
        _dragging = false;
        if (_pending != null) SummarizePaths(_pending.ToArray());
        else if (_pendingText == null) _dragTitle = _dragSub = null;
        if (_expanded) Measure();
        Invalidate();
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
    }

    // ---- sending ----

    /// <summary>Hands over what's being held (files or copied text) and clears it.</summary>
    (List<SendItem> Items, List<string> Sources) TakePending()
    {
        var result = _pendingText != null
            ? ([new SendItem("message.txt", null, _pendingText)], [])
            : (Sender.FromPaths(_pending!), _pending!);
        _pending = null;
        _pendingText = null;
        _dragTitle = _dragSub = null;
        Measure();
        return result;
    }

    void OnQrClick()
    {
        if (Holding)
        {
            var text = _pendingText;
            var (items, _) = TakePending();
            ShareByQr?.Invoke(items.Where(i => i.Path != null).ToList(), text);
            Collapse();
            return;
        }
        var menu = Ui.Menu();
        menu.Items.Add("Send files to a phone…", null, (_, _) => BeginInvoke(() =>
        {
            _modal = true;
            try
            {
                using var dlg = new OpenFileDialog { Multiselect = true, Title = "Share with a QR code" };
                if (dlg.ShowDialog(this) == DialogResult.OK) { ShareByQr?.Invoke(Sender.FromPaths(dlg.FileNames), null); Collapse(); }
            }
            finally { _modal = false; }
        }));
        menu.Items.Add("Get files from a phone", null, (_, _) => BeginInvoke(() => { ReceiveByQr?.Invoke(); Collapse(); }));
        _modal = true;
        menu.Closed += (_, _) => _modal = false;
        menu.Show(this, new Point(_qr.Left + S(48), _qr.Bottom - S(8)));
    }

    void OnEveryoneClick()
    {
        var targets = _devices.ToList();
        if (Holding)
        {
            var (items, sources) = TakePending();
            foreach (var d in targets) StartSend(d, items, sources);
            return;
        }
        _modal = true;
        try
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = "Send to everyone nearby" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                foreach (var d in targets) StartSend(d, Sender.FromPaths(dlg.FileNames), dlg.FileNames.ToList());
        }
        finally { _modal = false; }
    }

    void OnRowClick(Device d)
    {
        if (Holding)
        {
            var (items, sources) = TakePending();
            StartSend(d, items, sources);
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
            using var dlg = new OpenFileDialog { Multiselect = true, Title = $"Send to {NameOf(d)}" };
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
        if (sources.Count > 0) _ateFrom = sources[0]; else _ateText = true;
        var st = new SendState { Cts = new CancellationTokenSource(), Status = "Connecting…" };
        _sending[d.Fingerprint] = st;
        Invalidate();

        var progress = new Progress<(string text, double? fraction)>(p =>
        {
            st.Status = p.fraction == null ? $"Waiting for {NameOf(d)} to accept"
                : p.text == "Reconnecting…" ? "Connection lost. Reconnecting…"
                : $"Sending {p.text}";
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
