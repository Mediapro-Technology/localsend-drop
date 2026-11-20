using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>Wake-on-LAN: the magic packet, and learning a PC's hardware address from its IP.</summary>
static class WakeOnLan
{
    /// <summary>Broadcasts a magic packet for <paramref name="mac"/> ("AA:BB:CC:DD:EE:FF", "-" or none). False if the address is bad.</summary>
    public static bool Send(string mac)
    {
        if (Parse(mac) is not { } bytes) return false;
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++) bytes.CopyTo(packet, 6 + i * 6);
        // 255.255.255.255 leaves by one network card only (the default route), so each LAN's own broadcast too.
        var sent = false;
        foreach (var target in Targets())
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(target.local, 0)) { EnableBroadcast = true };
                udp.Send(packet, packet.Length, new IPEndPoint(target.broadcast, 9));
                sent = true;
            }
            catch (SocketException) { }
        }
        return sent;
    }

    /// <summary>The limited broadcast, plus each up IPv4 interface's directed broadcast, with the address to send from.</summary>
    static IEnumerable<(IPAddress local, IPAddress broadcast)> Targets()
    {
        yield return (IPAddress.Any, IPAddress.Broadcast);
        System.Net.NetworkInformation.NetworkInterface[] nics;
        try { nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces(); } catch { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is { } mask && !mask.Equals(IPAddress.Any))
                    yield return (a.Address, DirectedBroadcast(a.Address, mask));
        }
    }

    /// <summary>The broadcast address of <paramref name="ip"/>'s subnet.</summary>
    internal static IPAddress DirectedBroadcast(IPAddress ip, IPAddress mask)
    {
        var a = ip.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(a);
    }

    internal static byte[]? Parse(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? Convert.FromHexString(hex) : null;
    }

    /// <summary>The hardware address of <paramref name="ip"/> on the local network (ARP), or "".</summary>
    public static string LearnMac(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return "";
        var mac = new byte[6];
        var len = 6;
        var addr = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        return SendARP(addr, 0, mac, ref len) == 0 && len == 6 ? Convert.ToHexString(mac) : "";
    }

    [DllImport("iphlpapi.dll")] static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref int len);
}
