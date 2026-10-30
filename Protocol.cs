using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSendDrop;

// LocalSend protocol v2: https://github.com/localsend/protocol

sealed class DeviceInfo
{
    public string Alias { get; set; } = "";
    public string Version { get; set; } = "2.1";
    public string? DeviceModel { get; set; }
    public string? DeviceType { get; set; }
    public string Fingerprint { get; set; } = "";
    public int Port { get; set; } = 53317;
    public string Protocol { get; set; } = "https";
    public bool Download { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Announce { get; set; }
    /// <summary>LocalSend Drop: the sender's little mouth colour ("#rrggbb"), so it can visit the receiver's screen.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Creature { get; set; }
}

sealed class Device
{
    public required string Fingerprint { get; init; }
    public string Alias { get; set; } = "";
    public string? DeviceModel { get; set; }
    public string? DeviceType { get; set; }
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "https";
    public DateTime LastSeen { get; set; }

    public string Root => $"{Protocol}://{(Ip.Contains(':') ? $"[{Ip}]" : Ip)}:{Port}";
    public string BaseUrl => Root + "/api/localsend/v2";
}

/// <summary>Something to send: a file on disk, or a text message.</summary>
sealed record SendItem(string Name, string? Path, string? Text)
{
    public long Size => Path != null ? new FileInfo(Path).Length : System.Text.Encoding.UTF8.GetByteCount(Text ?? "");
}

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

static class LsHttp
{
    /// <summary>
    /// Client that presents our certificate (receivers may require mTLS) and, for
    /// HTTPS peers, only talks to the certificate whose fingerprint the peer announced.
    /// </summary>
    public static HttpClient Create(Identity id, string? expectedFingerprint, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { id.Certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => id.Certificate,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert != null && (expectedFingerprint == null ||
                        string.Equals(Identity.FingerprintOf(cert), expectedFingerprint, StringComparison.OrdinalIgnoreCase)),
            },
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    public static string? PinFor(Device d) => d.Protocol == "https" ? d.Fingerprint : null;
}

sealed class SendFailed(string message) : Exception(message);

/// <summary>Speed and time left, e.g. "42%, 11.2 MB/s, 20 s left".</summary>
static class Rate
{
    public static string Describe(long done, long total, TimeSpan elapsed)
    {
        var pct = done * 100 / Math.Max(total, 1);
        if (elapsed.TotalSeconds < 1 || done == 0) return $"{pct}%";
        var perSecond = done / elapsed.TotalSeconds;
        return $"{pct}%, {IncomingCard.FormatSize((long)perSecond)}/s, {Left((total - done) / perSecond)}";
    }

    static string Left(double seconds) => seconds switch
    {
        < 60 => $"{Math.Max(1, (int)Math.Ceiling(seconds))} s left",
        < 3600 => $"{(int)Math.Ceiling(seconds / 60)} min left",
        _ => $"{seconds / 3600:0.#} h left",
    };
}

/// <summary>A LocalSend Drop extension: wave at another PC, no file needed.</summary>
static class Waves
{
    /// <returns>null if it got there, otherwise why not.</returns>
    public static async Task<string?> SendAsync(Identity id, Device target)
    {
        try
        {
            using var client = LsHttp.Create(id, LsHttp.PinFor(target), TimeSpan.FromSeconds(8));
            var resp = await client.PostAsJsonAsync(target.Root + "/api/localsend-drop/v1/wave",
                new { alias = id.Alias, fingerprint = id.Fingerprint }, Json.Options);
            return (int)resp.StatusCode switch
            {
                200 => null,
                423 => "they have Do not disturb on",
                404 or 405 => "they need the latest LocalSend Drop",
                var code => $"their PC answered {code}",
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return "their PC didn't answer"; }
    }
}

/// <summary>A LocalSend Drop extension: ask another PC to send you a file.</summary>
sealed record FileRequest(string Alias, string Fingerprint, string Ip, int Port, string? Note);

static class FileRequests
{
    static readonly Dictionary<string, DateTime> Asked = [];

    /// <summary>Remember that we asked, so their file is accepted without a prompt.</summary>
    public static void Remember(string fingerprint) { lock (Asked) Asked[fingerprint] = DateTime.UtcNow; }

    /// <summary>True (once) if we asked this device within the last 15 minutes.</summary>
    public static bool Consume(string fingerprint)
    {
        lock (Asked)
            return Asked.Remove(fingerprint, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(15);
    }

    /// <returns>null if they got it, otherwise why not.</returns>
    public static async Task<string?> SendAsync(Identity id, int ourPort, Device target, string? note)
    {
        try
        {
            using var client = LsHttp.Create(id, LsHttp.PinFor(target), TimeSpan.FromSeconds(8));
            var resp = await client.PostAsJsonAsync(target.Root + "/api/localsend-drop/v1/request",
                new { alias = id.Alias, fingerprint = id.Fingerprint, port = ourPort, note }, Json.Options);
            return (int)resp.StatusCode switch
            {
                200 => null,
                423 => "they have Do not disturb on",
                404 or 405 => "they need LocalSend Drop to receive requests",
                var code => $"their PC answered {code}",
            };
        }
        catch (HttpRequestException) { return "couldn't reach them"; }
        catch (TaskCanceledException) { return "they didn't answer"; }
    }
}

static class Sender
{
    static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".heic"] = "image/heic", [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml",
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".mkv"] = "video/x-matroska", [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".m4a"] = "audio/mp4", [".flac"] = "audio/flac",
        [".pdf"] = "application/pdf", [".zip"] = "application/zip", [".txt"] = "text/plain",
        [".apk"] = "application/vnd.android.package-archive",
    };

    /// <summary>Expands dropped paths; folders keep their structure ("Folder/sub/file.txt").</summary>
    public static List<SendItem> FromPaths(IEnumerable<string> paths)
    {
        var items = new List<SendItem>();
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                items.Add(new SendItem(Path.GetFileName(p), p, null));
            }
            else if (Directory.Exists(p))
            {
                var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)) ?? p;
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    items.Add(new SendItem(Path.GetRelativePath(root, f).Replace('\\', '/'), f, null));
            }
        }
        return items;
    }

    /// <param name="askPin">Called when the receiver wants a PIN; null aborts.</param>
    /// <param name="progress">Status text and 0..1 progress (null while waiting).</param>
    /// <returns>Short result text for the UI.</returns>
    public static async Task<string> SendAsync(Identity id, int ourPort, Device target, List<SendItem> items,
        Func<string?> askPin, IProgress<(string, double?)> progress, CancellationToken ct)
    {
        if (items.Count == 0) throw new SendFailed("Nothing to send");

        var files = new Dictionary<string, object>();
        var byId = new Dictionary<string, SendItem>();
        var previews = 0;
        foreach (var item in items)
        {
            var fid = Guid.NewGuid().ToString("N");
            byId[fid] = item;
            var isText = item.Path == null;
            var info = item.Path != null ? new FileInfo(item.Path) : null;
            // LocalSend Drop shows these on the accept card; LocalSend ignores the extra field.
            var thumbnail = item.Path != null && previews < 4 ? Thumbs.DataUri(item.Path) : null;
            if (thumbnail != null) previews++;
            files[fid] = new
            {
                id = fid,
                fileName = item.Name,
                size = item.Size,
                fileType = isText ? "text/plain" : Mime.GetValueOrDefault(Path.GetExtension(item.Name), "application/octet-stream"),
                sha256 = (string?)null,
                preview = isText ? item.Text : null,
                thumbnail,
                metadata = info == null ? null : new
                {
                    modified = info.LastWriteTimeUtc.ToString("o"),
                    accessed = info.LastAccessTimeUtc.ToString("o"),
                },
            };
        }
        var body = new
        {
            info = new DeviceInfo
            {
                Alias = id.Alias, DeviceModel = "Windows", DeviceType = "desktop",
                Fingerprint = id.Fingerprint, Port = ourPort, Protocol = "https",
                Creature = Settings.Current.ZoneStyle == "Mouth" ? ColorTranslator.ToHtml(Ui.Solid(Ui.Accent)) : null,
            },
            files,
        };

        // The receiver holds prepare-upload open until its user accepts.
        using var client = LsHttp.Create(id, LsHttp.PinFor(target), Timeout.InfiniteTimeSpan);
        progress.Report(("Waiting for them to accept…", null));

        string? pin = null;
        HttpResponseMessage resp;
        for (var attempt = 0; ; attempt++)
        {
            var url = target.BaseUrl + "/prepare-upload" + (pin != null ? "?pin=" + Uri.EscapeDataString(pin) : "");
            try { resp = await client.PostAsJsonAsync(url, body, Json.Options, ct); }
            catch (HttpRequestException e) { throw new SendFailed($"Can't reach {target.Alias} ({e.HttpRequestError})"); }

            if (resp.StatusCode != HttpStatusCode.Unauthorized || attempt >= 3) break;
            pin = askPin();
            if (pin == null) throw new SendFailed("Cancelled");
        }

        switch (resp.StatusCode)
        {
            case HttpStatusCode.NoContent: return "Delivered";
            case HttpStatusCode.Forbidden: throw new SendFailed("Declined");
            case HttpStatusCode.Unauthorized: throw new SendFailed("Wrong PIN");
            case HttpStatusCode.Conflict: throw new SendFailed("They're busy with another transfer");
            case HttpStatusCode.TooManyRequests: throw new SendFailed("Too many PIN attempts");
            case HttpStatusCode.OK: break;
            default: throw new SendFailed($"Receiver error ({(int)resp.StatusCode})");
        }

        var session = await resp.Content.ReadFromJsonAsync<JsonElement>(Json.Options, ct);
        var sessionId = session.GetProperty("sessionId").GetString()!;
        var tokens = session.GetProperty("files").EnumerateObject()
            .Select(p => (fileId: p.Name, token: p.Value.GetString()!))
            .Where(t => byId.ContainsKey(t.fileId) && byId[t.fileId].Path != null)
            .ToList();
        if (tokens.Count == 0) return "Delivered";

        long total = tokens.Sum(t => byId[t.fileId].Size), done = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastPct = -1L;
        try
        {
            foreach (var (fileId, token) in tokens)
            {
                var item = byId[fileId];
                var query = $"sessionId={Uri.EscapeDataString(sessionId)}&fileId={Uri.EscapeDataString(fileId)}&token={Uri.EscapeDataString(token)}";
                long offset = 0;
                DateTime? lostAt = null;
                while (true)
                {
                    // A dead Wi-Fi link can hang a request for minutes; treat 20 s without progress as lost.
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var lastProgress = DateTime.UtcNow;
                    using var watchdog = new System.Threading.Timer(_ =>
                    {
                        if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(20)) stall.Cancel();
                    }, null, 5000, 5000);
                    try
                    {
                        await using var fs = new FileStream(item.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
                        fs.Seek(offset, SeekOrigin.Begin);
                        var baseDone = done + offset;
                        var content = new StreamContent(new ProgressStream(fs, read =>
                        {
                            lastProgress = DateTime.UtcNow;
                            var sent = baseDone + read;
                            var pct = sent * 100 / Math.Max(total, 1);
                            if (pct == Interlocked.Exchange(ref lastPct, pct)) return;
                            progress.Report((Rate.Describe(sent, total, clock.Elapsed), pct / 100.0));
                        }), 1 << 16);
                        content.Headers.ContentLength = item.Size - offset;
                        content.Headers.ContentType = new("application/octet-stream");

                        var url = $"{target.BaseUrl}/upload?{query}" + (offset > 0 ? $"&offset={offset}" : "");
                        var up = await client.PostAsync(url, content, stall.Token);
                        if (!up.IsSuccessStatusCode) throw new SendFailed($"Upload failed ({(int)up.StatusCode})");
                        break;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested &&
                                              e is HttpRequestException or IOException or OperationCanceledException)
                    {
                        // Lost the connection: wait for it to come back and continue where it stopped.
                        lostAt ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - lostAt > TimeSpan.FromMinutes(2)) throw new SendFailed("Connection lost");
                        progress.Report(("Reconnecting…", lastPct / 100.0));
                        long? resumeAt;
                        while (true)
                        {
                            await Task.Delay(3000, ct);
                            resumeAt = await ResumeOffset(id, target, query);
                            if (resumeAt != -1) break;   // reachable again (or can't resume)
                            if (DateTime.UtcNow - lostAt > TimeSpan.FromMinutes(2)) throw new SendFailed("Connection lost");
                        }
                        offset = resumeAt ?? throw new SendFailed("Connection lost");
                    }
                }
                done += item.Size;
                lastPct = -1;
            }
        }
        catch
        {
            // Free the receiver's session so the next send isn't blocked.
            try { using var c = LsHttp.Create(id, LsHttp.PinFor(target), TimeSpan.FromSeconds(3));
                  await c.PostAsync($"{target.BaseUrl}/cancel?sessionId={Uri.EscapeDataString(sessionId)}", null); }
            catch { }
            throw;
        }

        return tokens.Count == 1 ? "Sent" : $"Sent {tokens.Count} files";
    }

    /// <summary>
    /// How much of the file the receiver already has (LocalSend Drop only). Null when the
    /// receiver can't resume (LocalSend, or the session is gone); -1 while it's unreachable.
    /// </summary>
    static async Task<long?> ResumeOffset(Identity id, Device target, string query)
    {
        try
        {
            using var c = LsHttp.Create(id, LsHttp.PinFor(target), TimeSpan.FromSeconds(5));
            var resp = await c.GetAsync($"{target.Root}/api/localsend-drop/v1/resume?{query}");
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("offset").GetInt64();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return -1; }
    }

    sealed class ProgressStream(Stream inner, Action<long> onRead) : Stream
    {
        long _read;
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));
        int Count(int n) { _read += n; onRead(_read); return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
