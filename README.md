# Fake Car

A pretend BMW on your network. E-Sys finds it, connects to it and works with it like a real car: read the vehicle order (FA), FP and I-steps, read the module list (SVT), read and write coding, read and clear fault memory, reset modules.

Use it to try E-Sys and your coding tools at your desk, to test a tool before it ever touches a real car, or to record a video without a car (and without showing a real VIN).

Nothing here talks to a real car. Every VIN is made up.

## Car models

| Model | Folder | Modules |
|---|---|---|
| BMW F10 550i xDrive M Sport (US, 2015) | `cars\F10_550i_xDrive_US` | 35 |
| BMW F30 340i M Sport (US, 2017) | `cars\F30_340i_US` | 23 |
| BMW F80 M3 (US, 2017) | `cars\F80_M3_US` | 24 |

Each car is built on your own PC from your own PSdZData, the way the factory puts a car together: the modules its order gets, the software they carry at the current I-step, and the factory coding of every module for that order. So what E-Sys reads is what a real car with that order would answer.

## What you need

- Windows 10 or 11
- E-Sys with PSdZData (the F010 data for the F10, F020 for the F30 and M3)

## Use it

1. Download the latest release and unzip it anywhere.
2. Start `FakeCar.exe`, pick a car, press **Start**. The first start of a car builds it from your PSdZData (about half a minute, once).
3. In E-Sys: **Open Connection**, then either
   - **Connection via VIN**: pick the Fake Car's VIN, or
   - **Connection via gateway URL**: `tcp://127.0.0.1:6801`, with the target of that car's series (F010 or F020).
4. Work as with a real car: Read FA, Read SVT, Read Coding Data, Code, fault memory.

Close the window to switch the car off. What you code stays coded the next time; tick **Start fresh** to go back to the factory coding.

Tested with E-Sys 25.10. ISTA looks for cars the same way over the network, but it is not tested yet.

## Your own car model

Copy a car folder and change two files:

- `FA.xml`: the vehicle order. `typeKey` (type key), `series` (development series, for example F030 for an F30), `timeCriteria` (MMYY), the SA codes.
- `car.txt`: name, a made-up VIN with a valid check digit, the I-steps (`ISTEP_SHIPMENT` is when it was built), mileage, battery.

`faults.txt` lists the faults it has stored (module address, fault code, status, count, km).

Start it in `FakeCar.exe`; it is built on the first start. To build by hand:

```
builder\build-car.cmd cars\F30_340i_US C:\EC-Apps\E-Sys C:\Data
```

## Without the window

```
FakeCar.exe --car cars\F30_340i_US [--bind 127.0.0.1] [--fresh] [--log fakecar.log]
FakeCar.exe --stop
```

Every request and answer is written to `fakecar.log` in the car's folder.

## Build from source

- Fake Car: .NET 10 SDK, `dotnet build src\FakeCar -c Release -o build`
- Builder: JDK 11 or newer, compiled against E-Sys's `lib\PsdZ\psdz-api.jar`:

```
javac --release 11 -cp "C:\EC-Apps\E-Sys\lib\PsdZ\*" -d classes builder\CarBuilder.java
jar cf builder\CarBuilder.jar -C classes .
```

## Good to know

- One tester at a time, like the real central gateway.
- Windows may ask about network access the first time. Other PCs only reach the car if you allow it; E-Sys on the same PC can always use the gateway URL.
- No BMW data is in this repository. The car data is made on your PC from the PSdZData you already have.

Not made by or connected with BMW AG. BMW, E-Sys and ISTA are names of their owners.

## Licence

MIT, see [LICENSE](LICENSE).
