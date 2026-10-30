namespace LocalSendDrop;

/// <summary>
/// Windows blocks incoming connections on networks marked Public, and the package's
/// firewall rules only open Private and work networks. On a Public network nobody
/// can send to this PC (or open a QR link), so we tell the user how to fix it.
/// </summary>
static class NetCheck
{
    /// <summary>True if every connected network is marked Public.</summary>
    public static bool OnlyPublic()
    {
        try
        {
            // INetworkListManager (netprofm). Category: 0 Public, 1 Private, 2 Domain.
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!;
            dynamic nlm = Activator.CreateInstance(type)!;
            var any = false;
            foreach (dynamic network in nlm.GetNetworks(1)) // NLM_ENUM_NETWORK_CONNECTED
            {
                any = true;
                if ((int)network.GetCategory() != 0) return false;
            }
            return any;
        }
        catch { return false; }
    }

    /// <summary>Opens Windows Settings where the network profile (Public/Private) is changed.</summary>
    public static void OpenSettings() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
}
