using System.IO;
using Camzivo.Data.Storage;

namespace Camzivo.Desktop.ViewModels;

public sealed record RetentionChoice(int Days, string Label)
{
    public override string ToString() => Label;
}

public sealed record StartupChoice(bool RestoreLastSection, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings that exist. Appearance is owned by the shell (one source of truth for the theme); retention and startup
/// behaviour live here and are persisted by the shell through the preference store.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private bool _openLastSection;
    private int _eventRetentionDays = 30;
    private RetentionChoice? _selectedRetention;
    private StartupChoice? _selectedStartup;

    public SettingsViewModel(AppPaths paths, string versionLabel, bool isDemo)
    {
        ConfigPath = paths.ConfigRoot;
        DatabasePath = paths.DatabasePath;
        LogPath = paths.LogRoot;
        CachePath = paths.CacheRoot;
        ExportRoot = paths.ExportRoot;
        VersionLabel = versionLabel;
        DataNote = isDemo
            ? "Demo mode: nothing on this screen is written to your profile."
            : "Everything lives in your Windows profile — never in Program Files, so Camzivo never needs administrator rights.";

        StartupChoices =
        [
            new StartupChoice(false, "Always open Live View"),
            new StartupChoice(true, "Reopen the last page I used"),
        ];

        RetentionChoices =
        [
            new RetentionChoice(7, "7 days"),
            new RetentionChoice(30, "30 days"),
            new RetentionChoice(90, "90 days"),
            new RetentionChoice(365, "1 year"),
        ];

        _selectedStartup = StartupChoices[0];
        _selectedRetention = RetentionChoices[1];

        OpenExportFolderCommand = new RelayCommand(_ => RevealRequested?.Invoke(ExportRoot));
        OpenLogFolderCommand = new RelayCommand(_ => RevealRequested?.Invoke(LogPath));
    }

    public string ConfigPath { get; }

    public string DatabasePath { get; }

    public string LogPath { get; }

    public string CachePath { get; }

    public string ExportRoot { get; }

    public string VersionLabel { get; }

    public string DataNote { get; }

    public string DecodeBudgetText =>
        $"{LiveViewModel.DecodeBudget} streams. Tiles beyond the budget report QUEUED instead of silently thinning every stream.";

    public string CredentialText => "Windows DPAPI, per user. Passwords are never stored in the database or written to logs.";

    public string AppearanceNote => "Dark and light share one token set. Switch without restarting; every view remaps.";

    public IReadOnlyList<StartupChoice> StartupChoices { get; }

    public IReadOnlyList<RetentionChoice> RetentionChoices { get; }

    public RelayCommand OpenExportFolderCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public event Action<string>? RevealRequested;

    /// <summary>Raised when a persisted preference changes; the shell writes it and applies it.</summary>
    public event Action? PreferencesChanged;

    public StartupChoice? SelectedStartup
    {
        get => _selectedStartup;
        set
        {
            if (Set(ref _selectedStartup, value) && value is not null)
            {
                _openLastSection = value.RestoreLastSection;
                Raise(nameof(OpenLastSection));
                PreferencesChanged?.Invoke();
            }
        }
    }

    public bool OpenLastSection
    {
        get => _openLastSection;
        set
        {
            if (Set(ref _openLastSection, value))
            {
                _selectedStartup = StartupChoices.FirstOrDefault(choice => choice.RestoreLastSection == value);
                Raise(nameof(SelectedStartup));
                PreferencesChanged?.Invoke();
            }
        }
    }

    public RetentionChoice? SelectedRetention
    {
        get => _selectedRetention;
        set
        {
            if (Set(ref _selectedRetention, value) && value is not null)
            {
                _eventRetentionDays = value.Days;
                Raise(nameof(EventRetentionDays));
                Raise(nameof(RetentionNote));
                PreferencesChanged?.Invoke();
            }
        }
    }

    public int EventRetentionDays
    {
        get => _eventRetentionDays;
        set
        {
            if (Set(ref _eventRetentionDays, value))
            {
                _selectedRetention = RetentionChoices.FirstOrDefault(choice => choice.Days == value) ?? _selectedRetention;
                Raise(nameof(SelectedRetention));
                Raise(nameof(RetentionNote));
                PreferencesChanged?.Invoke();
            }
        }
    }

    public string RetentionNote => $"{EventRetentionDays} days of event history are kept in the local database.";

    public bool HasExportFolder => Directory.Exists(ExportRoot);
}
