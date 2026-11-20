namespace LocalSendDrop;

/// <summary>One PC in Settings' list: who it is, how it stands, and the device to reach it (null when not nearby).</summary>
sealed record PcRow(string Fingerprint, string Name, string Status, bool Paired, Device? Device);

/// <summary>What Settings needs to list PCs and pair: the nearby devices, and the app's pair and unpair.</summary>
sealed record PcAccess(Func<IEnumerable<Device>> Nearby, Func<string, bool> Linked, Action<Device> Pair, Action<string, Device?> Unpair);

/// <summary>Settings' list of PCs to share the mouse and keyboard with: paired ones and nearby ones, each with Pair or Unpair.</summary>
sealed class PcList : Drawn
{
    /// <summary>Paired PCs first, then nearby desktops, each once; phones can't take a mouse and keyboard.</summary>
    /// <param name="arranged">Whether a PC has a saved layout (then the side comes from Arrange screens, not the pairing).</param>
    public static List<PcRow> Rows(IEnumerable<Device> nearby, IEnumerable<PairedPc> paired, Func<string, bool> connected,
        Func<string, bool>? arranged = null)
    {
        var near = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in nearby)
            if (d.DeviceType != "mobile") near.TryAdd(d.Fingerprint, d);
        var rows = new List<PcRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paired.OrderBy(p => p.Alias, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!seen.Add(p.Fingerprint)) continue;
            near.TryGetValue(p.Fingerprint, out var d);
            var status = (connected(p.Fingerprint) ? "Paired · connected" : d != null ? "Paired · connecting" : "Paired · not nearby")
                + (arranged?.Invoke(p.Fingerprint) == true ? " · placed in Arrange screens" : $" · {Pairing.Describe(p.Edge)}");
            rows.Add(new PcRow(p.Fingerprint, p.Alias is { Length: > 0 } a ? a : d?.Alias ?? "PC", status, true, d));
        }
        foreach (var d in near.Values.OrderBy(d => d.Alias, StringComparer.CurrentCultureIgnoreCase))
            if (seen.Add(d.Fingerprint))
                rows.Add(new PcRow(d.Fingerprint, d.Alias, "Available", false, d));
        return rows;
    }

    List<PcRow> _rows = [];
    readonly List<PillButton> _buttons = [];

    /// <summary>Pair with a nearby PC.</summary>
    public event Action<Device>? Pair;
    /// <summary>Unpair by fingerprint (the device, when it's nearby).</summary>
    public event Action<string, Device?>? Unpair;

    /// <summary>The rows shown now.</summary>
    public IReadOnlyList<PcRow> Current => _rows;

    /// <summary>Rows shown (at least one line, for "none found").</summary>
    public int Lines => Math.Max(1, _rows.Count);

    /// <summary>Shows <paramref name="rows"/>; true when the number of lines changed (the card needs laying out again).</summary>
    public bool Show(List<PcRow> rows)
    {
        if (rows.SequenceEqual(_rows)) return false;
        var grew = Math.Max(1, rows.Count) != Lines;
        _rows = rows;
        foreach (var b in _buttons) { Controls.Remove(b); b.Dispose(); }
        _buttons.Clear();
        foreach (var r in rows)
        {
            var b = new PillButton(r.Paired ? "Unpair" : "Pair", primary: !r.Paired);
            var row = r;
            b.Click += (_, _) =>
            {
                if (row.Paired) Unpair?.Invoke(row.Fingerprint, row.Device);
                else if (row.Device != null) Pair?.Invoke(row.Device);
            };
            _buttons.Add(b);
            Controls.Add(b);
        }
        PlaceButtons();
        Invalidate();
        return grew;
    }

    void PlaceButtons()
    {
        for (var i = 0; i < _buttons.Count; i++)
            _buttons[i].SetBounds(Width - S(96), i * S(52) + S(8), S(96), S(32));
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); PlaceButtons(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBack(g);
        if (_rows.Count == 0)
        {
            Ui.DrawText(g, "No other PCs found. Open LocalSend Drop on the other PC, on the same network.", Ui.Caption,
                new Rectangle(0, 0, Width, S(48)), Ui.TextSecondary);
            return;
        }
        var textW = Width - S(112);
        for (var i = 0; i < _rows.Count; i++)
        {
            var y = i * S(52);
            Ui.DrawText(g, _rows[i].Name, Ui.Body, new Rectangle(0, y + S(4), textW, S(22)), Ui.Text);
            Ui.DrawText(g, _rows[i].Status, Ui.Caption, new Rectangle(0, y + S(25), textW, S(18)), Ui.TextSecondary);
        }
    }
}
