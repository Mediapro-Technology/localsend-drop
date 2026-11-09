using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace LocalSendDrop;

/// <summary>
/// The admin helper: this same exe run elevated (--input-helper) by a scheduled
/// task, so its hooks also see input meant for administrator windows and its
/// injected input can reach them. Off by default; turning it on in Settings
/// registers the task once, behind a UAC prompt.
///
/// Safeguards, because an elevated process that plays input is powerful:
///  - only this user can open the pipe, and the helper only talks to a client
///    running the same exe as itself (and the app only to a helper that is);
///  - it only plays input while a paired PC is crossed over here, or while
///    this PC's own input is crossed over (parking and returning the pointer);
///  - it exits as soon as the app disconnects; nothing starts it at sign-in.
/// </summary>
static class AdminHelper
{
    public const string TaskName = "LocalSend Drop input helper";

    // main -> helper
    const byte SetEdges = 1, Free = 2, MoveTo = 3, Button = 4, Key = 5, RemoteActive = 6;
    // helper -> main
    const byte Crossed = 20, Forward = 21, Emergency = 22, Stray = 23, Ready = 24;

    static string PipeName =>
        $"LocalSendDrop.input.{Process.GetCurrentProcess().SessionId}.{WindowsIdentity.GetCurrent().User!.Value}";

    // ---- frames: u16 length, then the payload (first byte: the op) ----

    internal static async Task<byte[]?> ReadFrame(Stream s, CancellationToken ct)
    {
        var head = new byte[2];
        if (!await Fill(s, head, ct)) return null;
        var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head)];
        return body.Length > 0 && await Fill(s, body, ct) ? body : null;
    }

    static async Task<bool> Fill(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    internal static byte[] Frame(byte op, ReadOnlySpan<byte> payload = default)
    {
        var f = new byte[3 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(f, (ushort)(1 + payload.Length));
        f[2] = op;
        payload.CopyTo(f.AsSpan(3));
        return f;
    }

    internal static byte[] Ints(params int[] values)
    {
        var b = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), values[i]);
        return b;
    }

    static int Int(byte[] f, int at) => BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(at));

    // ---- the helper process ----

    /// <summary>--input-helper: serve one app connection, then exit.</summary>
    public static int RunHelper()
    {
        ApplicationConfiguration.Initialize();   // per-monitor DPI aware: hook coordinates are real pixels
        KvmLog.Write($"helper started (elevated: {IsElevated()})");
        using var server = CreatePipe();
        if (server == null) { KvmLog.Write("helper: pipe already in use"); return 2; }
        try
        {
            if (!server.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(30))) { KvmLog.Write("helper: nobody connected"); return 3; }
        }
        catch (AggregateException) { return 3; }
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid) || !SameExe(pid))
        {
            KvmLog.Write($"helper: refused a client that isn't LocalSend Drop ({ImagePath(pid) ?? "unknown"})");
            return 4;
        }

        var write = new object();
        void Send(byte[] frame)
        {
            lock (write) { try { server.Write(frame); } catch (IOException) { } }
        }

        var remoteActive = false;
        long freedAt = 0;
        using var engine = new InputEngine();
        // Only play input while something crossed over: a paired PC here, or this PC's input there.
        bool MayInject() => remoteActive || engine.Captured || Environment.TickCount64 - freedAt < 3000;

        engine.Crossed += (e, p) => Send(Frame(Crossed, [(byte)e, .. Ints(p.X, p.Y)]));
        engine.Forward += m => Send(Frame(Forward, m));
        engine.Emergency += () => { freedAt = Environment.TickCount64; Send(Frame(Emergency)); };
        engine.Stray += (now, expected, slowest) =>
        {
            freedAt = Environment.TickCount64;
            Send(Frame(Stray, Ints(now.X, now.Y, expected.X, expected.Y, (int)(slowest * 1000))));
        };
        Send(Frame(Ready));
        KvmLog.Write("helper: connected to LocalSend Drop");

        while (ReadFrame(server, CancellationToken.None).GetAwaiter().GetResult() is { } f)
        {
            switch (f[0])
            {
                case SetEdges when f.Length >= 2: engine.SetEdges(f[1]); break;
                case Free: engine.Free(); freedAt = Environment.TickCount64; break;
                case RemoteActive when f.Length >= 2: remoteActive = f[1] != 0; break;
                case MoveTo when f.Length >= 9 && MayInject(): Inject.MoveTo(Int(f, 1), Int(f, 5)); break;
                case Button when f.Length >= 6 && MayInject(): Inject.Button((MouseKind)f[1], Int(f, 2)); break;
                case Key when f.Length >= 7 && MayInject():
                    Inject.Key(BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(1)), BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(3)), f[5] != 0, f[6] != 0);
                    break;
            }
        }
        KvmLog.Write("helper: LocalSend Drop disconnected; exiting");
        return 0;
    }

    static NamedPipeServerStream? CreatePipe()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        NamedPipeServerStream pipe;
        try
        {
            var security = new PipeSecurity();
            security.SetSecurityDescriptorSddlForm($"D:P(A;;GA;;;{sid})");   // only this user
            pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            KvmLog.Write($"helper: pipe failed ({e.Message})");   // usually: another helper has it
            return null;
        }
        // Label it medium so the app (not elevated) may open it. Needs no privilege after creation.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;ME)", 1, out var sd, out _)
            || !GetSecurityDescriptorSacl(sd, out _, out var sacl, out _)
            || SetSecurityInfo(pipe.SafePipeHandle.DangerousGetHandle(), 6, 0x10, 0, 0, 0, sacl) != 0)   // SE_KERNEL_OBJECT, LABEL_SECURITY_INFORMATION
            KvmLog.Write($"helper: couldn't label the pipe medium (error {Marshal.GetLastWin32Error()})");
        if (sd != 0) LocalFree(sd);
        return pipe;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint sd, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSecurityDescriptorSacl(nint sd, out bool present, out nint sacl, out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern uint SetSecurityInfo(nint handle, int type, uint info, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint mem);

    // ---- the app's side ----

    /// <summary>Connects to a running helper (the same exe, started by the task).</summary>
    public static async Task<HelperClient?> ConnectAsync(int timeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid) || !SameExe(pid))
            {
                KvmLog.Write($"app: the helper pipe belongs to something else ({ImagePath(pid) ?? "unknown"})");
                pipe.Dispose();
                return null;
            }
            using var ready = new CancellationTokenSource(3000);
            var first = await ReadFrame(pipe, ready.Token);
            if (first is not [Ready]) { pipe.Dispose(); return null; }
            return new HelperClient(pipe);
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            return null;
        }
    }

    /// <summary>Starts the helper through its task (no prompt) and connects. Null if it isn't set up or won't start.</summary>
    public static async Task<HelperClient?> StartAsync()
    {
        if (await ConnectAsync(200) is { } running) return running;
        if (Run("schtasks.exe", $"/Run /TN \"{TaskName}\"") != 0) { KvmLog.Write("app: couldn't run the helper task"); return null; }
        for (var i = 0; i < 10; i++)
        {
            if (await ConnectAsync(500) is { } c) return c;
        }
        KvmLog.Write("app: the helper didn't start");
        return null;
    }

    /// <summary>Registers the task (Windows asks for permission). Null when done, otherwise why not.</summary>
    public static Task<string?> InstallAsync()
    {
        var exe = Packaging.IsPackaged ? Packaging.AliasPath : Environment.ProcessPath!;
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Lets LocalSend Drop share the mouse and keyboard with administrator windows. LocalSend Drop starts it when needed; it stops when LocalSend Drop does.</Description>
                <SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{sid})</SecurityDescriptor>
              </RegistrationInfo>
              <Principals>
                <Principal id="User">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
              </Settings>
              <Actions Context="User">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--input-helper</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "LocalSendDrop-helper-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        return Elevated("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", () => { try { File.Delete(file); } catch { } });
    }

    /// <summary>Removes the task (Windows asks for permission). Null when done, otherwise why not.</summary>
    public static Task<string?> UninstallAsync() => Elevated("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", null);

    static async Task<string?> Elevated(string exe, string args, Action? after)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return "Windows didn't start it";
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? null : $"Task Scheduler answered {p.ExitCode}";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return "you didn't allow it"; }
        catch (Win32Exception e) { return e.Message; }
        finally { after?.Invoke(); }
    }

    static int Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return -1;
            p.WaitForExit(10_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    // ---- who's on the other end ----

    static bool SameExe(uint pid) =>
        ImagePath(pid) is { } path && string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    static string? ImagePath(uint pid)
    {
        var h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("kernel32.dll")] static extern bool GetNamedPipeClientProcessId(nint pipe, out uint pid);
    [DllImport("kernel32.dll")] static extern bool GetNamedPipeServerProcessId(nint pipe, out uint pid);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint process, int flags, char[] name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

    // ---- the client, as an input source and an injector ----

    internal static byte[] FrameSetEdges(int mask) => Frame(SetEdges, [(byte)mask]);
    internal static byte[] FrameFree() => Frame(Free);
    internal static byte[] FrameRemoteActive(bool on) => Frame(RemoteActive, [(byte)(on ? 1 : 0)]);
    internal static byte[] FrameMoveTo(int x, int y) => Frame(MoveTo, Ints(x, y));
    internal static byte[] FrameButton(MouseKind k, int data) => Frame(Button, [(byte)k, .. Ints(data)]);
    internal static byte[] FrameKey(ushort vk, ushort scan, bool up, bool ext)
    {
        var b = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(b, vk);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), scan);
        b[4] = (byte)(up ? 1 : 0);
        b[5] = (byte)(ext ? 1 : 0);
        return Frame(Key, b);
    }

    internal static void Dispatch(HelperClient c, byte[] f)
    {
        switch (f[0])
        {
            case Crossed when f.Length >= 10: c.OnCrossed((Edge)f[1], new Point(Int(f, 2), Int(f, 6))); break;
            case Forward when f.Length >= 2: c.OnForward(f[1..]); break;
            case Emergency: c.OnEmergency(); break;
            case Stray when f.Length >= 21: c.OnStray(new Point(Int(f, 1), Int(f, 5)), new Point(Int(f, 9), Int(f, 13)), Int(f, 17) / 1000.0); break;
        }
    }
}

/// <summary>The app's connection to the admin helper. Input source while connected, and where injected input goes.</summary>
sealed class HelperClient : IInputSource, IInjector
{
    readonly NamedPipeClientStream _pipe;
    readonly object _write = new();
    readonly CancellationTokenSource _cts = new();
    volatile bool _lost;

    public event Action<Edge, Point>? Crossed;
    public event Action<byte[]>? Forward;
    public event Action? Emergency;
    public event Action<Point, Point, double>? Stray;
    /// <summary>The helper went away (it exits if it's stopped or the pipe breaks).</summary>
    public event Action? Lost;

    internal HelperClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await AdminHelper.ReadFrame(_pipe, _cts.Token) is { } f) AdminHelper.Dispatch(this, f);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            GoneAway();
        });
    }

    public bool Connected => !_lost;

    internal void OnCrossed(Edge e, Point p) => Crossed?.Invoke(e, p);
    internal void OnForward(byte[] m) => Forward?.Invoke(m);
    internal void OnEmergency() => Emergency?.Invoke();
    internal void OnStray(Point now, Point expected, double slowest) => Stray?.Invoke(now, expected, slowest);

    bool Send(byte[] frame)
    {
        if (_lost) return false;
        lock (_write)
        {
            try { _pipe.Write(frame); return true; }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        GoneAway();
        return false;
    }

    void GoneAway()
    {
        if (_lost) return;
        _lost = true;
        Lost?.Invoke();
    }

    public void SetEdges(int mask) => Send(AdminHelper.FrameSetEdges(mask));
    public void Free() => Send(AdminHelper.FrameFree());
    public void SetRemoteActive(bool on) => Send(AdminHelper.FrameRemoteActive(on));

    public bool MoveTo(int x, int y) => Send(AdminHelper.FrameMoveTo(x, y));
    public bool Button(MouseKind kind, int data) => Send(AdminHelper.FrameButton(kind, data));
    public bool Key(ushort vk, ushort scan, bool up, bool extended) => Send(AdminHelper.FrameKey(vk, scan, up, extended));

    public void Dispose()
    {
        _lost = true;
        _cts.Cancel();
        try { _pipe.Dispose(); } catch { }
    }
}

/// <summary>
/// Diagnostics: --kvm-helpertest. Starts the helper (not elevated, so no prompt),
/// connects like the app does, and checks the pipe, the injection rule and that
/// the helper exits with the app. Moves the pointer briefly, then puts it back.
/// </summary>
static class HelperTest
{
    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        var failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        var start = Inject.CursorPos;
        var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--input-helper") { UseShellExecute = false, CreateNoWindow = true })!;
        HelperClient? client = null;
        try
        {
            for (var i = 0; i < 20 && client == null; i++) client = AdminHelper.ConnectAsync(500).GetAwaiter().GetResult();
            Check(client != null, "the app connects to the helper (same exe on both ends, this user only)");
            if (client == null) return 1;

            var second = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--input-helper") { UseShellExecute = false, CreateNoWindow = true })!;
            Check(second.WaitForExit(5000) && second.ExitCode == 2, "a second helper can't take the pipe");

            Inject.Via = client;
            var before = Inject.CursorPos;
            Inject.MoveTo(before.X + 40, before.Y + 40);
            Thread.Sleep(200);
            Check(Inject.CursorPos == before, $"with nothing crossed over, the helper plays no input (got {Inject.CursorPos})");

            client.SetRemoteActive(true);
            var target = new Point(before.X + 60, before.Y + 30);
            Inject.MoveTo(target.X, target.Y);
            Thread.Sleep(200);
            var got = Inject.CursorPos;
            Check(Math.Abs(got.X - target.X) <= 1 && Math.Abs(got.Y - target.Y) <= 1, $"while a paired PC is crossed over here, it plays input (got {got})");

            client.SetRemoteActive(false);
            Thread.Sleep(100);
            var after = Inject.CursorPos;
            Inject.MoveTo(after.X + 50, after.Y);
            Thread.Sleep(200);
            Check(Inject.CursorPos == after, "after they leave, it stops playing input");

            Inject.Via = null;
            client.Dispose();
            Check(helper.WaitForExit(3000), "the helper exits when the app disconnects");
        }
        finally
        {
            Inject.Via = null;
            client?.Dispose();
            if (!helper.HasExited) helper.Kill();
            Inject.MoveTo(start.X, start.Y);
        }
        Console.WriteLine(failures == 0 ? "RESULT: all passed" : $"RESULT: {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
