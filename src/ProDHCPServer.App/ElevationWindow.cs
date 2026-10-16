using System.Windows;
using System.Windows.Controls;

namespace ProDHCPServer.App;

/// <summary>Explains why admin rights are needed before Windows shows its UAC prompt.</summary>
public sealed class ElevationWindow : Window
{
    public bool Relaunch { get; private set; }

    public ElevationWindow()
    {
        Title = "ProDHCPServer needs administrator rights";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "WindowBg");

        var heading = new TextBlock { Text = "Administrator rights needed", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            Text = "To act as a DHCP server, ProDHCPServer has to:\n\n" +
                   "  •  listen on UDP port 67\n" +
                   "  •  set a static IP on the adapter you pick (and put it back afterwards)\n" +
                   "  •  add a Windows Firewall rule for DHCP\n\n" +
                   "Windows only allows that for administrators. Click Restart as administrator and choose Yes on the Windows prompt.",
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var cont = new Button { Content = "Restart as administrator", MinWidth = 120, Padding = new Thickness(16, 8, 16, 8), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        cont.SetResourceReference(StyleProperty, "AccentButton");
        cont.Click += (_, _) => { Relaunch = true; Close(); };
        var quit = new Button { Content = "Quit ProDHCPServer", MinWidth = 100, Padding = new Thickness(16, 8, 16, 8), IsCancel = true };
        quit.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                heading,
                body,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0), Children = { cont, quit } },
            },
        };
    }
}
