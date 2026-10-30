using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LocalSendDrop;

/// <summary>
/// Finds LocalSend devices. We multicast an announcement; every LocalSend on the
/// network answers by calling POST /register on our <see cref="Server"/>. We also
/// answer other devices' announcements, which is how LocalSend itself finds peers.
/// </summary>
sealed class Discovery(Identity id) : IDisposable
{
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.167");
    const int MulticastPort = 53317;

    readonly ConcurrentDictionary<string, Device> _devices = new();
    readonly CancellationTokenSource _stop = new();
    UdpClient? _udp;

    /// <summary>Port of our HTTPS server, set before <see cref="Start"/>.</summary>
    public int HttpPort { get; set; }
    public event Action? Changed;

    public IReadOnlyList<Device> Devices =>
        _devices.Values.OrderBy(d => d.Alias, StringComparer.CurrentCultureIgnoreCase).ToList();

    public void Start()
    {
        StartMulticast();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await AnnounceAsync();
                await Task.Delay(TimeSpan.FromMinutes(5), _stop.Token).ConfigureAwait(false);
            }
        });
    }

    public DeviceInfo OurInfo(bool? announce = null) => new()
    {
        Alias = id.Alias, DeviceModel = "Windows", DeviceType = "desktop",
        Fingerprint = id.Fingerprint, Port = HttpPort, Protocol = "https", Announce = announce,
    };

    // ---- multicast ----

    static List<IPAddress> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

    void StartMulticast()
    {
        var udp = new UdpClient { ExclusiveAddressUse = false };
        // LocalSend on this PC also binds 53317 with SO_REUSEADDR, so we can share it.
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        foreach (var ip in LocalIPv4())
        {
            try { udp.JoinMulticastGroup(Group, ip); } catch (SocketException) { }
        }
        _udp = udp;

        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; } // e.g. ICMP errors surfacing on Windows
                OnDatagram(r.Buffer, r.RemoteEndPoint.Address);
            }
        });
    }

    void OnDatagram(byte[] data, IPAddress from)
    {
        DeviceInfo? msg;
        try { msg = JsonSerializer.Deserialize<DeviceInfo>(data, Json.Options); }
        catch { return; }
        if (msg == null || string.IsNullOrEmpty(msg.Fingerprint) || msg.Fingerprint == id.Fingerprint) return;

        if (msg.Announce == false)
        {
            // Legacy fallback answer to our announcement: trust it as-is.
            Upsert(msg, from);
            return;
        }
        // Answer with a register request; list the device only if that succeeds,
        // so everything shown is actually reachable.
        _ = Task.Run(() => RegisterWithAsync(msg, from));
    }

    async Task RegisterWithAsync(DeviceInfo msg, IPAddress ip)
    {
        var d = new Device { Fingerprint = msg.Fingerprint, Ip = ip.ToString(), Port = msg.Port, Protocol = msg.Protocol };
        try
        {
            using var client = LsHttp.Create(id, LsHttp.PinFor(d), TimeSpan.FromSeconds(5));
            var resp = await client.PostAsJsonAsync(d.BaseUrl + "/register", OurInfo(), Json.Options, _stop.Token);
            if (resp.IsSuccessStatusCode) Upsert(msg, ip);
        }
        catch { /* unreachable or wrong certificate: ignore */ }
    }

    /// <summary>Asks every LocalSend device on the network to report in.</summary>
    public async Task AnnounceAsync()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(OurInfo(announce: true), Json.Options);
        var target = new IPEndPoint(Group, MulticastPort);
        foreach (var delay in new[] { 0, 300, 1000 })
        {
            await Task.Delay(delay).ConfigureAwait(false);
            foreach (var ip in LocalIPv4())
            {
                try
                {
                    // One socket per interface so the announcement leaves every network.
                    using var s = new UdpClient(new IPEndPoint(ip, 0));
                    s.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                    s.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    await s.SendAsync(payload, target);
                }
                catch (SocketException) { }
            }
        }
    }

    /// <summary>Announces, then drops devices that didn't answer within a few seconds.</summary>
    public async Task RefreshAsync()
    {
        var started = DateTime.UtcNow;
        await AnnounceAsync();
        await Task.Delay(3000);
        var stale = _devices.Values.Where(d => d.LastSeen < started).ToList();
        foreach (var d in stale) _devices.TryRemove(d.Fingerprint, out _);
        if (stale.Count > 0) Changed?.Invoke();
    }

    public void Upsert(DeviceInfo info, IPAddress ip)
    {
        if (info.Fingerprint == id.Fingerprint) return;
        var d = _devices.GetOrAdd(info.Fingerprint, fp => new Device { Fingerprint = fp });
        d.Alias = info.Alias;
        d.DeviceModel = info.DeviceModel;
        d.DeviceType = info.DeviceType;
        d.Ip = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        d.Port = info.Port;
        d.Protocol = info.Protocol;
        d.LastSeen = DateTime.UtcNow;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp?.Dispose();
    }
}
