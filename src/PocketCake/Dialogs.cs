using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketCake.Core.Model;
using PocketCake.Core.Protocol;
using PocketCake.Services;
using PocketCake.ViewModels;

namespace PocketCake;

/// <summary>Modal dialogs: device picker, save, slot actions, upload progress, confirmations and errors.</summary>
public static class Dialogs
{
    private static AppState State => MainWindow.Instance.State;

    private static XamlRoot Root => MainWindow.Instance.Content.XamlRoot;

    private static ContentDialog Create(XamlRoot root, string title, object content) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        DefaultButton = ContentDialogButton.Primary,
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
    };

    private static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    /// <summary>Surfaces a refused (Busy) operation in the status line; the gate itself sets no status.</summary>
    public static void ReportIfBusy(OpResult result)
    {
        if (result.Outcome == OpOutcome.Busy)
        {
            State.ReportStatus("busy with another device operation — try again in a moment");
        }
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm)
    {
        var dialog = Create(root, title, Body(message));
        dialog.PrimaryButtonText = confirm;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task ShowErrorAsync(string title, string message, bool showSupportedFormats = false)
    {
        var stack = new StackPanel { Spacing = 8, MaxWidth = 440 };
        stack.Children.Add(Body(message));

        if (showSupportedFormats)
        {
            stack.Children.Add(Caption("Supported formats:"));

            foreach (var f in NamFormat.SupportedFormats)
            {
                stack.Children.Add(Body($"•  {f}"));
            }
        }

        var dialog = Create(Root, title, stack);
        dialog.CloseButtonText = "OK";
        await dialog.ShowAsync();
    }

    // -------------------------------------------------------------------
    // Device picker
    // -------------------------------------------------------------------

    /// <summary>Transport-aware device picker; returns the chosen transport + device name, or null.</summary>
    public static async Task<(TransportKind Kind, string Target)?> PickDeviceAsync(XamlRoot root, AppState state)
    {
        var kind = state.TransportKind;
        (TransportKind, string)? picked = null;
        var scanVersion = 0;

        var usb = new SelectorBarItem { Text = "USB", Icon = new FontIcon { Glyph = "" } };
        var ble = new SelectorBarItem { Text = "Bluetooth", Icon = new FontIcon { Glyph = "" } };
        var transports = new SelectorBar { Items = { usb, ble }, SelectedItem = kind == TransportKind.Ble ? ble : usb };

        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var message = Caption("");
        message.HorizontalAlignment = HorizontalAlignment.Center;
        message.VerticalAlignment = VerticalAlignment.Center;
        var spinner = new ProgressRing { IsActive = true };

        var area = new Grid { Height = 280 };
        area.Children.Add(list);
        area.Children.Add(message);
        area.Children.Add(spinner);

        var stack = new StackPanel { Spacing = 12, Width = 400 };
        stack.Children.Add(transports);
        stack.Children.Add(area);

        var dialog = Create(root, "Connect a pedal", stack);
        dialog.PrimaryButtonText = "Rescan";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.None;

        async Task Scan()
        {
            var version = ++scanVersion;
            list.ItemsSource = null;
            message.Text = "";
            spinner.IsActive = true;
            dialog.IsPrimaryButtonEnabled = false;

            List<FrameworkElement> rows;

            try
            {
                rows = kind == TransportKind.Usb
                    ? (await MidiTransport.EnumerateAsync()).Select(d => DeviceRow(d.Name,
                        d.Connectable ? "USB MIDI · in + out" : d.HasInput ? "input only" : "output only", d.Connectable)).ToList()
                    : (await BleTransport.ScanAsync(TimeSpan.FromSeconds(3))).Select(d => DeviceRow(d.Name,
                        $"{d.Address} · RSSI {d.Rssi}", d.Name.Length > 0)).ToList();
            }
            catch (Exception e)
            {
                rows = [];
                message.Text = $"Scan failed: {e.Message}";
            }

            if (version != scanVersion)
            {
                return; // superseded by a transport switch / rescan
            }

            spinner.IsActive = false;
            dialog.IsPrimaryButtonEnabled = true;
            list.ItemsSource = rows;

            if (rows.Count == 0 && message.Text.Length == 0)
            {
                message.Text = kind == TransportKind.Usb ? "No USB MIDI devices found" : "No Bluetooth MIDI devices found";
            }
        }

        transports.SelectionChanged += (bar, args) =>
        {
            var k = bar.SelectedItem == ble ? TransportKind.Ble : TransportKind.Usb;

            if (k != kind)
            {
                kind = k;
                _ = Scan();
            }
        };

        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: string name, IsHitTestVisible: true })
            {
                picked = (kind, name);
                dialog.Hide();
            }
        };

        dialog.PrimaryButtonClick += (sender, e) =>
        {
            e.Cancel = true;
            _ = Scan();
        };

        dialog.Opened += (_, _) => _ = Scan();
        await dialog.ShowAsync();

        return picked;
    }

    private static FrameworkElement DeviceRow(string name, string subtitle, bool enabled)
    {
        var text = new StackPanel { Padding = new Thickness(0, 6, 0, 6) };
        text.Children.Add(Body(name.Length == 0 ? "(unnamed)" : name));
        text.Children.Add(Caption(subtitle));

        return new Border { Tag = name, Child = text, Opacity = enabled ? 1 : 0.5, IsHitTestVisible = enabled };
    }

    // -------------------------------------------------------------------
    // Save preset
    // -------------------------------------------------------------------

    public static async Task ShowSavePresetAsync(XamlRoot root, AppState state)
    {
        var current = state.CurrentPreset;
        var nameBox = new TextBox
        {
            Header = "Name",
            MaxLength = 10,
            Text = current is { } c ? state.PresetNames.GetValueOrDefault(c) ?? "" : "",
        };
        var slot = new ComboBox
        {
            Header = "User slot",
            ItemsSource = Enumerable.Range(1, 50)
                .Select(n => new PresetRef(PresetBank.User, n))
                .Select(p => $"{p.Label}  {state.PresetLabelName(p)}")
                .ToList(),
            SelectedIndex = current is { Bank: PresetBank.User } u ? u.Number - 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var stack = new StackPanel { Spacing = 12, Width = 340 };
        stack.Children.Add(nameBox);
        stack.Children.Add(slot);
        stack.Children.Add(Caption("Up to 10 characters. Saving overwrites the slot."));

        var dialog = Create(root, "Save preset", stack);
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Cancel";

        nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
        dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;

        dialog.PrimaryButtonClick += async (_, e) =>
        {
            // Keep the dialog up while the flash write is in flight.
            var deferral = e.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            var result = await state.SavePresetAsync(slot.SelectedIndex + 1, nameBox.Text.Trim());

            if (result.Outcome == OpOutcome.Busy)
            {
                // Nothing was sent: keep the dialog and its input so the user can retry.
                ReportIfBusy(result);
                e.Cancel = true;
                dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
            }

            deferral.Complete();
        };

        await dialog.ShowAsync();
    }

    // -------------------------------------------------------------------
    // User IR / User Profile slot actions
    // -------------------------------------------------------------------

    private static string SlotTitle(int slot, bool isClone) => $"{(isClone ? "User Profile" : "User IR")} {slot + 1}";

    /// <summary>A non-dismissable progress dialog tracking the shared status text and upload progress.</summary>
    public static Action ShowProgress(string title)
    {
        var state = State;
        var status = Body(state.Status ?? "Preparing…");
        var bar = new ProgressBar { Maximum = 1, IsIndeterminate = true };

        var stack = new StackPanel { Spacing = 12, Width = 340 };
        stack.Children.Add(status);
        stack.Children.Add(bar);

        var dialog = Create(Root, title, stack);
        var allowClose = false;
        dialog.Closing += (_, e) => e.Cancel = !allowClose;

        void OnChanged(object? s, PropertyChangedEventArgs e)
        {
            status.Text = state.Status ?? "Preparing…";
            bar.IsIndeterminate = state.ImportProgress is null;
            bar.Value = state.ImportProgress ?? 0;
        }

        state.PropertyChanged += OnChanged;
        _ = dialog.ShowAsync();

        return () =>
        {
            state.PropertyChanged -= OnChanged;
            allowClose = true;
            dialog.Hide();
        };
    }

    /// <summary>Picks a .wav (IR) or .nam (clone) file and uploads it into the slot.</summary>
    public static async Task LoadSlotAsync(int slot, bool isClone)
    {
        var state = State;

        if (state.IsLoading || await MainWindow.Instance.PickFileAsync(SlotUploadFile.Extension(isClone)) is not { } path)
        {
            return;
        }

        // Something else may have started while the picker was open.
        if (state.IsLoading)
        {
            ReportIfBusy(OpResult.Busy);
            return;
        }

        Func<Task<OpResult>> upload;

        try
        {
            if (SlotUploadFile.Problem(path, new FileInfo(path).Length, isClone) is { } problem)
            {
                await ShowErrorAsync("Couldn't read the file", $"{Path.GetFileName(path)}: {problem}.");
                return;
            }

            if (isClone)
            {
                var namJson = await File.ReadAllTextAsync(path);
                NamFormat.AssertSupported(namJson);
                upload = () => state.ConvertAndUploadCloneAsync(slot, namJson, Path.GetFileName(path));
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(path);
                upload = () => state.UploadIrAsync(slot, bytes, Path.GetFileName(path));
            }
        }
        catch (UnsupportedNamFormatException e)
        {
            await ShowErrorAsync("Unsupported model format",
                $"This .nam is a \"{e.Architecture}\" model, which the clone converter can't convert.", showSupportedFormats: true);
            return;
        }
        catch (Exception e)
        {
            await ShowErrorAsync("Couldn't read the file", e.Message);
            return;
        }

        var close = ShowProgress($"Uploading to {SlotTitle(slot, isClone)}");
        ImportException? failure = null;

        try
        {
            ReportIfBusy(await upload());
        }
        catch (ImportException e)
        {
            failure = e;
        }
        finally
        {
            close();
        }

        if (failure is not null)
        {
            await ShowErrorAsync("Upload failed", failure.Message, failure.IsFormatError);
        }
    }

    public static async Task RenameSlotAsync(int slot, bool isClone)
    {
        var state = State;
        var max = isClone ? CloUploadFrame.NameMaxChars : IrUploadFrame.NameMaxChars;
        var box = new TextBox
        {
            Header = "Name",
            MaxLength = max,
            Text = state.EffectDisplayName(ModuleIds.UserSlotBase(isClone) + slot),
            Width = 320,
        };

        var dialog = Create(Root, $"Rename {SlotTitle(slot, isClone)}", box);
        dialog.PrimaryButtonText = "Rename";
        dialog.CloseButtonText = "Cancel";
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = box.Text.Trim();
        ReportIfBusy(await (isClone ? state.RenameCloneSlotAsync(slot, name) : state.RenameIrSlotAsync(slot, name)));
    }

    public static async Task ClearSlotAsync(int slot, bool isClone)
    {
        var detail = isClone ? "This erases the clone profile stored in the slot." : "This erases the impulse response stored in the slot.";

        if (await ConfirmAsync(Root, $"Clear {SlotTitle(slot, isClone)}?", detail, "Clear"))
        {
            ReportIfBusy(await (isClone ? State.ClearCloneSlotAsync(slot) : State.ClearIrSlotAsync(slot)));
        }
    }
}
