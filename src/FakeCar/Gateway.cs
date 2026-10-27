using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FakeCar;

/// <summary>
/// The car's Ethernet gateway as an F10 ZGW shows it on the OBD cable: HSFZ on TCP 6801 (one tester at a time,
/// a second one is reset), and the vehicle search on UDP 6811.
/// </summary>
sealed class Gateway(Car car, Diagnostics diagnostics, Log log, IPAddress bind)
{
    const int DiagPort = 6801;
    const int SearchPort = 6811;
    const ushort ControlDiag = 0x0001;
    const ushort ControlAck = 0x0002;
    const ushort ControlIdent = 0x0011;
    const ushort ControlAlive = 0x0012;
    const byte Functional = 0xDF;

    readonly Lock busy = new();
    TcpClient? tester;

    public async Task RunAsync(CancellationToken stop)
    {
        var listener = new TcpListener(bind, DiagPort);
        listener.Start();
        log.Line($"listening tcp {bind}:{DiagPort}, VIN {car.Vin}, {car.Ecus.Count} modules");
        _ = Task.Run(() => SearchAsync(stop), stop);
        using var reg = stop.Register(listener.Stop);
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            lock (busy)
            {
                if (tester is not null)
                {
                    // Like an F10 gateway: one tester at a time; a second connection is reset.
                    log.Line($"second tester {client.Client.RemoteEndPoint} reset");
                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Close();
                    continue;
                }
                tester = client;
            }
            _ = Task.Run(() => ServeAsync(client, stop), stop);
        }
    }

    async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        var peer = client.Client.RemoteEndPoint;
        log.Line($"tester connected {peer}");
        client.NoDelay = true;
        var stream = client.GetStream();
        var header = new byte[6];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, stop);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                var control = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (length is < 0 or > 1_000_000) break;
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, stop);
                switch (control)
                {
                    case ControlDiag when body.Length >= 3:
                        await DiagAsync(stream, body, stop);
                        break;
                    case ControlIdent:
                        await SendAsync(stream, ControlIdent, Identification(), stop);
                        break;
                    case ControlAlive:
                        await SendAsync(stream, ControlAlive, [0x00, 0xF4], stop);
                        break;
                    default:
                        log.Line($"  control {control:X4} body {Convert.ToHexString(body)}");
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or EndOfStreamException)
        {
        }
        finally
        {
            log.Line($"tester gone {peer}");
            client.Close();
            lock (busy) tester = null;
        }
    }

    async Task DiagAsync(NetworkStream stream, byte[] body, CancellationToken stop)
    {
        var source = body[0];
        var target = body[1];
        var request = body[2..];
        log.Line($"{source:X2}->{target:X2} {Convert.ToHexString(request)}");
        // The gateway confirms every request first (control 0002, the start of what it got).
        await SendAsync(stream, ControlAck, body[..Math.Min(body.Length, 7)], stop);
        IEnumerable<Ecu> who = target == Functional
            ? car.Ecus.Values
            : car.Ecus.TryGetValue(target, out var one) ? [one] : [];
        foreach (var ecu in who)
        {
            var answer = diagnostics.Answer(ecu, request, target == Functional);
            if (answer is null) continue;
            log.Line($"   {ecu.Address:X2}<- {Hex(answer)}");
            await SendAsync(stream, ControlDiag, [ecu.Address, source, .. answer], stop);
        }
    }

    static async Task SendAsync(NetworkStream stream, ushort control, byte[] payload, CancellationToken stop)
    {
        var frame = new byte[6 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), control);
        payload.CopyTo(frame, 6);
        await stream.WriteAsync(frame, stop);
    }

    /// <summary>"DIAGADR10BMWMAC" + MAC + "BMWVIN" + VIN, as the gateway answers the vehicle search.</summary>
    byte[] Identification() => Encoding.ASCII.GetBytes($"DIAGADR{car.Gateway:X2}BMWMAC001A37DEC550BMWVIN{car.Vin}");

    async Task SearchAsync(CancellationToken stop)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(bind, SearchPort));
        log.Line($"listening udp {bind}:{SearchPort} (vehicle search)");
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult got;
            try { got = await udp.ReceiveAsync(stop); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }
            if (got.Buffer.Length < 6 || BinaryPrimitives.ReadUInt16BigEndian(got.Buffer.AsSpan(4)) != ControlIdent) continue;
            var payload = Identification();
            var frame = new byte[6 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), ControlIdent);
            payload.CopyTo(frame, 6);
            log.Line($"vehicle search from {got.RemoteEndPoint}: answered");
            await udp.SendAsync(frame, got.RemoteEndPoint, stop);
        }
    }

    static string Hex(byte[] b) => b.Length > 40 ? Convert.ToHexString(b, 0, 40) + $"... ({b.Length} bytes)" : Convert.ToHexString(b);
}
