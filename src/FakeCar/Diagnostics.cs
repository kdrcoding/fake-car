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
            case 0x34 when req.Length >= 4:
                return RequestDownload(ecu, req);
            case 0x36 when req.Length >= 2:
                return TransferData(ecu, req);
            case 0x37:
                return TransferExit(ecu);
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
        // Coding from another CAFD (version) carries that CAFD's ID: the SVK shows the CAFD now in the module.
        if (ecu.TakeSoftware(req[3..])) SvkChanged(ecu);
        return [0x6E, req[1], req[2]];
    }

    // Flashing: RequestDownload, TransferData blocks, RequestTransferExit, like a module's flash loader.
    const int MaxBlock = 0x0FFA;
    const int Keep = 256 * 1024;

    byte[] RequestDownload(Ecu ecu, byte[] req)
    {
        if (ecu.Session != 2) return [0x7F, 0x34, 0x22]; // conditions not correct: programming session first
        ecu.Download = [];
        ecu.DownloadBytes = 0;
        log.Line($"  FLASH {ecu.Name} [{ecu.Address:X2}] download {Convert.ToHexString(req[1..])}");
        // 0x20: the block length takes 2 bytes.
        return [0x74, 0x20, MaxBlock >> 8, MaxBlock & 0xFF];
    }

    static byte[] TransferData(Ecu ecu, byte[] req)
    {
        if (ecu.Download is null) return [0x7F, 0x36, 0x24]; // request sequence error
        ecu.DownloadBytes += req.Length - 2;
        if (ecu.Download.Count < Keep) ecu.Download.AddRange(req.Skip(2).Take(Keep - ecu.Download.Count));
        return [0x76, req[1]];
    }

    byte[] TransferExit(Ecu ecu)
    {
        if (ecu.Download is null) return [0x7F, 0x37, 0x24];
        log.Line($"  FLASH {ecu.Name} [{ecu.Address:X2}] {ecu.DownloadBytes} bytes in");
        if (ecu.TakeSoftware(ecu.Download)) SvkChanged(ecu);
        ecu.Download = null;
        return [0x77];
    }

    void SvkChanged(Ecu ecu)
    {
        car.Changed(ecu, 0xF101);
        log.Line($"  SVK {ecu.Name} [{ecu.Address:X2}] now {Convert.ToHexString(ecu.Dids[0xF101])}");
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
        if (req[2] == 0x0F && req[3] == 0x1F && control == 0x01) return ecu.TakesFsc ? Swt(ecu, req) : [0x7F, 0x31, RequestOutOfRange];
        return control switch
        {
            0x01 => [0x71, 0x01, req[2], req[3], 0x00],
            0x02 => [0x71, 0x02, req[2], req[3], 0x00],
            0x03 => [0x71, 0x03, req[2], req[3], 0x00],
            _ => [0x7F, 0x31, SubFunctionNotSupported],
        };
    }

    // Enabling codes (SWT routine 0F1F, as BMW's SWT_UDS description has it): the byte after the routine number says
    // what to do; most carry a software ID (application number 2 bytes, upgrade index 2 bytes). Answer: 71 01 0F 1F,
    // the same sub-function, a result (00 = OK), then its data.
    const byte SwtOk = 0x00, SwtInvalidFsc = 0xD1, SwtBadParam = 0xD6;

    byte[] Swt(Ecu ecu, byte[] req)
    {
        if (req.Length < 5) return [0x7F, 0x31, IncorrectLength];
        var what = req[4];
        uint? swid = req.Length >= 9 ? (uint)(req[5] << 24 | req[6] << 16 | req[7] << 8 | req[8]) : null;
        byte[] Ok(params byte[] data) => [0x71, 0x01, 0x0F, 0x1F, what, SwtOk, .. data];
        byte[] Fail(byte result) => [0x71, 0x01, 0x0F, 0x1F, what, result];
        switch (what)
        {
            case 0x04: // status: whole SWT status, then per code its software ID, signature status, status, type
                {
                    var list = new List<byte> { 0x02, 0x02, 0x02 };
                    foreach (var id in ecu.Fscs.Keys) list.AddRange([(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id, 0x02, 0x02, 0x02]);
                    return Ok([.. list]);
                }
            case 0x07 when swid is { } id && req.Length > 9: // write a code
                ecu.Fscs[id] = req[9..];
                car.FscChanged(ecu, id);
                log.Line($"  FSC {ecu.Name} [{ecu.Address:X2}] {id:X8} written ({req.Length - 9} bytes)");
                return Ok();
            case 0x0B when swid is { } id: // read a code
                return ecu.Fscs.TryGetValue(id, out var fsc) ? Ok(fsc) : Fail(SwtInvalidFsc);
            case 0x09 when swid is { } id: // check a code
                return ecu.Fscs.ContainsKey(id) ? Ok() : Fail(SwtInvalidFsc);
            case 0x0A when swid is { } id: // cancel (delete) a code
                if (!ecu.Fscs.Remove(id)) return Fail(SwtInvalidFsc);
                car.FscChanged(ecu, id);
                log.Line($"  FSC {ecu.Name} [{ecu.Address:X2}] {id:X8} cancelled");
                return Ok();
            case 0x01: // software IDs this module has codes for
                {
                    var list = new List<byte> { ecu.Address };
                    foreach (var id in ecu.Fscs.Keys) list.AddRange([(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]);
                    return Ok([.. list]);
                }
            case 0x0E: // SWT version: specification document number (8), sub document (3), version (2), library version (4)
                return Ok([.. "00000000"u8, .. "000"u8, .. "01"u8, 0x04, 0x00, 0x00, 0x00]);
            case 0x0D: // module data for the enabling server
                return Ok([.. car.VinBytes[^7..], ecu.Address]);
            case 0x03 or 0x05 or 0x06 or 0x08 or 0x0C or 0x0F: // signatures and certificates: accepted
                return Ok();
            default:
                return Fail(SwtBadParam);
        }
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
