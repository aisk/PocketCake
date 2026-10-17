# PocketCake

A native Windows editor for the **Sonicake Pocket Master** and **Sonicake Smart Box**
multi-effects pedals, built with **WinUI 3** and a Fluent (Mica) interface.

![PocketCake](screenshot.png)

## Features

* Real-time, two-way editing over **USB-MIDI** or **Bluetooth LE MIDI** (with auto-reconnect)
* Browse, search and load the Factory and User presets; save to any User slot
* Signal chain view with drag-to-reorder and per-module on/off
* Every effect parameter, effect type picker, FS1/FS2 footswitch assignment, DLY tap tempo
* Preset volume, per-preset BPM and master volume
* Global settings: levels, USB audio mode, re-amp, EXP/FS jack, backlight, ECO, power, factory reset
* User IR upload / rename / clear from `.wav`
* **NAM → .clo** clone-profile conversion and upload (an approximation, see [below](#nam-conversion))
* Preset JSON import / export

## NAM conversion

The pedal can't run [Neural Amp Modeler](https://www.neuralampmodeler.com/) models. Its User
Profiles use Sonicake's own `.clo` clone format, a small fixed model running at 44.1 kHz:

```
input → pre-filter (128-tap FIR) → asymmetric exponential saturator (4 parameters) → post-filter (2048-tap FIR) → output
```

To get a `.nam` onto the pedal, PocketCake measures the NAM model and fits that structure to it,
all on the PC:

1. **Probe.** A built-in reference DI clip is run through the NAM WaveNet (a C# inference engine) at
   five input levels, from very quiet to full scale.
2. **Fit.** The pre-filter starts from the ratio between the quiet and loud responses (smoothed,
   minimum-phase) and the post-filter from the hard-driven response. Coordinate descent then
   alternates between fitting the saturator to the deconvolved targets and re-estimating the
   post-filter from Welch-averaged cross-spectra across all levels.
3. **Upload.** The filters are resampled to 44.1 kHz and packed with the saturator gains into a
   `.clo`.

This is system identification, not neural-network training: there is no gradient descent or
random initialization, a conversion takes seconds, and the same `.nam` always produces the same
`.clo`.

### Limitations

* **It is an approximation.** The result can only be as good as a filter-saturator-filter model
  allows: it captures the overall EQ, cabinet colour and a single static distortion curve.
* **One gain stage.** Amps with several interacting stages, or very high-gain models, lose detail.
  The character of the clipping comes out closer at some input levels than at others.
* **No dynamics.** Power-supply sag, compression and other time-dependent behaviour are not
  modelled; touch sensitivity will feel flatter than the original.
* **Fixed probe signal.** The fit is driven by one reference DI, so playing outside its range
  (e.g. very hot pickups) may differ from the NAM model.
* **Supported files.** Only NAM WaveNet models (v0.5.x – v0.7.x, including Tone3000's
  SlimmableContainer) can be converted; LSTM and other architectures are rejected.

## Acknowledgements

The device protocol, data tables and NAM conversion are based on
[SonicMaster](https://github.com/Skyggedans/SonicMaster), whose author reverse-engineered the
pedal's USB-MIDI and BLE protocol. PocketCake ports that work to C# with its own WinUI 3 interface.

## Build & run

Requires the .NET 10 SDK on Windows 10 1904x or later. The app is unpackaged and self-contained
on the Windows App SDK.

```powershell
dotnet test tests/PocketCake.Core.Tests
dotnet run --project src/PocketCake -p:Platform=x64
```

| Path | |
| --- | --- |
| `src/PocketCake.Core` | Platform-free protocol, data tables, decoders, frame builders and the NAM → .clo DSP. |
| `src/PocketCake` | The WinUI 3 app: MIDI / BLE transports, app state, view models and views. |
| `tests/PocketCake.Core.Tests` | xUnit tests, including DSP parity tests against SonicMaster goldens. |

Debug builds can replay real device captures without a pedal by setting `POCKETCAKE_DEMO=1`, and
include a developer console for raw MIDI traffic.

Settings are stored in `%LOCALAPPDATA%\PocketCake\settings.json`; unhandled exceptions are logged to
`crash.log` in the same folder.

## License

MIT. Copyright (c) 2026 An Long; portions ported from SonicMaster, copyright (c) 2026 Andrii Klaptsov.

> Not affiliated with, or endorsed by, Sonicake.
