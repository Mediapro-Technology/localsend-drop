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
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            return true;
        }
        catch (SocketException) { return false; }
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
