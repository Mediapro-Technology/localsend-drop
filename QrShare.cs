using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace LocalSendDrop;

/// <summary>
/// Sharing with a phone (or anyone) that doesn't have LocalSend: a QR code opens a
/// download page in their browser. Plain HTTP on the local network, so phones get no
/// certificate warning; each share has a random link that stops working when its
/// window closes (or after an hour).
/// </summary>
static class QrShare
{
    public sealed class Share
    {
        public required string Token { get; init; }
        public required string From { get; init; }
        public required List<SendItem> Items { get; init; }
        public string? Text { get; init; }
        /// <summary>An inbox: the phone sends files to this PC instead.</summary>
        public bool Upload { get; init; }
        public readonly List<string> Received = [];
        public event Action<string>? FileReceived;
        public void OnReceived(string path) { lock (Received) Received.Add(path); FileReceived?.Invoke(path); }
        public DateTime Expires { get; init; } = DateTime.UtcNow.AddHours(1);
        public int Downloads;
        public event Action? Downloaded;
        public void OnDownloaded() { Interlocked.Increment(ref Downloads); Downloaded?.Invoke(); }
    }

    static readonly Dictionary<string, Share> Shares = [];

    /// <summary>Port of the plain-HTTP listener (set by Server).</summary>
    public static int Port { get; set; }

    public static Share Start(string from, List<SendItem> items, string? text, bool upload = false)
    {
        var share = new Share
        {
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            From = from, Items = items, Text = text, Upload = upload,
        };
        lock (Shares) Shares[share.Token] = share;
        return share;
    }

    public static void Stop(Share share) { lock (Shares) Shares.Remove(share.Token); }

    static Share? Find(string token)
    {
        lock (Shares)
        {
            if (!Shares.TryGetValue(token, out var s)) return null;
            if (DateTime.UtcNow > s.Expires) { Shares.Remove(token); return null; }
            return s;
        }
    }

    public static string Url(Share s) => $"http://{LanAddress()}:{Port}/s/{s.Token}";

    /// <summary>The address phones on the same Wi-Fi can reach: the adapter with a gateway, not a virtual one.</summary>
    static string LanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                        !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            .Select(n => (Props: n.GetIPProperties(), Wifi: n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .OrderByDescending(x => x.Wifi)
            .SelectMany(x => x.Props.UnicastAddresses.Select(a => a.Address))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork);
        return (candidates.FirstOrDefault() ?? IPAddress.Loopback).ToString();
    }

    // ---- web routes (registered by Server) ----

    public static IResult Page(string token)
    {
        if (Find(token) is not { } s) return Results.Content(Html("Link expired", "<p class=muted>Ask them to share it again.</p>"), "text/html; charset=utf-8");
        if (s.Upload) return Results.Content(Html($"Send to {s.From}", UploadBody(s.Token)), "text/html; charset=utf-8");
        var body = new System.Text.StringBuilder();
        if (s.Text != null)
        {
            body.Append($"<textarea id=t readonly>{WebUtility.HtmlEncode(s.Text)}</textarea>");
            body.Append("<button onclick=\"var t=document.getElementById('t');t.select();document.execCommand('copy');this.textContent='Copied'\">Copy text</button>");
        }
        for (var i = 0; i < s.Items.Count; i++)
        {
            var item = s.Items[i];
            body.Append($"<a class=file href=\"/s/{s.Token}/f/{i}\" download><span class=name>{WebUtility.HtmlEncode(item.Name)}</span>" +
                        $"<span class=muted>{IncomingCard.FormatSize(item.Size)}</span></a>");
        }
        if (s.Items.Count > 1)
            body.Append($"<a class=all href=\"/s/{s.Token}/all.zip\" download>Download all ({IncomingCard.FormatSize(s.Items.Sum(i => i.Size))})</a>");
        var title = s.Text != null && s.Items.Count == 0 ? $"Text from {s.From}" : $"Files from {s.From}";
        return Results.Content(Html(title, body.ToString()), "text/html; charset=utf-8");
    }

    public static IResult File(string token, int index)
    {
        if (Find(token) is not { } s || index < 0 || index >= s.Items.Count || s.Items[index].Path is not { } path) return Results.NotFound();
        s.OnDownloaded();
        return Results.File(path, "application/octet-stream", Path.GetFileName(s.Items[index].Name), enableRangeProcessing: true);
    }

    public static async Task All(HttpContext ctx, string token)
    {
        if (Find(token) is not { } s) { ctx.Response.StatusCode = 404; return; }
        s.OnDownloaded();
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"Files from {s.From}.zip\"";
        // ZipArchive finishes with synchronous writes.
        if (ctx.Features.Get<IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        using var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var item in s.Items.Where(i => i.Path != null))
        {
            var entry = zip.CreateEntry(item.Name, CompressionLevel.NoCompression);
            await using var src = System.IO.File.OpenRead(item.Path!);
            await using var dst = entry.Open();
            await src.CopyToAsync(dst, ctx.RequestAborted);
        }
    }

    /// <summary>Saves one file a phone uploads to an inbox share (POST /u/{token}?name=…).</summary>
    public static async Task<IResult> Receive(HttpContext ctx, string token, Func<string, Stream, CancellationToken, Task<string>> save)
    {
        if (Find(token) is not { Upload: true } s) return Results.NotFound();
        var name = ctx.Request.Query["name"].ToString();
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        var path = await save(Path.GetFileName(name), ctx.Request.Body, ctx.RequestAborted);
        s.OnReceived(path);
        return Results.Ok();
    }

    static string UploadBody(string token) => $$$"""
        <label class=all for=f>Choose photos or files</label>
        <input id=f type=file multiple hidden>
        <div id=list></div>
        <script>
        const list = document.getElementById('list');
        document.getElementById('f').addEventListener('change', async e => {
          for (const file of e.target.files) {
            const row = document.createElement('div'); row.className = 'file';
            row.innerHTML = '<span class=name></span><span class=muted>0%</span>';
            row.firstChild.textContent = file.name; list.appendChild(row);
            await new Promise(done => {
              const x = new XMLHttpRequest();
              x.open('POST', '/u/{{{token}}}?name=' + encodeURIComponent(file.name));
              x.upload.onprogress = p => { if (p.lengthComputable) row.lastChild.textContent = Math.round(p.loaded * 100 / p.total) + '%'; };
              x.onload = () => { row.lastChild.textContent = x.status == 200 ? 'Sent' : 'Failed'; done(); };
              x.onerror = () => { row.lastChild.textContent = 'Failed'; done(); };
              x.send(file);
            });
          }
          e.target.value = '';
        });
        </script>
        """;

    static string Html(string title, string body) => $$$"""
        <!doctype html><html lang=en><head><meta charset=utf-8>
        <meta name=viewport content="width=device-width,initial-scale=1">
        <title>{{{WebUtility.HtmlEncode(title)}}}</title>
        <style>
        :root{color-scheme:light dark;--bg:#f3f3f3;--card:#fbfbfb;--line:#e5e5e5;--text:#1b1b1b;--muted:#5d5d5d;--accent:#005fb8;--on:#fff}
        @media (prefers-color-scheme:dark){:root{--bg:#202020;--card:#2b2b2b;--line:#1d1d1d;--text:#fff;--muted:#c8c8c8;--accent:#60cdff;--on:#000}}
        body{margin:0;background:var(--bg);color:var(--text);font:16px/1.4 "Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,sans-serif}
        main{max-width:520px;margin:0 auto;padding:24px 16px}
        h1{font-size:24px;font-weight:600;margin:0 0 16px}
        .file{display:flex;justify-content:space-between;gap:12px;padding:14px 16px;margin:0 0 4px;background:var(--card);border:1px solid var(--line);border-radius:6px;color:inherit;text-decoration:none}
        .name{overflow-wrap:anywhere}.muted{color:var(--muted);white-space:nowrap}
        .all,button{display:block;width:100%;box-sizing:border-box;margin:12px 0 0;padding:12px;border:0;border-radius:6px;background:var(--accent);color:var(--on);font:inherit;text-align:center;text-decoration:none}
        textarea{width:100%;box-sizing:border-box;min-height:140px;padding:12px;border:1px solid var(--line);border-radius:6px;background:var(--card);color:inherit;font:inherit}
        footer{margin-top:24px;color:var(--muted);font-size:13px}
        </style></head><body><main><h1>{{{WebUtility.HtmlEncode(title)}}}</h1>{{{body}}}
        <footer>Shared with LocalSend Drop over your local network.</footer></main></body></html>
        """;
}
