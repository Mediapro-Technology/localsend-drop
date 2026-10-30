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

            using (var panel = new DropPanel(id, discovery, (_, _) => { }) { Opacity = 0 })
            {
                panel.Show();
                panel.ShowExpanded();
                Save(panel, Path.Combine(folder, $"panel-{mode}.png"));
            }

            using (var card = new IncomingCard { Opacity = 0 })
            {
                var request = new IncomingRequest
                {
                    SessionId = "s", SenderAlias = "HARSHID-PC", SenderIp = "127.0.0.1", SenderFingerprint = "A1",
                    Files = [new("1", "Site survey photos.zip", 48_200_000, null, null), new("2", "Floor plan v3.pdf", 2_100_000, null, null)],
                };
                _ = card.Ask(request, CancellationToken.None);
                Save(card, Path.Combine(folder, $"card-{mode}.png"));
                card.Receiving(request);
                card.OnProgress("s", 21_000_000, request.TotalSize);
                Save(card, Path.Combine(folder, $"card-receiving-{mode}.png"));
            }

            using (var settings = new SettingsForm(true, () => Task.FromResult(false)) { Opacity = 0 })
            {
                settings.Show();
                Save(settings, Path.Combine(folder, $"settings-{mode}.png"));
            }
        }
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
