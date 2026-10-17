---
name: pocketcake
description: Drive a Sonicake Pocket Master / Smart Box guitar pedal through the PocketCake editor's MCP tools. Use when asked to build or tweak a guitar tone, edit, browse or save pedal presets, change pedal settings, or load IRs / NAM clone models onto the pedal.
---

# PocketCake

PocketCake is a Windows editor for the Sonicake Pocket Master and Smart Box multi-effects pedals. Its
MCP tools (`get_status`, `get_preset`, `set_parameters`, …) act on the running app, and every edit shows
up in its window as the user watches.

## Setup

The tools exist only while PocketCake runs with **Settings → App → Agent control** switched on. If they are
missing, ask the user to turn it on, then copy the setup command from Settings → Agent control (**Copy setup
command**) and run it. The endpoint requires the token that command carries; if calls fail with
"token required", the token was reset and the setup command must be copied again.

## The pedal

A preset is a signal chain of nine modules. Each module runs one effect, can be on or off, and can be
assigned to footswitch FS1 or FS2 (at most three modules per switch).

| Module | Role |
| --- | --- |
| NR | Noise gate (fixed effect) |
| FX1, FX2 | Compressors, filters, modulation, pitch… |
| DRV | Overdrive / distortion / fuzz |
| AMP | Amp model; in *clone mode* it plays a User Profile (a converted NAM model) instead |
| IR | Cabinet impulse response, including 5 User IR slots |
| EQ | Equalizer |
| DLY | Delay (Time in ms, or note divisions while its Sync toggle is on) |
| RVB | Reverb |

DRV → AMP → IR → EQ is a fixed block; the other five modules can sit anywhere before or after it.
The pedal holds 50 User presets (P01–P50, writable) and 50 Factory presets (F01–F50, read-only).

## Workflow

1. `get_status`. With no pedal connected, `list_devices` + `connect`, or `start_demo` (a virtual Smart
   Box, safe for anything) when the user has no hardware at hand.
2. `get_preset` to read the live preset before changing it; tool results return the updated module, so
   re-read only after an error.
3. Edit live: `set_effect`, `set_parameters`, `set_module_enabled`, `set_chain_order`,
   `set_preset_settings`. `list_effects` shows what a module can run (`details: true` adds descriptions and
   parameter ranges). Changing an effect resets that module's parameters, so set the effect first, then
   its parameters.
4. Edits are live sound only. `save_preset` stores them; `load_preset` and `import_preset` discard unsaved
   edits (the user is asked to approve that), so finish or save the current work before loading another
   preset.
5. A "busy" error means another device operation or agent call is still running; wait a moment and retry.

## Values

- Parameters are keyed by the names `get_preset` reports. Numbers snap to the nearest step the pedal
  accepts; read the returned value to see where it landed.
- `choice` parameters take one of their `options`; `toggle` parameters take true/false.
- Turning a Sync toggle on turns that effect's Rate/Time into a `choice` of note divisions; `get_preset`
  reflects the switch.

## Flash writes

`save_preset`, `upload_ir`, `upload_clone`, `rename_user_slot` and `clear_user_slot` overwrite storage
on the pedal. Each pops a confirmation in PocketCake that the user must accept, so tell the user what
you are about to write (slot, name, what gets replaced) and wait for their go-ahead before calling one.
If they decline in the app, the tool fails with "declined"; leave the slot as it is. Save only into a slot
the user agreed to (`list_presets bank=user` shows what each holds).

In demo mode effect and amp-mode changes revert on the next state read (it replays fixed captures);
parameters, toggles, chain order and saves behave normally.
