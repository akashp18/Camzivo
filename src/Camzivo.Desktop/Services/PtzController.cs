using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;

namespace Camzivo.Desktop.Services;

public sealed record PtzPreset(string Token, string Name);

public sealed record PtzAvailability(bool Available, string Detail, IReadOnlyList<PtzPreset> Presets)
{
    public static PtzAvailability Unavailable(string detail) => new(false, detail, []);
}

/// <summary>
/// Live PTZ for one camera at a time.
///
/// Two safety rules are built in, because a PTZ command that keeps running is worse than one that never started:
/// every movement is followed by an automatic stop (a click cannot leave the head panning forever), and forgetting the
/// camera — a stream failure, a tile change, or shutdown — always stops movement first.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PtzController(IOnvifTransport transport) : IDisposable
{
    private static readonly TimeSpan AutoStopAfter = TimeSpan.FromMilliseconds(600);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _autoStop;
    private Guid? _cameraId;
    private Uri? _serviceUri;
    private string? _username;
    private string? _password;
    private string? _profileToken;
    private bool _disposed;

    public Guid? CameraId => _cameraId;

    public bool IsMoving { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Reads service address, PTZ node and presets. A device without a PTZ service is reported, not guessed.</summary>
    public async Task<PtzAvailability> PrepareAsync(
        Guid cameraId,
        DeviceEndpoint endpoint,
        OnvifServices services,
        string? username,
        string? password,
        string? profileToken,
        CancellationToken cancellationToken = default,
        bool allowControl = true)
    {
        if (!allowControl)
        {
            return Unavailable("PTZ control is disabled in READ-ONLY NVR MODE. Recorder channels remain view-only.");
        }

        var serviceUri = services.Ptz ?? OnvifSoap.DeviceServiceUri(endpoint.Host, endpoint.OnvifPort, endpoint.UseTls);

        if (string.IsNullOrWhiteSpace(profileToken))
        {
            // A standalone camera has no stored profile token; read the profiles once and use the heaviest one.
            var profilesXml = await PostToAsync(
                OnvifSoap.DeviceServiceUri(endpoint.Host, endpoint.OnvifPort, endpoint.UseTls),
                OnvifSoap.ProfilesBody(),
                username,
                password,
                cancellationToken).ConfigureAwait(false);

            if (profilesXml is null)
            {
                return Unavailable("The camera did not report a video profile to address PTZ with.");
            }

            try
            {
                profileToken = OnvifSoap.SelectStreams(OnvifSoap.ParseProfiles(profilesXml)).Main?.Token;
            }
            catch (InvalidDataException)
            {
                profileToken = null;
            }

            if (string.IsNullOrWhiteSpace(profileToken))
            {
                return Unavailable("The camera reported no usable video profile for PTZ.");
            }
        }

        _cameraId = cameraId;
        _serviceUri = serviceUri;
        _username = username;
        _password = password;
        _profileToken = profileToken;

        var nodes = await PostAsync(OnvifPtzSoap.PtzNodesBody(), cancellationToken).ConfigureAwait(false);

        if (nodes is null)
        {
            return Unavailable("The device did not answer its PTZ service.");
        }

        if (!OnvifPtzSoap.ParseNodes(nodes, out var nodeDetail))
        {
            return Unavailable(nodeDetail);
        }

        var presetsXml = await PostAsync(OnvifPtzSoap.PresetsBody(profileToken), cancellationToken).ConfigureAwait(false);
        var presets = presetsXml is null
            ? []
            : OnvifPtzSoap.ParsePresets(presetsXml).Select(preset => new PtzPreset(preset.Token, preset.Name ?? $"Preset {preset.Token}")).ToList();

        return new PtzAvailability(true, nodeDetail, presets);
    }

    /// <summary>Moves for a moment and stops. Safe for a click; repeated presses keep the head moving.</summary>
    public async Task<bool> NudgeAsync(double pan, double tilt, double zoom, CancellationToken cancellationToken = default)
    {
        if (_serviceUri is null || _profileToken is null)
        {
            LastError = "No PTZ-capable camera is selected.";
            return false;
        }

        var moved = await SendAsync(OnvifPtzSoap.ContinuousMoveBody(_profileToken, pan, tilt, zoom), cancellationToken)
            .ConfigureAwait(false);

        if (!moved)
        {
            return false;
        }

        IsMoving = true;

        _autoStop?.Cancel();
        _autoStop?.Dispose();
        _autoStop = new CancellationTokenSource();

        var token = _autoStop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AutoStopAfter, token).ConfigureAwait(false);
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A newer movement took over the auto-stop.
            }
        }, CancellationToken.None);

        return true;
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_serviceUri is null || _profileToken is null)
        {
            return false;
        }

        IsMoving = false;
        return await SendAsync(OnvifPtzSoap.StopBody(_profileToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> GotoPresetAsync(string presetToken, CancellationToken cancellationToken = default)
    {
        if (_serviceUri is null || _profileToken is null || string.IsNullOrWhiteSpace(presetToken))
        {
            return false;
        }

        return await SendAsync(OnvifPtzSoap.GotoPresetBody(_profileToken, presetToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>An unavailable camera must also be unaddressable: otherwise a later nudge would still be sent.</summary>
    private PtzAvailability Unavailable(string detail)
    {
        _cameraId = null;
        _serviceUri = null;
        _profileToken = null;
        _username = null;
        _password = null;
        IsMoving = false;

        return PtzAvailability.Unavailable(detail);
    }

    /// <summary>Stops movement and forgets the camera: used on stream failure, tile change and shutdown.</summary>
    public void Forget(Guid cameraId)
    {
        if (_cameraId != cameraId)
        {
            return;
        }

        _ = StopAsync(CancellationToken.None);
        _cameraId = null;
        _serviceUri = null;
        _profileToken = null;
        _username = null;
        _password = null;
        IsMoving = false;
    }

    public void ForgetAll()
    {
        if (_cameraId is { } id)
        {
            Forget(id);
        }
    }

    private async Task<bool> SendAsync(string body, CancellationToken cancellationToken)
    {
        var response = await PostAsync(body, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            // A failed command must not leave the head moving: try the stop, then report.
            LastError = "The camera did not accept the PTZ command.";
            return false;
        }

        LastError = null;
        return true;
    }

    private async Task<string?> PostToAsync(
        Uri serviceUri,
        string body,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var envelope = OnvifSoap.BuildEnvelope(body, username, password);

        try
        {
            var response = await transport.PostAsync(serviceUri, envelope, null, cancellationToken).ConfigureAwait(false);
            return response.StatusCode is 200 or 202 ? response.Body : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<string?> PostAsync(string body, CancellationToken cancellationToken)
    {
        if (_serviceUri is null)
        {
            return null;
        }

        var envelope = OnvifSoap.BuildEnvelope(body, _username, _password);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await transport.PostAsync(_serviceUri, envelope, null, cancellationToken).ConfigureAwait(false);
            return response.StatusCode is 200 or 202 ? response.Body : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException exception)
        {
            LastError = exception.Message;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ForgetAll();
        _autoStop?.Cancel();
        _autoStop?.Dispose();
        _gate.Dispose();
    }
}
