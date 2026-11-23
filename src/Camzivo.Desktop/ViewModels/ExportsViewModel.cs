using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Camzivo.Data.Repositories;
using Camzivo.Desktop.Services;

namespace Camzivo.Desktop.ViewModels;

/// <summary>One evidence file in the Exports page.</summary>
public sealed class ExportRowViewModel : ObservableObject
{
    private string _verification = "Not verified";

    public ExportRowViewModel(ExportRecord record, bool exists, TimeZoneInfo zone)
    {
        Id = record.Id;
        CameraName = record.CameraName;
        Path = record.Path;
        Bytes = record.Bytes;
        Samples = record.Samples;
        Exists = exists;

        var local = TimeZoneInfo.ConvertTime(record.CreatedUtc, zone);
        CreatedText = local.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

        var start = TimeZoneInfo.ConvertTime(record.RangeStartUtc, zone);
        var end = TimeZoneInfo.ConvertTime(record.RangeEndUtc, zone);
        RangeText = $"{start:d MMM HH:mm:ss} – {end:HH:mm:ss}";

        FileName = System.IO.Path.GetFileName(record.Path);
        Folder = System.IO.Path.GetDirectoryName(record.Path) ?? string.Empty;
    }

    public Guid Id { get; }

    public string CameraName { get; }

    public string Path { get; }

    public string FileName { get; }

    public string Folder { get; }

    public long Bytes { get; }

    public int Samples { get; }

    public bool Exists { get; }

    public string CreatedText { get; }

    public string RangeText { get; }

    public string MissingText => Exists ? string.Empty : "The file is no longer on disk.";

    public string Verification
    {
        get => _verification;
        set => Set(ref _verification, value);
    }

    public string AutomationName => $"{CameraName}, {RangeText}, {CreatedText}";
}

/// <summary>
/// Evidence already produced by this installation. Rows come from the local export record and are checked against the
/// file system, so a deleted file is shown as missing instead of silently disappearing from the list.
/// </summary>
public sealed class ExportsViewModel : ObservableObject
{
    private readonly ExportRepository? _repository;
    private readonly TimeZoneInfo _zone;
    private bool _isLoading;
    private string? _errorText;
    private string _summary = "No exports yet.";

    public ExportsViewModel(ExportRepository? repository, string exportRoot, TimeZoneInfo? zone = null)
    {
        _repository = repository;
        _zone = zone ?? TimeZoneInfo.Local;
        ExportRoot = exportRoot;

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        OpenFolderCommand = new RelayCommand(_ => RevealRequested?.Invoke(ExportRoot), _ => Directory.Exists(ExportRoot));
        OpenCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is ExportRowViewModel row)
                {
                    RevealRequested?.Invoke(row.Path);
                }
            },
            parameter => parameter is ExportRowViewModel { Exists: true });
        VerifyCommand = new RelayCommand(parameter => _ = VerifyAsync(parameter), parameter => parameter is ExportRowViewModel { Exists: true });
    }

    public ObservableCollection<ExportRowViewModel> Rows { get; } = [];

    public string ExportRoot { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand VerifyCommand { get; }

    /// <summary>The shell owns Explorer; the view model only names the file or folder.</summary>
    public event Action<string>? RevealRequested;

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool HasRows => Rows.Count > 0;

    public string EmptyText => _repository is null
        ? "The local database is unavailable, so export records cannot be read. Files already written remain in the export folder."
        : "Exports appear here after you export evidence from Playback.";

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null)
        {
            ErrorText = "The local database is unavailable.";
            return;
        }

        IsLoading = true;
        ErrorText = null;

        try
        {
            var records = await _repository.ListAsync(200, cancellationToken).ConfigureAwait(true);

            Rows.Clear();
            long bytes = 0;
            var missing = 0;

            foreach (var record in records)
            {
                var exists = File.Exists(record.Path);
                if (exists)
                {
                    bytes += record.Bytes;
                }
                else
                {
                    missing++;
                }

                Rows.Add(new ExportRowViewModel(record, exists, _zone));
            }

            Summary = records.Count switch
            {
                0 => "No exports yet.",
                _ => $"{records.Count} export{(records.Count == 1 ? string.Empty : "s")} · " +
                     $"{bytes / 1024.0 / 1024:F1} MB on disk" +
                     (missing > 0 ? $" · {missing} missing" : string.Empty),
            };

            Raise(nameof(HasRows));
            OpenFolderCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            ErrorText = $"Export records could not be read: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task VerifyAsync(object? parameter)
    {
        if (parameter is not ExportRowViewModel row)
        {
            return;
        }

        try
        {
            var valid = await EvidenceManifestWriter.VerifyAsync(row.Path).ConfigureAwait(true);
            row.Verification = valid ? "Hash verified" : "Hash mismatch or no sidecar";
        }
        catch (IOException exception)
        {
            row.Verification = $"Verification failed: {exception.Message}";
        }
    }
}
