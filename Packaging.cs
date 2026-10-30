using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Windows.Management.Deployment;

namespace LocalSendDrop;

/// <summary>
/// LocalSend Drop ships as a full MSIX package (Start menu, right-click menu,
/// firewall rules, clean uninstall). The package is signed with our own
/// "CN=Mediapro Technology" certificate, which each PC trusts once (one UAC prompt).
/// A copy running as a plain .exe (a 1.x install that just updated, or a fresh
/// download) installs the package and hands over to it.
/// </summary>
static class Packaging
{
    public const string PackageName = "MediaproTechnology.LocalSendDrop";
    const string Publisher = "CN=Mediapro Technology";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterApplicationRestart(string? commandLine, int flags);

    /// <summary>True when running from the installed package.</summary>
    public static bool IsPackaged { get; } = CheckPackaged();

    static bool CheckPackaged()
    {
        var len = 0;
        return GetCurrentPackageFullName(ref len, null) != 15700; // APPMODEL_ERROR_NO_PACKAGE
    }

    public static bool Supported => Environment.OSVersion.Version.Build >= 19041;

    /// <summary>The package's "LocalSendDrop.exe" alias: a stable path across updates.</summary>
    public static string AliasPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "LocalSendDrop.exe");

    /// <summary>What "start with Windows" should run.</summary>
    public static string LaunchCommand => IsPackaged ? $"\"{AliasPath}\"" : $"\"{Environment.ProcessPath}\"";

    static string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalSendDrop", "package");

    // ---- certificate ----

    static X509Certificate2 Certificate()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("package.cer")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return X509CertificateLoader.LoadCertificate(ms.ToArray());
    }

    public static bool CertificateTrusted()
    {
        using var cert = Certificate();
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
    }

    /// <summary>Adds our certificate to the machine's Trusted People (UAC prompt).</summary>
    static string? TrustCertificate()
    {
        Directory.CreateDirectory(WorkDir);
        var cer = Path.Combine(WorkDir, "LocalSendDrop.cer");
        using (var cert = Certificate()) File.WriteAllBytes(cer, cert.Export(X509ContentType.Cert));
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Import-Certificate -FilePath '{cer}' -CertStoreLocation Cert:\\LocalMachine\\TrustedPeople\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception) { return "Windows needs admin approval to install it"; }
        return CertificateTrusted() ? null : "The certificate wasn't trusted";
    }

    // ---- install / update ----

    /// <summary>
    /// Installs (or updates to) the given .msix. Removes the 1.3 "sparse" package of
    /// the same name first: it can't be upgraded in place to a full package.
    /// When called from the installed app, Windows closes it and restarts it after.
    /// </summary>
    public static async Task<string?> InstallAsync(string msix)
    {
        var pm = new PackageManager();
        foreach (var old in pm.FindPackagesForUser(string.Empty, PackageName, Publisher).ToList())
        {
            if (old.Id.Version.Major >= 2) continue;
            try { await pm.RemovePackageAsync(old.Id.FullName); } catch { }
        }

        if (IsPackaged) RegisterApplicationRestart("--updated", 0);
        var result = await pm.AddPackageAsync(new Uri(msix), null, DeploymentOptions.ForceApplicationShutdown);
        return result.IsRegistered ? null : (result.ErrorText is { Length: > 0 } e ? e.Split('\n')[0].Trim() : "Windows refused the package");
    }

    /// <summary>
    /// Plain .exe → installed package: download the .msix of this version, trust the
    /// certificate (prompting only if allowed), install. Null on success.
    /// </summary>
    public static async Task<string?> MigrateAsync(bool mayPrompt)
    {
        if (!CertificateTrusted())
        {
            if (!mayPrompt) return "needs admin approval";
            if (TrustCertificate() is { } err) return err;
        }

        Directory.CreateDirectory(WorkDir);
        var msix = Path.Combine(WorkDir, "LocalSendDrop.msix");
        // An .msix next to the .exe is used as-is: offline installs, and testing a build before release.
        var local = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "LocalSendDrop.msix");
        if (File.Exists(local)) msix = local;
        else
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocalSendDrop/{Updater.CurrentText}");
            var url = $"https://github.com/Mediapro-Technology/localsend-drop/releases/download/v{Updater.CurrentText}/LocalSendDrop.msix";
            await using var src = await http.GetStreamAsync(url);
            await using var dst = File.Create(msix);
            await src.CopyToAsync(dst);
        }
        return await InstallAsync(msix);
    }

    /// <summary>Starts the installed app (used right after migrating).</summary>
    public static void LaunchInstalled(string args) =>
        Process.Start(new ProcessStartInfo(AliasPath, args) { UseShellExecute = true });
}
