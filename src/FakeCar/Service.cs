using System.Globalization;

namespace FakeCar;

/// <summary>
/// Service data, so service tools (EDIABAS jobs for service reset and battery registration) work on Fake Car:
///   22 F150  which job file a module answers to (3 bytes, the group file picks the job file from it)
///   22 1001  service items (CBS): 12-byte records, kept per car (the written folder) after a reset
///   2E 1001  service item reset: changes the matching record
///   22 402B  battery sensor (temperature, voltage, current), 28 bytes
///   22 4023  battery state (capacity, health, charge), 28 bytes
/// </summary>
static class Service
{
    public const ushort Ident = 0xF150;
    public const ushort Cbs = 0x1001;
    public const ushort BatterySensor = 0x402B;
    public const ushort BatteryState = 0x4023;
    public const ushort Volts = 0xF442;

    public const byte Cluster = 0x60, Engine = 0x12, Brakes = 0x29;
    public const int RecordLength = 12;

    /// <summary>F-series cluster (KOMB01), and the G-series ones: IC_MID (KOMB_G11), IC_HIGH (KOMBSP18).</summary>
    static byte[] ClusterIdent(Ecu ecu) => ecu.Name switch
    {
        "IC_MID" => [0x0F, 0x22, 0x70],
        "IC_HIGH" => [0x0F, 0x28, 0x00],
        _ => [0x0F, 0x20, 0x70],
    };

    /// <summary>N63 V8 (MEVD1728: N63TU_R0), N55 (MEVD172x: MEVD172), B-series and later Bosch engines (8x: DME8FF_R).</summary>
    static byte[] EngineIdent(Ecu ecu) => ecu.Variant switch
    {
        "MEVD1728" => [0x0F, 0x1A, 0x20],
        var v when v.StartsWith("MEVD172", StringComparison.Ordinal) => [0x0F, 0x13, 0xF0],
        var v when v.StartsWith('8') => [0x0F, 0x24, 0x60],
        _ => [0x0F, 0x1A, 0x20],
    };

    /// <summary>F-series DSC (DSC_10), G30 (DSC_G11), F90 (DSC_G30N), G20/G05/G80 brake modules (DSC_G20).</summary>
    static byte[] BrakesIdent(Ecu ecu) => ecu.Variant switch
    {
        "EBC460" => [0x0F, 0x21, 0x00],
        "EBC_SP18" => [0x0F, 0x28, 0x50],
        "CT03" => [0x0F, 0x25, 0x10],
        _ when ecu.Name == "IB" => [0x0F, 0x25, 0x10],
        _ => [0x0F, 0x1B, 0xD0],
    };

    // Service item records: ID, distance left (2, FFFF = none), unit (low 4 bits: 0 %, 1 x10 km, F unused), count (low 5 bits),
    // percent left, forecast km/1000, due month (low 4 bits, F unused), due year - 2000, forecast months, status, spare.
    static readonly string[] ClusterItems =
    [
        "03 FFFF 0F 02 3C 00 08 1B 18 00 00", // brake fluid
        "11 FFFF 0F 01 30 00 05 1B 18 00 00", // vehicle check
        "64 FFFF 0F 01 30 00 05 1B 18 00 00", // linked vehicle check
        "20 FFFF 0F 00 50 00 03 1C 18 00 00", // inspection
        "21 FFFF 0F 00 50 00 03 1C 18 00 00", // emissions inspection
    ];
    static readonly string[] EngineItems = ["01 02EE 01 03 14 0F 0F 3F 18 00 00"]; // engine oil
    static readonly string[] BrakesItems =
    [
        "02 0FA0 01 01 3D 1E 0F 3F 30 00 00", // front brake pads
        "06 1B58 01 01 26 28 0F 3F 30 00 00", // rear brake pads
    ];

    public static void Fill(Car car, Dictionary<string, string> settings, Dictionary<string, string> problems)
    {
        if (car.Ecus.TryGetValue(Engine, out var dme))
        {
            var low = problems.GetValueOrDefault(Practice.LowBatteryKey) == "1";
            var mv = low ? Practice.LowBatteryMv : Int(settings, "BATTERY_MV", 12580);
            var ah = Int(settings, "BATTERY_AH", 90);
            dme.Dids[Volts] = [(byte)(mv >> 8), (byte)mv];
            dme.Dids[BatterySensor] = SensorRecord(20.0, mv / 1000.0, -0.48);
            dme.Dids[BatteryState] = StateRecord(ah, health: 85, charge: low ? 35 : 80);
        }
        Fill(car, settings, Cluster, ClusterIdent, ClusterItems);
        Fill(car, settings, Engine, EngineIdent, EngineItems);
        Fill(car, settings, Brakes, BrakesIdent, BrakesItems);
    }

    static void Fill(Car car, Dictionary<string, string> settings, byte address, Func<Ecu, byte[]> ident, string[] items)
    {
        if (!car.Ecus.TryGetValue(address, out var ecu)) return;
        // car.txt F150_<address>=<6 hex digits> picks another job file for that module.
        ecu.Dids[Ident] = settings.TryGetValue($"F150_{address:X2}", out var own) && own.Length == 6 ? Convert.FromHexString(own) : ident(ecu);
        ecu.Dids[Cbs] = [.. items.SelectMany(i => Convert.FromHexString(i.Replace(" ", "", StringComparison.Ordinal)))];
    }

    static int Int(Dictionary<string, string> settings, string key, int fallback) =>
        int.TryParse(settings.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>22 402B: temperature x10 (signed), voltage (V - 6) / 0.00025, current (A + 200) / 0.08, then 22 sensor bytes.</summary>
    public static byte[] SensorRecord(double celsius, double volts, double amps)
    {
        var t = (short)Math.Round(celsius * 10);
        var v = (ushort)Math.Clamp(Math.Round((volts - 6) / 0.00025), 0, 0xFFFF);
        var a = (ushort)Math.Clamp(Math.Round((amps + 200) / 0.08), 0, 0xFFFF);
        var r = new byte[28];
        r[0] = (byte)(t >> 8); r[1] = (byte)t;
        r[2] = (byte)(v >> 8); r[3] = (byte)v;
        r[4] = (byte)(a >> 8); r[5] = (byte)a;
        return r;
    }

    /// <summary>
    /// 22 4023: capacity (Ah), health % + 128, charge %, season temperature + 128, rest-voltage reading count,
    /// charge now and days 1-5 (Ah / 5), start-ability limit now and days 1-5, charge state % now and days 1-5, 5 diagnostic bytes.
    /// </summary>
    public static byte[] StateRecord(int ah, int health, int charge)
    {
        var r = new byte[28];
        r[0] = (byte)ah;
        r[1] = (byte)(health + 128);
        r[2] = (byte)charge;
        r[3] = 20 + 128;
        r[4] = 3;
        for (var i = 0; i < 6; i++)
        {
            r[5 + i] = (byte)(ah * charge / 100 / 5);
            r[11 + i] = 0x1E;
            r[17 + i] = (byte)Math.Max(0, charge - 5);
        }
        return r;
    }

    /// <summary>
    /// 2E 1001 [n] then n items of 11 bytes: ID, percent, count, distance (2), unit, forecast km, month, year, forecast months, spare.
    /// "Leave it" values: percent FF, distance 8000, unit 0F, forecast FF, month 0F, year 3F. Count 31 = one more.
    /// Null when an item is not in this module (nothing is changed then).
    /// </summary>
    public static byte[]? Reset(byte[] records, byte[] req)
    {
        var n = req[3];
        var updated = (byte[])records.Clone();
        for (var k = 0; k < n; k++)
        {
            var q = req.AsSpan(4 + k * 11, 11);
            var at = Find(updated, q[0]);
            if (at < 0) return null;
            var rec = updated.AsSpan(at, RecordLength);
            if (q[1] != 0xFF) rec[5] = q[1];
            var count = rec[4] & 0x1F;
            count = q[2] == 31 ? Math.Min(30, count + 1) : q[2] & 0x1F;
            rec[4] = (byte)(rec[4] & 0xE0 | count);
            if (q[3] != 0x80 || q[4] != 0x00) { rec[1] = q[3]; rec[2] = q[4]; }
            if ((q[5] & 0x0F) != 0x0F) rec[3] = (byte)(rec[3] & 0xF0 | q[5] & 0x0F);
            if (q[6] != 0xFF) rec[6] = q[6];
            if ((q[7] & 0x0F) != 0x0F) rec[7] = (byte)(rec[7] & 0xF0 | q[7] & 0x0F);
            if (q[8] != 0x3F) rec[8] = q[8];
            if (q[9] != 0xFF) rec[9] = q[9];
        }
        return updated;
    }

    /// <summary>Where the record with this item ID starts, or -1.</summary>
    public static int Find(byte[] records, byte id)
    {
        for (var at = 0; at + RecordLength <= records.Length; at += RecordLength)
            if (records[at] == id) return at;
        return -1;
    }
}
