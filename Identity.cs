using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LocalSendDrop;

/// <summary>
/// This device's TLS identity. Newer LocalSend receivers demand a client
/// certificate (mTLS) and identify senders by the SHA-256 of that certificate,
/// so we keep one self-signed RSA-2048 certificate per user, like LocalSend does.
/// </summary>
sealed class Identity
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }
    public string Alias { get; set; }

    Identity(X509Certificate2 cert, string alias)
    {
        Certificate = cert;
        Fingerprint = FingerprintOf(cert);
        Alias = alias;
    }

    public static string FingerprintOf(X509Certificate cert) =>
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    // LSDROP_DATA lets a second instance run with its own identity (testing).
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("LSDROP_DATA") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalSendDrop");

    public static Identity LoadOrCreate()
    {
        Directory.CreateDirectory(DataDir);
        var pfxPath = Path.Combine(DataDir, "identity.pfx");

        X509Certificate2? cert = null;
        if (File.Exists(pfxPath))
        {
            try { cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null, X509KeyStorageFlags.UserKeySet); }
            catch { cert = null; }
        }
        if (cert == null)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=LocalSend User", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(100));
            var pfx = created.Export(X509ContentType.Pfx);
            File.WriteAllBytes(pfxPath, pfx);
            // Re-import so SChannel can use the private key for client auth.
            cert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        }

        return new Identity(cert, Settings.Current.Alias ?? Environment.MachineName);
    }
}

sealed class Settings
{
    public string? Alias { get; set; }
    public bool FirstRunDone { get; set; }
    public string? SaveDir { get; set; }
    /// <summary>"ask", "known" (auto-accept trusted devices) or "all".</summary>
    public string ReceiveMode { get; set; } = "ask";
    /// <summary>TopLeft, TopRight, BottomLeft, BottomRight or TopCenter.</summary>
    public string Corner { get; set; } = "TopLeft";
    /// <summary>Screen.DeviceName of the chosen monitor; null = main monitor.</summary>
    public string? Monitor { get; set; }

    /// <summary>The chosen monitor, falling back to the main one if it's unplugged.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Forms.Screen TargetScreen =>
        System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Monitor) ?? System.Windows.Forms.Screen.PrimaryScreen!;
    public bool Sound { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Windows11, Windows10 or Glass.</summary>
    public string Theme { get; set; } = "Windows11";
    /// <summary>The corner when the panel is closed: "Strip" (thin accent line) or "Mouth".</summary>
    public string ZoneStyle { get; set; } = "Strip";
    /// <summary>How long (ms) to hold a file at the drop zone before the panel opens; dropped sooner, it goes on the shelf. 0 = right away.</summary>
    public int OpenDelay { get; set; } = 600;
    /// <summary>Decline incoming transfers (they're listed in Recent).</summary>
    public bool DoNotDisturb { get; set; }
    /// <summary>Also be quiet while presenting or running something fullscreen.</summary>
    public bool AutoQuiet { get; set; } = true;
    /// <summary>Names you gave devices, by fingerprint. Only you see them.</summary>
    public Dictionary<string, string> Nicknames { get; set; } = [];
    /// <summary>Named sets of devices ("Design team") that you send to in one go.</summary>
    public List<DeviceGroup> Groups { get; set; } = [];
    /// <summary>Pinned devices (fingerprints), listed first.</summary>
    public List<string> Favorites { get; set; } = [];
    /// <summary>We asked once on first run; after that it's the Settings link.</summary>
    public bool ShellMenuOffered { get; set; }
    /// <summary>Fingerprints of senders accepted before.</summary>
    public List<string> Trusted { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public bool Right => Corner.EndsWith("Right");
    [System.Text.Json.Serialization.JsonIgnore] public bool Bottom => Corner.StartsWith("Bottom");
    /// <summary>The middle of the top edge instead of a corner.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Center => Corner == "TopCenter";

    /// <summary>Left edge for a window of width <paramref name="w"/> by the drop zone: a corner, or centred.</summary>
    public int ZoneX(System.Drawing.Rectangle area, int w, int margin) =>
        Center ? area.Left + (area.Width - w) / 2 : Right ? area.Right - w - margin : area.Left + margin;

    public static string DefaultSaveDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    static readonly string FilePath = Path.Combine(Identity.DataDir, "settings.json");

    public static Settings Current { get; } = Load();

    static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Identity.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

/// <summary>A saved set of devices, listed as one row in the panel.</summary>
sealed class DeviceGroup
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = [];   // fingerprints
}
