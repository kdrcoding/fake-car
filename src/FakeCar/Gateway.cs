using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FakeCar;

/// <summary>
/// The car's Ethernet gateway as an F10 ZGW shows it on the OBD cable: HSFZ on TCP 6801 (one tester at a time,
/// a second one is reset), and the vehicle search on UDP 6811. <paramref name="testers"/> hears who connects (null = gone).
/// </summary>
sealed class Gateway(Car car, Diagnostics diagnostics, Log log, IPAddress bind, Action<IPEndPoint?>? testers = null)
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
        // E-Sys finds a car by a broadcast search and connects to the address the answer came from, and Windows never
        // hands a broadcast to a socket on 127.0.0.1 only: with "other PCs" off, E-Sys found no car (2026-11-03). So the car
        // listens on every address of this PC and, with other PCs off, answers and serves only this PC.
        var listener = new TcpListener(ListenOn, DiagPort);
        listener.Start();
        log.Line($"listening tcp {Where}:{DiagPort}, VIN {car.Vin}, {car.Ecus.Count} modules");
        _ = Task.Run(() => SearchAsync(stop), stop);
        using var reg = stop.Register(listener.Stop);
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            if (OnlyThisPc && client.Client.RemoteEndPoint is IPEndPoint other && !FromThisPc(other.Address))
            {
                log.Line($"tester {other} from another PC refused (other PCs are off in Options)");
                client.Client.LingerState = new LingerOption(true, 0);
                client.Close();
                continue;
            }
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
        testers?.Invoke(peer as IPEndPoint);
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
            testers?.Invoke(null);
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
        if (diagnostics.DropsHere(request))
        {
            log.Line("   connection dropped (practice)");
            throw new IOException("dropped on purpose");
        }
        IEnumerable<Ecu> who = target == Functional
            ? car.Ecus.Values
            : car.Ecus.TryGetValue(target, out var one) ? [one] : [];
        foreach (var ecu in who)
        {
            if (car.Silent.Contains(ecu.Address))
            {
                if (target != Functional) log.Line($"   {ecu.Address:X2} silent (practice: does not answer)");
                continue;
            }
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

    /// <summary>Other PCs off (Options): <see cref="bind"/> is 127.0.0.1, the car listens everywhere but serves this PC only.</summary>
    bool OnlyThisPc => IPAddress.IsLoopback(bind);

    IPAddress ListenOn => OnlyThisPc ? IPAddress.Any : bind;

    string Where => OnlyThisPc ? "0.0.0.0 (this PC only)" : bind.ToString();

    /// <summary>One of this PC's own addresses: a broadcast search leaves from a network card's address, not 127.0.0.1.</summary>
    static bool FromThisPc(IPAddress sender) =>
        IPAddress.IsLoopback(sender) || System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(card => card.GetIPProperties().UnicastAddresses).Any(own => own.Address.Equals(sender));

    /// <summary>"DIAGADR10BMWMAC" + MAC + "BMWVIN" + VIN, as the gateway answers the vehicle search.</summary>
    byte[] Identification() => Encoding.ASCII.GetBytes($"DIAGADR{car.Gateway:X2}BMWMAC001A37DEC550BMWVIN{car.Vin}");

    async Task SearchAsync(CancellationToken stop)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(ListenOn, SearchPort));
        log.Line($"listening udp {Where}:{SearchPort} (vehicle search)");
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult got;
            try { got = await udp.ReceiveAsync(stop); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }
            if (got.Buffer.Length < 6 || BinaryPrimitives.ReadUInt16BigEndian(got.Buffer.AsSpan(4)) != ControlIdent) continue;
            if (OnlyThisPc && !FromThisPc(got.RemoteEndPoint.Address)) continue;
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
