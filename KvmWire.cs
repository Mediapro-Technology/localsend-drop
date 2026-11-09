using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace LocalSendDrop;

/// <summary>
/// Messages on the mouse-and-keyboard WebSocket. One binary frame each, first
/// byte the type, then little-endian fields. The controlling PC sends input;
/// the controlled PC answers with Leave, Locked and its clipboard.
/// </summary>
enum KvmMsg : byte
{
    Enter = 1,      // u16 fraction along the edge
    Move = 2,       // i16 dx, i16 dy
    Mouse = 3,      // u8 MouseKind, i16 data
    Key = 4,        // u16 vk, u16 scan, u8 flags (1 up, 2 extended)
    Leave = 5,      // u16 fraction along the edge (controlled → controller)
    Beat = 6,       // still here
    ClipText = 7,   // UTF-8
    ClipImage = 8,  // PNG
    Locked = 9,     // controlled PC is locked or showing a secure prompt
    Release = 10,   // let go of every held key and button
    DragMeta = 11,  // carrying files over: i32 json length, json (KvmDrag.Meta), PNG drag image
    DragWant = 12,  // token: send the files now (the other side is copying while you drag)
    DragCancel = 13,// token: that drag ended without a drop here
}

static class KvmWire
{
    public const int MaxMessage = 20 * 1024 * 1024;

    public static byte[] Simple(KvmMsg t) => [(byte)t];

    public static byte[] Fraction(KvmMsg t, double f)
    {
        var m = new byte[3];
        m[0] = (byte)t;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(1), (ushort)Math.Round(Math.Clamp(f, 0, 1) * 65535));
        return m;
    }

    public static double ReadFraction(ReadOnlySpan<byte> m) => m.Length >= 3 ? BinaryPrimitives.ReadUInt16LittleEndian(m[1..]) / 65535.0 : 0.5;

    public static byte[] Move(int dx, int dy)
    {
        var m = new byte[5];
        m[0] = (byte)KvmMsg.Move;
        BinaryPrimitives.WriteInt16LittleEndian(m.AsSpan(1), (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(m.AsSpan(3), (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        return m;
    }

    public static byte[] Mouse(MouseKind kind, int data)
    {
        var m = new byte[4];
        m[0] = (byte)KvmMsg.Mouse;
        m[1] = (byte)kind;
        BinaryPrimitives.WriteInt16LittleEndian(m.AsSpan(2), (short)Math.Clamp(data, short.MinValue, short.MaxValue));
        return m;
    }

    public static byte[] Key(KeyEvent e)
    {
        var m = new byte[6];
        m[0] = (byte)KvmMsg.Key;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(1), e.Vk);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(3), e.Scan);
        m[5] = (byte)((e.Up ? 1 : 0) | (e.Extended ? 2 : 0));
        return m;
    }

    public static byte[] WithPayload(KvmMsg t, byte[] payload)
    {
        var m = new byte[payload.Length + 1];
        m[0] = (byte)t;
        payload.CopyTo(m, 1);
        return m;
    }

    /// <summary>The point at <paramref name="f"/> along an edge of <paramref name="d"/>, <paramref name="inset"/> px inside.</summary>
    public static Point OnEdge(Edge e, double f, Rectangle d, int inset)
    {
        int Along(int start, int length) => start + (int)Math.Round(f * (length - 1));
        return e switch
        {
            Edge.Left => new Point(d.Left + inset, Along(d.Top, d.Height)),
            Edge.Right => new Point(d.Right - 1 - inset, Along(d.Top, d.Height)),
            Edge.Top => new Point(Along(d.Left, d.Width), d.Top + inset),
            _ => new Point(Along(d.Left, d.Width), d.Bottom - 1 - inset),
        };
    }

    /// <summary>How far along the edge <paramref name="p"/> is, 0 to 1.</summary>
    public static double Along(Edge e, Point p, Rectangle d) => e is Edge.Left or Edge.Right
        ? Math.Clamp((p.Y - d.Top) / (double)Math.Max(1, d.Height - 1), 0, 1)
        : Math.Clamp((p.X - d.Left) / (double)Math.Max(1, d.Width - 1), 0, 1);

    /// <summary>Whether <paramref name="p"/> is on (or past) that edge of the desktop.</summary>
    public static bool AtEdge(Edge e, Point p, Rectangle d) => e switch
    {
        Edge.Left => p.X <= d.Left,
        Edge.Right => p.X >= d.Right - 1,
        Edge.Top => p.Y <= d.Top,
        _ => p.Y >= d.Bottom - 1,
    };

    /// <summary>Corners stay put, so Start, close buttons and hot corners keep working.</summary>
    public static bool NearCorner(Edge e, Point p, Rectangle d)
    {
        var (pos, start, length) = e is Edge.Left or Edge.Right ? (p.Y, d.Top, d.Height) : (p.X, d.Left, d.Width);
        var margin = Math.Max(30, length * 3 / 100);
        return pos - start < margin || start + length - 1 - pos < margin;
    }

    /// <summary>Whether <paramref name="p"/> is past that edge of the desktop (off it).</summary>
    public static bool Past(Edge e, Point p, Rectangle d) => e switch
    {
        Edge.Left => p.X < d.Left,
        Edge.Right => p.X > d.Right - 1,
        Edge.Top => p.Y < d.Top,
        _ => p.Y > d.Bottom - 1,
    };
}

/// <summary>
/// A WebSocket with a send queue (only one send may run at a time) and a read
/// loop that hands over whole messages. Send never blocks, so the hook thread can call it.
/// </summary>
sealed class WsPipe(WebSocket ws)
{
    readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public bool Send(byte[] message) => ws.State == WebSocketState.Open && _out.Writer.TryWrite(message);

    /// <summary>Runs until either side closes; <paramref name="onMessage"/> is called on the reading thread.</summary>
    public async Task RunAsync(Action<byte[]> onMessage, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteLoop(linked.Token);
        try { await ReadLoop(onMessage, linked.Token); }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            _out.Writer.TryComplete();
            linked.Cancel();
            try { await writer; } catch { }
            try { ws.Abort(); } catch { }
        }
    }

    async Task WriteLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var m in _out.Reader.ReadAllAsync(ct))
                await ws.SendAsync(m, WebSocketMessageType.Binary, true, ct);
        }
        catch { try { ws.Abort(); } catch { } }
    }

    async Task ReadLoop(Action<byte[]> onMessage, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var whole = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                if (r.EndOfMessage && whole.Length == 0)
                {
                    if (r.Count > 0) onMessage(buffer.AsSpan(0, r.Count).ToArray());
                    continue;
                }
                whole.Write(buffer, 0, r.Count);
                if (whole.Length > KvmWire.MaxMessage) return;
                if (!r.EndOfMessage) continue;
                onMessage(whole.ToArray());
                whole.SetLength(0);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
