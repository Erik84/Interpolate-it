using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace InterpolatIt.App;

/// <summary>
/// Command-line options, all optional and meant for unattended checks of the application itself:
/// <c>--start-on-title TEXT</c> starts on the first window whose title contains TEXT,
/// <c>--factor N</c> / <c>--model ID</c> override the saved choices for this run,
/// <c>--log FILE</c> writes a status line per second, <c>--exit-after SECONDS</c> closes the application.
/// </summary>
public sealed record StartupOptions(string? StartOnTitle, string? LogPath, double ExitAfter, int Factor, string? Model)
{
    /// <summary><c>--pacing-log FILE</c>: the engine's per-refresh log (numbers only), one file per start.</summary>
    public string? PacingLogPath { get; init; }

    /// <summary><c>--record FILE</c>: where the Record button / Ctrl+Alt+R writes (default: a dated file in
    /// Videos\Interpolate-it).</summary>
    public string? RecordPath { get; init; }

    /// <summary><c>--no-settings</c>: start from the defaults and save nothing (a check must not depend on, or
    /// change, what the user chose last time).</summary>
    public bool NoSettings { get; init; }

    /// <summary><c>--enhance</c>: start with "Clean up compressed video" switched on.</summary>
    public bool Enhance { get; init; }

    /// <summary><c>--render-windows FOLDER</c>: draw the main window and the About window into PNG files
    /// without showing them anywhere, then exit. For looking at the layout from a script.</summary>
    public string? RenderWindowsTo { get; init; }

    /// <summary>Nobody is watching: an error is logged and ends the run instead of waiting in a message box.</summary>
    public bool Unattended => ExitAfter > 0;

    public static StartupOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        double.TryParse(Value("exit-after"), NumberStyles.Float, CultureInfo.InvariantCulture, out double exitAfter);
        int.TryParse(Value("factor"), out int factor);
        return new StartupOptions(Value("start-on-title"), Value("log"), exitAfter, factor, Value("model"))
        {
            PacingLogPath = Value("pacing-log"),
            RecordPath = Value("record"),
            NoSettings = args.Any(a => a.Equals("--no-settings", StringComparison.OrdinalIgnoreCase)),
            Enhance = args.Any(a => a.Equals("--enhance", StringComparison.OrdinalIgnoreCase)),
            RenderWindowsTo = Value("render-windows"),
        };
    }
}

public partial class App : Application
{
    public static StartupOptions Options { get; private set; } = new(null, null, 0, 0, null);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Numbers are shown and logged the same way everywhere (the readout drawn in the target window has a
        // bitmap font with a decimal point only).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Options = StartupOptions.Parse(e.Args);
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (Options.LogPath is string log)
        {
            try { System.IO.File.AppendAllText(log, $"unhandled {e.Exception}\n"); } catch (System.IO.IOException) { }
        }
        e.Handled = true;
        if (Options.Unattended)
        {
            Current.Shutdown(1);        // a message box would wait for nobody
            return;
        }
        MessageBox.Show(e.Exception.Message, "Interpolate-it", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
