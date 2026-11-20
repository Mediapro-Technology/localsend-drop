using System.Collections.Concurrent;

namespace LocalSendDrop;

/// <summary>
/// Keeps the two paired PCs' view of each other current: which monitors each has, and the shared
/// crossing settings. Messages arrive on link reading threads.
/// </summary>
static class KvmSync
{
    /// <summary>The other PCs' monitors, by fingerprint.</summary>
    public static readonly ConcurrentDictionary<string, DisplayInfo[]> PeerDisplays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a peer's monitors changed.</summary>
    public static event Action<string>? DisplaysChanged;

    /// <summary>A link just came up (either direction): tell them about our monitors.</summary>
    public static void Hello(Func<byte[], bool> send)
    {
        try { send(KvmWire.JsonMsg(KvmMsg.Displays, KvmZones.Current())); }
        catch { }
    }

    /// <summary>Displays and Sync messages from <paramref name="fingerprint"/>.</summary>
    public static void OnMessage(string fingerprint, byte[] m)
    {
        if (m.Length < 2) return;
        switch ((KvmMsg)m[0])
        {
            case KvmMsg.Displays when KvmWire.ReadJson<DisplayInfo[]>(m) is { } displays:
                PeerDisplays[fingerprint] = displays;
                DisplaysChanged?.Invoke(fingerprint);
                break;
            case KvmMsg.Sync when KvmWire.ReadJson<CrossingSettings>(m) is { } theirs:
                MergeSettings(fingerprint, theirs);
                break;
        }
    }

    // Stub: T-005 merges the received settings (newer stamp wins) and shows them.
    static void MergeSettings(string fingerprint, CrossingSettings theirs) { }
}
