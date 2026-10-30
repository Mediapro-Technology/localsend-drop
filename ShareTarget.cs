using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace LocalSendDrop;

/// <summary>
/// "LocalSend Drop" in Windows' Share sheet (Explorer, Edge, Photos, Snipping Tool…).
/// Windows starts a new copy of the app for each share; it reads what was shared
/// and hands it to the running copy like a right-click "Send with".
/// </summary>
static class ShareTarget
{
    public sealed record Shared(List<string> Paths, string? Text);

    /// <summary>What was shared, if this process was started from the Share sheet.</summary>
    public static Shared? Read()
    {
        if (!Packaging.IsPackaged) return null;
        try { return Task.Run(ReadAsync).GetAwaiter().GetResult(); }
        catch { return null; }
    }

    static async Task<Shared?> ReadAsync()
    {
        if (AppInstance.GetActivatedEventArgs() is not ShareTargetActivatedEventArgs args) return null;
        var op = args.ShareOperation;
        var data = op.Data;
        var paths = new List<string>();
        string? text = null;
        try
        {
            op.ReportStarted();
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                foreach (var item in await data.GetStorageItemsAsync())
                {
                    if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
                    else if (item is StorageFile file) paths.Add(await CopyOut(file));   // e.g. from an app's virtual folder
                }
            }
            else if (data.Contains(StandardDataFormats.Bitmap))
            {
                var bitmap = await data.GetBitmapAsync();
                using var stream = await bitmap.OpenReadAsync();
                var ext = stream.ContentType.Contains("jpeg") ? ".jpg" : ".png";
                var path = Path.Combine(Captures, $"Shared image {DateTime.Now:yyyy-MM-dd HHmmss}{ext}");
                Directory.CreateDirectory(Captures);
                await using (var fs = File.Create(path)) await stream.AsStreamForRead().CopyToAsync(fs);
                paths.Add(path);
            }
            else if (data.Contains(StandardDataFormats.WebLink)) text = (await data.GetWebLinkAsync()).ToString();
            else if (data.Contains(StandardDataFormats.Uri)) text = (await data.GetUriAsync()).ToString();
            else if (data.Contains(StandardDataFormats.Text)) text = await data.GetTextAsync();
        }
        finally { op.ReportCompleted(); }
        return paths.Count > 0 || text != null ? new Shared(paths, text) : null;
    }

    static string Captures => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalSendDrop", "Captures");

    static async Task<string> CopyOut(StorageFile file)
    {
        Directory.CreateDirectory(Captures);
        var folder = await StorageFolder.GetFolderFromPathAsync(Captures);
        var copy = await file.CopyAsync(folder, file.Name, NameCollisionOption.GenerateUniqueName);
        return copy.Path;
    }
}
