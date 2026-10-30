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
        if (args.Length == 2 && args[0] == "--snapshot") return Snapshot.Run(args[1]);
        if (args.Length >= 2 && args[0] == "--qr-serve") return Snapshot.QrServe(args[1..]);
        if (args.Length == 3 && args[0] == "--send") return SendCli(args[1], args[2]);

        // Explorer's "Send with LocalSend Drop" passes: --share "<path>"
        var share = args.Length >= 2 && args[0] == "--share" ? args.Skip(1).ToList() : null;
        // Started from Windows' Share sheet?
        string? shareText = null;
        if (share == null && ShareTarget.Read() is { } shared)
        {
            if (shared.Paths.Count > 0) share = shared.Paths;
            else shareText = shared.Text;
        }
        var updated = args.Contains("--updated");
        if ((share != null || shareText != null) && Mutex.TryOpenExisting(@"Local\LocalSendDrop", out var running))
        {
            running.Dispose();
            if (shareText != null ? Shell.SendTextToRunning(shareText) : Shell.SendToRunning(share!)) return 0;
        }

        // Windows blocks drag-and-drop from Explorer into elevated windows, so an
        // admin-launched copy restarts itself as the normal user.
        // Development builds (run from bin\) and test identities (LSDROP_DATA) must never touch the
        // real install: no relaunch through Explorer, Start-with-Windows or right-click changes.
        var devBuild = Environment.ProcessPath!.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                       Environment.GetEnvironmentVariable("LSDROP_DATA") is { Length: > 0 };
        if (!devBuild && !Packaging.IsPackaged && IsElevated() && RelaunchUnelevated()) return 0;

        // A test identity (LSDROP_DATA) gets its own lock so it can run beside the real app.
        var lockName = Environment.GetEnvironmentVariable("LSDROP_DATA") is { Length: > 0 } testData
            ? @"Local\LocalSendDrop." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(testData)))[..12]
            : @"Local\LocalSendDrop";
        using var single = new Mutex(true, lockName, out var first);
        if (!first && updated)
        {
            // Just updated: the previous version is still shutting down.
            try { first = single.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
        using var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LocalSendDrop.settings");
        if (!first)
        {
            if (shareText != null) Shell.SendTextToRunning(shareText);
            else if (share != null) Shell.SendToRunning(share);
            else openSettings.Set(); // launching it again while it runs opens Settings
            return 0;
        }
        Updater.CleanUp();

        ApplicationConfiguration.Initialize();

        var settings = Settings.Current;
        if (devBuild) { }
        else if (!settings.FirstRunDone)
        {
            // It's meant to always be there, so start with Windows by default.
            SetAutostart(true);
            settings.FirstRunDone = true;
            settings.Save();
        }
        else if (IsAutostart()) SetAutostart(true); // points it at this copy (the package after migrating)

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
            // We asked them for this file: take it without asking again.
            if (FileRequests.Consume(r.SenderFingerprint))
            {
                card.BeginInvoke(() => card.Receiving(r));
                return true;
            }
            if (Quiet.Active)
            {
                // Declined quietly; Recent shows who tried.
                History.Add(new HistoryEntry
                {
                    Time = DateTime.UtcNow, Received = true, Peer = r.SenderAlias, PeerFingerprint = r.SenderFingerprint,
                    Title = History.TitleFor(r.Files.Select(f => f.FileName).ToList()),
                    Ok = false, Error = "declined during Do not disturb",
                });
                return false;
            }
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
        server.Reconnecting += s => card.BeginInvoke(() => card.OnReconnecting(s));
        server.MessageReceived += (alias, fingerprint, text) =>
        {
            if (fingerprint.Length > 0) Notes.Add(fingerprint, fromMe: false, text);
            var first = text.Replace("\r", "").Split('\n')[0].Trim();
            History.Add(new HistoryEntry
            {
                Time = DateTime.UtcNow, Received = true, Peer = alias, Ok = true, Text = text,
                Title = first.Length > 0 ? $"\u201C{first}\u201D" : "Message",
            });
            if (!Quiet.Active) card.BeginInvoke(() => card.OnMessage(alias, fingerprint, text));
        };
        server.Ended += (r, saved, error) => History.Add(new HistoryEntry
        {
            Time = DateTime.UtcNow, Received = true, Peer = r.SenderAlias, PeerFingerprint = r.SenderFingerprint,
            Paths = saved, Title = saved.Count > 0 ? History.TitleFor(saved) : History.TitleFor(r.Files.Select(f => f.FileName).ToList()),
            Ok = error == null, Error = error,
        });

        // The package brings its own right-click entry; a plain .exe uses the classic one.
        if (Packaging.IsPackaged) Shell.Unregister(); else if (!devBuild) Shell.Register();
        Shell.Listen(paths => panel.BeginInvoke(() => panel.ShowShare(paths)),
                     text => panel.BeginInvoke(() => panel.ShowShareText(text)));

        // A plain .exe moves itself into the installed package, then hands over.
        async Task<bool> InstallPackage(bool mayPrompt)
        {
            if (Packaging.IsPackaged || !Packaging.Supported) return false;
            // A development build (run from bin\) must not replace the installed copy.
            if (!mayPrompt && Environment.ProcessPath!.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) return false;
            if (mayPrompt) Notify("Installing LocalSend Drop… approve the Windows prompt", false);
            string? error;
            try { error = await Packaging.MigrateAsync(mayPrompt); }
            catch (Exception ex) { error = ex.Message; }
            if (error != null)
            {
                if (mayPrompt) Notify($"Couldn't install: {error}", true);
                return false;
            }
            Shell.Unregister();
            Packaging.LaunchInstalled("--updated");
            Application.Exit();
            return true;
        }
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => panel.BeginInvoke(async () =>
        {
            var first = !Settings.Current.ShellMenuOffered;
            if (first) { Settings.Current.ShellMenuOffered = true; Settings.Current.Save(); }
            await InstallPackage(mayPrompt: first);
        }));

        var menu = Ui.Menu();
        var quiet = new ToolStripMenuItem("Do not disturb") { Checked = Settings.Current.DoNotDisturb };
        void ToggleQuiet()
        {
            var st = Settings.Current;
            st.DoNotDisturb = !st.DoNotDisturb;
            st.Save();
            quiet.Checked = st.DoNotDisturb;
            panel.Invalidate();
            Notify(st.DoNotDisturb ? "Do not disturb is on. Incoming files are declined." : "Do not disturb is off", false);
        }
        quiet.Click += (_, _) => ToggleQuiet();
        panel.ToggleQuiet = ToggleQuiet;

        // ---- quick notes ----
        var openNotes = new Dictionary<string, NotesForm>();
        void OpenNotes(string fingerprint, string name)
        {
            if (openNotes.TryGetValue(fingerprint, out var existing)) { existing.Activate(); return; }
            var form = new NotesForm(fingerprint, name, async text =>
            {
                if (panel.Find(fingerprint) is not { } device) return "they're not nearby right now";
                try
                {
                    await Sender.SendAsync(id, discovery.HttpPort, device, [new SendItem("message.txt", null, text)],
                        () => null, new Progress<(string, double?)>(), CancellationToken.None);
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            });
            openNotes[fingerprint] = form;
            form.FormClosed += (_, _) => { openNotes.Remove(fingerprint); form.Dispose(); };
            form.Show();
        }
        panel.ShareByQr = (items, text) =>
        {
            if (items.Count == 0 && string.IsNullOrEmpty(text)) return;
            var form = new QrForm(QrShare.Start(id.Alias, items, text));
            form.FormClosed += (_, _) => form.Dispose();
            form.Show();
        };
        panel.OpenNotes = d => OpenNotes(d.Fingerprint, DropPanel.DisplayName(d));
        card.Reply = (fingerprint, alias) => OpenNotes(fingerprint, panel.Find(fingerprint) is { } d ? DropPanel.DisplayName(d) : alias);

        // ---- asking for a file ----
        panel.AskForFile = async d =>
        {
            var name = DropPanel.DisplayName(d);
            var note = PromptForm.Ask($"Ask {name} for a file", "Optional: say what you need", "Ask", allowEmpty: true);
            if (note == null) return;
            FileRequests.Remember(d.Fingerprint);
            var error = await FileRequests.SendAsync(id, discovery.HttpPort, d, note.Trim());
            Notify(error == null ? $"Asked {name}. Their file will arrive without a prompt." : $"Couldn't ask {name}: {error}", error != null);
        };
        server.FileRequested += req => card.BeginInvoke(() => card.OnFileRequest(req));
        card.AnswerRequest = req =>
        {
            var device = panel.Find(req.Fingerprint) ?? new Device
            {
                Fingerprint = req.Fingerprint, Alias = req.Alias, Ip = req.Ip, Port = req.Port, Protocol = "https", DeviceType = "desktop",
            };
            using var dlg = new OpenFileDialog { Multiselect = true, Title = $"Send to {req.Alias}" };
            if (dlg.ShowDialog() == DialogResult.OK) panel.SendFiles(device, dlg.FileNames.ToList());
        };
        var thisPc = new ToolStripMenuItem($"This PC: {id.Alias}") { Enabled = false };
        SettingsForm? open = null;
        void ShowSettings()
        {
            if (open != null) { open.Activate(); return; }
            SettingsForm f;
            bool ok;
            try
            {
                f = open = new SettingsForm(IsAutostart(), () => InstallPackage(mayPrompt: true),
                    status => CheckForUpdates(manual: true, status));
                ok = f.ShowDialog() == DialogResult.OK;
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(Identity.DataDir, "error.log"), $"{DateTime.Now:s} Settings: {ex}\n");
                Notify($"Couldn't open Settings: {ex.Message}", true);
                return;
            }
            finally { open?.Dispose(); open = null; }
            if (!ok) return;
            var s = Settings.Current;
            s.Alias = f.DeviceName == Environment.MachineName ? null : f.DeviceName;
            s.SaveDir = f.SaveDir == Settings.DefaultSaveDir ? null : f.SaveDir;
            s.ReceiveMode = f.ReceiveMode;
            s.Corner = f.Corner;
            s.Monitor = f.Monitor;
            s.Sound = f.Sound;
            s.AutoUpdate = f.AutoUpdate;
            s.DoNotDisturb = f.DoNotDisturb;
            s.AutoQuiet = f.AutoQuiet;
            s.Theme = f.Theme;
            quiet.Checked = s.DoNotDisturb;
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
        menu.Items.Add(quiet);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Check for updates", null, (_, _) => _ = CheckForUpdates(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(thisPc);
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) panel.ShowExpanded(); };

        if (Process.GetProcessesByName("localsend_app").Length > 0)
            Notify("LocalSend is also running. Quit it so files come to LocalSend Drop instead.", true);

        // On a Public network Windows blocks everything incoming: say so, once per network change.
        var publicWarned = false;
        void CheckNetwork()
        {
            var isPublic = NetCheck.OnlyPublic();
            if (isPublic && !publicWarned)
                Notify("This network is set to Public, so other devices can't send to you. Select here, then set it to Private.", true);
            publicWarned = isPublic;
        }
        tray.BalloonTipClicked += (_, _) => { if (publicWarned) NetCheck.OpenSettings(); };
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
            _ = Task.Delay(3000).ContinueWith(_ => panel.BeginInvoke(CheckNetwork));
        _ = Task.Delay(5000).ContinueWith(_ => panel.BeginInvoke(CheckNetwork));

        // Updates: check shortly after start, then every 6 hours; install when idle.
        // status: where a manual check from Settings shows its progress; otherwise notifications.
        async Task CheckForUpdates(bool manual, Action<string>? status = null)
        {
            void Report(string text, bool error = false)
            {
                if (status != null) status(text);
                else if (manual || error) Notify(text, error);
            }
            status?.Invoke("Checking for updates…");
            string? version;
            try { version = await Updater.DownloadIfNewerAsync(); }
            catch (Exception ex)
            {
                if (manual) Report($"Couldn't check for updates: {ex.Message}", true);
                return;
            }
            if (version == null)
            {
                if (manual) Report($"You're up to date. {Updater.CurrentText} is the latest version.");
                return;
            }
            if (panel.Busy || server.Busy) Report($"Version {version} will install when the current transfer finishes");
            while (panel.Busy || server.Busy) await Task.Delay(TimeSpan.FromSeconds(20));
            Report($"Installing {version}. LocalSend Drop will restart.");
            await Task.Delay(1500);
            if (await Updater.ApplyAsync() is { } error) Report($"Couldn't update to {version}: {error}", true);
            else if (!Packaging.IsPackaged) Application.Exit();
        }
        var updateTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds };
        updateTimer.Tick += (_, _) => { if (Settings.Current.AutoUpdate) _ = CheckForUpdates(manual: false); };
        updateTimer.Start();
        // Windows only restarts the app after an update if it ran for a minute or more.
        _ = Task.Delay(TimeSpan.FromSeconds(75)).ContinueWith(_ =>
            panel.BeginInvoke(() => { if (Settings.Current.AutoUpdate) _ = CheckForUpdates(manual: false); }));

        if (updated) Notify($"Updated to {Updater.CurrentText}", false);

        panel.Show();
        Clip.CleanUp();
        Shortcuts = panel.RegisterHotkeys();
        panel.Hotkey += key =>
        {
            if (key == 1) Clip.SendClipboard(panel, Notify);
            else if (key == 2) Clip.Screenshot(panel, Notify);
        };
        if (share != null) panel.BeginInvoke(() => panel.ShowShare(share));
        else if (shareText != null) panel.BeginInvoke(() => panel.ShowShareText(shareText));
        Application.Run();
        tray.Visible = false;
        discovery.Dispose();
        server.DisposeAsync().AsTask().Wait(2000);
        return 0;
    }

    /// <summary>Whether Ctrl+Alt+S and Ctrl+Alt+Shift+S could be registered (another app may own them).</summary>
    public static (bool Clipboard, bool Screenshot) Shortcuts { get; private set; }

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
        if (on) k.SetValue(RunName, Packaging.LaunchCommand);
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
