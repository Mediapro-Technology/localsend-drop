using System.Net;

namespace LocalSendDrop;

/// <summary>
/// Diagnostics: `LocalSendDrop.exe --snapshot <folder>` renders the panel, the
/// incoming notification and Settings, in light and dark, with sample devices,
/// to PNGs. Windows are drawn invisibly, so nothing flashes on screen.
/// </summary>
static class Snapshot
{
    public static int Run(string folder)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(folder);
        foreach (var dark in new[] { false, true })
        {
            Ui.Force(dark);
            var mode = dark ? "dark" : "light";
            var id = Identity.LoadOrCreate();
            using var discovery = new Discovery(id);
            discovery.Upsert(new DeviceInfo { Alias = "HARSHID-PC", DeviceModel = "Windows", DeviceType = "desktop", Fingerprint = "A1" }, IPAddress.Loopback);
            discovery.Upsert(new DeviceInfo { Alias = "Sara's Pixel 8", DeviceModel = "Google", DeviceType = "mobile", Fingerprint = "B2" }, IPAddress.Loopback);
            discovery.Upsert(new DeviceInfo { Alias = "Studio iMac", DeviceModel = "Mac", DeviceType = "desktop", Fingerprint = "C3" }, IPAddress.Loopback);

            // Sample nickname and pin, in memory only (never saved).
            Settings.Current.Nicknames["C3"] = "Design studio";
            if (!Settings.Current.Favorites.Contains("C3")) Settings.Current.Favorites.Add("C3");

            using (var panel = new DropPanel(id, discovery, (_, _) => { }) { Opacity = 0 })
            {
                panel.Show();
                panel.ShowExpanded();
                Save(panel, Path.Combine(folder, $"panel-{mode}.png"));
                panel.ShowShareText("Meeting moved to 3 pm, room B");
                Save(panel, Path.Combine(folder, $"panel-holding-{mode}.png"));
            }

            using (var card = new IncomingCard { Opacity = 0 })
            {
                // Real thumbnails, made from the pictures this run already saved.
                var pics = Directory.GetFiles(folder, "panel-*.png").Take(2).ToList();
                var request = new IncomingRequest
                {
                    SessionId = "s", SenderAlias = "HARSHID-PC", SenderIp = "127.0.0.1", SenderFingerprint = "A1",
                    Files =
                    [
                        new("1", "Site survey 01.png", 4_200_000, null, null, pics.Count > 0 ? Thumbs.DataUri(pics[0]) : null),
                        new("2", "Site survey 02.png", 3_900_000, null, null, pics.Count > 1 ? Thumbs.DataUri(pics[1]) : null),
                        new("3", "Floor plan v3.pdf", 2_100_000, null, null),
                    ],
                };
                _ = card.Ask(request, CancellationToken.None);
                Save(card, Path.Combine(folder, $"card-{mode}.png"));
                card.Receiving(request);
                Thread.Sleep(1200); // so there's a speed to show
                card.OnProgress("s", 4_800_000, request.TotalSize);
                Save(card, Path.Combine(folder, $"card-receiving-{mode}.png"));
                card.OnFileRequest(new FileRequest("HARSHID-PC", "A1", "127.0.0.1", 53317, "The signed contract from yesterday"));
                Save(card, Path.Combine(folder, $"card-request-{mode}.png"));
            }

            if (Notes.For("A1").Count == 0)
            {
                Notes.Add("A1", fromMe: false, "Can you send me the floor plan?");
                Notes.Add("A1", fromMe: true, "Sure, sending it now. It's the v3 with the new stairs.");
                Notes.Add("A1", fromMe: false, "Got it, thanks!");
            }
            using (var qr = new QrForm(QrShare.Start("Rizwan PC", Directory.GetFiles(folder, "panel-*.png").Take(2)
                       .Select(f => new SendItem(Path.GetFileName(f), f, null)).ToList(), null)) { Opacity = 0 })
            {
                qr.Show();
                Save(qr, Path.Combine(folder, $"qr-{mode}.png"));
            }

            using (var notes = new NotesForm("A1", "HARSHID-PC", _ => Task.FromResult<string?>(null)) { Opacity = 0 })
            {
                notes.Show();
                Save(notes, Path.Combine(folder, $"notes-{mode}.png"));
            }

            using (var settings = new SettingsForm(true, () => Task.FromResult(false), _ => Task.CompletedTask) { Opacity = 0 })
            {
                settings.Show();
                Save(settings, Path.Combine(folder, $"settings-{mode}.png"));
                settings.Controls.OfType<ScrollPanel>().First().AutoScrollPosition = new Point(0, 100_000);
                settings.Refresh();
                Save(settings, Path.Combine(folder, $"settings-bottom-{mode}.png"));
            }
        }
        return 0;
    }

    /// <summary>Testing: `--qr-serve <file>...` shares files by QR link, prints it, serves for a minute.</summary>
    public static int QrServe(string[] paths)
    {
        var id = Identity.LoadOrCreate();
        using var discovery = new Discovery(id);
        var server = new Server(id, discovery);
        server.StartAsync().GetAwaiter().GetResult();
        var share = QrShare.Start(id.Alias, Sender.FromPaths(paths), "Hello from the QR test");
        Console.WriteLine(QrShare.Url(share));
        Console.Out.Flush();
        Thread.Sleep(60_000);
        Console.WriteLine($"downloads: {share.Downloads}");
        server.DisposeAsync().AsTask().Wait(2000);
        return 0;
    }

    static void Save(Control c, string path)
    {
        Application.DoEvents();
        using var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
