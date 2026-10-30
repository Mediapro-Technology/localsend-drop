using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace LocalSendDrop;

static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "LocalSendDrop";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--list")) return ListDevices();
        if (args.Length == 3 && args[0] == "--send") return SendCli(args[1], args[2]);

        // Explorer's "Send with LocalSend Drop" passes: --share "<path>"
        var share = args.Length >= 2 && args[0] == "--share" ? args.Skip(1).ToList() : null;
        var updated = args.Contains("--updated");
        if (share != null && Mutex.TryOpenExisting(@"Local\LocalSendDrop", out var running))
        {
            running.Dispose();
            if (Shell.SendToRunning(share)) return 0;
        }

        // Windows blocks drag-and-drop from Explorer into elevated windows, so an
        // admin-launched copy restarts itself as the normal user.
        if (IsElevated() && RelaunchUnelevated()) return 0;

        using var single = new Mutex(true, @"Local\LocalSendDrop", out var first);
        if (!first && updated)
        {
            // Just updated: the previous version is still shutting down.
            try { first = single.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
        using var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LocalSendDrop.settings");
        if (!first)
        {
            if (share != null) Shell.SendToRunning(share);
            else openSettings.Set(); // launching it again while it runs opens Settings
            return 0;
        }
        Updater.CleanUp();

        ApplicationConfiguration.Initialize();

        var settings = Settings.Current;
        if (!settings.FirstRunDone)
        {
            // It's meant to always be there, so start with Windows by default.
            SetAutostart(true);
            settings.FirstRunDone = true;
            settings.Save();
        }

        var id = Identity.LoadOrCreate();
        Discovery discovery;
        Server server;
        try { (discovery, server) = StartNetwork(id); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LocalSend Drop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using var tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "LocalSend Drop — drag files to the top-left corner",
            Visible = true,
        };
        void Notify(string text, bool error) =>
            tray.ShowBalloonTip(3000, "LocalSend Drop", text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);

        using var panel = new DropPanel(id, discovery, Notify);
        using var card = new IncomingCard { CancelReceive = server.CancelCurrent };
        _ = card.Handle; // so background threads can BeginInvoke before it's first shown

        server.AskAccept = async (r, ct) =>
        {
            var s = Settings.Current;
            var trusted = r.SenderFingerprint.Length > 0 && s.Trusted.Contains(r.SenderFingerprint);
            if (s.ReceiveMode == "all" || (s.ReceiveMode == "known" && trusted))
            {
                card.BeginInvoke(() => card.Receiving(r));
                return true;
            }
            var ok = await card.Invoke(() => card.Ask(r, ct));
            if (ok && !trusted && r.SenderFingerprint.Length > 0)
                card.BeginInvoke(() => { s.Trusted.Add(r.SenderFingerprint); s.Save(); });
            return ok;
        };
        server.Progress += (s, got, total) => card.BeginInvoke(() => card.OnProgress(s, got, total));
        server.Finished += (s, saved) => card.BeginInvoke(() => card.OnFinished(s, saved));
        server.Failed += (s, reason) => card.BeginInvoke(() => card.OnFailed(s, reason));
        server.MessageReceived += (alias, text) => card.BeginInvoke(() => card.OnMessage(alias, text));
        server.Ended += (r, saved, error) => History.Add(new HistoryEntry
        {
            Time = DateTime.UtcNow, Received = true, Peer = r.SenderAlias, PeerFingerprint = r.SenderFingerprint,
            Paths = saved, Title = saved.Count > 0 ? History.TitleFor(saved) : History.TitleFor(r.Files.Select(f => f.FileName).ToList()),
            Ok = error == null, Error = error,
        });

        Shell.Register();
        Shell.Listen(paths => panel.BeginInvoke(() => panel.ShowShare(paths)));

        // Windows 11: the packaged menu entry replaces the "Show more options" one.
        async Task SetUpShellMenu(bool ask)
        {
            if (!ShellPackage.Supported) return;
            if (await Task.Run(ShellPackage.IsInstalled))
            {
                ShellPackage.EnsureDll();
                Shell.Unregister();
                return;
            }
            if (!ask) return;
            var error = await Task.Run(ShellPackage.Install);
            if (error == null)
            {
                Shell.Unregister();
                Notify("\"Send with LocalSend Drop\" is now in the right-click menu", false);
            }
            else Notify($"Couldn't add to the right-click menu: {error}", true);
        }
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => panel.BeginInvoke(async () =>
        {
            var first = !Settings.Current.ShellMenuOffered;
            if (first) { Settings.Current.ShellMenuOffered = true; Settings.Current.Save(); }
            await SetUpShellMenu(ask: first);
        }));

        var menu = new ContextMenuStrip();
        var thisPc = new ToolStripMenuItem($"This PC: {id.Alias}") { Enabled = false };
        SettingsForm? open = null;
        void ShowSettings()
        {
            if (open != null) { open.Activate(); return; }
            using var f = open = new SettingsForm(IsAutostart(), () => SetUpShellMenu(ask: true));
            var ok = f.ShowDialog() == DialogResult.OK;
            open = null;
            if (!ok) return;
            var s = Settings.Current;
            s.Alias = f.DeviceName == Environment.MachineName ? null : f.DeviceName;
            s.SaveDir = f.SaveDir == Settings.DefaultSaveDir ? null : f.SaveDir;
            s.ReceiveMode = f.ReceiveMode;
            s.Corner = f.Corner;
            s.Monitor = f.Monitor;
            s.Sound = f.Sound;
            s.AutoUpdate = f.AutoUpdate;
            if (f.ForgetTrusted) s.Trusted.Clear();
            s.Save();
            SetAutostart(f.Autostart);
            if (id.Alias != f.DeviceName)
            {
                id.Alias = f.DeviceName;
                thisPc.Text = $"This PC: {id.Alias}";
                _ = discovery.AnnounceAsync(); // others pick up the new name
            }
            panel.Reposition();
        }
        panel.OpenSettings = ShowSettings;
        new Thread(() =>
        {
            while (openSettings.WaitOne()) panel.BeginInvoke(ShowSettings);
        }) { IsBackground = true }.Start();
        menu.Items.Add("Open panel", null, (_, _) => panel.ShowExpanded());
        menu.Items.Add("Refresh devices", null, (_, _) => _ = discovery.RefreshAsync());
        menu.Items.Add("Open Downloads", null, (_, _) => Process.Start("explorer.exe", $"\"{server.SaveDir}\""));
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Check for updates", null, (_, _) => _ = CheckForUpdates(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(thisPc);
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) panel.ShowExpanded(); };

        if (Process.GetProcessesByName("localsend_app").Length > 0)
            Notify("LocalSend is also running. Quit it so files come to LocalSend Drop instead.", true);

        // Updates: check shortly after start, then every 6 hours; install when idle.
        async Task CheckForUpdates(bool manual)
        {
            string? version;
            try { version = await Updater.DownloadIfNewerAsync(); }
            catch (Exception ex)
            {
                if (manual) Notify($"Couldn't check for updates: {ex.Message}", true);
                return;
            }
            if (version == null)
            {
                if (manual) Notify($"You have the latest version ({Updater.CurrentText})", false);
                return;
            }
            while (panel.Busy || server.Busy) await Task.Delay(TimeSpan.FromSeconds(20));
            Notify($"Updating to {version}…", false);
            await Task.Delay(1500);
            if (Updater.ApplyAndRestart()) Application.Exit();
            else Notify($"Version {version} is ready, but this folder can't be written to. Move LocalSendDrop.exe somewhere like C:\\Tools.", true);
        }
        var updateTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds };
        updateTimer.Tick += (_, _) => { if (Settings.Current.AutoUpdate) _ = CheckForUpdates(manual: false); };
        updateTimer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
            panel.BeginInvoke(() => { if (Settings.Current.AutoUpdate) _ = CheckForUpdates(manual: false); }));

        if (updated) Notify($"Updated to {Updater.CurrentText}", false);

        panel.Show();
        if (share != null) panel.BeginInvoke(() => panel.ShowShare(share));
        Application.Run();
        tray.Visible = false;
        discovery.Dispose();
        server.DisposeAsync().AsTask().Wait(2000);
        return 0;
    }

    static (Discovery, Server) StartNetwork(Identity id)
    {
        var discovery = new Discovery(id);
        var server = new Server(id, discovery);
        server.StartAsync().GetAwaiter().GetResult();
        discovery.HttpPort = server.Port;
        discovery.Start();
        return (discovery, server);
    }

    static bool IsElevated()
    {
        using var wi = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(wi).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Restarts via Explorer (which runs unelevated). Returns false if we just tried.</summary>
    static bool RelaunchUnelevated()
    {
        var marker = Path.Combine(Path.GetTempPath(), "LocalSendDrop.relaunch");
        // With UAC off Explorer is elevated too; don't loop forever.
        if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromSeconds(15)) return false;
        File.WriteAllText(marker, "");
        Process.Start("explorer.exe", $"\"{Environment.ProcessPath}\"");
        return true;
    }

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunName, false);
    }

    /// <summary>Diagnostics: `LocalSendDrop.exe --list` prints the devices it can see.</summary>
    static int ListDevices()
    {
        var id = Identity.LoadOrCreate();
        var (d, server) = StartNetwork(id);
        Thread.Sleep(5000);
        Console.WriteLine($"me: {id.Alias} fp={id.Fingerprint[..12]}… https={d.HttpPort}");
        foreach (var dev in d.Devices)
            Console.WriteLine($"{dev.Alias}\t{dev.DeviceType}\t{dev.BaseUrl}\tfp={dev.Fingerprint[..Math.Min(12, dev.Fingerprint.Length)]}");
        if (d.Devices.Count == 0) Console.WriteLine("(no devices found)");
        d.Dispose();
        server.DisposeAsync().AsTask().Wait(2000);
        return 0;
    }

    /// <summary>Diagnostics: `LocalSendDrop.exe --send <path> <alias>` sends without the UI.</summary>
    static int SendCli(string path, string alias)
    {
        var id = Identity.LoadOrCreate();
        var (d, server) = StartNetwork(id);
        Thread.Sleep(4000);
        var target = d.Devices.FirstOrDefault(x => x.Alias.Contains(alias, StringComparison.OrdinalIgnoreCase));
        if (target == null) { Console.WriteLine($"no device matching '{alias}'"); return 2; }
        Console.WriteLine($"-> {target.Alias} {target.BaseUrl}");
        var progress = new SyncProgress(p => Console.WriteLine(p.Item1));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var r = Sender.SendAsync(id, d.HttpPort, target, Sender.FromPaths([path]), () => null, progress, cts.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine("RESULT: " + r);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message); return 1; }
        finally { d.Dispose(); server.DisposeAsync().AsTask().Wait(2000); }
    }

    sealed class SyncProgress(Action<(string, double?)> on) : IProgress<(string, double?)>
    {
        public void Report((string, double?) value) => on(value);
    }
}
