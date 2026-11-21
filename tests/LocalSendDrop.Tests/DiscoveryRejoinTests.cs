using System.Net;
using System.Net.Sockets;
using LocalSendDrop;
using Xunit;

/// <summary>Multicast membership is lost when Wi-Fi reconnects; discovery joins again, as often as needed.</summary>
public class DiscoveryRejoinTests
{
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.167");

    [Fact]
    public void Rejoin_Twice_StillJoined_AndHearsTheGroup()
    {
        using var udp = new UdpClient { ExclusiveAddressUse = false };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var ips = new[] { IPAddress.Loopback };

        Assert.Equal(1, Discovery.Rejoin(udp, Group, ips));
        Assert.Equal(1, Discovery.Rejoin(udp, Group, ips));   // a second time: no "already joined" failure

        using var send = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        send.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
        send.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        send.Send([1, 2, 3], 3, new IPEndPoint(Group, port));
        var got = udp.ReceiveAsync().Wait(2000);
        Assert.True(got);
    }
}
