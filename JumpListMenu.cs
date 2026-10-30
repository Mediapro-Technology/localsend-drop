using Windows.UI.StartScreen;

namespace LocalSendDrop;

/// <summary>
/// Right-click LocalSend Drop on the taskbar: "Send to" lists pinned and recent
/// devices; picking one opens a file picker for it (LocalSendDrop.exe --send-to <fingerprint>).
/// </summary>
static class JumpListMenu
{
    static CancellationTokenSource? _pending;

    /// <summary>Rebuilds the list (debounced: history changes come in bursts).</summary>
    public static void Update()
    {
        if (!Packaging.IsPackaged) return;
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        _ = Task.Delay(2000, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try
            {
                if (!JumpList.IsSupported()) return;
                var s = Settings.Current;
                // Pinned first, then whoever you sent to most recently.
                var devices = s.Favorites
                    .Select(fp => (fp, Name: s.Nicknames.GetValueOrDefault(fp) ?? History.Latest(30).FirstOrDefault(h => h.PeerFingerprint == fp)?.Peer))
                    .Concat(History.Latest(30).Where(h => !h.Received && h.PeerFingerprint != null)
                        .Select(h => (fp: h.PeerFingerprint!, Name: s.Nicknames.GetValueOrDefault(h.PeerFingerprint!) ?? h.Peer)))
                    .Where(d => d.Name != null)
                    .DistinctBy(d => d.fp)
                    .Take(6)
                    .ToList();

                var list = await JumpList.LoadCurrentAsync();
                list.SystemGroupKind = JumpListSystemGroupKind.None;
                list.Items.Clear();
                foreach (var (fp, name) in devices)
                {
                    var item = JumpListItem.CreateWithArguments($"--send-to {fp}", $"Send to {name}");
                    item.GroupName = "Send to";
                    item.Logo = new Uri("ms-appx:///Assets/Square44x44Logo.png");
                    list.Items.Add(item);
                }
                await list.SaveAsync();
            }
            catch { /* the taskbar list is a nicety */ }
        }, TaskScheduler.Default);
    }
}
