using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalSendDrop;

/// <summary>
/// Self-update from the public GitHub releases of Mediapro-Technology/localsend-drop.
/// The installed package downloads the newer LocalSendDrop.msix and hands it to
/// Windows, which closes and restarts the app. A plain .exe (1.x) downloads the newer
/// LocalSendDrop.exe and swaps it in; that 2.x .exe then moves itself into the package.
/// Every download is checked against GitHub's SHA-256 digest.
/// </summary>
static class Updater
{
    const string LatestUrl = "https://api.github.com/repos/Mediapro-Technology/localsend-drop/releases/latest";
    static string AssetName => Packaging.IsPackaged ? "LocalSendDrop.msix" : "LocalSendDrop.exe";

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    static string Exe => Environment.ProcessPath!;
    static string OldExe => Path.ChangeExtension(Exe, ".old.exe");
    static string NewExe => Path.ChangeExtension(Exe, ".new.exe");
    static string NewMsix => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalSendDrop", "update", "LocalSendDrop.msix");
    static string Downloaded => Packaging.IsPackaged ? NewMsix : NewExe;

    /// <summary>Removes the previous version left behind by the last update.</summary>
    public static void CleanUp()
    {
        if (Packaging.IsPackaged)
        {
            try { if (File.Exists(NewMsix)) File.Delete(NewMsix); } catch { }
            return;
        }
        try { if (File.Exists(OldExe)) File.Delete(OldExe); } catch { }
        try { if (File.Exists(NewExe)) File.Delete(NewExe); } catch { }
    }

    /// <summary>
    /// Returns the newer version if one was downloaded and is ready to apply,
    /// null if already up to date. Throws on network or verification errors.
    /// </summary>
    public static async Task<string?> DownloadIfNewerAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocalSendDrop/{CurrentText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestUrl, ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        if (Normalize(latest) <= Normalize(Current)) return null;

        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
        if (asset.ValueKind == JsonValueKind.Undefined) return null;
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;

        var tmp = Downloaded + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        await using (var src = await http.GetStreamAsync(url, ct))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct);

        var info = new FileInfo(tmp);
        if (info.Length != size) { File.Delete(tmp); throw new InvalidDataException("Download was incomplete"); }
        if (digest is { } d && d.StartsWith("sha256:"))
        {
            await using var fs = File.OpenRead(tmp);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(d[7..], StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(tmp);
                throw new InvalidDataException("Download didn't match the release checksum");
            }
        }
        File.Move(tmp, Downloaded, overwrite: true);
        return $"{latest.Major}.{latest.Minor}.{Math.Max(0, latest.Build)}";
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>
    /// Installs the download. Packaged: Windows closes this process and restarts it,
    /// so this only returns on failure. Plain .exe: swaps the file and starts it;
    /// the caller exits right after.
    /// </summary>
    /// <returns>null when the caller should exit, otherwise why it failed.</returns>
    public static async Task<string?> ApplyAsync()
    {
        if (Packaging.IsPackaged) return await Packaging.InstallAsync(NewMsix);
        return ApplyAndRestart() ? null : "this folder can't be written to; move LocalSendDrop.exe somewhere like C:\\Tools";
    }

    static bool ApplyAndRestart()
    {
        if (!File.Exists(NewExe)) return false;
        try
        {
            if (File.Exists(OldExe)) File.Delete(OldExe);
            File.Move(Exe, OldExe);
            File.Move(NewExe, Exe);
        }
        catch
        {
            // e.g. installed somewhere read-only: put things back and carry on.
            try { if (!File.Exists(Exe) && File.Exists(OldExe)) File.Move(OldExe, Exe); } catch { }
            return false;
        }
        Process.Start(new ProcessStartInfo(Exe, "--updated") { UseShellExecute = false });
        return true;
    }
}
