using PocketCake.Core.Device;
using PocketCake.Core.Model;
using PocketCake.Core.Protocol;

namespace PocketCake.ViewModels;

/// <summary>Stored-preset commands: rename, copy, factory reset and <c>.prst</c> files. Each writes flash directly.</summary>
public sealed partial class AppState
{
    public Task<OpResult> RenamePresetAsync(PresetRef preset, string name) => RunExclusiveAsync(async () =>
    {
        if (preset.Bank != PresetBank.User)
        {
            return OpResult.Fail("only User presets can be renamed");
        }

        var stored = new string(name.Where(c => c is >= ' ' and < (char)0x7F).Take(PresetCommandFrame.NameChars).ToArray()).Trim();

        if (stored.Length == 0)
        {
            return OpResult.Fail("the name needs at least one ASCII character");
        }

        return await StoreCommandAsync(PresetCommandFrame.Rename(preset.Index, stored), preset, stored, reload: false,
            $"renamed {preset.Label} to \"{stored}\"");
    });

    public Task<OpResult> CopyPresetAsync(PresetRef source, PresetRef destination) => RunExclusiveAsync(async () =>
    {
        if (destination.Bank != PresetBank.User)
        {
            return OpResult.Fail("presets can only be copied into User presets");
        }

        if (source == destination)
        {
            return OpResult.Fail("source and destination are the same preset");
        }

        return await StoreCommandAsync(PresetCommandFrame.Copy(source.Index, destination.Index), destination,
            PresetNames.GetValueOrDefault(source), reload: true, $"copied {source.Label} to {destination.Label}");
    });

    public Task<OpResult> ResetPresetAsync(PresetRef preset) => RunExclusiveAsync(async () =>
    {
        if (preset.Bank != PresetBank.User)
        {
            return OpResult.Fail("only User presets can be reset");
        }

        // P01 restores F01, and so on.
        var factory = new PresetRef(PresetBank.Factory, preset.Number);

        return await StoreCommandAsync(PresetCommandFrame.Reset(preset.Index), preset,
            PresetNames.GetValueOrDefault(factory), reload: true, $"reset {preset.Label} to {factory.Label}");
    });

    /// <summary>Reads a stored preset as a <c>.prst</c> file; null (with <see cref="Status"/> set) on failure.</summary>
    public async Task<(byte[] Bytes, string FileName)?> ExportPresetFileAsync(PresetRef preset)
    {
        (byte[], string)? exported = null;

        var result = await RunExclusiveAsync(async () =>
        {
            try
            {
                Status = $"reading {preset.Label}…";

                if (await _service.RequestPresetFileAsync(preset.Index) is not { } reply ||
                    PresetFile.FromReply(reply, EffectiveModel) is not { } file)
                {
                    return Fail($"export: {preset.Label} did not answer");
                }

                exported = (file.ToBytes(GlobalSettings?.FirmwareVersion), file.FileName(preset));
                Status = $"read {preset.Label}";

                return OpResult.Ok;
            }
            catch (Exception e)
            {
                return Fail($"export failed: {e.Message}");
            }
        });

        if (result.Outcome == OpOutcome.Busy)
        {
            Status = result.Message;
        }

        return exported;
    }

    /// <summary>Writes a <c>.prst</c> file into User preset <paramref name="destination"/>.</summary>
    public Task<OpResult> ImportPresetFileAsync(PresetRef destination, PresetFile file) => RunExclusiveAsync(async () =>
    {
        if (destination.Bank != PresetBank.User)
        {
            return OpResult.Fail("preset files can only be loaded into User presets");
        }

        ImportProgress = null;

        try
        {
            Status = $"writing {destination.Label}…";

            var result = await _service.ImportPresetFileAsync(file.ImportChunks(destination.Index), (sent, total) =>
                _dispatcher.TryEnqueue(() => ImportProgress = total == 0 ? null : (double)sent / total));

            if (result != WriteResult.Committed)
            {
                return Fail($"loading {destination.Label} failed (no ACK)");
            }

            await AfterStoreCommandAsync(destination, file.Name, reload: true);
            Status = $"loaded \"{file.Name}\" into {destination.Label}";

            return OpResult.Ok;
        }
        catch (Exception e)
        {
            return Fail($"loading {destination.Label} failed: {e.Message}");
        }
        finally
        {
            ImportProgress = null;
        }
    });

    private async Task<OpResult> StoreCommandAsync(string frame, PresetRef target, string? name, bool reload, string done)
    {
        try
        {
            if (!await _service.SendAndAwaitAckAsync(frame))
            {
                return Fail($"{target.Label}: no ACK (timeout)");
            }

            await AfterStoreCommandAsync(target, name, reload);
            Status = done;

            return OpResult.Ok;
        }
        catch (Exception e)
        {
            return Fail($"{target.Label}: {e.Message}");
        }
    }

    /// <summary>
    /// Mirrors a stored-preset change locally. When the pedal is playing <paramref name="target"/>, its live
    /// buffer still holds the old content, so (like the official app) re-select it and re-read the state.
    /// </summary>
    private async Task AfterStoreCommandAsync(PresetRef target, string? name, bool reload)
    {
        if (name is not null)
        {
            PresetNames = new Dictionary<PresetRef, string>(PresetNames) { [target] = name };
        }

        if (!reload || CurrentPreset != target || Data?.Commands.PresetSelect(target.Bank, target.Number) is not { } select)
        {
            return;
        }

        // Let the flash write commit before re-selecting.
        await Task.Delay(600);
        await _service.SendAndAwaitAckAsync(select);
        await Task.Delay(300);
        await RefreshDecodedStateCoreAsync();
        IsModified = false;
    }
}
