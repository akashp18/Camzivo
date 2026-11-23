using System.Collections.ObjectModel;
using System.Globalization;
using Camzivo.Data.Repositories;

namespace Camzivo.Desktop.ViewModels;

/// <summary>One row of the event history, formatted for the page but kept free of WPF types.</summary>
public sealed record EventRowViewModel(
    long Id,
    DateTimeOffset OccurredUtc,
    Guid? CameraId,
    string CameraName,
    string Kind,
    string KindLabel,
    EventSeverity Severity,
    string Message,
    string Detail,
    string TimeText,
    string DateText)
{
    public string SeverityLabel => Severity switch
    {
        EventSeverity.Critical => "Critical",
        EventSeverity.Warning => "Warning",
        _ => "Info",
    };

    public bool CanOpenPlayback => CameraId is not null;

    public string AutomationName => $"{TimeText} {CameraName}, {SeverityLabel}: {Message}";

    public string ToolTip => string.IsNullOrWhiteSpace(Detail) ? Message : $"{Message}\n{Detail}";
}

public sealed record CameraFilterOption(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record SeverityFilterOption(EventSeverity? Minimum, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Local event history. Events come from the real stream engine (connect, interruption, failure, recovery) and from
/// device and export operations; nothing is synthesised. Retention is bounded and stated on the page.
/// </summary>
public sealed class EventsViewModel : ObservableObject
{
    private readonly EventRepository? _repository;
    private readonly TimeZoneInfo _zone;
    private readonly List<EventRowViewModel> _all = [];

    private CameraFilterOption? _selectedCamera;
    private SeverityFilterOption? _selectedSeverity;
    private string _searchText = string.Empty;
    private bool _isDateFilterEnabled;
    private DateTime _fromDate = DateTime.Today.AddDays(-7);
    private DateTime _toDate = DateTime.Today;
    private bool _isLoading;
    private string? _errorText;
    private string _summary = "No events loaded yet.";
    private int _retentionDays;

    public EventsViewModel(EventRepository? repository, TimeZoneInfo? zone = null, int retentionDays = EventRepository.DefaultRetentionDays)
    {
        _repository = repository;
        _zone = zone ?? TimeZoneInfo.Local;
        _retentionDays = Math.Max(1, retentionDays);

        CameraChoices = [new CameraFilterOption(null, "All cameras")];
        SeverityChoices =
        [
            new SeverityFilterOption(null, "All severities"),
            new SeverityFilterOption(EventSeverity.Warning, "Warning and above"),
            new SeverityFilterOption(EventSeverity.Critical, "Critical only"),
        ];

        _selectedCamera = CameraChoices[0];
        _selectedSeverity = SeverityChoices[0];

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        ClearFiltersCommand = new RelayCommand(_ =>
        {
            SearchText = string.Empty;
            IsDateFilterEnabled = false;
            SelectedCamera = CameraChoices[0];
            SelectedSeverity = SeverityChoices[0];
        });
        OpenPlaybackCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is EventRowViewModel { CameraId: { } cameraId } row)
                {
                    OpenPlaybackRequested?.Invoke(cameraId, row.OccurredUtc);
                }
            },
            parameter => parameter is EventRowViewModel { CameraId: not null });
    }

    public ObservableCollection<EventRowViewModel> Rows { get; } = [];

    public ObservableCollection<CameraFilterOption> CameraChoices { get; }

    public IReadOnlyList<SeverityFilterOption> SeverityChoices { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public RelayCommand OpenPlaybackCommand { get; }

    /// <summary>Raised when the operator asks to jump to the recording of an event.</summary>
    public event Action<Guid, DateTimeOffset>? OpenPlaybackRequested;

    public CameraFilterOption? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (Set(ref _selectedCamera, value))
            {
                ApplyFilter();
            }
        }
    }

    public SeverityFilterOption? SelectedSeverity
    {
        get => _selectedSeverity;
        set
        {
            if (Set(ref _selectedSeverity, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsDateFilterEnabled
    {
        get => _isDateFilterEnabled;
        set
        {
            if (Set(ref _isDateFilterEnabled, value))
            {
                ApplyFilter();
            }
        }
    }

    public DateTime FromDate
    {
        get => _fromDate;
        set
        {
            if (Set(ref _fromDate, value))
            {
                ApplyFilter();
            }
        }
    }

    public DateTime ToDate
    {
        get => _toDate;
        set
        {
            if (Set(ref _toDate, value))
            {
                ApplyFilter();
            }
        }
    }

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

    public string RetentionNote => _repository is null
        ? "Event history is unavailable because the local store could not be opened."
        : $"History is kept locally for {_retentionDays} days. Events are produced by the stream engine and device operations, never invented.";

    public int RetentionDays
    {
        get => _retentionDays;
        set
        {
            if (Set(ref _retentionDays, value))
            {
                Raise(nameof(RetentionNote));
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null)
        {
            ErrorText = "The local database is unavailable, so the event history cannot be read.";
            return;
        }

        IsLoading = true;
        ErrorText = null;

        try
        {
            var events = await _repository.QueryAsync(new EventQuery(Limit: 500), cancellationToken).ConfigureAwait(true);

            _all.Clear();
            foreach (var entry in events)
            {
                _all.Add(ToRow(entry));
            }

            ApplyFilter();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            ErrorText = $"The event history could not be read: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Adds an event that just happened at the top, so the list is live without a query per event.</summary>
    public void Prepend(EventRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = ToRow(entry);
        _all.Insert(0, row);

        while (_all.Count > 500)
        {
            _all.RemoveAt(_all.Count - 1);
        }

        ApplyFilter();
    }

    public void SetCameras(IEnumerable<(Guid Id, string Name)> cameras)
    {
        var current = SelectedCamera?.Id;
        var options = new List<CameraFilterOption> { new(null, "All cameras") };

        foreach (var camera in cameras.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            options.Add(new CameraFilterOption(camera.Id, camera.Name));
        }

        CameraChoices.Clear();
        foreach (var option in options)
        {
            CameraChoices.Add(option);
        }

        SelectedCamera = CameraChoices.FirstOrDefault(option => option.Id == current) ?? CameraChoices[0];
        Raise(nameof(CameraChoices));
    }

    private void ApplyFilter()
    {
        var filtered = _all.AsEnumerable();

        if (SelectedCamera is { Id: { } cameraId })
        {
            filtered = filtered.Where(row => row.CameraId == cameraId);
        }

        if (SelectedSeverity is { Minimum: { } minimum })
        {
            filtered = filtered.Where(row => row.Severity >= minimum);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            filtered = filtered.Where(row =>
                row.Message.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.CameraName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.KindLabel.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (IsDateFilterEnabled)
        {
            var from = new DateTimeOffset(DateTime.SpecifyKind(FromDate.Date, DateTimeKind.Unspecified), _zone.GetUtcOffset(FromDate.Date));
            var to = new DateTimeOffset(DateTime.SpecifyKind(ToDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified), _zone.GetUtcOffset(ToDate.Date));

            filtered = filtered.Where(row => row.OccurredUtc >= from.ToUniversalTime() && row.OccurredUtc <= to.ToUniversalTime());
        }

        var rows = filtered.ToList();

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        Summary = rows.Count switch
        {
            0 => _all.Count == 0 ? "No events recorded yet." : "No events match these filters.",
            1 => "1 event",
            _ => $"{rows.Count} events",
        };

        Raise(nameof(HasRows));
    }

    private EventRowViewModel ToRow(EventRecord entry)
    {
        var local = TimeZoneInfo.ConvertTime(entry.OccurredUtc, _zone);

        return new EventRowViewModel(
            entry.Id,
            entry.OccurredUtc,
            entry.CameraId,
            entry.CameraName,
            entry.Kind,
            KindLabel(entry.Kind),
            entry.Severity,
            entry.Message,
            entry.Detail,
            local.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            local.ToString("d MMM", CultureInfo.CurrentCulture));
    }

    private static string KindLabel(string kind) => kind switch
    {
        "stream" => "Stream",
        "device" => "Device",
        "export" => "Export",
        "recording" => "Recording",
        "authentication" => "Authentication",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(kind),
    };
}
