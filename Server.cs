using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace LocalSendDrop;

sealed record IncomingFile(string Id, string FileName, long Size, string? FileType, string? Preview);

/// <summary>A transfer someone wants to send us; the UI accepts or declines it.</summary>
sealed class IncomingRequest
{
    public required string SessionId { get; init; }
    public required string SenderAlias { get; init; }
    public required string SenderIp { get; init; }
    public required string SenderFingerprint { get; init; }
    public required List<IncomingFile> Files { get; init; }
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>
/// LocalSend v2 receiver (HTTPS, our own certificate), so the LocalSend app
/// isn't needed on this PC. Files are saved to Downloads, keeping folder structure.
/// </summary>
sealed class Server(Identity id, Discovery discovery) : IAsyncDisposable
{
    WebApplication? _app;
    Session? _session;
    readonly object _lock = new();

    public int Port { get; private set; }
    public string SaveDir => Settings.Current.SaveDir is { Length: > 0 } dir ? dir : Settings.DefaultSaveDir;

    /// <summary>Asks the user; true = accept. Called on a background thread.</summary>
    public Func<IncomingRequest, CancellationToken, Task<bool>>? AskAccept;
    public event Action<string, long, long>? Progress;              // sessionId, received, total
    public event Action<string, List<string>>? Finished;            // sessionId, saved paths
    public event Action<string, string>? Failed;                    // sessionId, reason
    public event Action<string, string>? MessageReceived;           // alias, text
    /// <summary>A transfer ended: saved paths on success, or a reason on failure.</summary>
    public event Action<IncomingRequest, List<string>, string?>? Ended;

    /// <summary>True while a transfer is being asked about or received.</summary>
    public bool Busy { get { lock (_lock) return _session != null; } }

    sealed class Session
    {
        public required IncomingRequest Request;
        public required Dictionary<string, (IncomingFile File, string Token)> Files;
        public readonly HashSet<string> Done = [];
        public readonly List<string> Saved = [];
        public long Received;
        public HttpContext? Current;   // the upload in flight, aborted on cancel
    }

    static int FreePort()
    {
        for (var port = 53317; port < 53340; port++)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No free port between 53317 and 53339.");
    }

    public async Task StartAsync()
    {
        Port = FreePort();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = null;       // files can be any size
            o.Limits.MinRequestBodyDataRate = null;   // slow phones on bad Wi-Fi
            o.Listen(IPAddress.Any, Port, l => l.UseHttps(h =>
            {
                h.ServerCertificate = id.Certificate;
                // Newer LocalSend senders present a certificate, older ones don't.
                h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                h.ClientCertificateValidation = (_, _, _) => true;
            }));
        });
        var app = builder.Build();

        foreach (var v in new[] { "v1", "v2" })
        {
            app.MapGet($"/api/localsend/{v}/info", () => Results.Json(discovery.OurInfo(), Json.Options));
            app.MapPost($"/api/localsend/{v}/register", (Func<HttpContext, Task<IResult>>)(ctx => Register(ctx)));
        }
        app.MapPost("/api/localsend/v2/prepare-upload", (Func<HttpContext, Task<IResult>>)(ctx => PrepareUpload(ctx)));
        app.MapPost("/api/localsend/v2/upload", (Func<HttpContext, Task<IResult>>)(ctx => Upload(ctx)));
        app.MapPost("/api/localsend/v2/cancel", (Func<HttpContext, IResult>)(ctx => Cancel(ctx)));

        await app.StartAsync();
        _app = app;
    }

    static string RemoteIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    async Task<IResult> Register(HttpContext ctx)
    {
        var info = await JsonSerializer.DeserializeAsync<DeviceInfo>(ctx.Request.Body, Json.Options);
        if (info != null && !string.IsNullOrEmpty(info.Fingerprint))
            discovery.Upsert(info, IPAddress.Parse(RemoteIp(ctx)));
        return Results.Json(discovery.OurInfo(), Json.Options);
    }

    async Task<IResult> PrepareUpload(HttpContext ctx)
    {
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch { return Results.BadRequest(); }

        var alias = body.TryGetProperty("info", out var info) && info.TryGetProperty("alias", out var a) ? a.GetString() ?? "Someone" : "Someone";
        // Prefer the fingerprint proven by the TLS client certificate over the claimed one.
        var fingerprint = ctx.Connection.ClientCertificate is { } cc ? Identity.FingerprintOf(cc)
            : info.ValueKind == JsonValueKind.Object && info.TryGetProperty("fingerprint", out var fp) ? fp.GetString() ?? "" : "";
        var files = new List<IncomingFile>();
        if (!body.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Object) return Results.BadRequest();
        foreach (var p in filesEl.EnumerateObject())
        {
            var f = p.Value;
            files.Add(new IncomingFile(
                p.Name,
                f.TryGetProperty("fileName", out var n) ? n.GetString() ?? "file" : "file",
                f.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0,
                f.TryGetProperty("fileType", out var t) ? t.GetString() : null,
                f.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null));
        }
        if (files.Count == 0) return Results.BadRequest();

        // A text message: show it, nothing to upload.
        if (files.Count == 1 && files[0].Preview != null && (files[0].FileType ?? "").StartsWith("text/"))
        {
            MessageReceived?.Invoke(alias, files[0].Preview!);
            return Results.NoContent();
        }

        var request = new IncomingRequest
        {
            SessionId = Guid.NewGuid().ToString("N"),
            SenderAlias = alias,
            SenderIp = RemoteIp(ctx),
            SenderFingerprint = fingerprint,
            Files = files,
        };
        lock (_lock)
        {
            if (_session != null) return Results.StatusCode(409);
            _session = new Session { Request = request, Files = [] };  // reserve the slot while asking
        }

        var accepted = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            accepted = AskAccept != null && await AskAccept(request, timeout.Token);
        }
        catch (OperationCanceledException) { }

        if (!accepted || ctx.RequestAborted.IsCancellationRequested)
        {
            lock (_lock) _session = null;
            return Results.StatusCode(403);
        }

        var tokens = files.ToDictionary(f => f.Id, f => (f, Guid.NewGuid().ToString("N")));
        lock (_lock) _session = new Session { Request = request, Files = tokens };
        return Results.Json(new
        {
            sessionId = request.SessionId,
            files = tokens.ToDictionary(kv => kv.Key, kv => kv.Value.Item2),
        });
    }

    async Task<IResult> Upload(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        string sessionId = q["sessionId"]!, fileId = q["fileId"]!, token = q["token"]!;
        Session? s;
        IncomingFile file;
        lock (_lock)
        {
            s = _session;
            if (s == null || s.Request.SessionId != sessionId || s.Request.SenderIp != RemoteIp(ctx) ||
                !s.Files.TryGetValue(fileId ?? "", out var entry) || entry.Token != token || s.Done.Contains(fileId!))
                return Results.StatusCode(403);
            file = entry.File;
            s.Current = ctx;
        }

        var target = SafeTarget(file.FileName);
        var part = target + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buf = new byte[1 << 16];
                int n;
                long lastReport = 0;
                while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                    var received = Interlocked.Add(ref s.Received, n);
                    if (received - lastReport > 256 * 1024)
                    {
                        lastReport = received;
                        Progress?.Invoke(sessionId, received, s.Request.TotalSize);
                    }
                }
            }
            File.Move(part, target);
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            End(s, ex is OperationCanceledException or IOException ? "Transfer interrupted" : ex.Message);
            return Results.StatusCode(500);
        }

        bool all;
        lock (_lock)
        {
            s.Done.Add(fileId!);
            s.Saved.Add(target);
            all = s.Done.Count == s.Files.Count;
            if (all && _session == s) _session = null;
        }
        Progress?.Invoke(sessionId, s.Received, s.Request.TotalSize);
        if (all)
        {
            Finished?.Invoke(sessionId, s.Saved);
            Ended?.Invoke(s.Request, s.Saved, null);
        }
        return Results.Ok();
    }

    IResult Cancel(HttpContext ctx)
    {
        Session? s;
        lock (_lock) s = _session;
        if (s != null && s.Request.SessionId == ctx.Request.Query["sessionId"] && s.Request.SenderIp == RemoteIp(ctx))
            End(s, "Sender cancelled");
        return Results.Ok();
    }

    void End(Session s, string reason)
    {
        lock (_lock) { if (_session == s) _session = null; else return; }
        try { s.Current?.Abort(); } catch { }
        Failed?.Invoke(s.Request.SessionId, reason);
        if (s.Files.Count > 0) Ended?.Invoke(s.Request, s.Saved, reason);
    }

    /// <summary>Called by the UI when the user cancels a running receive.</summary>
    public void CancelCurrent()
    {
        Session? s;
        lock (_lock) s = _session;
        if (s != null) End(s, "Cancelled");
    }

    /// <summary>
    /// Maps a sender-supplied name like "Photos/2024/a.jpg" into SaveDir: invalid
    /// characters and ".." are stripped, and existing files are never overwritten.
    /// </summary>
    string SafeTarget(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.'))
            .Where(p => p.Length > 0 && p != "." && p != "..")
            .ToList();
        if (parts.Count == 0) parts.Add("file");

        var root = Path.GetFullPath(SaveDir);
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(root, parts[^1]);

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; File.Exists(path) || File.Exists(path + ".part"); i++)
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
    }
}
