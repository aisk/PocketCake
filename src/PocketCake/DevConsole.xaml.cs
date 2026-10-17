using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PocketCake.Core.Protocol;
using PocketCake.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace PocketCake;

/// <summary>
/// Developer / protocol-RE console (Debug builds): send a read-only query or a raw
/// frame, watch classified inbound messages and raw RX/TX traffic, optionally capture to a file.
/// </summary>
public sealed partial class DevConsole : Page
{
    private const string GlobalSettingsQuery = "8080F00B0900010000000201020100F7";
    private const int MaxLines = 2000;

    private static readonly string CapturePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketCake", "midi-capture.log");

    private readonly AppState _state;

    public DevConsole()
    {
        _state = MainWindow.Instance.State;
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        Log.CollectionChanged += (_, _) =>
        {
            CountText.Text = Log.Count == 1 ? "1 entry" : $"{Log.Count} entries";
            EmptyState.Visibility = Log.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        _state.InboundObserved += OnInbound;
        _state.TrafficObserved += OnTraffic;
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<LogEntry> Log { get; } = [];

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendFrameAsync();

    private async void OnFrameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SendFrameAsync();
        }
    }

    private async Task SendFrameAsync()
    {
        if (FrameBox.Text.Replace(" ", "").Trim() is { Length: > 0 } hex)
        {
            await Send(hex);
        }
    }

    private async void OnQueryClick(object sender, RoutedEventArgs e) => await Send(GlobalSettingsQuery);

    private void OnMarkClick(object sender, RoutedEventArgs e) => AddMarker();

    private void OnMarkerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            AddMarker();
        }
    }

    private void AddMarker()
    {
        if (MarkerBox.Text.Trim() is { Length: > 0 } label)
        {
            Add(LogKind.Mark, label);
            MarkerBox.Text = "";
        }
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (CaptureToggle.IsChecked == true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
            Add(LogKind.Info, $"Capturing to {CapturePath}");
        }
    }

    private void OnLogSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CopyButton.IsEnabled = LogList.SelectedItems.Count > 0;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        // Copy in chronological order, like a log file.
        var lines = LogList.SelectedItems.Cast<LogEntry>().OrderBy(Log.IndexOf).Reverse().Select(l => l.ToString());
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, lines));
        Clipboard.SetContent(package);
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => Log.Clear();

    private async Task Send(string hex)
    {
        try
        {
            await _state.SendRawAsync(hex);
        }
        catch (Exception e)
        {
            Add(LogKind.Error, e.Message);
        }
    }

    private void OnInbound(InboundMessage m)
    {
        switch (m)
        {
            case DataFrame d:
                Add(LogKind.Frame, d.Hex, d.Hex.Length / 2);
                break;
            case MalformedFrame f:
                Add(LogKind.Malformed, f.Hex, f.Hex.Length / 2);
                break;
            default:
                Add(LogKind.Message, m.GetType().Name.Replace("Message", ""));
                break;
        }
    }

    private void OnTraffic(string dir, byte[] bytes)
    {
        var entry = new LogEntry(DateTime.Now, dir == "TX" ? LogKind.Tx : LogKind.Rx, Convert.ToHexString(bytes), bytes.Length);

        if (RawToggle.IsChecked == true)
        {
            Add(entry);
        }
        else if (CaptureToggle.IsChecked == true)
        {
            WriteCapture(entry);
        }
    }

    private void Add(LogKind kind, string text, int? size = null) => Add(new LogEntry(DateTime.Now, kind, text, size));

    private void Add(LogEntry entry)
    {
        Log.Insert(0, entry);

        while (Log.Count > MaxLines)
        {
            Log.RemoveAt(Log.Count - 1);
        }

        if (CaptureToggle.IsChecked == true)
        {
            WriteCapture(entry);
        }
    }

    private static void WriteCapture(LogEntry entry)
    {
        try
        {
            File.AppendAllText(CapturePath, $"{entry.Time:O}  {entry.Kind,-5}  {entry.Text}{Environment.NewLine}");
        }
        catch
        {
            // Capture is best-effort.
        }
    }
}

public enum LogKind { Rx, Tx, Frame, Malformed, Message, Mark, Info, Error }

/// <summary>One line of the developer console log.</summary>
public sealed class LogEntry(DateTime time, LogKind kind, string text, int? size)
{
    private static readonly Dictionary<LogKind, (SolidColorBrush Brush, SolidColorBrush Tint)> Colors = new()
    {
        [LogKind.Rx] = Make(0x25, 0x63, 0xEB),
        [LogKind.Tx] = Make(0x16, 0xA3, 0x4A),
        [LogKind.Frame] = Make(0x8B, 0x5C, 0xF6),
        [LogKind.Malformed] = Make(0xDC, 0x26, 0x26),
        [LogKind.Message] = Make(0x08, 0x91, 0xB2),
        [LogKind.Mark] = Make(0xD9, 0x77, 0x06),
        [LogKind.Info] = Make(0x64, 0x74, 0x8B),
        [LogKind.Error] = Make(0xDC, 0x26, 0x26),
    };

    public DateTime Time { get; } = time;

    public string Kind { get; } = kind switch
    {
        LogKind.Malformed => "BAD",
        LogKind.Message => "MSG",
        _ => kind.ToString().ToUpperInvariant(),
    };

    public string Text { get; } = text;

    public string TimeText => Time.ToString("HH:mm:ss.fff");

    public string SizeText { get; } = size is { } s ? $"{s} B" : "";

    public SolidColorBrush Brush { get; } = Colors[kind].Brush;

    public SolidColorBrush Tint { get; } = Colors[kind].Tint;

    public override string ToString() => $"{TimeText}  {Kind,-5}  {SizeText,6}  {Text}";

    private static (SolidColorBrush, SolidColorBrush) Make(byte r, byte g, byte b) =>
        (new SolidColorBrush(Color.FromArgb(0xFF, r, g, b)), new SolidColorBrush(Color.FromArgb(0x30, r, g, b)));
}
