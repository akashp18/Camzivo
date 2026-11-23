using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Camzivo.Core.Devices;
using Camzivo.Data.Devices;
using Camzivo.Data.Net;
using Camzivo.Desktop.Services;

namespace Camzivo.Desktop.ViewModels;

public enum NvrConnectionState
{
    Idle = 0,
    Confirming = 1,
    Testing = 2,
    Passed = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>One labelled fact in the test result. Never holds a credential.</summary>
public sealed record NvrConnectionRow(string Label, string Value);

/// <summary>
/// The manual NVR connection test behind <c>NvrConnectionWindow</c>.
///
/// The password is never a property, field, binding target or log value: the window passes it as a method argument for
/// the duration of one attempt, and this class does not keep it. Opening the window performs no network activity —
/// <see cref="RequestTest"/> only validates and asks for confirmation, and <see cref="ConfirmAsync"/> is the single
/// place where an authorization decision exists, for that one test only.
/// </summary>
public sealed class NvrConnectionViewModel : ObservableObject
{
    private readonly Func<IOnvifTransport> _transportFactory;
    private readonly TimeSpan _overallTimeout;
    private readonly ObservableCollection<NvrConnectionRow> _rows = [];

    private CancellationTokenSource? _test;
    private string _host = string.Empty;
    private bool _useTls;
    private string _portText = "80";
    private string _username = "admin";
    private bool _isPasswordRevealed;
    private NvrConnectionState _state = NvrConnectionState.Idle;
    private string? _error;
    private string _confirmationDetail = string.Empty;
    private string _progress = string.Empty;
    private NvrMetadataResult? _result;

    public NvrConnectionViewModel(Func<IOnvifTransport>? transportFactory = null, TimeSpan? overallTimeout = null)
    {
        _transportFactory = transportFactory ?? (() => new HttpOnvifTransport());
        _overallTimeout = overallTimeout ?? NvrMetadataProbe.DefaultOverallTimeout;
        Rows = new ReadOnlyObservableCollection<NvrConnectionRow>(_rows);
    }

    /// <summary>Rows shown in the result panel; cleared before every attempt.</summary>
    public ReadOnlyObservableCollection<NvrConnectionRow> Rows { get; }

    public string Host
    {
        get => _host;
        set
        {
            if (Set(ref _host, value))
            {
                Raise(nameof(Destination));
            }
        }
    }

    /// <summary>False = HTTP, true = HTTPS. Changing it moves the port between the two defaults if it was untouched.</summary>
    public bool UseTls
    {
        get => _useTls;
        set
        {
            if (!Set(ref _useTls, value))
            {
                return;
            }

            Raise(nameof(Scheme));
            Raise(nameof(Destination));
            Raise(nameof(IsHttp));
            Raise(nameof(IsHttps));

            var previousDefault = NvrConnectionForm.DefaultPort(!value).ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(PortText) || PortText.Trim() == previousDefault)
            {
                PortText = NvrConnectionForm.DefaultPort(value).ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    public bool IsHttp
    {
        get => !UseTls;
        set
        {
            if (value)
            {
                UseTls = false;
            }
        }
    }

    public bool IsHttps
    {
        get => UseTls;
        set
        {
            if (value)
            {
                UseTls = true;
            }
        }
    }

    public string PortText
    {
        get => _portText;
        set
        {
            if (Set(ref _portText, value))
            {
                Raise(nameof(Destination));
            }
        }
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    /// <summary>A UI switch only; the password itself never enters this class.</summary>
    public bool IsPasswordRevealed
    {
        get => _isPasswordRevealed;
        set => Set(ref _isPasswordRevealed, value);
    }

    public string Scheme => UseTls ? "https" : "http";

    /// <summary>The exact destination shown in the confirmation, built from the validated fields.</summary>
    public string Destination
    {
        get
        {
            var port = int.TryParse(PortText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : NvrConnectionForm.DefaultPort(UseTls);

            return $"{Scheme}://{(string.IsNullOrWhiteSpace(Host) ? "<host>" : Host.Trim())}:{port}";
        }
    }

    public NvrConnectionState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value))
            {
                return;
            }

            Raise(nameof(IsConfirming));
            Raise(nameof(IsTesting));
            Raise(nameof(IsIdle));
            Raise(nameof(HasResult));
            Raise(nameof(CanTest));
            Raise(nameof(StatusText));
        }
    }

    public bool IsConfirming => State == NvrConnectionState.Confirming;

    public bool IsTesting => State == NvrConnectionState.Testing;

    public bool IsIdle => State == NvrConnectionState.Idle;

    public bool HasResult => State is NvrConnectionState.Passed or NvrConnectionState.Failed or NvrConnectionState.Cancelled;

    public bool CanTest => State is NvrConnectionState.Idle or NvrConnectionState.Passed or NvrConnectionState.Failed
        or NvrConnectionState.Cancelled;

    public string StatusText => State switch
    {
        NvrConnectionState.Confirming => "Confirm this test",
        NvrConnectionState.Testing => "Reading metadata…",
        NvrConnectionState.Passed => "Metadata read — no stream opened",
        NvrConnectionState.Failed => "Test failed",
        NvrConnectionState.Cancelled => "Test cancelled",
        _ => "Not tested",
    };

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public string ConfirmationDetail
    {
        get => _confirmationDetail;
        private set => Set(ref _confirmationDetail, value);
    }

    public string Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>The last result, kept so the UI can show it again. It contains no credential by construction.</summary>
    public NvrMetadataResult? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    /// <summary>
    /// Validates the fields and asks for confirmation. It never contacts anything: a valid form only moves the window
    /// to <see cref="NvrConnectionState.Confirming"/>.
    /// </summary>
    public void RequestTest(string? password)
    {
        if (IsTesting)
        {
            return;
        }

        Result = null;
        _rows.Clear();

        var error = NvrConnectionForm.Validate(Host, PortText, UseTls, out _);
        if (error is not null)
        {
            Error = error;
            State = NvrConnectionState.Idle;
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            Error = "Enter the NVR password.";
            State = NvrConnectionState.Idle;
            return;
        }

        Error = null;
        ConfirmationDetail = Destination;
        State = NvrConnectionState.Confirming;
    }

    /// <summary>Dismisses the confirmation without contacting anything.</summary>
    public void Decline()
    {
        if (!IsConfirming)
        {
            return;
        }

        ConfirmationDetail = string.Empty;
        State = NvrConnectionState.Idle;
    }

    /// <summary>
    /// Runs the metadata-only test the operator just confirmed. The authorization decision is created here, from the
    /// confirmed destination and for this call only; it is not stored and the full-probe flag is never consulted.
    /// </summary>
    public async Task ConfirmAsync(string? password)
    {
        if (!IsConfirming)
        {
            return;
        }

        var error = NvrConnectionForm.Validate(Host, PortText, UseTls, out var port);
        if (error is not null)
        {
            Error = error;
            State = NvrConnectionState.Idle;
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            Error = "Enter the NVR password.";
            State = NvrConnectionState.Idle;
            return;
        }

        var host = Host.Trim();
        var access = NvrMetadataAccessGate.Evaluate(host, port, UseTls, metadataAuthorized: true, confirmedHost: host);
        if (!access.Allowed)
        {
            Error = access.Detail;
            State = NvrConnectionState.Failed;
            return;
        }

        Error = null;
        ConfirmationDetail = string.Empty;
        Progress = "Reading device information and metadata…";
        State = NvrConnectionState.Testing;

        _test = new CancellationTokenSource();
        var token = _test.Token;
        IOnvifTransport? transport = null;

        try
        {
            transport = _transportFactory();
            var probe = new NvrMetadataProbe(transport, NvrMetadataProbe.DefaultMaxHttpAttempts, _overallTimeout);
            var result = await probe
                .RunAsync(host, port, UseTls, Username?.Trim(), password, token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                State = NvrConnectionState.Cancelled;
                Error = "The test was cancelled. No further request was sent.";
                return;
            }

            Apply(result);
        }
        catch (OperationCanceledException)
        {
            State = NvrConnectionState.Cancelled;
            Error = "The test was cancelled. No further request was sent.";
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or NvrAccessDeniedException
                                              or InvalidOperationException
                                              or IOException
                                              or UriFormatException)
        {
            State = NvrConnectionState.Failed;
            Error = exception.Message;
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
            _test?.Dispose();
            _test = null;
            Progress = string.Empty;
        }
    }

    /// <summary>Stops an in-flight test. The probe records the timeout and returns; nothing else is attempted.</summary>
    public void CancelTest()
    {
        if (!IsTesting)
        {
            return;
        }

        Progress = "Cancelling…";
        _test?.Cancel();
    }

    /// <summary>Cancels any test and forgets the last result and the reveal state. Called when the window closes.</summary>
    public void Reset()
    {
        _test?.Cancel();
        _rows.Clear();
        Result = null;
        Error = null;
        ConfirmationDetail = string.Empty;
        Progress = string.Empty;
        IsPasswordRevealed = false;
        State = NvrConnectionState.Idle;
    }

    private void Apply(NvrMetadataResult result)
    {
        Result = result;
        _rows.Clear();

        Add("Status", result.Success ? "Metadata read successfully — no stream opened" : "Test did not complete");
        Add("Manufacturer", Or(result.Information?.Manufacturer));
        Add("Model", Or(result.Information?.Model));
        Add("Firmware", Or(result.Information?.FirmwareVersion));
        Add("ONVIF services",
            result.Services.Names.Count > 0 ? string.Join(", ", result.Services.Names) : "Not advertised");
        Add("Device time", result.SystemTime?.Utc is { } utc
            ? $"{utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC · {result.SystemTime.Local ?? "local time not returned"}"
            : "Not returned");
        Add("Time zone", result.SystemTime?.TimeZone ?? "Not returned");
        Add("Media profiles", result.Profiles.Count.ToString(CultureInfo.InvariantCulture));
        Add("Video sources", result.Sources.Count.ToString(CultureInfo.InvariantCulture));
        Add("Encoder configurations", result.Encoders.Count.ToString(CultureInfo.InvariantCulture));

        if (result.Relationships.Count == 0)
        {
            Add("Stream relationships", "No usable main/sub profile was reported");
        }

        foreach (var relationship in result.Relationships)
        {
            Add($"{RoleLabel(relationship.Role)} stream", Describe(relationship));
        }

        Add("Unsupported capabilities", result.UnsupportedOperations.Count == 0
            ? "None reported"
            : string.Join(", ", result.UnsupportedOperations));
        Add("Blocked endpoints", result.BlockedEndpoints.Count == 0
            ? "None"
            : string.Join(" · ", result.BlockedEndpoints));
        Add("Requests", $"{result.HttpAttempts} HTTP attempt(s) in {result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");

        // Every attempt is listed with its status, so a failure is a fact the operator can act on rather than a
        // single sentence. The target is the masked endpoint; no body, header or credential is shown.
        foreach (var request in result.Requests)
        {
            Add($"Request · {request.Operation}", $"{request.State} — {request.Detail} ({request.Target})");
        }

        if (result.Success)
        {
            State = NvrConnectionState.Passed;
            Error = null;
            return;
        }

        State = NvrConnectionState.Failed;
        Error = result.Error ?? "The recorder did not return the requested metadata.";

        if (Diagnose(result) is { Length: > 0 } hint)
        {
            Add("If this keeps failing", hint);
        }
    }

    private void Add(string label, string value) => _rows.Add(new NvrConnectionRow(label, value));

    /// <summary>
    /// Turns the recorded status codes into the few causes that actually happen on Dahua firmware. It only explains
    /// what was already reported; it never retries and never contacts anything.
    /// </summary>
    private static string? Diagnose(NvrMetadataResult result)
    {
        var text = string.Join(" ", result.Requests.Select(request => request.Detail)) + " " + (result.Error ?? string.Empty);

        if (text.Contains("401", StringComparison.Ordinal))
        {
            return "The recorder answered 401. Check that ONVIF is enabled on the NVR (Network → Access Platform / " +
                   "ONVIF) and that this account is allowed to use it; also compare the NVR clock with this PC, because " +
                   "the WS-Security digest is time-limited.";
        }

        if (text.Contains("30", StringComparison.Ordinal) &&
            (text.Contains("HTTP 301", StringComparison.Ordinal) ||
             text.Contains("HTTP 302", StringComparison.Ordinal) ||
             text.Contains("HTTP 307", StringComparison.Ordinal) ||
             text.Contains("HTTP 308", StringComparison.Ordinal)))
        {
            return "The recorder redirected the request and Camzivo does not follow redirects. Try HTTPS, or the port " +
                   "its web interface actually uses.";
        }

        if (text.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows does not trust the recorder's HTTPS certificate, so the connection was refused. Import that " +
                   "certificate, or test over HTTP on the ONVIF port.";
        }

        if (text.Contains("time limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Nothing answered in time. Confirm the address and ONVIF port from another device on the same " +
                   "network; SmartPSS can work over Dahua's private port while ONVIF stays closed.";
        }

        if (text.Contains("not in the metadata-only allowlist", StringComparison.Ordinal))
        {
            return "A request outside the metadata-only allowlist was blocked locally. Nothing was sent to the recorder.";
        }

        return null;
    }

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "Not returned" : value!;

    private static string RoleLabel(string role) => role switch
    {
        "main" => "Main",
        "sub" => "Sub",
        _ => "Profile",
    };

    private static string Describe(NvrProfileRelationship relationship)
    {
        var name = string.IsNullOrWhiteSpace(relationship.ProfileName) ? "unnamed" : relationship.ProfileName;
        var resolution = relationship.Width > 0 && relationship.Height > 0
            ? $"{relationship.Width}×{relationship.Height}"
            : "resolution unknown";
        var encoding = string.IsNullOrWhiteSpace(relationship.Encoding) ? "encoding unknown" : relationship.Encoding;
        var rate = relationship.FrameRate is { } frameRate
            ? $" @ {frameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps"
            : string.Empty;

        return $"{name} · {resolution} · {encoding}{rate}";
    }
}
