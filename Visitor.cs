namespace LocalSendDrop;

/// <summary>
/// A colleague's little mouth, in their colour with their name, hopping onto this
/// screen to hand over what they sent, then waving and hopping off again.
/// Click-through: it never gets in the way.
/// </summary>
sealed class Visitor : Form
{
    const double Duration = 2600;   // ms, enter to exit
    readonly Color _color;
    readonly string _name;
    readonly Image? _food;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    readonly long _start = Environment.TickCount64;
    PointF _from, _to;
    float _facing;

    /// <summary>Plays once on the drop zone's screen and closes itself.</summary>
    public static void Play(Color color, string name, Image? food) => new Visitor(color, name, food).Show();

    Visitor(Color color, string name, Image? food)
    {
        _color = color;
        _name = name;
        _food = food;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        _tick.Tick += (_, _) => Step();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // layered, click-through, tool window, topmost, never activated
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8 | 0x8000000;
            return cp;
        }
    }

    float Scale_ => DeviceDpi / 96f;
    int Box => (int)(220 * Scale_);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Walk in from the screen edge next to the drop zone, to just inside it.
        var st = Settings.Current;
        var wa = st.TargetScreen.WorkingArea;
        var s = Scale_;
        if (st.Center)
        {
            var x = wa.Left + wa.Width / 2f + 150 * s;
            _from = new PointF(x, wa.Top - 120 * s);
            _to = new PointF(x, wa.Top + 150 * s);
        }
        else
        {
            var y = st.Bottom ? wa.Bottom - 190 * s : wa.Top + 190 * s;
            _from = new PointF(st.Right ? wa.Right + 120 * s : wa.Left - 120 * s, y);
            _to = new PointF(st.Right ? wa.Right - 150 * s : wa.Left + 150 * s, y);
        }
        _facing = MathF.Atan2(_to.Y - _from.Y, _to.X - _from.X) * 180 / MathF.PI;
        Ui.KeepOnTop(Handle);
        _tick.Start();
        Step();
    }

    void Step()
    {
        var t = (Environment.TickCount64 - _start) / Duration;
        if (t >= 1) { _tick.Stop(); Close(); return; }
        var s = Scale_;
        PointF at;
        float angle = _facing;
        Mouth.Pose pose;
        if (t < 0.3)
        {
            // Hop in, slowing down.
            var u = t / 0.3;
            var e = 1 - Math.Pow(1 - u, 3);
            at = Lerp(_from, _to, e);
            at.Y -= (float)(Math.Abs(Math.Sin(u * 3 * Math.PI)) * 18 * s * (1 - u));
            pose = new Mouth.Pose(0.15);
        }
        else if (t < 0.42) { at = _to; pose = new Mouth.Pose((t - 0.3) / 0.12); }
        else if (t < 0.62) { at = _to; pose = new Mouth.Pose(1, Food: 1 - (t - 0.42) / 0.2, FoodImage: _food); }
        else if (t < 0.8) { at = _to; pose = Mouth.AtWave((t - 0.62) / 0.18); }
        else
        {
            // Turn round and hop off.
            var u = (t - 0.8) / 0.2;
            at = Lerp(_to, _from, u * u);
            at.Y -= (float)(Math.Abs(Math.Sin(u * 2 * Math.PI)) * 14 * s);
            angle = _facing + 180;
            pose = new Mouth.Pose(0, Happy: true);
        }
        Render(at, angle, pose);
    }

    static PointF Lerp(PointF a, PointF b, double t) => new((float)(a.X + (b.X - a.X) * t), (float)(a.Y + (b.Y - a.Y) * t));

    void Render(PointF at, float angle, Mouth.Pose pose)
    {
        var s = Scale_;
        var box = Box;
        using var bmp = new Bitmap(box, box, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            var centre = new PointF(box / 2f, box / 2f + 14 * s);
            Mouth.Paint(g, s * 0.8f, pose, centre, angle, face: 1.2f, color: _color);
            // Whose it is, above its head.
            var font = Ui.Caption;
            var size = TextRenderer.MeasureText(_name, font, Size.Empty, TextFormatFlags.NoPadding);
            var tag = new RectangleF(box / 2f - size.Width / 2f - 8 * s, centre.Y - 52 * s - size.Height, size.Width + 16 * s, size.Height + 8 * s);
            using (var path = Ui.Round(tag, tag.Height / 2))
            using (var fill = new SolidBrush(Color.FromArgb(230, 32, 32, 32)))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, _name, font, Rectangle.Round(tag), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        Layered.Present(Handle, bmp, new Point((int)(at.X - box / 2f), (int)(at.Y - box / 2f - 14 * s)));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Dispose();
        base.OnFormClosed(e);
    }
}
