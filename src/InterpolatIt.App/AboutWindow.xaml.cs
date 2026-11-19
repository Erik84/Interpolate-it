using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace InterpolatIt.App;

/// <summary>Who made this and why, where it lives, and how to say thanks.</summary>
public partial class AboutWindow : Window
{
    public const string GitHubUrl = "https://github.com/Erik84/Interpolate-it";

    public const string DonateUrl =
        "https://www.paypal.com/donate/?business=FT2K6CLC4RLBJ&no_recurring=1&item_name=Enjoying+Interpolate-it?+Feel+free+to+leave+a+donation%21+Huge+thanks%2C+it+keeps+me+motivated+to+build+more+cool+tools.&currency_code=EUR";

    public AboutWindow()
    {
        InitializeComponent();
        string version = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        VersionText.Text = version.Length > 0 ? "Version " + version : "";
    }

    private void Donate_Click(object sender, RoutedEventArgs e) => Open(DonateUrl);

    private void GitHub_Click(object sender, RoutedEventArgs e) => Open(GitHubUrl);

    /// <summary>The licence texts the installer puts next to the application; from a build folder, the ones on GitHub.</summary>
    private void Licences_Click(object sender, RoutedEventArgs e)
    {
        string licence = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        string notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(licence) && File.Exists(notices))
        {
            Open(licence);
            Open(notices);
        }
        else
        {
            Open(GitHubUrl + "/blob/main/THIRD-PARTY-NOTICES.md");
        }
    }

    /// <summary>Opens a web page or a file with whatever the user has set up for it.</summary>
    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // No browser or no program for text files: there is nothing useful to do about that here.
        }
    }
}
