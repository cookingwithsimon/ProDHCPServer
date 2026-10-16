using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProDHCPServer.Core.Dhcp;
using ProDHCPServer.Core.Net;
using ProDHCPServer.Core.Profiles;

namespace ProDHCPServer.App.ViewModels;

/// <summary>Dialogs the profile editor needs; the window supplies them so the view model stays testable.</summary>
public interface ISettingsDialogs
{
    bool Confirm(string title, string message, string confirmText);
    string? OpenFile(string title, string filter);
    string? SaveFile(string title, string filter, string fileName);
}

public sealed record ProfileItem(string Name, bool IsPreset)
{
    public string Display => IsPreset ? $"{Name}  (preset)" : Name;
    public override string ToString() => Display;
}

public sealed partial class CustomOptionRow : ObservableObject
{
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private CustomOptionFormat _format = CustomOptionFormat.Text;
    [ObservableProperty] private string _value = "";
}

/// <summary>
/// The settings slide-over: pick a profile (which Start uses), edit your own profiles, and manage them
/// (new, duplicate, delete, import, export). Presets are shown read-only.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] FormFields =
    [
        nameof(Name), nameof(Notes), nameof(ServerIp), nameof(Prefix), nameof(PoolStart), nameof(PoolEnd), nameof(LeaseMinutes),
        nameof(Router), nameof(DnsServers), nameof(DomainName), nameof(NtpServers), nameof(Option66), nameof(Option150),
        nameof(Option43), nameof(AlwaysBroadcast),
    ];

    private const string JsonFilter = "ProDHCPServer profiles (*.json)|*.json|All files (*.*)|*.*";

    private readonly ProfileLibrary _library;
    private ISettingsDialogs? _dialogs;
    private bool _loading;

    public SettingsViewModel(ProfileLibrary library, string? activeProfile, string theme)
    {
        _library = library;
        _theme = theme;
        ReloadList();
        var active = _library.Find(activeProfile) ?? ProfilePresets.Default;
        _selectedProfile = Profiles.First(p => p.Name == active.Name);
        ActiveProfile = active;
        LoadForm(active);
    }

    public void AttachDialogs(ISettingsDialogs dialogs) => _dialogs = dialogs;

    public static IReadOnlyList<PrefixOption> Prefixes { get; } =
        Enumerable.Range(8, 23).Reverse().Select(p => new PrefixOption(p)).ToList();

    public static IReadOnlyList<string> Themes { get; } = ["System", "Dark", "Light"];
    public static IReadOnlyList<CustomOptionFormat> Formats { get; } = Enum.GetValues<CustomOptionFormat>();

    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<CustomOptionRow> CustomOptions { get; } = [];

    /// <summary>The saved profile Start uses: the one picked in the list, without unsaved edits.</summary>
    public Profile ActiveProfile { get; private set; }

    /// <summary>Raised when a different profile is picked, or the active one is saved or renamed.</summary>
    public event Action<Profile>? ActiveProfileChanged;
    public event Action<string>? ThemeChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreset), nameof(CanEdit), nameof(CanDelete))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand), nameof(DiscardCommand))]
    private ProfileItem _selectedProfile;

    /// <summary>True while the server runs: the profile in use can't be switched or changed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanDelete), nameof(CanManage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand), nameof(NewCommand), nameof(DuplicateCommand), nameof(ImportCommand), nameof(DiscardCommand))]
    private bool _isLocked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    private bool _isDirty;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _serverIp = "";
    [ObservableProperty] private PrefixOption _prefix = new(24);
    [ObservableProperty] private string _poolStart = "";
    [ObservableProperty] private string _poolEnd = "";
    [ObservableProperty] private string _leaseMinutes = "60";
    [ObservableProperty] private string _router = "";
    [ObservableProperty] private string _dnsServers = "";
    [ObservableProperty] private string _domainName = "";
    [ObservableProperty] private string _ntpServers = "";
    [ObservableProperty] private string _option66 = "";
    [ObservableProperty] private string _option150 = "";
    [ObservableProperty] private string _option43 = "";
    [ObservableProperty] private bool _alwaysBroadcast;
    [ObservableProperty] private string _theme;
    [ObservableProperty] private string _errors = "";
    /// <summary>A one-off message after import/export/delete.</summary>
    [ObservableProperty] private string _notice = "";

    public bool IsPreset => SelectedProfile.IsPreset;
    public bool CanEdit => !IsPreset && !IsLocked;
    public bool CanDelete => !IsPreset && !IsLocked;
    public bool CanManage => !IsLocked;
    public bool HasExtraOptions => !string.IsNullOrWhiteSpace(DomainName) || !string.IsNullOrWhiteSpace(NtpServers) || !string.IsNullOrWhiteSpace(Option66)
        || !string.IsNullOrWhiteSpace(Option150) || !string.IsNullOrWhiteSpace(Option43) || CustomOptions.Count > 0;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || !FormFields.Contains(e.PropertyName)) return;
        // Any edit clears the error text straight away; it is checked again when the field loses focus or on Save.
        Errors = "";
        IsDirty = true;
    }

    partial void OnThemeChanged(string value) => ThemeChanged?.Invoke(value);

    partial void OnSelectedProfileChanged(ProfileItem? oldValue, ProfileItem newValue)
    {
        if (_loading || newValue is null) return;
        if (IsDirty && oldValue is not null && _dialogs is not null &&
            !_dialogs.Confirm($"Discard your changes to \"{oldValue.Name}\"?",
                "You changed this profile but didn't save it. Switching to another profile throws those changes away.",
                "Discard changes"))
        {
            // Put the picker back after this change has finished.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _loading = true; SelectedProfile = oldValue; _loading = false; });
            return;
        }
        if (_library.Find(newValue.Name) is not { } profile) return;
        Notice = "";
        LoadForm(profile);
        SetActive(profile);
    }

    private void SetActive(Profile profile)
    {
        ActiveProfile = profile;
        ActiveProfileChanged?.Invoke(profile);
    }

    private void ReloadList(string? select = null)
    {
        _loading = true;
        try
        {
            Profiles.Clear();
            foreach (var p in _library.All) Profiles.Add(new ProfileItem(p.Name, p.IsPreset));
            if (select is not null) SelectedProfile = Profiles.First(p => string.Equals(p.Name, select, StringComparison.OrdinalIgnoreCase));
        }
        finally { _loading = false; }
    }

    private void LoadForm(Profile p)
    {
        _loading = true;
        try
        {
            Name = p.Name;
            Notes = p.Notes ?? "";
            ServerIp = p.ServerIp;
            Prefix = Prefixes.FirstOrDefault(x => x.Length == p.PrefixLength) ?? new PrefixOption(24);
            PoolStart = p.PoolStart;
            PoolEnd = p.PoolEnd;
            LeaseMinutes = p.LeaseMinutes.ToString();
            Router = p.Router ?? "";
            DnsServers = p.DnsServers ?? "";
            DomainName = p.DomainName ?? "";
            NtpServers = p.NtpServers ?? "";
            Option66 = p.TftpServerName ?? "";
            Option150 = p.TftpServers ?? "";
            Option43 = p.VendorSpecificHex ?? "";
            AlwaysBroadcast = p.AlwaysBroadcast;
            foreach (var row in CustomOptions) row.PropertyChanged -= OnRowChanged;
            CustomOptions.Clear();
            foreach (var c in p.CustomOptions) AddRow(new CustomOptionRow { Code = c.Code.ToString(), Format = c.Format, Value = c.Value });
            Errors = "";
            IsDirty = false;
            OnPropertyChanged(nameof(HasExtraOptions));
        }
        finally { _loading = false; }
    }

    private void AddRow(CustomOptionRow row)
    {
        row.PropertyChanged += OnRowChanged;
        CustomOptions.Add(row);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading) return;
        Errors = "";
        IsDirty = true;
    }

    /// <summary>The form as a profile, or null with <see cref="Errors"/> set when a number field isn't a number.</summary>
    private Profile? FormProfile()
    {
        if (!int.TryParse(LeaseMinutes.Trim(), out var minutes) || minutes <= 0)
        {
            Errors = "Lease time must be a whole number of minutes.";
            return null;
        }
        var custom = new List<CustomOption>();
        foreach (var row in CustomOptions)
        {
            if (string.IsNullOrWhiteSpace(row.Code) && string.IsNullOrWhiteSpace(row.Value)) continue;
            if (!byte.TryParse(row.Code.Trim(), out var code) || code is 0 or 255)
            {
                Errors = $"Option code \"{row.Code.Trim()}\" isn't valid. Use a number from 1 to 254.";
                return null;
            }
            custom.Add(new CustomOption(code, row.Format, row.Value.Trim()));
        }
        return new Profile
        {
            Name = Name.Trim(),
            Notes = Blank(Notes),
            ServerIp = ServerIp.Trim(),
            PrefixLength = Prefix.Length,
            PoolStart = PoolStart.Trim(),
            PoolEnd = PoolEnd.Trim(),
            LeaseMinutes = minutes,
            Router = Blank(Router),
            DnsServers = Blank(DnsServers),
            DomainName = Blank(DomainName),
            NtpServers = Blank(NtpServers),
            TftpServerName = Blank(Option66),
            TftpServers = Blank(Option150),
            VendorSpecificHex = Blank(Option43),
            CustomOptions = custom,
            AlwaysBroadcast = AlwaysBroadcast,
        };
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Checks the form (when a field loses focus). Presets aren't checked: they can't be changed.</summary>
    public void Validate()
    {
        if (!CanEdit) return;
        if (FormProfile() is not { } p) return;
        Errors = string.Join(Environment.NewLine, p.ToConfig().Errors);
    }

    /// <summary>Saves the form if it has changes. Returns false (with <see cref="Errors"/> set) if it can't.</summary>
    public bool SaveIfDirty() => !IsDirty || !CanEdit || TrySave();

    private bool TrySave()
    {
        if (FormProfile() is not { } p) return false;
        var (_, errors) = p.ToConfig();
        if (errors.Count > 0)
        {
            Errors = string.Join(Environment.NewLine, errors);
            return false;
        }
        try
        {
            var original = SelectedProfile.Name;
            _library.Save(p, original);
            ReloadList(p.Name);
            LoadForm(_library.Find(p.Name)!);
            SetActive(_library.Find(p.Name)!);
            Notice = "";
            return true;
        }
        catch (Exception ex) when (ex is ProfileException or IOException or UnauthorizedAccessException)
        {
            Errors = ex is ProfileException ? ex.Message : $"Couldn't save the profile: {ex.Message}";
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Save() => TrySave();

    private bool CanDiscard() => IsDirty && !IsLocked;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        if (_library.Find(SelectedProfile.Name) is { } p) LoadForm(p);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void New()
    {
        if (!ConfirmLeave()) return;
        var p = ProfilePresets.Default with { Name = _library.UniqueName("New profile"), Notes = null, IsPreset = false };
        Select(() => { _library.Save(p, null); return p.Name; });
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void Duplicate()
    {
        if (!ConfirmLeave()) return;
        if (_library.Find(SelectedProfile.Name) is not { } source) return;
        Select(() => _library.Duplicate(source).Name);
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        var name = SelectedProfile.Name;
        if (_dialogs is not null && !_dialogs.Confirm($"Delete the profile \"{name}\"?",
                $"\"{name}\" will be removed from this laptop. Export it first if you might want it again.", $"Delete \"{name}\""))
            return;
        try
        {
            _library.Delete(name);
        }
        catch (Exception ex) when (ex is ProfileException or IOException or UnauthorizedAccessException)
        {
            Errors = ex.Message;
            return;
        }
        ReloadList(ProfilePresets.DefaultName);
        LoadForm(ProfilePresets.Default);
        SetActive(ProfilePresets.Default);
        Notice = $"Deleted \"{name}\". Now using the Default profile.";
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void Import()
    {
        if (_dialogs?.OpenFile("Import profiles", JsonFilter) is not { } path) return;
        if (!ConfirmLeave()) return;
        string json;
        try
        {
            if (new FileInfo(path).Length > ProfileLibrary.MaxImportBytes)
            {
                Notice = "That file is too big to be a ProDHCPServer profiles file.";
                return;
            }
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}";
            return;
        }
        ProfileImportResult result;
        try { result = _library.Import(json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = $"Couldn't save the imported profiles: {ex.Message}";
            return;
        }
        if (result.Added.Count > 0)
        {
            ReloadList(result.Added[0].Name);
            LoadForm(result.Added[0]);
            SetActive(result.Added[0]);
        }
        Notice = result.Describe();
    }

    [RelayCommand]
    private void Export()
    {
        if (_library.Find(SelectedProfile.Name) is not { } p) return;
        var fileName = string.Concat(p.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json";
        if (_dialogs?.SaveFile("Export profile", JsonFilter, fileName) is not { } path) return;
        try
        {
            File.WriteAllText(path, ProfileLibrary.Export([p]), new UTF8Encoding(false));
            Notice = IsDirty
                ? $"Exported the saved version of \"{p.Name}\" to {Path.GetFileName(path)}. Your unsaved changes aren't in it."
                : $"Exported \"{p.Name}\" to {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = $"Couldn't write {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddCustomOption()
    {
        AddRow(new CustomOptionRow());
        IsDirty = true;
        OnPropertyChanged(nameof(HasExtraOptions));
    }

    [RelayCommand]
    private void RemoveCustomOption(CustomOptionRow? row)
    {
        if (row is null || !CanEdit) return;
        row.PropertyChanged -= OnRowChanged;
        CustomOptions.Remove(row);
        IsDirty = true;
        Errors = "";
    }

    /// <summary>Fills the pool as .100–.199 (or the nearest fit) of the server's subnet.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SuggestPool()
    {
        if (!IPv4.TryParse(ServerIp, out var server)) return;
        uint net = IPv4.ToUInt32(IPv4.NetworkAddress(server, Prefix.Length));
        uint size = (uint)(1L << (32 - Prefix.Length));
        uint start = size >= 256 ? 100u : Math.Max(2u, size / 4);
        uint end = size >= 256 ? 199u : size - 2;
        PoolStart = IPv4.FromUInt32(net + start).ToString();
        PoolEnd = IPv4.FromUInt32(net + end).ToString();
    }

    /// <summary>The config for Start: the active profile as saved.</summary>
    public (DhcpServerConfig? Config, IReadOnlyList<string> Errors) BuildActive() => ActiveProfile.ToConfig();

    private bool ConfirmLeave() =>
        !IsDirty || _dialogs is null || _dialogs.Confirm($"Discard your changes to \"{SelectedProfile.Name}\"?",
            "You changed this profile but didn't save it. Carrying on throws those changes away.", "Discard changes");

    private void Select(Func<string> create)
    {
        string name;
        try { name = create(); }
        catch (Exception ex) when (ex is ProfileException or IOException or UnauthorizedAccessException)
        {
            Errors = ex.Message;
            return;
        }
        ReloadList(name);
        var p = _library.Find(name)!;
        LoadForm(p);
        SetActive(p);
        Notice = "";
    }
}

public sealed record PrefixOption(int Length)
{
    public override string ToString() => $"/{Length}  ({IPv4.PrefixToMaskAddress(Length)})";
}
