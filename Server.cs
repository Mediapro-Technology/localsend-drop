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

sealed record IncomingFile(string Id, string FileName, long Size, string? FileType, string? Preview, string? Thumbnail = null);

/// <summary>A transfer someone wants to send us; the UI accepts or declines it.</summary>
sealed class IncomingRequest
{
    public required string SessionId { get; init; }
    public required string SenderAlias { get; init; }
    public required string SenderIp { get; init; }
    public required string SenderFingerprint { get; init; }
    public required List<IncomingFile> Files { get; init; }
    /// <summary>The sender's little mouth colour, if they use one.</summary>
    public Color? SenderCreature { get; init; }
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
    public event Action<string, string, string>? MessageReceived;   // alias, fingerprint, text
    /// <summary>Another LocalSend Drop PC asks us to send it a file.</summary>
    public event Action<FileRequest>? FileRequested;
    public event Action<string, string>? Waved;                     // alias, fingerprint
    public event Action<ScreenOffer>? ScreenOffered;
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
        /// <summary>Files being written: where to, and how much has arrived (kept across reconnects).</summary>
        public readonly Dictionary<string, Partial> Partials = [];
        /// <summary>Running while we wait for a dropped sender to reconnect.</summary>
        public CancellationTokenSource? Grace;
    }

    sealed class Partial
    {
        public required string Target, Part;
        public long Written;
        public int Attempt;         // newest upload request for this file; older ones stand down
        public bool Writing;
    }

    /// <summary>Testing: LSDROP_TEST_DROP_AT=bytes makes the receiver cut the first upload there, once.</summary>
    static readonly long TestDropAt = long.TryParse(Environment.GetEnvironmentVariable("LSDROP_TEST_DROP_AT"), out var at) ? at : -1;
    static int _testDropped;

    /// <summary>How long a dropped transfer may take to reconnect before it's given up.</summary>
    static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(2);

    /// <summary>The sender lost the connection; the transfer resumes if it comes back.</summary>
    public event Action<string>? Reconnecting;

    static int FreePort(int from = 53317)
    {
        for (var port = from; port < 53340; port++)
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
        // Plain HTTP for QR shares: phones open it in a browser without certificate warnings.
        QrShare.Port = FreePort(Math.Max(53330, Port + 1));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = null;       // files can be any size
            o.Limits.MinRequestBodyDataRate = null;   // slow phones on bad Wi-Fi
            o.Listen(IPAddress.Any, QrShare.Port);
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
        app.MapGet("/s/{token}", (Func<string, IResult>)(token => QrShare.Page(token)));
        app.MapPost("/u/{token}", (Func<HttpContext, string, Task<IResult>>)((ctx, token) => QrShare.Receive(ctx, token, SaveAsync)));
        app.MapGet("/s/{token}/f/{index:int}", (Func<string, int, IResult>)((token, index) => QrShare.File(token, index)));
        app.MapGet("/s/{token}/all.zip", (Func<HttpContext, string, Task>)((ctx, token) => QrShare.All(ctx, token)));
        app.MapGet("/api/localsend-drop/v1/resume", (Func<HttpContext, IResult>)(ctx => ResumeOffset(ctx)));
        app.MapPost("/api/localsend-drop/v1/request", (Func<HttpContext, Task<IResult>>)(ctx => Request(ctx)));
        app.MapPost("/api/localsend-drop/v1/wave", (Func<HttpContext, Task<IResult>>)(ctx => Wave(ctx)));
        app.MapPost("/api/localsend-drop/v1/screen/offer", (Func<HttpContext, Task<IResult>>)(ctx => ScreenOffer(ctx)));
        app.MapGet("/api/localsend-drop/v1/screen", (Func<HttpContext, Task>)(ctx => ScreenShare.Stream(ctx)));

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

    /// <summary>LocalSend Drop extension: "please send me a file".</summary>
    async Task<IResult> Request(HttpContext ctx)
    {
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch { return Results.BadRequest(); }
        if (Quiet.Active) return Results.StatusCode(423);
        string? Str(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var claimed = Str("fingerprint") ?? "";
        var fingerprint = ctx.Connection.ClientCertificate is { } cc ? Identity.FingerprintOf(cc) : claimed;
        var port = body.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : 53317;
        var note = Str("note");
        FileRequested?.Invoke(new FileRequest(Str("alias") ?? "Someone", fingerprint, RemoteIp(ctx), port,
            note is { Length: > 0 } ? note[..Math.Min(note.Length, 200)] : null));
        return Results.Ok();
    }

    async Task<IResult> Wave(HttpContext ctx)
    {
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch { return Results.BadRequest(); }
        if (Quiet.Active) return Results.StatusCode(423);
        string? Str(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var fingerprint = ctx.Connection.ClientCertificate is { } cc ? Identity.FingerprintOf(cc) : Str("fingerprint") ?? "";
        var alias = Str("alias") ?? "Someone";
        Waved?.Invoke(alias[..Math.Min(alias.Length, 60)], fingerprint);
        return Results.Ok();
    }

    async Task<IResult> ScreenOffer(HttpContext ctx)
    {
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch { return Results.BadRequest(); }
        if (Quiet.Active) return Results.StatusCode(423);
        string? Str(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var token = Str("token");
        if (token is not { Length: > 16 }) return Results.BadRequest();
        var fingerprint = ctx.Connection.ClientCertificate is { } cc ? Identity.FingerprintOf(cc) : Str("fingerprint") ?? "";
        var port = body.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : 53317;
        var alias = Str("alias") ?? "Someone";
        ScreenOffered?.Invoke(new ScreenOffer(alias[..Math.Min(alias.Length, 60)], fingerprint, RemoteIp(ctx), port, token));
        return Results.Ok();
    }

    static Color? ParseColor(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); } catch { return null; }
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
                f.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null,
                f.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.String ? th.GetString() : null));
        }
        if (files.Count == 0) return Results.BadRequest();

        // A text message: show it, nothing to upload.
        if (files.Count == 1 && files[0].Preview != null && (files[0].FileType ?? "").StartsWith("text/"))
        {
            MessageReceived?.Invoke(alias, fingerprint, files[0].Preview!);
            return Results.NoContent();
        }

        var request = new IncomingRequest
        {
            SessionId = Guid.NewGuid().ToString("N"),
            SenderAlias = alias,
            SenderIp = RemoteIp(ctx),
            SenderFingerprint = fingerprint,
            Files = files,
            SenderCreature = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("creature", out var cr)
                && cr.ValueKind == JsonValueKind.String && cr.GetString() is { Length: 7 } hex && hex[0] == '#'
                ? ParseColor(hex) : null,
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

    /// <summary>
    /// Receives one file. With ?offset=N (LocalSend Drop senders, after a dropped
    /// connection) it continues the partial file from byte N instead of starting over.
    /// </summary>
    async Task<IResult> Upload(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        string sessionId = q["sessionId"]!, fileId = q["fileId"]!, token = q["token"]!;
        long.TryParse(q["offset"], out var offset);
        Session? s;
        IncomingFile file;
        Partial partial;
        int attempt;
        HttpContext? stale;
        lock (_lock)
        {
            s = _session;
            if (s == null || s.Request.SessionId != sessionId || s.Request.SenderIp != RemoteIp(ctx) ||
                !s.Files.TryGetValue(fileId ?? "", out var entry) || entry.Token != token || s.Done.Contains(fileId!))
                return Results.StatusCode(403);
            file = entry.File;
            if (!s.Partials.TryGetValue(fileId!, out partial!))
            {
                var target = SafeTarget(file.FileName);
                s.Partials[fileId!] = partial = new Partial { Target = target, Part = target + ".part" };
            }
            attempt = ++partial.Attempt;
            stale = partial.Writing ? s.Current : null;   // a half-dead earlier connection
            s.Current = ctx;
            s.Grace?.Cancel();
            s.Grace = null;
        }
        try { stale?.Abort(); } catch { }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partial.Target)!);
            FileStream? fs = null;
            // The stale connection may still hold the file for a moment.
            for (var i = 0; fs == null; i++)
            {
                try { fs = new FileStream(partial.Part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16, true); }
                catch (IOException) when (i < 20) { await Task.Delay(150, ctx.RequestAborted); }
            }
            await using (fs)
            {
                offset = Math.Clamp(offset, 0, Math.Min(fs.Length, partial.Written));
                lock (_lock)
                {
                    s.Received -= partial.Written - offset;   // anything past the offset is sent again
                    partial.Written = offset;
                    partial.Writing = true;
                }
                fs.SetLength(offset);
                fs.Seek(offset, SeekOrigin.Begin);

                var buf = new byte[1 << 16];
                int n;
                long lastReport = 0;
                while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                    long received;
                    lock (_lock) { partial.Written += n; received = s.Received += n; }
                    if (TestDropAt > 0 && partial.Written > TestDropAt && Interlocked.Exchange(ref _testDropped, 1) == 0) ctx.Abort();
                    if (received - lastReport > 256 * 1024)
                    {
                        lastReport = received;
                        Progress?.Invoke(sessionId, received, s.Request.TotalSize);
                    }
                }
            }
            if (partial.Written < file.Size) throw new IOException("The connection closed before the file was complete");
            File.Move(partial.Part, partial.Target);
        }
        catch (Exception ex)
        {
            bool superseded, alive;
            lock (_lock)
            {
                partial.Writing = false;
                superseded = partial.Attempt != attempt;   // a reconnect already took over
                alive = _session == s;
            }
            if (superseded || !alive) return Results.StatusCode(500);

            if (ex is OperationCanceledException or IOException or BadHttpRequestException)
            {
                // Keep what arrived and give the sender a couple of minutes to come back.
                var grace = new CancellationTokenSource();
                lock (_lock) { s.Grace?.Cancel(); s.Grace = grace; }
                Reconnecting?.Invoke(sessionId);
                _ = Task.Delay(ResumeWindow, grace.Token).ContinueWith(t =>
                {
                    if (!t.IsCanceled) End(s, "The connection was lost");
                }, TaskScheduler.Default);
            }
            else End(s, ex.Message);
            return Results.StatusCode(500);
        }

        bool all;
        lock (_lock)
        {
            partial.Writing = false;
            s.Partials.Remove(fileId!);
            s.Done.Add(fileId!);
            s.Saved.Add(partial.Target);
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

    /// <summary>Saves a file uploaded from a phone's browser (QR inbox) into the save folder.</summary>
    async Task<string> SaveAsync(string name, Stream body, CancellationToken ct)
    {
        string target;
        lock (_lock) target = SafeTarget(name);
        var part = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                await body.CopyToAsync(fs, ct);
            File.Move(part, target);
            return target;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>LocalSend Drop extension: how much of a file already arrived, to resume from.</summary>
    IResult ResumeOffset(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        lock (_lock)
        {
            var s = _session;
            if (s == null || s.Request.SessionId != q["sessionId"] || s.Request.SenderIp != RemoteIp(ctx) ||
                !s.Files.TryGetValue(q["fileId"].ToString(), out var entry) || entry.Token != q["token"])
                return Results.NotFound();
            var written = s.Partials.TryGetValue(q["fileId"].ToString(), out var p) ? p.Written : 0;
            return Results.Json(new { offset = written });
        }
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
        s.Grace?.Cancel();
        try { s.Current?.Abort(); } catch { }
        // Partial files are useless once the transfer is over.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            foreach (var part in s.Partials.Values)
                try { File.Delete(part.Part); } catch { }
        });
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
