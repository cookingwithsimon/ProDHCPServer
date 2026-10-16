using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using ProDHCPServer.App.ViewModels;

namespace ProDHCPServer.App;

/// <summary>The real dialogs behind <see cref="ISettingsDialogs"/>, owned by the main window.</summary>
public sealed class WindowDialogs(Window owner) : ISettingsDialogs
{
    public bool Confirm(string title, string message, string confirmText) => ConfirmDialog.Show(owner, title, message, confirmText);

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, AddExtension = true, DefaultExt = ".json" };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>Opens a web page through Explorer so the browser runs as the normal user, not elevated like this app.</summary>
    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception) { /* nothing useful to add: the link text is on screen */ }
    }
}
