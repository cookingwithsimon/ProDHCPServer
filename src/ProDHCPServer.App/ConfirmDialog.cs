using System.Windows;
using System.Windows.Controls;

namespace ProDHCPServer.App;

/// <summary>
/// Confirmation for a risky action: names what will happen, labels the risky button with its verb in the problem colour,
/// and makes Cancel the default so Enter or Escape backs out.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private bool _confirmed;

    private ConfirmDialog(string title, string message, string confirmText)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBg");
        SetResourceReference(FontFamilyProperty, "TextFont");

        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var body = new TextBlock { Text = message, FontSize = 16, TextWrapping = TextWrapping.Wrap };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var confirm = new Button { Content = confirmText, MinWidth = 120, MinHeight = 48, Margin = new Thickness(0, 0, 8, 0) };
        confirm.SetResourceReference(StyleProperty, "DangerButton");
        confirm.Click += (_, _) => { _confirmed = true; Close(); };
        var cancel = new Button { Content = "Cancel", MinWidth = 100, MinHeight = 48, IsDefault = true, IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        cancel.Click += (_, _) => Close();
        Loaded += (_, _) => cancel.Focus();

        Content = new StackPanel
        {
            Margin = new Thickness(32),
            Children =
            {
                heading,
                body,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0), Children = { confirm, cancel } },
            },
        };
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; true only if the risky button was pressed.</summary>
    public static bool Show(Window? owner, string title, string message, string confirmText)
    {
        var dialog = new ConfirmDialog(title, message, confirmText) { Owner = owner };
        dialog.ShowDialog();
        return dialog._confirmed;
    }
}
