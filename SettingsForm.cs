using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>Settings, laid out like the Windows 11 Settings app: sections of cards.</summary>
sealed class SettingsForm : Form
{
    static readonly (string Key, string Title, string Sub)[] Modes =
    [
        ("ask", "Ask every time", "A notification lets you accept or decline"),
        ("known", "Accept from devices I've accepted before", "Ask the first time, then accept automatically"),
        ("all", "Accept everything", "Anyone on this network can send you files"),
    ];

    readonly ScrollPanel _scroll = new();
    readonly TextField _name = new();
    readonly PillButton _browse = new("Browse");
    readonly List<RadioRow> _modes = [];
    readonly CornerPicker _corner = new();
    readonly ToggleSwitch _sound = new(), _autostart = new(), _update = new();
    readonly PillButton _forget = new("Forget");
    readonly PillButton _install = new("Install", primary: true);
    readonly PillButton _checkUpdates = new("Check for updates");
    readonly PillButton _save = new("Save", primary: true);
    readonly PillButton _cancel = new("Cancel");
    readonly Card _folderCard;
    readonly Func<Task<bool>> _installAction;
    string _folder;
    Rectangle _close;
    bool _closeHot;

    /// <param name="checkUpdates">Checks for (and installs) an update, reporting progress text.</param>
    public SettingsForm(bool autostart, Func<Task<bool>> install, Func<Action<string>, Task> checkUpdates)
    {
        var s = Settings.Current;
        _installAction = install;
        Text = "LocalSend Drop settings";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        BackColor = Ui.Base;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _name.Text = s.Alias ?? Environment.MachineName;
        _name.MaxLength = 40;
        _folder = s.SaveDir is { Length: > 0 } d ? d : Settings.DefaultSaveDir;
        foreach (var m in Modes)
        {
            var row = new RadioRow(m.Title, m.Sub) { Selected = m.Key == s.ReceiveMode };
            row.Click += (_, _) => { foreach (var r in _modes) r.Selected = r == row; };
            _modes.Add(row);
        }
        if (!_modes.Any(r => r.Selected)) _modes[0].Selected = true;
        _corner.Corner = s.Corner;
        _corner.Monitor = s.Monitor;
        _sound.Checked = s.Sound;
        _autostart.Checked = autostart;
        _update.Checked = s.AutoUpdate;
        _forget.Click += (_, _) => { ForgetTrusted = true; _forget.Enabled = false; _forget.Text = "Forgotten"; };
        _install.Click += async (_, _) =>
        {
            _install.Enabled = false;
            _install.Text = "Installing…";
            if (await _installAction()) { DialogResult = DialogResult.Cancel; return; }
            _install.Enabled = true;
            _install.Text = "Try again";
        };
        _save.Click += (_, _) => TrySave();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };

        // Sections and cards
        _scroll.Add(new SectionHeader("This PC"));
        _scroll.Add(new Card("Name", "How this PC appears to other devices", right: _name, rightWidth: 240));
        _scroll.Add(_folderCard = new Card("Save received files in", _folder, right: _browse, rightWidth: 96));
        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _folder, Description = "Save received files in" };
            if (dlg.ShowDialog(this) == DialogResult.OK) { _folder = dlg.SelectedPath; _folderCard.Description = _folder; }
        };

        _scroll.Add(new SectionHeader("Receiving"));
        _scroll.Add(new Card("When someone sends you files", null, below: [.. _modes]));
        var trusted = s.Trusted.Count;
        if (trusted > 0)
            _scroll.Add(new Card("Trusted devices",
                $"{trusted} device{(trusted == 1 ? "" : "s")} you've accepted files from", right: _forget, rightWidth: 96));

        _scroll.Add(new SectionHeader("Drop zone"));
        _scroll.Add(new Card(Screen.AllScreens.Length > 1 ? "Monitor and corner" : "Corner",
            "Select a corner to move the drop zone there", below: [_corner]));

        _scroll.Add(new SectionHeader("General"));
        _scroll.Add(new Card("Sound", "Play a sound when someone sends you something", right: _sound, rightWidth: 84));
        _scroll.Add(new Card("Start with Windows", "Keep the drop zone ready after you sign in", right: _autostart, rightWidth: 84));
        _scroll.Add(new Card("Updates", "Install new versions automatically", right: _update, rightWidth: 84));
        Card versionCard = null!;
        _scroll.Add(versionCard = new Card($"LocalSend Drop {Updater.CurrentText}", "Check whether a newer version is available",
            right: _checkUpdates, rightWidth: 160));
        _checkUpdates.Click += async (_, _) =>
        {
            _checkUpdates.Enabled = false;
            await checkUpdates(text =>
            {
                // Settings may have been closed while it was checking.
                if (IsHandleCreated && !IsDisposed) BeginInvoke(() => versionCard.Description = text);
            });
            if (!IsDisposed) _checkUpdates.Enabled = true;
        };
        if (Packaging.Supported && !Packaging.IsPackaged)
            _scroll.Add(new Card("Install LocalSend Drop", "Adds it to the Start menu and the right-click menu", right: _install, rightWidth: 112));

        Controls.AddRange([_scroll, _save, _cancel]);
        Ui.OnChange(this, () => { BackColor = Ui.Base; Ui.Chrome(Handle); Ui.ThemeScrollbars(_scroll); Invalidate(true); });
    }

    int S(int v) => LogicalToDeviceUnits(v);
    const int W = 680, HeaderH = 84, FooterH = 80;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = S(W), pad = S(32);
        _scroll.SetBounds(pad - S(4), S(HeaderH), w - 2 * pad + S(8), 10);
        var content = _scroll.LayoutCards();
        // A dialog-sized window, like Windows' own; the rest scrolls.
        var h = Math.Min(S(HeaderH) + content + S(FooterH), Math.Min(S(820), wa.Height - S(48)));
        ClientSize = new Size(w, h);
        _scroll.Height = h - S(HeaderH) - S(FooterH);
        _save.SetBounds(w - pad - S(120), h - S(FooterH) + S(24), S(120), S(32));
        _cancel.SetBounds(w - pad - S(248), h - S(FooterH) + S(24), S(120), S(32));
        _close = new Rectangle(w - S(52), S(16), S(36), S(36));
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        Ui.ThemeScrollbars(_scroll);
    }

    void TrySave()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { _name.Error = true; _name.Focus(); return; }
        try { Directory.CreateDirectory(_folder); }
        catch { _folderCard.Description = "That folder can't be used. Choose another."; _folderCard.Error = true; return; }
        DialogResult = DialogResult.OK;
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
        int pad = S(32), w = ClientSize.Width, h = ClientSize.Height;
        Ui.DrawText(g, "Settings", Ui.Title, new Rectangle(pad, S(24), S(300), S(40)), Ui.Text);
        if (_closeHot) Ui.FillRound(g, Ui.SubtleHover, _close, S(4));
        Ui.DrawIcon(g, Ui.GlyphClose, 10f, _close, Ui.Text);

        var fy = h - S(FooterH);
        using (var line = new Pen(Ui.Divider)) g.DrawLine(line, 0, fy, w, fy);
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_close.Contains(e.Location)) { DialogResult = DialogResult.Cancel; return; }
        if (e.Button == MouseButtons.Left && e.Y < S(HeaderH))
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0); // drag the window by its header
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = _close.Contains(e.Location);
        if (hot != _closeHot) { _closeHot = hot; Invalidate(_close); }
    }

    public string DeviceName => _name.Text.Trim();
    public string SaveDir => _folder.Trim();
    public string ReceiveMode => Modes[_modes.FindIndex(r => r.Selected)].Key;
    public string Corner => _corner.Corner;
    public string? Monitor => _corner.Monitor;
    public bool Sound => _sound.Checked;
    public bool Autostart => _autostart.Checked;
    public bool AutoUpdate => _update.Checked;
    public bool ForgetTrusted { get; private set; }
}

/// <summary>Vertical stack of section headers and cards that scrolls.</summary>
sealed class ScrollPanel : Panel
{
    readonly List<Control> _items = [];

    public ScrollPanel()
    {
        AutoScroll = true;
        DoubleBuffered = true;
        BackColor = Ui.Base;
        Ui.OnChange(this, () => BackColor = Ui.Base);
    }

    public void Add(Control c) { _items.Add(c); Controls.Add(c); }

    /// <summary>Stacks the items (4 px between cards, like Windows Settings); returns the content height.</summary>
    public int LayoutCards()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        var w = ClientSize.Width - S(8) - SystemInformation.VerticalScrollBarWidth;
        var y = 0;
        foreach (var c in _items)
        {
            var h = c is Card card ? card.Measure(w) : S(40);
            if (c is SectionHeader && y > 0) y += S(16);
            c.SetBounds(S(4), y, w, h);
            y += h + (c is Card ? S(4) : 0);
        }
        return y + S(8);
    }
}

sealed class SectionHeader : Drawn
{
    public SectionHeader(string text) => Text = text;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Ui.Base);
        Ui.DrawText(e.Graphics, Text, Ui.BodyStrong, new Rectangle(S(2), S(12), Width, S(24)), Ui.Text);
    }
}

/// <summary>A Windows Settings card: title and description, a control on the right and/or content below.</summary>
sealed class Card : Drawn
{
    readonly string _title;
    string? _description;
    bool _error;
    readonly Control? _right;
    readonly int _rightWidth;
    readonly Control[] _below;

    public Card(string title, string? description, Control? right = null, int rightWidth = 0, Control[]? below = null)
    {
        _title = title;
        _description = description;
        _right = right;
        _rightWidth = rightWidth;
        _below = below ?? [];
        if (right != null) Controls.Add(right);
        foreach (var c in _below) Controls.Add(c);
    }

    public string? Description { get => _description; set { _description = value; _error = false; Invalidate(); } }
    public bool Error { get => _error; set { _error = value; Invalidate(); } }
    public override Color BackFill => Ui.Card;

    int HeaderHeight => S(_description == null ? 48 : 68);

    /// <summary>Lays out children for the given width and returns the card's height.</summary>
    public int Measure(int width)
    {
        var y = HeaderHeight;
        if (_right != null)
            _right.SetBounds(width - S(16) - S(_rightWidth), (HeaderHeight - S(32)) / 2, S(_rightWidth), S(32));
        foreach (var c in _below)
        {
            var h = c is CornerPicker ? S(150) : S(52);
            c.SetBounds(S(16), y, width - S(32), h);
            y += h;
        }
        return y + (_below.Length > 0 ? S(12) : 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Base);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Ui.FillRound(g, Ui.Card, r, S(4));
        Ui.StrokeRound(g, Ui.CardStroke, r, S(4));
        var textW = Width - S(32) - (_right != null ? S(_rightWidth) + S(16) : 0);
        if (_description == null)
            Ui.DrawText(g, _title, Ui.Body, new Rectangle(S(16), 0, textW, HeaderHeight), Ui.Text);
        else
        {
            Ui.DrawText(g, _title, Ui.Body, new Rectangle(S(16), S(14), textW, S(20)), Ui.Text);
            Ui.DrawText(g, _description, Ui.Caption, new Rectangle(S(16), S(35), textW, S(18)), _error ? Ui.Error : Ui.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);
        }
    }
}

/// <summary>Windows 11 text box: control fill, hairline border, accent underline when focused.</summary>
sealed class TextField : Drawn
{
    readonly TextBox _box = new() { BorderStyle = BorderStyle.None };
    bool _error;

    public TextField()
    {
        _box.Font = Ui.Body;
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _box.TextChanged += (_, _) => { if (_error) Error = false; };
        Controls.Add(_box);
        Cursor = Cursors.IBeam;
        Recolor();
        Ui.OnChange(this, Recolor);
    }

    void Recolor()
    {
        var bg = _box.Focused ? (Ui.Dark ? Ui.Flyout : Color.White) : Ui.ControlFill;
        if (_box.BackColor != bg) _box.BackColor = bg;
        if (_box.ForeColor != Ui.Text) _box.ForeColor = Ui.Text;
    }

    public override string Text { get => _box.Text; set => _box.Text = value; }
    public int MaxLength { get => _box.MaxLength; set => _box.MaxLength = value; }
    public bool Error { get => _error; set { _error = value; Invalidate(); } }
    public new void Focus() => _box.Focus();

    protected override void OnClick(EventArgs e) { base.OnClick(e); _box.Focus(); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        _box.SetBounds(S(11), (Height - _box.PreferredHeight) / 2, Width - S(22), _box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Recolor();
        var g = e.Graphics;
        g.Clear(Surface);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var radius = 4 * Scale_;
        Ui.FillRound(g, _box.BackColor, r, radius);
        Ui.StrokeRound(g, _error ? Ui.Error : Ui.ControlStroke, r, radius);
        // Bottom edge: hairline at rest, 2 px accent while typing.
        var focused = _box.Focused;
        var h = focused || _error ? 2 * Scale_ : Scale_;
        using var b = new SolidBrush(_error ? Ui.Error : focused ? Ui.Accent : Ui.StrongStroke);
        using var clip = Ui.Round(r, radius);
        g.SetClip(clip);
        g.FillRectangle(b, 0, Height - h, Width, h);
        g.ResetClip();
    }
}

/// <summary>Windows 11 radio button with a title and a description.</summary>
sealed class RadioRow : Drawn
{
    readonly string _title, _sub;
    bool _selected;

    public RadioRow(string title, string sub) { _title = title; _sub = sub; TabStop = true; }

    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var size = S(20);
        var dot = new Rectangle(0, S(6), size, size);
        if (_selected)
        {
            using (var b = new SolidBrush(Hot ? Ui.AccentHover : Ui.Accent)) g.FillEllipse(b, dot);
            var inner = Rectangle.Inflate(dot, -S(Hot ? 4 : 5), -S(Hot ? 4 : 5));
            using var ib = new SolidBrush(Ui.TextOnAccent);
            g.FillEllipse(ib, inner);
        }
        else
        {
            using (var b = new SolidBrush(Hot ? Ui.ControlHover : Ui.ControlFill)) g.FillEllipse(b, dot);
            using var pen = new Pen(Ui.StrongStroke, Scale_);
            g.DrawEllipse(pen, dot);
        }
        var x = dot.Right + S(12);
        Ui.DrawText(g, _title, Ui.Body, new Rectangle(x, S(4), Width - x, S(22)), Ui.Text);
        Ui.DrawText(g, _sub, Ui.Caption, new Rectangle(x, S(25), Width - x, S(18)), Ui.TextSecondary);
        FocusRect(g, new Rectangle(0, 0, Width, Height - S(4)), 4 * Scale_);
    }
}

/// <summary>Windows 11 toggle switch with its On/Off label.</summary>
sealed class ToggleSwitch : Drawn
{
    bool _checked;

    public ToggleSwitch() => TabStop = true;

    public bool Checked { get => _checked; set { _checked = value; Invalidate(); } }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) Checked = !Checked;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        var track = new RectangleF(Width - S(40) - 0.5f, (Height - S(20)) / 2f, S(40), S(20));
        Ui.DrawText(g, _checked ? "On" : "Off", Ui.Body, new Rectangle(0, 0, (int)track.Left - S(12), Height), Ui.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        var knob = S(Hot ? 14 : 12);
        if (_checked)
        {
            Ui.FillRound(g, Hot ? Ui.AccentHover : Ui.Accent, track, track.Height / 2);
            using var b = new SolidBrush(Ui.TextOnAccent);
            g.FillEllipse(b, track.Right - S(4) - knob, track.Top + (track.Height - knob) / 2, knob, knob);
        }
        else
        {
            Ui.FillRound(g, Hot ? Ui.ControlHover : Ui.ControlFill, track, track.Height / 2);
            Ui.StrokeRound(g, Ui.StrongStroke, track, track.Height / 2, Scale_);
            using var b = new SolidBrush(Ui.TextSecondary);
            g.FillEllipse(b, track.Left + S(4), track.Top + (track.Height - knob) / 2, knob, knob);
        }
        FocusRect(g, Rectangle.Round(RectangleF.Inflate(track, S(3), S(3))), track.Height / 2 + S(3));
    }
}

/// <summary>
/// Your monitors, arranged as Windows arranges them (as in Display settings).
/// Select a corner of any monitor to put the drop zone there.
/// </summary>
sealed class CornerPicker : Drawn
{
    static readonly (string Key, string Name)[] Corners =
        [("TopLeft", "top left"), ("TopRight", "top right"), ("BottomLeft", "bottom left"), ("BottomRight", "bottom right")];

    string _corner = "TopLeft";
    string? _monitor;
    (string Monitor, string Corner)? _hover;
    readonly List<(Screen Screen, Rectangle Box, string Name)> _boxes = [];

    public string Corner { get => _corner; set { _corner = value; Invalidate(); } }
    /// <summary>Screen.DeviceName, or null for the main monitor.</summary>
    public string? Monitor { get => _monitor; set { _monitor = value; Invalidate(); } }

    public CornerPicker() => Cursor = Cursors.Hand;

    string Selected => _monitor ?? Screen.PrimaryScreen!.DeviceName;

    void LayoutBoxes()
    {
        _boxes.Clear();
        var screens = Screen.AllScreens;
        var all = screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        var area = new Rectangle(0, S(4), Math.Min(Width, S(320)), S(104));
        var scale = Math.Min((double)area.Width / all.Width, (double)area.Height / all.Height);
        var gap = S(4);
        for (var i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            var box = new Rectangle(area.Left + (int)((b.Left - all.Left) * scale) + gap, area.Top + (int)((b.Top - all.Top) * scale) + gap,
                (int)(b.Width * scale) - 2 * gap, (int)(b.Height * scale) - 2 * gap);
            _boxes.Add((screens[i], box, $"Monitor {i + 1}"));
        }
    }

    (string, string)? HitTest(Point p)
    {
        foreach (var (screen, box, _) in _boxes)
        {
            if (!box.Contains(p)) continue;
            var right = p.X > box.Left + box.Width / 2;
            var bottom = p.Y > box.Top + box.Height / 2;
            return (screen.DeviceName, (bottom ? "Bottom" : "Top") + (right ? "Right" : "Left"));
        }
        return null;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBoxes(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = null; base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (HitTest(e.Location) is not { } h) return;
        _monitor = h.Item1 == Screen.PrimaryScreen!.DeviceName ? null : h.Item1;
        _corner = h.Item2;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        if (_boxes.Count == 0) LayoutBoxes();

        foreach (var (screen, box, _) in _boxes)
        {
            var chosen = screen.DeviceName == Selected;
            Ui.FillRound(g, Ui.ControlFill, box, S(4));
            Ui.StrokeRound(g, chosen ? Ui.Accent : Ui.StrongStroke, box, S(4), chosen ? 2 * Scale_ : Scale_);
            if (_boxes.Count > 1)
                Ui.DrawText(g, (_boxes.FindIndex(b => b.Screen == screen) + 1).ToString(), Ui.Subtitle, box,
                    Ui.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (chosen) DrawL(g, box, _corner, Ui.Accent);
            if (_hover is { } h && h.Item1 == screen.DeviceName && !(chosen && h.Item2 == _corner))
                DrawL(g, box, h.Item2, Ui.TextTertiary);
        }

        var showing = _hover ?? (Selected, _corner);
        var monitorName = _boxes.FirstOrDefault(b => b.Screen.DeviceName == showing.Item1).Name ?? "Monitor";
        var cornerName = Corners.First(c => c.Key == showing.Item2).Name;
        var label = _boxes.Count > 1 ? $"{monitorName}, {cornerName}" : char.ToUpper(cornerName[0]) + cornerName[1..];
        Ui.DrawText(g, label, Ui.Body, new Rectangle(0, S(120), Width, S(22)),
            _hover != null && _hover != (Selected, _corner) ? Ui.TextSecondary : Ui.Text);
    }

    void DrawL(Graphics g, Rectangle s, string key, Color c)
    {
        int len = Math.Min(S(24), Math.Min(s.Width, s.Height) / 3), t = S(3), inset = S(6);
        var right = key.EndsWith("Right");
        var bottom = key.StartsWith("Bottom");
        var x = right ? s.Right - inset - len : s.Left + inset;
        var y = bottom ? s.Bottom - inset - len : s.Top + inset;
        Ui.FillRound(g, c, new Rectangle(x, bottom ? y + len - t : y, len, t), t / 2f);
        Ui.FillRound(g, c, new Rectangle(right ? x + len - t : x, y, t, len), t / 2f);
    }
}
