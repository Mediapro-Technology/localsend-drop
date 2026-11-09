using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace LocalSendDrop;

/// <summary>Which side of this PC's desktop another PC sits on.</summary>
enum Edge { Left, Right, Top, Bottom }

/// <summary>A PC paired for sharing the mouse and keyboard. Separate from Trusted: this one can type.</summary>
sealed class PairedPc
{
    public string Fingerprint { get; set; } = "";
    public string Alias { get; set; } = "";
    /// <summary>Where that PC is, seen from this one.</summary>
    public Edge Edge { get; set; } = Edge.Right;
}

/// <summary>
/// Pairing two PCs for the mouse and keyboard. Both screens show 6 digits made
/// from the two certificate fingerprints; mutual TLS ties each side to its
/// fingerprint, so if the digits match on both screens nobody is in between.
/// The PC that starts it confirms on its screen, the other accepts on its card.
/// </summary>
static class Pairing
{
    const string Base = "/api/localsend-drop/v1/kvm";

    /// <summary>Runs work on the UI thread; settings are only changed there.</summary>
    public static Action<Action> Ui = a => a();

    /// <summary>Shown on this PC when another asks to pair: alias, code. True = accept.</summary>
    public static Func<string, string, CancellationToken, Task<bool>>? AskAccept;

    public static event Action? Changed;

    static PairedPc[] _snapshot = [.. Settings.Current.Paired];

    /// <summary>Safe to read from any thread.</summary>
    public static IReadOnlyList<PairedPc> All => _snapshot;

    /// <summary>Diagnostics only: pairs in memory, nothing saved.</summary>
    internal static void UseForTest(PairedPc[] list) => _snapshot = list;

    public static PairedPc? Find(string fingerprint) =>
        _snapshot.FirstOrDefault(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

    public static string Code(string a, string b)
    {
        var (first, second) = string.CompareOrdinal(a.ToUpperInvariant(), b.ToUpperInvariant()) < 0 ? (a, b) : (b, a);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"lsdrop-kvm|{first.ToUpperInvariant()}|{second.ToUpperInvariant()}"));
        var n = BitConverter.ToUInt32(hash, 0) % 1_000_000;
        return $"{n / 1000:000} {n % 1000:000}";
    }

    public static Edge Opposite(Edge e) => e switch
    {
        Edge.Left => Edge.Right, Edge.Right => Edge.Left, Edge.Top => Edge.Bottom, _ => Edge.Top,
    };

    public static string Describe(Edge e) => e switch
    {
        Edge.Left => "on the left", Edge.Right => "on the right", Edge.Top => "above", _ => "below",
    };

    /// <summary>UI thread.</summary>
    public static void Save(string fingerprint, string alias, Edge edge)
    {
        var s = Settings.Current;
        s.Paired.RemoveAll(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        // One PC per edge: pairing a new one on the same side replaces the old.
        s.Paired.RemoveAll(p => p.Edge == edge);
        s.Paired.Add(new PairedPc { Fingerprint = fingerprint, Alias = alias, Edge = edge });
        s.Save();
        _snapshot = [.. s.Paired];
        Changed?.Invoke();
    }

    /// <summary>UI thread.</summary>
    public static void Remove(string fingerprint)
    {
        var s = Settings.Current;
        if (s.Paired.RemoveAll(p => string.Equals(p.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) == 0) return;
        s.Save();
        _snapshot = [.. s.Paired];
        Changed?.Invoke();
    }

    // ---- this PC asks ----

    /// <returns>null if they accepted, otherwise why not.</returns>
    public static async Task<string?> RequestAsync(Identity id, Device target, Edge edge, CancellationToken ct)
    {
        try
        {
            using var client = LsHttp.Create(id, LsHttp.PinFor(target), TimeSpan.FromMinutes(3));
            var resp = await client.PostAsJsonAsync(target.Root + Base + "/pair",
                new { alias = id.Alias, edge = edge.ToString() }, Json.Options, ct);
            var quiet = (int)resp.StatusCode == 423 ? await Quiet.WhyAsync(resp) : null;
            return (int)resp.StatusCode switch
            {
                200 => null,
                403 => "they declined",
                423 => quiet!,
                404 or 405 => "they need the latest LocalSend Drop",
                var code => $"their PC answered {code}",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "cancelled"; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return "their PC didn't answer"; }
    }

    /// <summary>Tells them where they are now (from their side), or that the pairing is gone.</summary>
    public static async Task NotifyAsync(Identity id, Device target, Edge? edge)
    {
        try
        {
            using var client = LsHttp.Create(id, LsHttp.PinFor(target), TimeSpan.FromSeconds(8));
            if (edge is { } e) await client.PostAsJsonAsync(target.Root + Base + "/edge", new { edge = e.ToString() }, Json.Options);
            else await client.PostAsync(target.Root + Base + "/unpair", null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
    }

    // ---- another PC asks (server side) ----

    /// <summary>The fingerprint the TLS client certificate proves; claimed ones don't count here.</summary>
    static string? Proven(HttpContext ctx) =>
        ctx.Connection.ClientCertificate is { } cc ? Identity.FingerprintOf(cc) : null;

    static async Task<(string? alias, Edge? edge)> ReadBody(HttpContext ctx)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            string? Str(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Str("alias"), Enum.TryParse<Edge>(Str("edge"), out var e) ? e : null);
        }
        catch { return (null, null); }
    }

    public static async Task<IResult> HandlePair(HttpContext ctx, Identity id)
    {
        if (Proven(ctx) is not { } fingerprint) return Results.BadRequest("A client certificate is required.");
        var (alias, edge) = await ReadBody(ctx);
        if (edge is not { } theirSide) return Results.BadRequest();
        // Someone has to accept at that PC anyway, so only Do not disturb set by hand blocks it.
        if (Settings.Current.DoNotDisturb) return Quiet.Refusal();
        alias = (alias ?? "Someone")[..Math.Min((alias ?? "Someone").Length, 60)];

        var ask = AskAccept;
        if (ask == null) return Results.StatusCode(503);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(async () =>
        {
            try { tcs.TrySetResult(await ask(alias, Code(id.Fingerprint, fingerprint), timeout.Token)); }
            catch { tcs.TrySetResult(false); }
        });
        bool accepted;
        try { accepted = await tcs.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { accepted = false; }
        if (!accepted || ctx.RequestAborted.IsCancellationRequested) return Results.StatusCode(403);

        // They told us where we are for them; for us they're on the other side.
        Ui(() => Save(fingerprint, alias, Opposite(theirSide)));
        return Results.Ok();
    }

    public static async Task<IResult> HandleEdge(HttpContext ctx)
    {
        if (Proven(ctx) is not { } fingerprint || Find(fingerprint) is not { } paired) return Results.StatusCode(403);
        var (_, edge) = await ReadBody(ctx);
        if (edge is not { } theirSide) return Results.BadRequest();
        Ui(() => Save(paired.Fingerprint, paired.Alias, Opposite(theirSide)));
        return Results.Ok();
    }

    public static IResult HandleUnpair(HttpContext ctx)
    {
        if (Proven(ctx) is not { } fingerprint || Find(fingerprint) == null) return Results.StatusCode(403);
        Ui(() => Remove(fingerprint));
        return Results.Ok();
    }
}

/// <summary>"Which side of this PC is it?" Four buttons, or Cancel.</summary>
sealed class EdgeForm : Form
{
    public static Edge? Ask(string name)
    {
        using var f = new EdgeForm(name);
        return f.ShowDialog() == DialogResult.OK ? f._chosen : null;
    }

    Edge? _chosen;
    readonly string _name;

    EdgeForm(string name)
    {
        _name = name;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Ui.Base;
        int S(int v) => LogicalToDeviceUnits(v);
        ClientSize = new Size(S(360), S(236));
        (string text, Edge edge, int x, int y)[] buttons =
        [
            ("Above", Edge.Top, 104, 84), ("Left", Edge.Left, 24, 124),
            ("Right", Edge.Right, 184, 124), ("Below", Edge.Bottom, 104, 164),
        ];
        foreach (var (text, edge, x, y) in buttons)
        {
            var b = new PillButton(text, primary: true);
            b.SetBounds(S(x), S(y), S(152), S(32));
            b.Click += (_, _) => { _chosen = edge; DialogResult = DialogResult.OK; };
            Controls.Add(b);
        }
        var cancel = new PillButton("Cancel");
        cancel.SetBounds(S(264), S(196), S(72), S(28));
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(cancel);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.Chrome(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int v) => LogicalToDeviceUnits(v);
        e.Graphics.Clear(Ui.Base);
        Ui.DrawText(e.Graphics, $"Where is {_name}?", Ui.Subtitle, new Rectangle(S(24), S(20), S(312), S(30)), Ui.Text);
        Ui.DrawText(e.Graphics, "Push the pointer off that edge to use it.", Ui.Body, new Rectangle(S(24), S(52), S(312), S(22)), Ui.TextSecondary);
    }
}

/// <summary>Shows the pairing code while the other PC decides. "Codes match" or Cancel.</summary>
sealed class CodeForm : Form
{
    readonly string _name, _code;
    readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly PillButton _match = new("Codes match", primary: true);
    string _status = "Waiting for them to accept…";

    /// <summary>True once "Codes match" is clicked; false on Cancel or close.</summary>
    public Task<bool> Answer => _answer.Task;

    public CodeForm(string name, string code)
    {
        _name = name;
        _code = code;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Ui.Base;
        int S(int v) => LogicalToDeviceUnits(v);
        ClientSize = new Size(S(360), S(240));
        var cancel = new PillButton("Cancel");
        _match.SetBounds(S(24), S(188), S(152), S(32));
        cancel.SetBounds(S(184), S(188), S(152), S(32));
        _match.Click += (_, _) => { _answer.TrySetResult(true); _status = "Waiting for them to accept…"; _match.Enabled = false; Invalidate(); };
        cancel.Click += (_, _) => { _answer.TrySetResult(false); Close(); };
        FormClosed += (_, _) => _answer.TrySetResult(false);
        Controls.AddRange([_match, cancel]);
    }

    public void SetStatus(string text) { _status = text; Invalidate(); }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.Chrome(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int v) => LogicalToDeviceUnits(v);
        e.Graphics.Clear(Ui.Base);
        Ui.DrawText(e.Graphics, $"Pair with {_name}", Ui.Subtitle, new Rectangle(S(24), S(20), S(312), S(30)), Ui.Text);
        Ui.DrawText(e.Graphics, $"{_name} shows a code too. Continue only if it's this one:", Ui.Body,
            new Rectangle(S(24), S(54), S(312), S(40)), Ui.TextSecondary);
        Ui.DrawText(e.Graphics, _code, Ui.Title, new Rectangle(S(24), S(100), S(312), S(44)), Ui.Text);
        Ui.DrawText(e.Graphics, _status, Ui.Caption, new Rectangle(S(24), S(152), S(312), S(22)), Ui.TextSecondary);
    }
}
