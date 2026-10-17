using Microsoft.UI.Xaml;

namespace PocketCake;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>Appends an unhandled exception to %LOCALAPPDATA%\PocketCake\crash.log.</summary>
    public static void LogCrash(Exception exception)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketCake");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
        }
        catch (Exception e)
        {
            LogCrash(e);
            throw;
        }

        _window.Activate();
    }
}
