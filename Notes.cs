using System.Text.Json;

namespace LocalSendDrop;

sealed class Note
{
    public DateTime Time { get; set; }
    public bool FromMe { get; set; }
    public string Text { get; set; } = "";
    public bool Failed { get; set; }
}

/// <summary>Short messages with each device, kept on this PC (the last 100 per device).</summary>
static class Notes
{
    const int Keep = 100;
    static readonly string FilePath = Path.Combine(Identity.DataDir, "notes.json");
    static readonly Dictionary<string, List<Note>> ByDevice = Load();

    /// <summary>Raised with the device's fingerprint.</summary>
    public static event Action<string>? Changed;

    public static IReadOnlyList<Note> For(string fingerprint)
    {
        lock (ByDevice) return ByDevice.TryGetValue(fingerprint, out var list) ? list.ToList() : [];
    }

    public static Note Add(string fingerprint, bool fromMe, string text)
    {
        var note = new Note { Time = DateTime.UtcNow, FromMe = fromMe, Text = text };
        lock (ByDevice)
        {
            if (!ByDevice.TryGetValue(fingerprint, out var list)) ByDevice[fingerprint] = list = [];
            list.Add(note);
            if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
            Save();
        }
        Changed?.Invoke(fingerprint);
        return note;
    }

    public static void MarkFailed(string fingerprint, Note note)
    {
        lock (ByDevice) { note.Failed = true; Save(); }
        Changed?.Invoke(fingerprint);
    }

    static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(ByDevice)); } catch { }
    }

    static Dictionary<string, List<Note>> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, List<Note>>>(File.ReadAllText(FilePath)) ?? []; }
        catch { return []; }
    }
}
