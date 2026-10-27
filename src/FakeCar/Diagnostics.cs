using System.Security.Cryptography;

namespace FakeCar;

/// <summary>UDS answers of one module, like a real F-series module gives them.</summary>
sealed class Diagnostics(Car car, Log log)
{
    const byte ServiceNotSupported = 0x11;
    const byte SubFunctionNotSupported = 0x12;
    const byte RequestOutOfRange = 0x31;
    const byte IncorrectLength = 0x13;

    /// <summary>The answer, or null when the module stays silent (functional requests it does not serve, 3E 80).</summary>
    public byte[]? Answer(Ecu ecu, byte[] req, bool functional)
    {
        if (req.Length == 0) return null;
        var answer = Dispatch(ecu, req);
        // Functional requests: modules keep quiet instead of saying "not supported" / "out of range".
        if (functional && answer is [0x7F, _, ServiceNotSupported or SubFunctionNotSupported or RequestOutOfRange]) return null;
        return answer;
    }

    byte[]? Dispatch(Ecu ecu, byte[] req)
    {
        switch (req[0])
        {
            case 0x10 when req.Length >= 2:
                ecu.Session = (byte)(req[1] & 0x7F);
                if (ecu.Session == 1) ecu.Unlocked = false;
                return Suppress(req[1], [0x50, ecu.Session, 0x00, 0x32, 0x01, 0xF4]);
            case 0x11 when req.Length >= 2:
                log.Line($"  RESET {ecu.Name} [{ecu.Address:X2}] type {req[1]:X2}");
                ecu.Session = 1;
                ecu.Unlocked = false;
                return Suppress(req[1], [0x51, (byte)(req[1] & 0x7F)]);
            case 0x14:
                log.Line($"  CLEAR faults {ecu.Name} [{ecu.Address:X2}] ({ecu.Faults.Count})");
                ecu.Faults.Clear();
                return [0x54];
            case 0x19:
                return ReadFaults(ecu, req);
            case 0x22:
                return ReadDids(ecu, req);
            case 0x2E:
                return WriteDid(ecu, req);
            case 0x27 when req.Length >= 2:
                return SecurityAccess(ecu, req);
            case 0x28 when req.Length >= 2:
                return Suppress(req[1], [0x68, (byte)(req[1] & 0x7F)]);
            case 0x31 when req.Length >= 4:
                return Routine(ecu, req);
            case 0x01 when req.Length >= 2 && ecu.Dids.TryGetValue(0xF442, out var volts):
                // OBD mode 01, answered by the engine computer: 42 = module voltage (mV), 0C = engine speed (rpm x4).
                return req[1] switch
                {
                    0x42 => [0x41, 0x42, .. volts],
                    0x0C => [0x41, 0x0C, 0x00, 0x00],
                    0x0D => [0x41, 0x0D, 0x00],
                    _ => [0x7F, 0x01, RequestOutOfRange],
                };
            case 0x3E when req.Length >= 2:
                return Suppress(req[1], [0x7E, 0x00]);
            case 0x85 when req.Length >= 2:
                return Suppress(req[1], [0xC5, (byte)(req[1] & 0x7F)]);
            default:
                log.Line($"  UNKNOWN service {Convert.ToHexString(req)} to {ecu.Name} [{ecu.Address:X2}]");
                return [0x7F, req[0], ServiceNotSupported];
        }
    }

    /// <summary>Bit 7 of the sub-function: "suppress positive response".</summary>
    static byte[]? Suppress(byte sub, byte[] positive) => (sub & 0x80) != 0 ? null : positive;

    byte[] ReadDids(Ecu ecu, byte[] req)
    {
        if (req.Length < 3 || (req.Length - 1) % 2 != 0) return [0x7F, 0x22, IncorrectLength];
        var answer = new List<byte> { 0x62 };
        var any = false;
        for (var i = 1; i + 1 < req.Length; i += 2)
        {
            var did = (ushort)(req[i] << 8 | req[i + 1]);
            // Live state: active session (F186) and the session state block (F100: session, state, energy mode, ROE).
            byte[]? data = did switch
            {
                0xF186 => [ecu.Session],
                0xF100 => [ecu.Session, 0x00, 0x00, 0x00],
                _ => null,
            };
            if (data is null && !ecu.Dids.TryGetValue(did, out data))
            {
                log.Line($"  MISSING DID {did:X4} at {ecu.Name} [{ecu.Address:X2}]");
                continue;
            }
            answer.Add(req[i]);
            answer.Add(req[i + 1]);
            answer.AddRange(data);
            any = true;
        }
        return any ? [.. answer] : [0x7F, 0x22, RequestOutOfRange];
    }

    byte[] WriteDid(Ecu ecu, byte[] req)
    {
        if (req.Length < 4) return [0x7F, 0x2E, IncorrectLength];
        var did = (ushort)(req[1] << 8 | req[2]);
        ecu.Dids[did] = req[3..];
        log.Line($"  WRITE {ecu.Name} [{ecu.Address:X2}] DID {did:X4} {req.Length - 3} bytes");
        car.Changed(ecu, did);
        return [0x6E, req[1], req[2]];
    }

    static byte[] SecurityAccess(Ecu ecu, byte[] req)
    {
        var level = req[1];
        if ((level & 1) == 1)
        {
            // Seed request: a fresh random seed, like the module.
            // BMW modules hand out an 8-byte seed (PSdZ refuses any other length).
            return [0x67, level, .. (ecu.Unlocked ? new byte[8] : RandomNumberGenerator.GetBytes(8))];
        }
        ecu.Unlocked = true;
        return [0x67, level];
    }

    byte[] Routine(Ecu ecu, byte[] req)
    {
        var control = req[1];
        log.Line($"  ROUTINE {control:X2} {req[2]:X2}{req[3]:X2} {Convert.ToHexString(req[4..])} at {ecu.Name} [{ecu.Address:X2}]");
        return control switch
        {
            0x01 => [0x71, 0x01, req[2], req[3], 0x00],
            0x02 => [0x71, 0x02, req[2], req[3], 0x00],
            0x03 => [0x71, 0x03, req[2], req[3], 0x00],
            _ => [0x7F, 0x31, SubFunctionNotSupported],
        };
    }

    static byte[] ReadFaults(Ecu ecu, byte[] req)
    {
        if (req.Length < 2) return [0x7F, 0x19, IncorrectLength];
        switch (req[1])
        {
            case 0x02:
                {
                    var mask = req.Length > 2 ? req[2] : (byte)0xFF;
                    var answer = new List<byte> { 0x59, 0x02, 0xFF };
                    foreach (var f in ecu.Faults.Where(f => (f.Status & mask) != 0))
                        answer.AddRange([(byte)(f.Code >> 16), (byte)(f.Code >> 8), (byte)f.Code, f.Status]);
                    return [.. answer];
                }
            case 0x04 or 0x06 when req.Length >= 5:
                {
                    var code = req[2] << 16 | req[3] << 8 | req[4];
                    var f = ecu.Faults.FirstOrDefault(x => x.Code == code);
                    if (f is null) return [0x7F, 0x19, RequestOutOfRange];
                    if (req[1] == 0x06) return [0x59, 0x06, req[2], req[3], req[4], f.Status, 0x02, f.Count];
                    // Snapshot: mileage (3 bytes, km) and a time stamp, as BMW modules give it.
                    return [0x59, 0x04, req[2], req[3], req[4], f.Status, 0x01, 0x02, 0x17, 0x00,
                        (byte)(f.Mileage >> 16), (byte)(f.Mileage >> 8), (byte)f.Mileage, 0x17, 0x01, 0x00, 0x00, 0x12, 0x34];
                }
            default:
                return [0x7F, 0x19, SubFunctionNotSupported];
        }
    }
}
