using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>
/// Ctrl+Alt+S sends whatever was copied (files, an image, text); Ctrl+Alt+Shift+S
/// opens Windows' screen snip and sends the result. Both open the panel with the
/// item in hand, waiting for a device.
/// </summary>
static class Clip
{
    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalSendDrop", "Captures");

    public static void SendClipboard(DropPanel panel, Action<string, bool> notify)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
                panel.ShowShare(Clipboard.GetFileDropList().Cast<string>().ToList());
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                panel.ShowShare([Save(image, "Copied image")]);
            else if (Clipboard.ContainsText())
                panel.ShowShareText(Clipboard.GetText());
            else
                notify("Nothing to send. Copy a file, an image or some text first.", true);
        }
        catch (ExternalException) { notify("Another app is using the clipboard. Try again.", true); }
    }

    public static async void Screenshot(DropPanel panel, Action<string, bool> notify)
    {
        var before = GetClipboardSequenceNumber();
        try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch { notify("Windows' screen snip isn't available on this PC.", true); return; }

        // The snip lands on the clipboard; wait up to a minute for it.
        for (var waited = 0; waited < 60_000; waited += 300)
        {
            await Task.Delay(300);
            if (GetClipboardSequenceNumber() == before) continue;
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    panel.ShowShare([Save(image, "Screenshot")]);
                    return;
                }
            }
            catch (ExternalException) { }
        }
    }

    static string Save(Image image, string name)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"{name} {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    /// <summary>Captures are only needed until they're sent; keep a day's worth.</summary>
    public static void CleanUp()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Folder))
                if (DateTime.Now - File.GetCreationTime(f) > TimeSpan.FromDays(1)) File.Delete(f);
        }
        catch { }
    }
}
