using System.Diagnostics;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Net;

namespace Camzivo.Core.Devices;

/// <summary>Transport seam so the connection test can be driven by fixtures instead of a camera.</summary>
public interface IOnvifTransport
{
    Task<OnvifHttpResponse> PostAsync(Uri uri, string body, string? authorization, CancellationToken cancellationToken);
}

/// <summary>TCP reachability of the RTSP endpoint. Null failure means "the port accepted a connection".</summary>
public interface IRtspEndpointProbe
{
    Task<StreamFailure?> ProbeAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>Seam for onboarding and tests; the production implementation is <see cref="ConnectionTester"/>.</summary>
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(
        DeviceEndpoint endpoint,
        string? username,
        string? password,
        CancellationToken cancellationToken = default);
}

public sealed record OnvifHttpResponse(int StatusCode, string Body, string? WwwAuthenticate = null);

public enum ConnectionStage
{
    Reach = 0,
    Authenticate = 1,
    Profiles = 2,
    Stream = 3,
}

public sealed record StageResult(ConnectionStage Stage, bool Success, StreamFailure Failure, string Detail, TimeSpan Elapsed)
{
    public string Title => Stage switch
    {
        ConnectionStage.Reach => "Contact device",
        ConnectionStage.Authenticate => "Sign in",
        ConnectionStage.Profiles => "Read stream profiles",
        _ => "Check stream address",
    };
}

/// <summary>
/// The four-stage onboarding test: reach → authenticate → read profiles → verify the stream address answers.
/// Holds no credentials in its result, so it can be shown or logged without leaking anything.
/// </summary>
public sealed record ConnectionTestResult(
    bool Success,
    IReadOnlyList<StageResult> Stages,
    OnvifDeviceInformation? Information,
    IReadOnlyList<OnvifVideoProfile> Profiles,
    Uri? MainStream,
    Uri? SubStream,
    Diagnostic? Failure,
    OnvifServices? Services = null,
    OnvifSystemTime? SystemTime = null);

public sealed class ConnectionTester(IOnvifTransport transport, IRtspEndpointProbe rtspProbe) : IConnectionTester
{
    private const int MaxDigestRetries = 1;

    public async Task<ConnectionTestResult> TestAsync(
        DeviceEndpoint endpoint,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var stages = new List<StageResult>(4);
        var deviceUri = OnvifSoap.DeviceServiceUri(endpoint.Host, endpoint.OnvifPort, endpoint.UseTls);

        var reach = await PostAsync(deviceUri, OnvifSoap.DeviceInformationBody(), username, password, cancellationToken)
            .ConfigureAwait(false);
        stages.Add(reach.Stage);

        if (!reach.Stage.Success)
        {
            return Fail(stages, reach.Stage.Failure, reach.Stage.Detail);
        }

        stages.Add(new StageResult(
            ConnectionStage.Authenticate,
            true,
            StreamFailure.None,
            string.IsNullOrWhiteSpace(username) ? "Device answered without credentials" : $"Signed in as {username}",
            reach.Stage.Elapsed));

        OnvifDeviceInformation information;
        try
        {
            information = OnvifSoap.ParseDeviceInformation(reach.Body);
        }
        catch (InvalidDataException)
        {
            stages.Add(new StageResult(ConnectionStage.Profiles, false, StreamFailure.ProtocolViolation, "Device information was not valid ONVIF XML", TimeSpan.Zero));
            return Fail(stages, StreamFailure.ProtocolViolation, "GetDeviceInformation returned malformed XML.");
        }

        // Real recorders commonly publish media at a path other than /onvif/device_service. Read the advertised
        // XAddr first; if the device does not publish one, the device service is the only address we can try.
        var services = await ReadServicesAsync(deviceUri, username, password, cancellationToken).ConfigureAwait(false);
        var systemTime = await ReadSystemTimeAsync(deviceUri, username, password, cancellationToken).ConfigureAwait(false);
        var mediaUri = services.Media ?? deviceUri;

        var profilesResponse = await PostAsync(mediaUri, OnvifSoap.ProfilesBody(), username, password, cancellationToken)
            .ConfigureAwait(false);

        if (!profilesResponse.Stage.Success)
        {
            stages.Add(profilesResponse.Stage);
            return Fail(stages, profilesResponse.Stage.Failure, profilesResponse.Stage.Detail);
        }

        IReadOnlyList<OnvifVideoProfile> profiles;
        try
        {
            profiles = OnvifSoap.ParseProfiles(profilesResponse.Body);
        }
        catch (InvalidDataException)
        {
            stages.Add(new StageResult(ConnectionStage.Profiles, false, StreamFailure.ProtocolViolation, "The profile response was not ONVIF XML", TimeSpan.Zero));
            return Fail(stages, StreamFailure.ProtocolViolation, "The device did not return an ONVIF profile list.");
        }

        var (main, sub) = OnvifSoap.SelectStreams(profiles);
        if (main is null)
        {
            // "No video profile" covered two very different devices: one that returned no profiles at all and one
            // whose profiles carried nothing that describes video. The count and tokens say which without a packet
            // capture.
            var detail = profiles.Count == 0
                ? "The recorder returned no ONVIF profiles"
                : $"None of the {profiles.Count} returned profile(s) described video ({string.Join(", ", profiles.Take(4).Select(profile => profile.Token))})";

            stages.Add(new StageResult(ConnectionStage.Profiles, false, StreamFailure.NotFound, detail, TimeSpan.Zero));
            return Fail(stages, StreamFailure.NotFound, "The device reported no usable video profiles.");
        }

        stages.Add(new StageResult(
            ConnectionStage.Profiles,
            true,
            StreamFailure.None,
            $"{profiles.Count} profile(s); main {main.ResolutionText}, sub {(sub is null ? "none" : sub.ResolutionText)}",
            profilesResponse.Stage.Elapsed));

        var streamResponse = await PostAsync(mediaUri, OnvifSoap.StreamUriBody(main.Token), username, password, cancellationToken)
            .ConfigureAwait(false);

        if (!streamResponse.Stage.Success)
        {
            stages.Add(streamResponse.Stage);
            return Fail(stages, streamResponse.Stage.Failure, streamResponse.Stage.Detail);
        }

        OnvifMedia media;
        try
        {
            media = OnvifSoap.ParseStreamUri(streamResponse.Body);
        }
        catch (InvalidDataException)
        {
            stages.Add(new StageResult(ConnectionStage.Stream, false, StreamFailure.ProtocolViolation, "Stream URI response was not valid ONVIF XML", TimeSpan.Zero));
            return Fail(stages, StreamFailure.ProtocolViolation, "GetStreamUri returned malformed XML.");
        }

        if (media.StreamUri is null || !string.Equals(media.StreamUri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
        {
            stages.Add(new StageResult(ConnectionStage.Stream, false, StreamFailure.NotFound, "No RTSP URI was returned", TimeSpan.Zero));
            return Fail(stages, StreamFailure.NotFound, "The device did not return an RTSP stream URI.");
        }

        var rtspFailure = await rtspProbe.ProbeAsync(media.StreamUri, cancellationToken).ConfigureAwait(false);
        if (rtspFailure is not null)
        {
            stages.Add(new StageResult(ConnectionStage.Stream, false, rtspFailure.Value, $"RTSP endpoint did not answer ({rtspFailure})", TimeSpan.Zero));
            return Fail(stages, rtspFailure.Value, "The device returned a stream URI but its RTSP port did not accept a connection.");
        }

        // Sub stream is optional: a device may expose one profile only, or refuse the second GetStreamUri. Neither is
        // a reason to fail onboarding, because the wall can run on the main stream alone.
        Uri? subStream = null;
        if (sub is not null)
        {
            var subResponse = await PostAsync(mediaUri, OnvifSoap.StreamUriBody(sub.Token), username, password, cancellationToken)
                .ConfigureAwait(false);

            if (subResponse.Stage.Success)
            {
                try
                {
                    subStream = OnvifSoap.ParseStreamUri(subResponse.Body).StreamUri;
                }
                catch (InvalidDataException)
                {
                    subStream = null;
                }
            }
        }

        stages.Add(new StageResult(
            ConnectionStage.Stream,
            true,
            StreamFailure.None,
            $"RTSP endpoint reachable ({media.StreamUri.Host}:{media.StreamUri.Port})" +
            (subStream is null ? "; no sub stream" : "; main and sub stream available"),
            TimeSpan.Zero));

        return new ConnectionTestResult(true, stages, information, profiles, media.StreamUri, subStream, null, services, systemTime);
    }

    /// <summary>Capabilities/GetServices are optional; a device that refuses them is still tested over the device service.</summary>
    private async Task<OnvifServices> ReadServicesAsync(
        Uri deviceUri,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var capabilities = await PostAsync(deviceUri, OnvifPtzSoap.CapabilitiesBody(), username, password, cancellationToken)
            .ConfigureAwait(false);
        var services = capabilities.Stage.Success
            ? OnvifPtzSoap.ParseServices(capabilities.Body, deviceUri)
            : OnvifServices.None;

        if (services.Media is null || services.Ptz is null || services.Replay is null ||
            services.Recording is null || services.Search is null)
        {
            var list = await PostAsync(deviceUri, OnvifPtzSoap.ServicesBody(), username, password, cancellationToken)
                .ConfigureAwait(false);

            if (list.Stage.Success)
            {
                var advertised = OnvifPtzSoap.ParseServices(list.Body, deviceUri);
                services = services with
                {
                    Media = services.Media ?? advertised.Media,
                    Ptz = services.Ptz ?? advertised.Ptz,
                    Replay = services.Replay ?? advertised.Replay,
                    Recording = services.Recording ?? advertised.Recording,
                    Search = services.Search ?? advertised.Search,
                    Names = advertised.Names.Count > 0 ? advertised.Names : services.Names,
                };
            }
        }

        return services;
    }

    private async Task<OnvifSystemTime?> ReadSystemTimeAsync(
        Uri deviceUri,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var time = await PostAsync(deviceUri, OnvifSoap.SystemDateAndTimeBody(), username, password, cancellationToken)
            .ConfigureAwait(false);

        if (!time.Stage.Success)
        {
            return null;
        }

        var parsed = OnvifSoap.ParseSystemDateAndTime(time.Body);
        return parsed.Utc is null && parsed.Local is null ? null : parsed;
    }

    /// <summary>One ONVIF call with WS-Security, retried once with HTTP Digest if the device demands it.</summary>
    private async Task<PostOutcome> PostAsync(
        Uri uri,
        string body,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string? authorization = null;
        var attempt = 0;

        while (true)
        {
            var envelope = OnvifSoap.BuildEnvelope(body, username, password);

            try
            {
                var response = await transport
                    .PostAsync(uri, envelope, authorization, cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                if (response.StatusCode is 200 or 202)
                {
                    return new PostOutcome(
                        new StageResult(ConnectionStage.Reach, true, StreamFailure.None, "Device answered", stopwatch.Elapsed),
                        response.Body);
                }

                if (response.StatusCode == 401 &&
                    attempt < MaxDigestRetries &&
                    !string.IsNullOrWhiteSpace(username) &&
                    HttpDigest.TryParse(response.WwwAuthenticate, out var challenge) &&
                    !string.IsNullOrEmpty(challenge.Nonce))
                {
                    authorization = HttpDigest.BuildAuthorization(
                        challenge,
                        "POST",
                        uri.PathAndQuery,
                        username,
                        password ?? string.Empty,
                        clientNonce: Guid.NewGuid().ToString("N"));
                    attempt++;
                    continue;
                }

                var failure = OnvifSoap.ClassifyFailure(response.StatusCode, response.Body);
                return new PostOutcome(
                    new StageResult(ConnectionStage.Reach, false, failure, $"HTTP {response.StatusCode}", stopwatch.Elapsed),
                    string.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return new PostOutcome(
                    new StageResult(ConnectionStage.Reach, false, StreamFailure.Timeout, "The request exceeded the timeout", stopwatch.Elapsed),
                    string.Empty);
            }
            catch (HttpRequestException exception)
            {
                stopwatch.Stop();
                var failure = exception.InnerException is System.Net.Sockets.SocketException socket
                    ? (socket.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound
                        ? StreamFailure.DnsFailure
                        : StreamFailure.ConnectionRefused)
                    : exception.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
                        ? StreamFailure.DnsFailure
                        : StreamFailure.ConnectionRefused;

                return new PostOutcome(
                    new StageResult(ConnectionStage.Reach, false, failure, exception.Message, stopwatch.Elapsed),
                    string.Empty);
            }
        }
    }

    private static ConnectionTestResult Fail(IReadOnlyList<StageResult> stages, StreamFailure failure, string? detail) =>
        new(false, stages, null, [], null, null, ConnectionDiagnostics.Explain(failure, detail));

    /// <summary>Stage outcome plus the response payload, which the caller parses.</summary>
    private sealed record PostOutcome(StageResult Stage, string Body);
}
