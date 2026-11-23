using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Net;
using Camzivo.Core.Recording;
using Camzivo.Data.Net;

namespace Camzivo.Data.Devices;

public sealed record NvrDiscoveryResult(
    bool Success,
    OnvifDeviceInformation? Information,
    OnvifServices Services,
    IReadOnlyList<DiscoveredChannel> Channels,
    IReadOnlyList<StageResult> Stages,
    Diagnostic? Failure,
    string? ValidationError = null,
    OnvifSystemTime? SystemTime = null,
    IReadOnlyList<OnvifVideoProfile>? Profiles = null);

/// <summary>
/// Reads a recorder's channels through the read-only NVR boundary.
///
/// The connection test proves reach → sign-in → profiles → stream. This adds the facts an NVR import needs: the
/// service endpoints it advertises, a stream URI per profile, and — when Profile G exposes them — the recording
/// source tokens that map a channel to its recordings.
///
/// Every request is guarded by <see cref="ReadOnlyNvrTransport"/>: only the exact approved host and ONVIF port are
/// contacted, only allowlisted operations are sent, and RTSP stream addresses on another host or port are rejected
/// rather than followed. A cross-host service XAddr therefore fails with an approval request, never a silent redirect.
/// </summary>
public sealed class NvrDiscovery(IOnvifTransport transport, IRtspEndpointProbe rtspProbe)
{
    private const int MaxRecordingSources = 64;

    public async Task<NvrDiscoveryResult> DiscoverAsync(
        DeviceEndpoint endpoint,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var policy = NvrAccessPolicy.ForEndpoint(endpoint);
        var guardedTransport = new ReadOnlyNvrTransport(transport, policy);
        var guardedProbe = new ReadOnlyNvrRtspProbe(rtspProbe, policy);

        try
        {
            return await DiscoverCoreAsync(endpoint, username, password, guardedTransport, guardedProbe, policy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NvrAccessDeniedException exception)
        {
            return new NvrDiscoveryResult(
                false,
                null,
                OnvifServices.None,
                [],
                [],
                ConnectionDiagnostics.Explain(StreamFailure.NvrPermissionDenied, exception.Message),
                ValidationError: exception.Message);
        }
    }

    private async Task<NvrDiscoveryResult> DiscoverCoreAsync(
        DeviceEndpoint endpoint,
        string? username,
        string? password,
        IOnvifTransport onvif,
        IRtspEndpointProbe probe,
        NvrAccessPolicy policy,
        CancellationToken cancellationToken)
    {
        var tester = new ConnectionTester(onvif, probe);
        var test = await tester.TestAsync(endpoint, username, password, cancellationToken).ConfigureAwait(false);

        if (!test.Success)
        {
            return new NvrDiscoveryResult(false, null, OnvifServices.None, [], test.Stages, test.Failure);
        }

        var deviceUri = OnvifSoap.DeviceServiceUri(endpoint.Host, endpoint.OnvifPort, endpoint.UseTls);
        var services = test.Services ?? await ReadServicesAsync(onvif, deviceUri, username, password, cancellationToken)
            .ConfigureAwait(false);

        // Media lives at the advertised XAddr on real firmware; the device service is only the fallback.
        var mediaUri = services.Media ?? deviceUri;

        var uris = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var profile in test.Profiles)
        {
            if (uris.ContainsKey(profile.Token))
            {
                continue;
            }

            var response = await PostAsync(
                onvif,
                mediaUri,
                OnvifSoap.StreamUriBody(profile.Token),
                username,
                password,
                cancellationToken).ConfigureAwait(false);

            if (response is null)
            {
                continue;
            }

            try
            {
                if (OnvifSoap.ParseStreamUri(response).StreamUri is { } uri)
                {
                    if (!policy.AllowsStreamUri(uri))
                    {
                        return BlockedStream(policy, uri, test);
                    }

                    uris[profile.Token] = uri;
                }
            }
            catch (InvalidDataException)
            {
                // One unreadable profile must not lose the channels that did resolve.
            }
        }

        var channels = NvrChannelPlanner.Plan(test.Profiles, token => uris.TryGetValue(token, out var uri) ? uri : null);
        channels = await AttachRecordingSourcesAsync(onvif, deviceUri, channels, username, password, cancellationToken)
            .ConfigureAwait(false);

        if (channels.Count == 0)
        {
            return new NvrDiscoveryResult(
                false,
                test.Information,
                services,
                [],
                test.Stages,
                ConnectionDiagnostics.Explain(StreamFailure.NotFound, "No channel produced a stream address."),
                SystemTime: test.SystemTime,
                Profiles: test.Profiles);
        }

        return new NvrDiscoveryResult(
            true,
            test.Information,
            services,
            channels,
            test.Stages,
            null,
            SystemTime: test.SystemTime,
            Profiles: test.Profiles);
    }

    private static NvrDiscoveryResult BlockedStream(NvrAccessPolicy policy, Uri uri, ConnectionTestResult test)
    {
        var message =
            $"The recorder advertised a stream on {NvrSafety.Redact(uri)}, which is outside the approved " +
            $"{policy.ExpectedRtspOrigin}. Nothing was opened. Approve that host/port explicitly before importing.";

        return new NvrDiscoveryResult(
            false,
            test.Information,
            test.Services ?? OnvifServices.None,
            [],
            test.Stages,
            ConnectionDiagnostics.Explain(StreamFailure.NvrPermissionDenied, message),
            ValidationError: message,
            SystemTime: test.SystemTime,
            Profiles: test.Profiles);
    }

    /// <summary>
    /// Profile G does not link a media profile to a recording source directly. The documented bridge is
    /// GetRecordings → GetRecordingInformation, whose Source has a token and a name. We only accept a source whose
    /// name/token contains the same channel number as the discovered channel; anything else stays unmapped.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredChannel>> AttachRecordingSourcesAsync(
        IOnvifTransport onvif,
        Uri deviceUri,
        IReadOnlyList<DiscoveredChannel> channels,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (channels.Count == 0)
        {
            return channels;
        }

        var recordingServices = await ReadRecordingServicesAsync(onvif, deviceUri, username, password, cancellationToken)
            .ConfigureAwait(false);
        var recordingUri = recordingServices.Recording;

        if (recordingUri is null)
        {
            return channels;
        }

        var listXml = await PostAsync(
            onvif,
            recordingUri,
            OnvifRecordingSoap.GetRecordingsBody(),
            username,
            password,
            cancellationToken).ConfigureAwait(false);

        if (listXml is null)
        {
            return channels;
        }

        var sources = new List<OnvifRecordingSoap.RecordingSource>();
        sources.AddRange(OnvifRecordingSoap.ParseRecordingSources(listXml));

        var tokens = OnvifRecordingSoap.ParseRecordings(listXml);
        foreach (var token in tokens.Take(MaxRecordingSources))
        {
            if (sources.Any(source => string.Equals(source.RecordingToken, token, StringComparison.Ordinal)))
            {
                continue;
            }

            var info = await PostAsync(
                onvif,
                recordingUri,
                OnvifRecordingSoap.GetRecordingInformationBody(token),
                username,
                password,
                cancellationToken).ConfigureAwait(false);

            if (info is not null)
            {
                sources.AddRange(OnvifRecordingSoap.ParseRecordingSources(info));
            }
        }

        if (sources.Count == 0)
        {
            return channels;
        }

        return channels
            .Select(channel =>
            {
                var match = sources.FirstOrDefault(source =>
                    NvrChannelPlanner.ReadChannelNumber(source.SourceName) == channel.Channel ||
                    NvrChannelPlanner.ReadChannelNumber(source.SourceToken) == channel.Channel);

                return match?.SourceToken is { Length: > 0 } token
                    ? channel with { SourceToken = token }
                    : channel;
            })
            .ToList();
    }

    private async Task<OnvifServices> ReadServicesAsync(
        IOnvifTransport onvif,
        Uri deviceUri,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        // Both calls are optional: a recorder that answers neither is still importable, it just reports no services.
        var capabilities = await PostAsync(onvif, deviceUri, OnvifPtzSoap.CapabilitiesBody(), username, password, cancellationToken)
            .ConfigureAwait(false);
        var services = capabilities is null ? OnvifServices.None : OnvifPtzSoap.ParseServices(capabilities, deviceUri);

        if (services.Media is null || services.Ptz is null || services.Replay is null)
        {
            var list = await PostAsync(onvif, deviceUri, OnvifPtzSoap.ServicesBody(), username, password, cancellationToken)
                .ConfigureAwait(false);

            if (list is not null)
            {
                var advertised = OnvifPtzSoap.ParseServices(list, deviceUri);
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

    private async Task<RecordingServices> ReadRecordingServicesAsync(
        IOnvifTransport onvif,
        Uri deviceUri,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        // GetCapabilities is the older, more widely implemented call; GetServices is the fallback for devices that
        // publish the Profile G namespaces only there.
        var capabilities = await PostAsync(onvif, deviceUri, OnvifPtzSoap.CapabilitiesBody(), username, password, cancellationToken)
            .ConfigureAwait(false);

        if (capabilities is not null)
        {
            var advertised = OnvifPtzSoap.ParseServices(capabilities, deviceUri);
            if (advertised.Recording is not null)
            {
                return new RecordingServices(advertised.Recording, advertised.Search, advertised.Replay);
            }
        }

        var services = await PostAsync(onvif, deviceUri, OnvifPtzSoap.ServicesBody(), username, password, cancellationToken)
            .ConfigureAwait(false);

        return services is null ? RecordingServices.None : OnvifRecordingSoap.ParseServices(services, deviceUri);
    }

    /// <summary>One authenticated ONVIF call, retried once with HTTP Digest when the device challenges.</summary>
    private async Task<string?> PostAsync(
        IOnvifTransport onvif,
        Uri uri,
        string body,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        string? authorization = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var envelope = OnvifSoap.BuildEnvelope(body, username, password);

            try
            {
                var response = await onvif.PostAsync(uri, envelope, authorization, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode is 200 or 202)
                {
                    return response.Body;
                }

                if (response.StatusCode == 401 &&
                    attempt == 0 &&
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
                    continue;
                }

                return null;
            }
            catch (NvrAccessDeniedException)
            {
                throw;
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

        return null;
    }
}
