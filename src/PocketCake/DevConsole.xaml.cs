using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PocketCake.Core.Protocol;
using PocketCake.ViewModels;

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
    private readonly ObservableCollection<string> _log = [];
    private readonly ToggleSwitch _capture;
    private readonly ToggleSwitch _raw;

    public DevConsole()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        var state = _state = MainWindow.Instance.State;

        var query = new Button { Content = "Query global settings" };
        query.Click += async (_, _) => await Send(GlobalSettingsQuery);

        var clear = new Button { Content = "Clear log" };
        clear.Click += (_, _) => _log.Clear();

        var frame = new TextBox { PlaceholderText = "8080F0…F7 raw frame", Width = 320, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        var send = new Button { Content = "Send" };
        send.Click += async (_, _) => await Send(frame.Text.Trim().Replace(" ", ""));

        var marker = new TextBox { PlaceholderText = "marker", Width = 160 };
        var mark = new Button { Content = "Mark" };
        mark.Click += (_, _) =>
        {
            if (marker.Text.Trim() is { Length: > 0 } label)
            {
                Add($"MARK  {label}");
                marker.Text = "";
            }
        };

        _capture = new ToggleSwitch { OnContent = "Capture", OffContent = "Capture" };
        _capture.Toggled += (_, _) =>
        {
            if (_capture.IsOn)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
                Add($"CAPTURE → {CapturePath}");
            }
        };
        _raw = new ToggleSwitch { OnContent = "Raw RX/TX", OffContent = "Raw RX/TX" };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(24, 12, 24, 8) };

        foreach (var c in new FrameworkElement[] { query, clear, frame, send, marker, mark, _capture, _raw })
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(c);
        }

        var list = new ListView
        {
            ItemsSource = _log,
            SelectionMode = ListViewSelectionMode.Extended,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new ScrollViewer { Content = bar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        Content = grid;

        state.InboundObserved += OnInbound;
        state.TrafficObserved += OnTraffic;
    }

    private async Task Send(string hex)
    {
        try
        {
            await _state.SendRawAsync(hex);
        }
        catch (Exception e)
        {
            Add($"SEND ERROR: {e.Message}");
        }
    }

    private void OnInbound(InboundMessage m) => Add(m switch
    {
        DataFrame d => $"DataFrame {d.Hex.Length / 2}B  {d.Hex}",
        MalformedFrame f => $"Malformed  {f.Hex}",
        _ => m.GetType().Name,
    });

    private void OnTraffic(string dir, byte[] bytes)
    {
        var line = $"{dir,-4}  {bytes.Length}B  {Convert.ToHexString(bytes)}";

        if (_raw.IsOn)
        {
            Add(line);
        }
        else if (_capture.IsOn)
        {
            WriteCapture(line);
        }
    }

    private void Add(string line)
    {
        _log.Insert(0, line);

        while (_log.Count > MaxLines)
        {
            _log.RemoveAt(_log.Count - 1);
        }

        if (_capture.IsOn)
        {
            WriteCapture(line);
        }
    }

    private static void WriteCapture(string line)
    {
        try
        {
            File.AppendAllText(CapturePath, $"{DateTime.Now:O}  {line}{Environment.NewLine}");
        }
        catch
        {
            // Capture is best-effort.
        }
    }
}
