using System.Text.Json;

namespace LocalSendDrop;

sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public bool Received { get; set; }
    public string Peer { get; set; } = "";
    public string? PeerFingerprint { get; set; }
    /// <summary>Saved files (received) or source files (sent).</summary>
    public List<string> Paths { get; set; } = [];
    public string Title { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>The last few transfers, shown under "Recent" in the panel.</summary>
static class History
{
    const int Keep = 30;
    static readonly string FilePath = Path.Combine(Identity.DataDir, "history.json");
    static readonly List<HistoryEntry> Items = Load();

    public static event Action? Changed;

    public static IReadOnlyList<HistoryEntry> Latest(int n)
    {
        lock (Items) return Items.Take(n).ToList();
    }

    public static void Add(HistoryEntry e)
    {
        lock (Items)
        {
            Items.Insert(0, e);
            if (Items.Count > Keep) Items.RemoveRange(Keep, Items.Count - Keep);
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Items)); } catch { }
        }
        Changed?.Invoke();
    }

    static List<HistoryEntry> Load()
    {
        try { return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? []; }
        catch { return []; }
    }

    public static string TitleFor(IReadOnlyList<string> names) =>
        names.Count == 1 ? Path.GetFileName(names[0].TrimEnd('/', '\\')) : $"{names.Count} files";

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        return utc.ToLocalTime().ToString("d MMM");
    }
}
