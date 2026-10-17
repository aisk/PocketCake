# PocketCake

A native **WinUI 3** editor for the **Sonicake Pocket Master** and **Sonicake Smart Box**
multi-effects pedals. It is a C# port of [SonicMaster](https://github.com/skyggedans/SonicMaster)
(Flutter + Rust) with the same features and a standard Fluent (Mica) interface:

* Real-time, two-way editing over **USB-MIDI** (WinRT MIDI) or **Bluetooth LE MIDI** (WinRT GATT, with auto-reconnect)
* Browse, search and load the 50 Factory + 50 User presets; save to any User slot (with an unsaved-changes guard)
* Visual signal chain with drag-to-reorder (NR, FX1, FX2, DLY, RVB around the fixed DRV-AMP-IR-EQ block)
* Every effect's parameters, effect-type picker gated by device model, FS1/FS2 footswitch assignment
* Preset volume, per-preset BPM, master volume, DLY tap tempo with note divisions, tempo-sync divisions
* Global settings (levels, USB mode, re-amp, EXP/FS, backlight, ECO, power, factory reset)
* User IR upload / rename / clear from `.wav`
* **NAM → .clo** clone-profile conversion and upload (WaveNet inference + Wiener–Hammerstein fit, pure C#)
* Preset JSON import / export (compatible with SonicMaster)
* Debug-build developer console (raw traffic, raw frames, capture log)

## Layout

| Path | |
| --- | --- |
| `src/PocketCake.Core` | Platform-free protocol, data tables (embedded JSON), decoders, frame builders, `DeviceService`, and the NAM→.clo DSP (`Dsp/`). |
| `src/PocketCake` | The WinUI 3 app: MIDI/BLE transports, `AppState` (all device actions), view models, and the Presets / IR & NAM slots / Settings views. |
| `tests/PocketCake.Core.Tests` | xUnit tests ported from SonicMaster plus DSP parity tests against goldens from the original Rust generator. |

## Build & run

Requires the .NET 10 SDK on Windows 10 1904x or later. The app is unpackaged and self-contained
on the Windows App SDK.

```powershell
dotnet test tests/PocketCake.Core.Tests
dotnet run --project src/PocketCake -p:Platform=x64
```

### Debug demo mode

Debug builds can replay real device captures without a pedal:

```powershell
$env:POCKETCAKE_DEMO = "1"
dotnet run --project src/PocketCake -p:Platform=x64
```

`POCKETCAKE_DEMO_PRESET=P01`, `POCKETCAKE_DEMO_MODULE=<id>`, `POCKETCAKE_DEMO_PAGE=slots|settings` and
`POCKETCAKE_SNAPSHOT=<file.png>` (render the window to a PNG and exit) help with headless UI checks.

Settings are stored in `%LOCALAPPDATA%\PocketCake\settings.json`; unhandled exceptions are logged to
`crash.log` in the same folder.

## License

Code ported from SonicMaster (MIT).

> Not affiliated with, or endorsed by, Sonicake.
