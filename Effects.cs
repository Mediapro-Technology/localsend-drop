using System.Drawing.Drawing2D;

namespace LocalSendDrop;

/// <summary>
/// Glass backdrops and the open animation.
/// Windows Forms can't blur live, so Glass takes a snapshot of the screen behind a
/// window as it opens, blurs it, and paints it under a tint and a highlight edge.
/// </summary>
static class Effects
{
    static readonly Dictionary<Form, (Bitmap Image, Point Origin)> Backdrops = [];

    /// <summary>Snapshots and blurs the screen area a window is about to cover (Glass only).</summary>
    public static void Capture(Form f, Rectangle screenArea)
    {
        if (Ui.Style != Ui.Look.Glass) return;
        screenArea.Intersect(SystemInformation.VirtualScreen);
        if (screenArea.Width < 8 || screenArea.Height < 8) return;
        Bitmap blurred;
        using (var shot = new Bitmap(screenArea.Width, screenArea.Height))
        {
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(screenArea.Location, Point.Empty, screenArea.Size);
            // Down to a sixteenth and back up: a soft, cheap blur.
            using var tiny = Scale(shot, Math.Max(2, shot.Width / 16), Math.Max(2, shot.Height / 16));
            using var mid = Scale(tiny, Math.Max(2, shot.Width / 4), Math.Max(2, shot.Height / 4));
            blurred = Scale(mid, shot.Width, shot.Height);
        }
        lock (Backdrops)
        {
            if (Backdrops.Remove(f, out var old)) old.Image.Dispose();
            else f.Disposed += (_, _) => { lock (Backdrops) if (Backdrops.Remove(f, out var b)) b.Image.Dispose(); };
            Backdrops[f] = (blurred, screenArea.Location);
        }
    }

    static Bitmap Scale(Image src, int w, int h)
    {
        var dst = new Bitmap(w, h);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return dst;
    }

    /// <summary>A window's background: frosted glass on Glass, the plain colour otherwise.</summary>
    public static void PaintSurface(Graphics g, Form f, Color solid)
    {
        (Bitmap Image, Point Origin) backdrop;
        bool has;
        lock (Backdrops) has = Backdrops.TryGetValue(f, out backdrop);
        if (Ui.Style != Ui.Look.Glass || !has)
        {
            g.Clear(solid);
            return;
        }
        g.Clear(solid);
        g.DrawImageUnscaled(backdrop.Image, backdrop.Origin.X - f.Left, backdrop.Origin.Y - f.Top);
        var size = f.ClientSize;
        // Tint, so text stays readable over anything.
        using (var tint = new SolidBrush(Ui.Dark ? Color.FromArgb(170, 16, 17, 25) : Color.FromArgb(150, 247, 248, 252)))
            g.FillRectangle(tint, 0, 0, size.Width, size.Height);
        // Light catching the top edge.
        var glowH = Math.Max(1, Math.Min(size.Height, 90 * f.DeviceDpi / 96));
        using (var glow = new LinearGradientBrush(new Rectangle(0, 0, size.Width, glowH),
                   Color.FromArgb(Ui.Dark ? 26 : 70, Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical))
            g.FillRectangle(glow, 0, 0, size.Width, glowH);
        using var edge = new Pen(Color.FromArgb(Ui.Dark ? 46 : 140, Color.White), Math.Max(1, f.DeviceDpi / 96f));
        g.DrawLine(edge, 0, 0.5f, size.Width, 0.5f);
    }

    /// <summary>
    /// Fades a window in while sliding it the last few pixels into place
    /// (from the direction of its corner). Windows 10 windows just appear.
    /// </summary>
    public static void Show(Form f, int fromY)
    {
        if (Ui.Style == Ui.Look.Windows10) return;
        Stop(f);
        var slide = new Slide { Offset = fromY };
        Slides[f] = slide;
        f.Opacity = 0.01;
        f.Top += fromY;
        var started = Environment.TickCount64;
        const double duration = 180;
        var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += (_, _) =>
        {
            if (f.IsDisposed || slide.Stopped) { timer.Dispose(); return; }
            var t = Math.Min(1, (Environment.TickCount64 - started) / duration);
            var e = 1 - Math.Pow(1 - t, 3);   // ease out
            // Measured from where the window is now, so a re-layout mid-slide doesn't jump.
            var home = f.Top - slide.Offset;
            slide.Offset = t >= 1 ? 0 : (int)Math.Round(fromY * (1 - e));
            f.Opacity = t >= 1 ? 1 : Math.Max(0.01, e);
            f.Top = home + slide.Offset;
            if (t >= 1) { timer.Dispose(); Slides.Remove(f); }
        };
        timer.Start();
    }

    sealed class Slide { public int Offset; public bool Stopped; }
    static readonly Dictionary<Form, Slide> Slides = [];

    /// <summary>How far the window still is from its place; add it when moving the window mid-slide.</summary>
    public static int SlideOffset(Form f) => Slides.TryGetValue(f, out var s) ? s.Offset : 0;

    /// <summary>Ends the open animation now (e.g. the window is closing): fully opaque, nothing moves it after.</summary>
    public static void Stop(Form f)
    {
        if (!Slides.Remove(f, out var s)) return;
        s.Stopped = true;
        if (!f.IsDisposed) f.Opacity = 1;
    }
}
