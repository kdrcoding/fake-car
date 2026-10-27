using System.Globalization;
using System.Text;

namespace FakeCar;

/// <summary>One module of the car: its diagnostic address, data identifiers, fault memory and session state.</summary>
sealed class Ecu
{
    public Ecu(byte address, string name)
    {
        Address = address;
        Name = name;
    }

    public byte Address { get; }
    public string Name { get; }
    public Dictionary<ushort, byte[]> Dids { get; } = [];
    public List<Fault> Faults { get; } = [];
    public byte Session { get; set; } = 1;
    public bool Unlocked { get; set; }

    /// <summary>The start of the software being flashed now (enough to find its SGBMID), and how much came in.</summary>
    public List<byte>? Download { get; set; }

    /// <summary>Enabling codes (FSC) by software ID (application number, upgrade index), as the module keeps them.</summary>
    public SortedDictionary<uint, byte[]> Fscs { get; } = [];

    /// <summary>Modules that take enabling codes on F-series: the head unit and the instrument cluster.</summary>
    public bool TakesFsc => Name.StartsWith("HU", StringComparison.Ordinal) || Name.EndsWith("KOMBI", StringComparison.Ordinal);
    public long DownloadBytes { get; set; }

    /// <summary>
    /// The SVK (22 F101) follows what is put into the module: a flashed software unit or a coded CAFD carries its own
    /// SGBMID (process class, number, version), like the header a real module reads its SVK from. An entry with the same
    /// process class and number takes the new version. True when the SVK changed.
    /// </summary>
    public bool TakeSoftware(IReadOnlyList<byte> data)
    {
        if (!Dids.TryGetValue(0xF101, out var svk) || svk.Length < 16) return false;
        for (var e = 8; e + 8 <= svk.Length; e += 8)
        {
            for (var i = 0; i + 8 <= data.Count; i++)
            {
                if (data[i] != svk[e] || data[i + 1] != svk[e + 1] || data[i + 2] != svk[e + 2] || data[i + 3] != svk[e + 3] || data[i + 4] != svk[e + 4]) continue;
                // A version (not erased flash) that differs from the one installed.
                if (data[i + 5] == 0xFF || (data[i + 5] == svk[e + 5] && data[i + 6] == svk[e + 6] && data[i + 7] == svk[e + 7])) continue;
                var changed = (byte[])svk.Clone();
                changed[e + 5] = data[i + 5];
                changed[e + 6] = data[i + 6];
                changed[e + 7] = data[i + 7];
                Dids[0xF101] = changed;
                return true;
            }
        }
        return false;
    }
}

/// <summary>A stored fault: 3-byte code, status byte, and how often it happened.</summary>
sealed record Fault(int Code, byte Status, byte Count, int Mileage);

/// <summary>The whole car, loaded from a car folder made by the generator.</summary>
sealed class Car
{
    public required string Vin { get; init; }
    public byte[] VinBytes => System.Text.Encoding.ASCII.GetBytes(Vin);
    public required string Folder { get; init; }
    public Dictionary<byte, Ecu> Ecus { get; } = [];
    public byte Gateway => 0x10;

    public static Car Load(string folder)
    {
        var settings = ReadSettings(Path.Combine(folder, "car.txt"));
        var car = new Car { Vin = settings["VIN"], Folder = folder };

        foreach (var line in File.ReadAllLines(Path.Combine(folder, "modules.txt")))
        {
            var p = line.Split('\t');
            if (p.Length < 3 || line.StartsWith('#')) continue;
            var address = byte.Parse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            car.Ecus[address] = new Ecu(address, p[1].Split('-')[0]);
        }

        foreach (var line in File.ReadAllLines(Path.Combine(folder, "svk.txt")))
        {
            var p = line.Split('\t');
            if (p.Length < 2) continue;
            var ecu = car.Ecus[byte.Parse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)];
            ecu.Dids[0xF101] = Convert.FromHexString(p[1].Trim());
        }

        // The CAF's default values first (every block the module has), then the factory coding for this order on top.
        foreach (var layer in new[] { "NCD_default", "NCD" })
        {
            var ncdRoot = Path.Combine(folder, layer);
            if (!Directory.Exists(ncdRoot)) continue;
            foreach (var dir in Directory.GetDirectories(ncdRoot))
            {
                if (!car.Ecus.TryGetValue(byte.Parse(Path.GetFileName(dir), NumberStyles.HexNumber, CultureInfo.InvariantCulture), out var ecu)) continue;
                foreach (var ncd in Directory.GetFiles(dir)) LoadNcd(ecu, ncd);
            }
        }

        var vin = Encoding.ASCII.GetBytes(car.Vin);
        foreach (var ecu in car.Ecus.Values) FillIdentity(ecu, vin);

        // The vehicle order, FP and I-steps live in the gateway's VCM (F-series).
        var gw = car.Ecus[car.Gateway];
        foreach (var line in File.ReadAllLines(Path.Combine(folder, "vcm.txt")))
        {
            var p = line.Split('\t');
            if (p.Length == 2) gw.Dids[p[0] == "FA" ? Dids.Fa : Dids.Fp] = VcmRecord(Convert.FromHexString(p[1].Trim()));
        }
        gw.Dids[Dids.IStep] = IStepBytes(settings["ISTEP_CURRENT"], settings["ISTEP_LAST"], settings["ISTEP_SHIPMENT"]);

        // Odometer in the gateway (and cluster): 3 bytes, km.
        var km = int.Parse(settings.GetValueOrDefault("KM", "48312"), CultureInfo.InvariantCulture);
        foreach (var a in new byte[] { car.Gateway, 0x60 })
            if (car.Ecus.TryGetValue(a, out var withKm)) withKm.Dids[0x2540] = [(byte)(km >> 16), (byte)(km >> 8), (byte)km];
        // Battery: voltage at the engine computer (mV) and its registered capacity (Ah).
        if (car.Ecus.TryGetValue(0x12, out var dme))
        {
            var mv = int.Parse(settings.GetValueOrDefault("BATTERY_MV", "12580"), CultureInfo.InvariantCulture);
            dme.Dids[0xF442] = [(byte)(mv >> 8), (byte)mv];
            dme.Dids[0x4023] = [byte.Parse(settings.GetValueOrDefault("BATTERY_AH", "90"), CultureInfo.InvariantCulture)];
        }

        // Terminal status at the body module (CAS on F-series, BDC on G): 0x0A = ignition on (KL15).
        if (car.Ecus.TryGetValue(0x40, out var body)) body.Dids[0xDC56] = [0x0A];

        LoadFaults(car, Path.Combine(folder, "faults.txt"));
        car.LoadWritten();
        return car;
    }

    string WrittenFolder => Path.Combine(Folder, "written");

    /// <summary>What was coded into the car stays coded after a restart (until "--fresh").</summary>
    public void Changed(Ecu ecu, ushort did)
    {
        Directory.CreateDirectory(WrittenFolder);
        File.WriteAllText(Path.Combine(WrittenFolder, $"{ecu.Address:X2}_{did:X4}.hex"), Convert.ToHexString(ecu.Dids[did]));
    }

    public void FscChanged(Ecu ecu, uint swid)
    {
        Directory.CreateDirectory(WrittenFolder);
        var file = Path.Combine(WrittenFolder, $"{ecu.Address:X2}_FSC_{swid:X8}.hex");
        if (ecu.Fscs.TryGetValue(swid, out var fsc)) File.WriteAllText(file, Convert.ToHexString(fsc));
        else File.Delete(file);
    }

    void LoadWritten()
    {
        if (!Directory.Exists(WrittenFolder)) return;
        foreach (var file in Directory.GetFiles(WrittenFolder, "*_FSC_*.hex"))
        {
            var p = Path.GetFileNameWithoutExtension(file).Split('_');
            if (p.Length == 3 && Ecus.TryGetValue(byte.Parse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture), out var withFsc))
                withFsc.Fscs[uint.Parse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = Convert.FromHexString(File.ReadAllText(file).Trim());
        }
        foreach (var file in Directory.GetFiles(WrittenFolder, "*.hex"))
        {
            var p = Path.GetFileNameWithoutExtension(file).Split('_');
            if (p.Length == 2 && Ecus.TryGetValue(byte.Parse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture), out var ecu))
                ecu.Dids[ushort.Parse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = Convert.FromHexString(File.ReadAllText(file).Trim());
        }
    }

    public void Forget()
    {
        if (Directory.Exists(WrittenFolder)) Directory.Delete(WrittenFolder, true);
    }

    /// <summary>Coding data from an NCD (Motorola S-records): address = DID * 256 + offset inside the block.</summary>
    static void LoadNcd(Ecu ecu, string path)
    {
        var blocks = new SortedDictionary<ushort, SortedDictionary<int, byte[]>>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (!line.StartsWith("S2", StringComparison.Ordinal) || line.Length < 12) continue;
            var count = Convert.ToInt32(line.Substring(2, 2), 16);
            var address = Convert.ToInt32(line.Substring(4, 6), 16);
            var data = Convert.FromHexString(line.Substring(10, (count - 4) * 2));
            var did = (ushort)(address >> 8);
            if (!blocks.TryGetValue(did, out var parts)) blocks[did] = parts = [];
            parts[address & 0xFF] = data;
        }
        foreach (var (did, parts) in blocks)
        {
            var size = parts.Max(p => p.Key + p.Value.Length);
            var block = new byte[size];
            foreach (var (offset, data) in parts) data.CopyTo(block, offset);
            ecu.Dids[did] = block;
        }
    }

    static void FillIdentity(Ecu ecu, byte[] vin)
    {
        var a = ecu.Address;
        ecu.Dids.TryAdd(0xF190, vin);
        ecu.Dids.TryAdd(0xF186, [0x01]);
        ecu.Dids.TryAdd(0xF18A, Encoding.ASCII.GetBytes("000" + (100 + a).ToString(CultureInfo.InvariantCulture)));
        ecu.Dids.TryAdd(0xF18B, [0x15, 0x06, (byte)(0x01 + a % 0x1C)]);
        ecu.Dids.TryAdd(0xF18C, Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{0x4D2A00 + a * 37:X6}{a:X2}0550")));
        ecu.Dids.TryAdd(0xF187, Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{9_200_000 + a * 1013:D7}")));
        ecu.Dids.TryAdd(0xF191, Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{9_300_000 + a * 911:D7}")));
        ecu.Dids.TryAdd(0xF197, Encoding.ASCII.GetBytes(ecu.Name));
        // Flash timing parameters (seconds): erase, check memory, bootloader install, authentication, reset.
        ecu.Dids.TryAdd(0x2504, [0x00, 0x1E, 0x00, 0x0A, 0x00, 0x0A, 0x00, 0x05, 0x00, 0x02]);
        // Energy mode: 00 = normal (not transport / flash mode).
        ecu.Dids.TryAdd(0x100A, [0x00]);
        // Coding stamp: the last 7 of the VIN this module was coded on.
        ecu.Dids.TryAdd(0x37FE, [.. vin[^7..], 0x00, 0x00]);
    }

    /// <summary>A VCM record: 2-byte length, the data (no signature part).</summary>
    static byte[] VcmRecord(byte[] data) => [(byte)(data.Length >> 8), (byte)data.Length, .. data];

    /// <summary>24 bytes: current, last, shipment; each 4 letters, year, month, 2-byte counter.</summary>
    static byte[] IStepBytes(params string[] steps)
    {
        var b = new List<byte>();
        foreach (var s in steps)
        {
            var p = s.Split('-');
            b.AddRange(Encoding.ASCII.GetBytes(p[0]));
            b.Add(byte.Parse(p[1], CultureInfo.InvariantCulture));
            b.Add(byte.Parse(p[2], CultureInfo.InvariantCulture));
            var n = int.Parse(p[3], CultureInfo.InvariantCulture);
            b.Add((byte)(n >> 8));
            b.Add((byte)n);
        }
        return [.. b];
    }

    static void LoadFaults(Car car, string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            var p = line.Split('\t');
            if (p.Length < 5 || line.StartsWith('#')) continue;
            var address = byte.Parse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (!car.Ecus.TryGetValue(address, out var ecu)) continue;
            ecu.Faults.Add(new Fault(Convert.ToInt32(p[1], 16), Convert.ToByte(p[2], 16), byte.Parse(p[3], CultureInfo.InvariantCulture), int.Parse(p[4], CultureInfo.InvariantCulture)));
        }
    }

    internal static Dictionary<string, string> ReadSettings(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var i = line.IndexOf('=', StringComparison.Ordinal);
            if (i > 0 && !line.StartsWith('#')) d[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return d;
    }
}

/// <summary>Data identifiers the car keeps outside the coding blocks.</summary>
static class Dids
{
    // Learnt from what E-Sys asks for (VCM on the gateway); see the request log when a new one shows up.
    public const ushort Fa = 0x3F06;
    public const ushort Fp = 0x3F09;
    public const ushort IStep = 0x100B;
}
