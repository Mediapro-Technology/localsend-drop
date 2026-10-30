using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>Name, save folder, receive behaviour, corner, sound, autostart.</summary>
sealed class SettingsForm : Form
{
    static readonly (string Key, string Title, string Sub)[] Modes =
    [
        ("ask", "Ask me every time", "A card pops up so you can accept or decline"),
        ("known", "Trust devices I've accepted before", "Ask the first time, then accept from them automatically"),
        ("all", "Accept everything", "Anyone on this network can drop files on you"),
    ];

    readonly TextField _name = new();
    readonly PathField _folder = new();
    readonly List<ChoiceRow> _modes = [];
    readonly CornerPicker _corner = new();
    readonly ToggleRow _sound = new("Play a sound when something arrives");
    readonly ToggleRow _autostart = new("Start with Windows");
    readonly ToggleRow _update = new("Install updates automatically");
    readonly LinkText _forget = new("");
    readonly PillButton _save = new("Save", primary: true);
    readonly PillButton _cancel = new("Cancel");
    readonly List<(string Text, int X, int Y)> _labels = [];
    Rectangle _close;
    bool _closeHot;

    public SettingsForm(bool autostart)
    {
        var s = Settings.Current;
        Text = "LocalSend Drop settings";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Ink;
        TopMost = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _name.Text = s.Alias ?? Environment.MachineName;
        _name.MaxLength = 40;
        _folder.Path = s.SaveDir is { Length: > 0 } d ? d : Settings.DefaultSaveDir;
        _folder.ChangeClicked += () =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _folder.Path, Description = "Save received files in" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Path = dlg.SelectedPath;
        };
        foreach (var m in Modes)
        {
            var row = new ChoiceRow(m.Title, m.Sub) { Selected = m.Key == s.ReceiveMode };
            row.Click += (_, _) => { foreach (var r in _modes) r.Selected = r == row; };
            _modes.Add(row);
        }
        if (!_modes.Any(r => r.Selected)) _modes[0].Selected = true;
        _corner.Corner = s.Corner;
        _corner.Monitor = s.Monitor;
        _sound.Checked = s.Sound;
        _autostart.Checked = autostart;
        _update.Checked = s.AutoUpdate;
        var trusted = s.Trusted.Count;
        _forget.Text = $"Forget {trusted} trusted device{(trusted == 1 ? "" : "s")}";
        _forget.Visible = trusted > 0;
        _forget.Click += (_, _) => { ForgetTrusted = true; _forget.Text = "They'll be forgotten when you save"; _forget.Width = _forget.Measure().Width; };

        _save.Click += (_, _) => TrySave();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            if (e.KeyCode == Keys.Enter && !_modes.Any(m => m.Focused)) TrySave();
        };

        Controls.AddRange([_name, _folder, .. _modes, _corner, _sound, _autostart, _update, _forget, _save, _cancel]);
        DoLayout();
    }

    int S(int v) => LogicalToDeviceUnits(v);

    /// <summary>
    /// Two columns so it fits on a laptop screen: identity and switches on the
    /// left, receive behaviour and corner on the right.
    /// </summary>
    void DoLayout()
    {
        int pad = S(28), gap = S(32), w = S(700);
        int colW = (w - 2 * pad - gap) / 2;
        int lx = pad, rx = pad + colW + gap;
        _labels.Clear();

        var y = S(80);
        void Label(int x, string text) { _labels.Add((text, x, y)); y += S(24); }

        Label(lx, "Your name on the network");
        _name.SetBounds(lx, y, colW, S(40)); y += S(40) + S(20);
        Label(lx, "Save received files in");
        _folder.SetBounds(lx, y, colW, S(40)); y += S(40) + S(24);
        _sound.SetBounds(lx, y, colW, S(36)); y += S(40);
        _autostart.SetBounds(lx, y, colW, S(36)); y += S(40);
        _update.SetBounds(lx, y, colW, S(36)); y += S(40);
        if (_forget.Visible) { y += S(4); _forget.SetBounds(lx, y, _forget.Measure().Width, S(24)); y += S(28); }
        var leftBottom = y;

        y = S(80);
        Label(rx, "When someone sends you files");
        foreach (var r in _modes) { r.SetBounds(rx, y, colW, S(56)); y += S(62); }
        y += S(14);
        Label(rx, Screen.AllScreens.Length > 1 ? "Drop corner and monitor" : "Drop corner");
        _corner.SetBounds(rx, y, colW, S(144)); y += S(144);

        y = Math.Max(y, leftBottom) + S(24);
        _save.SetBounds(w - pad - S(120), y, S(120), S(40));
        _cancel.SetBounds(w - pad - S(250), y, S(120), S(40));
        y += S(40) + pad;

        ClientSize = new Size(w, y);
        _close = new Rectangle(w - S(52), S(18), S(34), S(34));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        DoLayout(); // again, now that the real DPI is known
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + Math.Max(0, (wa.Height - Height) / 2));
    }

    void TrySave()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { _name.Error = true; _name.Focus(); return; }
        try { Directory.CreateDirectory(_folder.Path); }
        catch { _folder.Error = true; return; }
        DialogResult = DialogResult.OK;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.SetCorners(Handle, true);
        Ui.SetBorderColor(Handle, Ui.Line);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Ink);
        int pad = S(28);
        Ui.DrawText(g, "Settings", Ui.Display(18f), new Rectangle(pad, S(22), S(300), S(34)), Ui.Text);
        foreach (var (text, x, y) in _labels)
            Ui.DrawText(g, text, Ui.Body(9f), new Rectangle(x, y, S(320), S(20)), Ui.Muted);
        Ui.DrawText(g, $"Version {Updater.CurrentText}", Ui.Body(8.5f),
            new Rectangle(pad, _save.Top, S(200), _save.Height), Ui.Muted);
        if (_closeHot) Ui.FillRound(g, Ui.InkHover, _close, _close.Height / 2f);
        Ui.DrawText(g, "", Ui.Glyph(10f), _close, _closeHot ? Ui.Text : Ui.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_close.Contains(e.Location)) { DialogResult = DialogResult.Cancel; return; }
        if (e.Button == MouseButtons.Left && e.Y < S(70))
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
    public string SaveDir => _folder.Path.Trim();
    public string ReceiveMode => Modes[_modes.FindIndex(r => r.Selected)].Key;
    public string Corner => _corner.Corner;
    public string? Monitor => _corner.Monitor;
    public bool Sound => _sound.Checked;
    public bool Autostart => _autostart.Checked;
    public bool AutoUpdate => _update.Checked;
    public bool ForgetTrusted { get; private set; }
}

/// <summary>Rounded text input; yellow outline while focused.</summary>
sealed class TextField : Drawn
{
    readonly TextBox _box = new() { BorderStyle = BorderStyle.None, BackColor = Ui.Ink2, ForeColor = Ui.Text };
    bool _error;

    public TextField()
    {
        BackColor = Ui.Ink2;
        _box.Font = Ui.Body(10.5f);
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _box.TextChanged += (_, _) => { if (_error) Error = false; };
        Controls.Add(_box);
        Cursor = Cursors.IBeam;
    }

    public override string Text { get => _box.Text; set => _box.Text = value; }
    public int MaxLength { get => _box.MaxLength; set => _box.MaxLength = value; }
    public bool Error { get => _error; set { _error = value; Invalidate(); } }
    public new void Focus() => _box.Focus();

    protected override void OnClick(EventArgs e) { base.OnClick(e); _box.Focus(); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        _box.SetBounds(S(14), (Height - _box.PreferredHeight) / 2, Width - S(28), _box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Ink);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Ui.FillRound(g, Ui.Ink2, r, S(8));
        var edge = _error ? Ui.Error : _box.Focused ? Ui.Postal : Hot ? Ui.Line : Color.Empty;
        if (edge != Color.Empty) Ui.StrokeRound(g, edge, r, S(8), _box.Focused || _error ? 1.5f : 1f);
    }
}

/// <summary>Shows a folder with a "Change" action.</summary>
sealed class PathField : Drawn
{
    string _path = "";
    bool _error;
    public event Action? ChangeClicked;

    public PathField() => Cursor = Cursors.Hand;

    public string Path { get => _path; set { _path = value; _error = false; Invalidate(); } }
    public bool Error { get => _error; set { _error = value; Invalidate(); } }

    protected override void OnClick(EventArgs e) { base.OnClick(e); ChangeClicked?.Invoke(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Ink);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Ui.FillRound(g, Hot ? Ui.InkHover : Ui.Ink2, r, S(8));
        if (_error) Ui.StrokeRound(g, Ui.Error, r, S(8), 1.5f);
        var change = TextRenderer.MeasureText("Change", Ui.BodyStrong());
        var linkR = new Rectangle(Width - S(14) - change.Width, 0, change.Width, Height);
        Ui.DrawText(g, _error ? "That folder can't be used" : _path, Ui.Body(10f),
            new Rectangle(S(14), 0, linkR.Left - S(26), Height), _error ? Ui.Error : Ui.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);
        Ui.DrawText(g, "Change", Ui.BodyStrong(), linkR, Ui.Postal);
    }
}

/// <summary>One option of a single-choice group: radio dot, title, explanation.</summary>
sealed class ChoiceRow : Drawn
{
    readonly string _title, _sub;
    bool _selected;

    public ChoiceRow(string title, string sub) { _title = title; _sub = sub; Cursor = Cursors.Hand; TabStop = true; }

    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Ink);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Ui.FillRound(g, Hot ? Ui.InkHover : Ui.Ink2, r, S(10));
        if (_selected) Ui.StrokeRound(g, Ui.Postal, r, S(10), 1.5f);

        var dot = new Rectangle(S(16), (Height - S(18)) / 2, S(18), S(18));
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(_selected ? Ui.Postal : Ui.Muted, S(2))) g.DrawEllipse(pen, dot);
        if (_selected)
        {
            var inner = Rectangle.Inflate(dot, -S(5), -S(5));
            using var b = new SolidBrush(Ui.Postal);
            g.FillEllipse(b, inner);
        }
        var x = dot.Right + S(14);
        Ui.DrawText(g, _title, Ui.BodyStrong(10f), new Rectangle(x, S(8), Width - x - S(12), S(22)), Ui.Text);
        Ui.DrawText(g, _sub, Ui.Body(9f), new Rectangle(x, S(29), Width - x - S(12), S(18)), Ui.Muted);
    }
}

/// <summary>Label on the left, switch on the right.</summary>
sealed class ToggleRow : Drawn
{
    bool _checked;

    public ToggleRow(string text) { Text = text; Cursor = Cursors.Hand; TabStop = true; }

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
        g.Clear(Parent?.BackColor ?? Ui.Ink);
        var track = new Rectangle(Width - S(44), (Height - S(24)) / 2, S(44), S(24));
        Ui.DrawText(g, Text, Ui.Body(10f), new Rectangle(0, 0, track.Left - S(12), Height), Hot ? Ui.Text : Color.FromArgb(0xD5, 0xDB, 0xE8));
        Ui.FillRound(g, _checked ? Ui.Postal : Ui.Line, track, track.Height / 2f);
        var knob = S(18);
        var kx = _checked ? track.Right - knob - S(3) : track.Left + S(3);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_checked ? Ui.Ink : Ui.Muted);
        g.FillEllipse(b, kx, track.Top + S(3), knob, knob);
    }
}

/// <summary>
/// Your monitors, laid out as Windows arranges them. Click a corner of any
/// monitor to put the drop zone there; the choice carries the same yellow L
/// the real one has.
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
        var area = new Rectangle(S(2), S(2), Width - S(4), S(92));
        var scale = Math.Min((double)area.Width / all.Width, (double)area.Height / all.Height);
        var ox = area.Left + (int)((area.Width - all.Width * scale) / 2);
        var gap = S(3);
        for (var i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            var box = new Rectangle(ox + (int)((b.Left - all.Left) * scale) + gap, area.Top + (int)((b.Top - all.Top) * scale) + gap,
                (int)(b.Width * scale) - 2 * gap, (int)(b.Height * scale) - 2 * gap);
            _boxes.Add((screens[i], box, $"Monitor {i + 1}{(screens[i].Primary ? " (main)" : "")}"));
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
        g.Clear(Parent?.BackColor ?? Ui.Ink);
        if (_boxes.Count == 0) LayoutBoxes();

        foreach (var (screen, box, _) in _boxes)
        {
            var chosen = screen.DeviceName == Selected;
            Ui.FillRound(g, chosen ? Ui.Ink2 : Ui.Ink, box, S(6));
            Ui.StrokeRound(g, chosen ? Ui.Muted : Ui.Line, box, S(6), S(2));
            if (_boxes.Count > 1)
                Ui.DrawText(g, (_boxes.FindIndex(b => b.Screen == screen) + 1).ToString(), Ui.Display(11f), box,
                    chosen ? Ui.Text : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (chosen) DrawL(g, box, _corner, Ui.Postal);
            if (_hover is { } h && h.Item1 == screen.DeviceName && !(chosen && h.Item2 == _corner))
                DrawL(g, box, h.Item2, Ui.Muted);
        }

        var showing = _hover ?? (Selected, _corner);
        var monitorName = _boxes.FirstOrDefault(b => b.Screen.DeviceName == showing.Item1).Name ?? "Monitor";
        var cornerName = Corners.First(c => c.Key == showing.Item2).Name;
        var label = _boxes.Count > 1 ? $"{monitorName}, {cornerName}" : char.ToUpper(cornerName[0]) + cornerName[1..];
        var isHover = _hover != null && _hover != (Selected, _corner);
        Ui.DrawText(g, label, Ui.Display(11.5f), new Rectangle(0, S(102), Width, S(22)), isHover ? Ui.Muted : Ui.Text);
        Ui.DrawText(g, isHover ? "Click to move it here" : "Drag files into this corner", Ui.Body(9f),
            new Rectangle(0, S(124), Width, S(18)), Ui.Muted);
    }

    void DrawL(Graphics g, Rectangle s, string key, Color c)
    {
        int len = Math.Min(S(26), Math.Min(s.Width, s.Height) / 3), t = S(4), inset = S(5);
        var right = key.EndsWith("Right");
        var bottom = key.StartsWith("Bottom");
        var x = right ? s.Right - inset - len : s.Left + inset;
        var y = bottom ? s.Bottom - inset - len : s.Top + inset;
        Ui.FillRound(g, c, new Rectangle(x, bottom ? y + len - t : y, len, t), t / 2f);
        Ui.FillRound(g, c, new Rectangle(right ? x + len - t : x, y, t, len), t / 2f);
    }
}
