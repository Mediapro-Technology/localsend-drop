using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace LocalSendDrop;

/// <summary>
/// Puts "Send with LocalSend Drop" in Windows 11's short right-click menu. Windows
/// only shows packaged handlers there, so we register a tiny sparse MSIX (built by
/// shell/build.ps1, embedded in this exe) whose handler DLL sits next to the exe.
/// The package is signed with our own certificate, which each PC must trust once:
/// that's the single admin (UAC) prompt.
/// </summary>
static class ShellPackage
{
    const string PackageName = "MediaproTechnology.LocalSendDrop";
    /// <summary>Must match the Version passed to shell/build.ps1.</summary>
    static readonly Version PackageVersion = new(1, 0, 0, 0);

    public static bool Supported => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Result of the last check, for the Settings window.</summary>
    public static bool Installed { get; private set; }

    static string ExeDir => Path.GetDirectoryName(Environment.ProcessPath)!;
    static string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalSendDrop", "shell");

    static byte[] Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The handler DLL must sit next to the exe (the package points at this folder).</summary>
    public static void EnsureDll()
    {
        var target = Path.Combine(ExeDir, "LocalSendDropShell.dll");
        var bytes = Resource("shell.dll");
        try
        {
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;
            File.WriteAllBytes(target, bytes);
        }
        catch { /* in use by Explorer right now; next start will retry */ }
    }

    static (int Code, string Output) PowerShell(string command, bool elevated = false)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"")
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevated) { psi.UseShellExecute = true; psi.Verb = "runas"; }
        else { psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
        using var p = Process.Start(psi)!;
        var output = elevated ? "" : p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }

    /// <summary>True if our package is registered at the current exe folder and up to date.</summary>
    public static bool IsInstalled()
    {
        var (_, v) = PowerShell($"(Get-AppxPackage {PackageName}).Version");
        return Installed = Version.TryParse(v, out var installed) && installed >= PackageVersion &&
               string.Equals(Settings.Current.ShellMenuDir, ExeDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trusts the certificate (UAC prompt if needed) and registers the package.</summary>
    /// <returns>null on success, otherwise what went wrong.</returns>
    public static string? Install()
    {
        try
        {
            Directory.CreateDirectory(WorkDir);
            var msix = Path.Combine(WorkDir, "LocalSendDrop.msix");
            var cer = Path.Combine(WorkDir, "LocalSendDrop.cer");
            File.WriteAllBytes(msix, Resource("shell.msix"));
            File.WriteAllBytes(cer, Resource("shell.cer"));
            EnsureDll();

            using var cert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(cer));
            if (!Trusted(cert))
            {
                try { PowerShell($"Import-Certificate -FilePath '{cer}' -CertStoreLocation Cert:\\LocalMachine\\TrustedPeople", elevated: true); }
                catch (System.ComponentModel.Win32Exception) { return "Windows needs admin approval to add the menu"; }
                if (!Trusted(cert)) return "The certificate wasn't trusted";
            }

            // Re-register cleanly so the package points at this exe's folder.
            PowerShell($"Get-AppxPackage {PackageName} | Remove-AppxPackage");
            var (code, output) = PowerShell($"Add-AppxPackage -Path '{msix}' -ExternalLocation '{ExeDir}'");
            if (code != 0) return output.Split('\n').FirstOrDefault()?.Trim() ?? "Windows refused the package";

            Settings.Current.ShellMenuDir = ExeDir;
            Settings.Current.Save();
            Installed = true;
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    static bool Trusted(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
    }
}
