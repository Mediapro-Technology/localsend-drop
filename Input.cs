using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalSendDrop;

enum MouseKind : byte { Move, LeftDown, LeftUp, RightDown, RightUp, MiddleDown, MiddleUp, XDown, XUp, Wheel, HWheel }

/// <summary>A mouse event seen by the low-level hook. Data: wheel delta, or 1/2 for the X buttons.</summary>
readonly record struct MouseEvent(MouseKind Kind, int X, int Y, int Data, bool Injected);

/// <summary>A key event seen by the low-level hook.</summary>
readonly record struct KeyEvent(ushort Vk, ushort Scan, bool Up, bool Extended, bool Injected);

/// <summary>
/// Low-level mouse and keyboard hooks on a thread of their own, with its own
/// message loop. Windows silently drops a low-level hook whose callback takes
/// longer than about 300 ms, so the callbacks given here must only decide
/// (true = swallow the event) and hand work to a queue, never block.
/// Events injected by <see cref="Inject"/> reach the callbacks flagged Injected and are never swallowed.
/// Hook coordinates are real pixels: the process must be per-monitor DPI aware
/// (ApplicationConfiguration.Initialize) or they won't match <see cref="Inject.Desktop"/>.
/// </summary>
sealed class InputHook : IDisposable
{
    /// <summary>Runs on the hook thread. Return true to keep the event from this PC.</summary>
    public Func<MouseEvent, bool>? OnMouse;
    /// <summary>Runs on the hook thread. Return true to keep the event from this PC.</summary>
    public Func<KeyEvent, bool>? OnKey;

    /// <summary>Slowest callback so far, in milliseconds (diagnostics).</summary>
    public double MaxCallbackMs { get; private set; }

    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    uint _threadId;
    Exception? _startError;
    // Kept in fields so the GC never collects the delegates Windows calls.
    readonly Native.HookProc _mouseProc, _keyProc;

    public InputHook()
    {
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Input hooks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
        if (_startError != null) throw _startError;
    }

    void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        var module = Native.GetModuleHandle(null);
        var mouse = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        var keys = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, module, 0);
        if (mouse == 0 || keys == 0)
        {
            _startError = new InvalidOperationException($"Couldn't install input hooks (error {Marshal.GetLastWin32Error()}).");
            if (mouse != 0) Native.UnhookWindowsHookEx(mouse);
            if (keys != 0) Native.UnhookWindowsHookEx(keys);
            _ready.Set();
            return;
        }
        _ready.Set();

        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(in msg);
            Native.DispatchMessage(in msg);
        }
        Native.UnhookWindowsHookEx(mouse);
        Native.UnhookWindowsHookEx(keys);
    }

    nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && OnMouse is { } on)
        {
            var started = Stopwatch.GetTimestamp();
            var s = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            var high = (short)(s.mouseData >> 16);
            MouseKind? kind = (int)wParam switch
            {
                0x200 => MouseKind.Move,
                0x201 => MouseKind.LeftDown, 0x202 => MouseKind.LeftUp,
                0x204 => MouseKind.RightDown, 0x205 => MouseKind.RightUp,
                0x207 => MouseKind.MiddleDown, 0x208 => MouseKind.MiddleUp,
                0x20B => MouseKind.XDown, 0x20C => MouseKind.XUp,
                0x20A => MouseKind.Wheel, 0x20E => MouseKind.HWheel,
                _ => null,
            };
            var injected = (s.flags & Native.LLMHF_INJECTED) != 0 && s.dwExtraInfo == Inject.Marker;
            var swallow = kind is { } k && on(new MouseEvent(k, s.pt.X, s.pt.Y, high, injected)) && !injected;
            Track(started);
            if (swallow) return 1;
        }
        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    nint KeyProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && OnKey is { } on)
        {
            var started = Stopwatch.GetTimestamp();
            var s = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var injected = (s.flags & Native.LLKHF_INJECTED) != 0 && s.dwExtraInfo == Inject.Marker;
            var swallow = on(new KeyEvent((ushort)s.vkCode, (ushort)s.scanCode,
                (s.flags & Native.LLKHF_UP) != 0, (s.flags & Native.LLKHF_EXTENDED) != 0, injected)) && !injected;
            Track(started);
            if (swallow) return 1;
        }
        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    void Track(long started)
    {
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (ms > MaxCallbackMs) MaxCallbackMs = ms;
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
        _thread.Join(2000);
    }
}

/// <summary>Something that plays input for us: the elevated admin helper.</summary>
interface IInjector
{
    bool MoveTo(int x, int y);
    bool Button(MouseKind kind, int data);
    bool Key(ushort vk, ushort scan, bool up, bool extended);
}

/// <summary>Plays input on this PC, as if from its own mouse and keyboard.</summary>
static class Inject
{
    /// <summary>When set (the admin helper is connected), input is played there, so it reaches admin windows too.</summary>
    public static volatile IInjector? Via;

    /// <summary>Tags our own injected input so our hooks can tell it apart.</summary>
    public static readonly nuint Marker = 0x4C534452; // "LSDR"

    /// <summary>The whole desktop across all monitors, in pixels.</summary>
    public static Rectangle Desktop => new(
        Native.GetSystemMetrics(76), Native.GetSystemMetrics(77),   // SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN
        Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));  // SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN

    /// <summary>Puts the pointer at a desktop pixel. Absolute, so this PC's pointer acceleration isn't applied twice.</summary>
    public static void MoveTo(int x, int y)
    {
        if (Via is { } via && via.MoveTo(x, y)) return;
        var d = Desktop;
        var nx = (int)((x - d.Left) * 65535L / Math.Max(1, d.Width - 1));
        var ny = (int)((y - d.Top) * 65535L / Math.Max(1, d.Height - 1));
        Mouse(nx, ny, 0, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK);
    }

    /// <summary>Presses or releases a button, or turns a wheel (data = delta, or the X button 1/2).</summary>
    public static void Button(MouseKind kind, int data = 0)
    {
        if (Via is { } via && via.Button(kind, data)) return;
        var (flags, mouseData) = kind switch
        {
            MouseKind.LeftDown => (0x0002u, 0), MouseKind.LeftUp => (0x0004u, 0),
            MouseKind.RightDown => (0x0008u, 0), MouseKind.RightUp => (0x0010u, 0),
            MouseKind.MiddleDown => (0x0020u, 0), MouseKind.MiddleUp => (0x0040u, 0),
            MouseKind.XDown => (0x0080u, data), MouseKind.XUp => (0x0100u, data),
            MouseKind.Wheel => (0x0800u, data), MouseKind.HWheel => (0x1000u, data),
            _ => (0u, 0),
        };
        if (flags != 0) Mouse(0, 0, mouseData, flags);
    }

    /// <summary>Presses or releases a key. By scan code when there is one, so the other PC's keyboard layout applies.</summary>
    public static void Key(ushort vk, ushort scan, bool up, bool extended)
    {
        if (Via is { } via && via.Key(vk, scan, up, extended)) return;
        uint flags = (up ? Native.KEYEVENTF_KEYUP : 0) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0);
        if (scan != 0) flags |= Native.KEYEVENTF_SCANCODE;
        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = scan != 0 ? (ushort)0 : vk, wScan = scan, dwFlags = flags, dwExtraInfo = Marker } },
        };
        Send(input);
    }

    static void Mouse(int dx, int dy, int data, uint flags)
    {
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Marker } },
        };
        Send(input);
    }

    static void Send(Native.INPUT input)
    {
        // Returns 0 when Windows blocks it: an admin window or the secure desktop (UAC, lock screen) has focus.
        Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());
    }

    public static Point CursorPos => Native.GetCursorPos(out var p) ? new Point(p.X, p.Y) : Point.Empty;
}

/// <summary>Diagnostics for the hooks and injection: --kvm-hooktest [seconds], --kvm-injecttest.</summary>
static class InputTest
{
    /// <summary>Logs what the hooks see (nothing is swallowed) and how fast the callbacks are.</summary>
    public static int HookTest(int seconds)
    {
        ApplicationConfiguration.Initialize();   // per-monitor DPI aware, like the app: hooks report real pixels
        var queue = new BlockingCollection<string>();
        int moves = 0, buttons = 0, keys = 0;
        using var hook = new InputHook
        {
            OnMouse = e =>
            {
                if (e.Injected) return false;
                if (e.Kind == MouseKind.Move) Interlocked.Increment(ref moves);
                else { Interlocked.Increment(ref buttons); queue.Add($"mouse {e.Kind} at {e.X},{e.Y} data={e.Data}"); }
                return false;
            },
            OnKey = e =>
            {
                if (e.Injected) return false;
                Interlocked.Increment(ref keys);
                queue.Add($"key {(Keys)e.Vk} scan={e.Scan:X2}{(e.Extended ? " ext" : "")} {(e.Up ? "up" : "down")}");
                return false;
            },
        };
        Console.WriteLine($"Hooks on for {seconds}s. Move the mouse, click, scroll, type. Desktop: {Inject.Desktop}");

        var end = DateTime.UtcNow.AddSeconds(seconds);
        var lastTick = DateTime.UtcNow;
        var lastMoves = 0;
        while (DateTime.UtcNow < end)
        {
            if (queue.TryTake(out var line, 100)) Console.WriteLine(line);
            if (DateTime.UtcNow - lastTick >= TimeSpan.FromSeconds(1))
            {
                var m = Volatile.Read(ref moves);
                if (m != lastMoves) Console.WriteLine($"  {m - lastMoves} moves/s, cursor {Inject.CursorPos}");
                lastMoves = m;
                lastTick = DateTime.UtcNow;
            }
        }
        Console.WriteLine($"RESULT: moves={moves} buttons={buttons} keys={keys} slowest callback={hook.MaxCallbackMs:0.000} ms");
        return hook.MaxCallbackMs < 5 ? 0 : 1;
    }

    /// <summary>Moves the pointer in a circle by injection, checks it landed, and puts it back.</summary>
    public static int InjectTest()
    {
        ApplicationConfiguration.Initialize();
        var start = Inject.CursorPos;
        var worst = 0.0;
        for (var i = 0; i <= 60; i++)
        {
            var a = i / 60.0 * Math.PI * 2;
            var x = start.X + (int)(Math.Cos(a) * 100) - 100;
            var y = start.Y + (int)(Math.Sin(a) * 100);
            Inject.MoveTo(x, y);
            Thread.Sleep(16);
            var p = Inject.CursorPos;
            worst = Math.Max(worst, Math.Abs(p.X - x) + Math.Abs(p.Y - y));
        }
        Inject.MoveTo(start.X, start.Y);
        Console.WriteLine($"RESULT: worst landing error {worst} px (0-1 expected)");
        return worst <= 2 ? 0 : 1;
    }
}

static partial class Native
{
    public delegate nint HookProc(int code, nint wParam, nint lParam);

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const uint WM_QUIT = 0x0012;
    public const uint LLMHF_INJECTED = 0x01;
    public const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;
    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, HookProc fn, nint hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hhk, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(in MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessage(in MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
}
