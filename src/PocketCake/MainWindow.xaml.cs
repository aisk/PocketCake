using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;
using PocketCake.Core.Model;
using PocketCake.Services;
using PocketCake.ViewModels;
using PocketCake.Views;

namespace PocketCake;

/// <summary>The shell: title bar (master volume, connection), navigation between the pages, status line.</summary>
public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> PageTypes = new()
    {
        ["presets"] = typeof(PresetsView),
        ["slots"] = typeof(SlotsView),
        ["settings"] = typeof(SettingsView),
#if DEBUG
        ["console"] = typeof(DevConsole),
#endif
    };

    public MainWindow()
    {
        Instance = this;
        State = new AppState(DispatcherQueue, AppSettings.Load());
        Settings = new SettingsViewModel(State);
        Presets = new PresetsViewModel(State);
        Slots = new SlotsViewModel(State);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // AppWindow sizes are physical pixels; scale the design size by the monitor DPI.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(840 * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(860 * scale);
            presenter.PreferredMinimumHeight = (int)(600 * scale);
        }

#if DEBUG
        ConsoleItem.Visibility = Visibility.Visible;
#endif

        Nav.SelectedItem = Nav.MenuItems[0];
        State.PropertyChanged += OnStateChanged;
        RefreshConnection();

        Root.Loaded += async (_, _) =>
        {
            await State.InitializeAsync();
#if DEBUG
            await DebugSnapshotAsync();
#endif
        };
    }

    public static MainWindow Instance { get; private set; } = null!;

    public AppState State { get; }

    public SettingsViewModel Settings { get; }

    public PresetsViewModel Presets { get; }

    public SlotsViewModel Slots { get; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;

        if (tag is null || !PageTypes.TryGetValue(tag, out var type) || ContentFrame.CurrentSourcePageType == type)
        {
            return;
        }

        // Pages cache themselves (NavigationCacheMode.Required); there is no back navigation.
        ContentFrame.Navigate(type, null, args.RecommendedNavigationTransitionInfo);
        ContentFrame.BackStack.Clear();
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    // -------------------------------------------------------------------
    // Connection
    // -------------------------------------------------------------------

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.IsConnected) or nameof(AppState.ConnectedDeviceName) or nameof(AppState.IsReconnecting)
            or nameof(AppState.TransportKind))
        {
            RefreshConnection();
        }
    }

    private void RefreshConnection()
    {
        var connected = State.IsConnected;

        ConnectText.Text = connected ? State.ConnectedDeviceName ?? "Connected" : State.IsReconnecting ? "Reconnecting…" : "Connect";
        ConnectGlyph.Glyph = !connected ? "" : State.TransportKind == TransportKind.Ble ? "" : "";
        ConnectButton.Style = (Style)Application.Current.Resources[connected ? "DefaultButtonStyle" : "AccentButtonStyle"];
        ConnectButton.IsEnabled = connected || !State.IsReconnecting;
        ConnectGlyph.Foreground = connected
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (!State.IsConnected)
        {
            await ConnectAsync();
            return;
        }

        var disconnect = new MenuFlyoutItem { Text = "Disconnect", Icon = new FontIcon { Glyph = "" } };
        disconnect.Click += async (_, _) => await State.DisconnectAsync();

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(disconnect);
        menu.ShowAt(ConnectButton);
    }

    /// <summary>Opens the device picker and connects to the chosen pedal.</summary>
    public async Task ConnectAsync()
    {
        if (await Dialogs.PickDeviceAsync(Root.XamlRoot, State) is { } pick)
        {
            await State.ConnectAndPersistAsync(pick.Kind, pick.Target);
        }
    }

    // -------------------------------------------------------------------
    // File pickers
    // -------------------------------------------------------------------

    /// <summary>Shows a file-open picker for <paramref name="extension"/>; returns the path or null.</summary>
    public async Task<string?> PickFileAsync(string extension)
    {
        var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(extension);

        try
        {
            return (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            State.ReportStatus($"file picker failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Shows a file-save picker for a <paramref name="extension"/> file; returns the path or null.</summary>
    public async Task<string?> PickSaveFileAsync(string suggestedName, string typeName, string extension)
    {
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(typeName, [extension]);

        try
        {
            return (await picker.PickSaveFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            State.ReportStatus($"file picker failed: {ex.Message}");
            return null;
        }
    }

#if DEBUG
    /// <summary>
    /// Debug hook for headless UI checks: POCKETCAKE_DEMO_PRESET (P01…) loads a preset,
    /// POCKETCAKE_DEMO_MODULE selects a module, POCKETCAKE_DEMO_PAGE (slots / settings) picks
    /// the page, POCKETCAKE_THEME=dark forces the dark theme, and POCKETCAKE_SNAPSHOT=&lt;png&gt;
    /// renders the window to a file and exits.
    /// </summary>
    private async Task DebugSnapshotAsync()
    {
        if (Environment.GetEnvironmentVariable("POCKETCAKE_THEME") == "dark")
        {
            Root.RequestedTheme = ElementTheme.Dark;
        }

        if (Environment.GetEnvironmentVariable("POCKETCAKE_DEMO_PRESET") is { Length: 3 } label &&
            PresetRef.All.FirstOrDefault(p => p.Label == label) is { Number: > 0 } preset)
        {
            await State.LoadPresetAsync(preset);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("POCKETCAKE_DEMO_MODULE"), out var module))
        {
            State.SelectedModule = module;
        }

        switch (Environment.GetEnvironmentVariable("POCKETCAKE_DEMO_PAGE"))
        {
            case "slots":
                Nav.SelectedItem = Nav.MenuItems[1];
                break;
            case "settings":
                Nav.SelectedItem = Nav.SettingsItem;
                break;
        }

        if (Environment.GetEnvironmentVariable("POCKETCAKE_SNAPSHOT") is not { Length: > 0 } path)
        {
            return;
        }

        await Task.Delay(1500);


        var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await rtb.RenderAsync(Root);
        var pixels = await rtb.GetPixelsAsync();

        using (var stream = File.Create(path).AsRandomAccessStream())
        {
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
            await encoder.FlushAsync();
        }

        Application.Current.Exit();
    }
#endif
}
