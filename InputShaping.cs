namespace LocalSendDrop;

/// <summary>
/// Spots a double-tap of right Ctrl: down, up, down, up, the two downs within the window and no
/// other key in between. Feed it every key event; it says true on the second release.
/// Not thread safe: the hook thread only.
/// </summary>
sealed class DoubleTap(int windowMs)
{
    public const ushort RightCtrl = 0xA3;

    enum Phase { Idle, Down1, Up1, Down2 }
    Phase _phase;
    long _firstDown;

    public int WindowMs { get; set; } = windowMs;

    public void Reset() => _phase = Phase.Idle;

    public bool Feed(ushort vk, bool up, long nowMs)
    {
        if (vk != RightCtrl)
        {
            if (!up) _phase = Phase.Idle;   // a chord, or typing: not a tap
            return false;
        }
        if (!up)
        {
            switch (_phase)
            {
                case Phase.Down1 or Phase.Down2: break;   // key repeat while held
                case Phase.Up1 when nowMs - _firstDown <= WindowMs: _phase = Phase.Down2; break;
                default: _phase = Phase.Down1; _firstDown = nowMs; break;
            }
            return false;
        }
        switch (_phase)
        {
            case Phase.Down1: _phase = Phase.Up1; return false;
            case Phase.Down2: _phase = Phase.Idle; return true;
            default: _phase = Phase.Idle; return false;
        }
    }
}

/// <summary>Pointer and wheel speed for input sent to the other PC. Fractions carry over so a slow speed still moves.</summary>
sealed class PointerScaler
{
    double _rx, _ry, _rw, _rh;

    static int Take(int value, double speed, ref double rest)
    {
        if (speed == 1.0 && rest == 0) return value;
        var v = value * speed + rest;
        var whole = (int)Math.Truncate(v);
        rest = v - whole;
        return whole;
    }

    public (int dx, int dy) Scale(int dx, int dy, double speed) =>
        (Take(dx, speed, ref _rx), Take(dy, speed, ref _ry));

    /// <summary>A wheel delta times the scroll speed, sign flipped for reverse scrolling.</summary>
    public int Wheel(int delta, double speed, bool reverse, bool horizontal = false)
    {
        var d = Take(delta, speed, ref horizontal ? ref _rh : ref _rw);
        return reverse ? -d : d;
    }
}
