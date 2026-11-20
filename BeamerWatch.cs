using System.Diagnostics;

namespace LocalSendDrop;

/// <summary>Notices Beamer.exe starting and quitting. Sharing stays paused while it runs, so the two never fight over the mouse.</summary>
sealed class BeamerWatch : IDisposable
{
    /// <summary>True while Beamer is running (the last poll).</summary>
    public static bool Paused { get; private set; }

    readonly Func<bool> _isRunning;
    readonly System.Threading.Timer _timer;
    bool _last;
    int _busy;

    /// <summary>Raised with the new state when Beamer starts (true) or quits (false). Fires on a thread-pool thread.</summary>
    public event Action<bool>? Changed;

    public BeamerWatch(Func<bool>? isRunning = null, TimeSpan? every = null)
    {
        _isRunning = isRunning ?? DefaultCheck;
        var period = every ?? TimeSpan.FromSeconds(2);
        _last = Safe();
        Paused = _last;
        _timer = new System.Threading.Timer(_ => Poll(), null, period, period);
    }

    /// <summary>The state when the watch started, so the caller can pause right away.</summary>
    public bool Running => _last;

    internal void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = Safe();
            if (now == _last) return;
            _last = now;
            Paused = now;
            Changed?.Invoke(now);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    bool Safe()
    {
        try { return _isRunning(); }
        catch { return _last; }
    }

    static bool DefaultCheck()
    {
        var found = Process.GetProcessesByName("Beamer");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    public void Dispose() => _timer.Dispose();
}
