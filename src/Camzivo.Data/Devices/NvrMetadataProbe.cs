using System.Diagnostics;
using System.Net.Http;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Net;
using Camzivo.Data.Net;

namespace Camzivo.Data.Devices;

/// <summary>One ONVIF request the metadata probe attempted, with its outcome and duration. Never contains a body or credential.</summary>
public sealed record NvrMetadataRequest(
    string Operation,
    string Target,
    string State,
    string Detail,
    TimeSpan Elapsed);

/// <summary>Binds a media profile to the source and encoder it references, and says which one is the main stream.</summary>
public sealed record NvrProfileRelationship(
    string ProfileToken,
    string? ProfileName,
    string Role,
    string? SourceToken,
    string? EncoderToken,
    int Width,
    int Height,
    double? FrameRate,
    string? Encoding);

/// <summary>
/// Everything a metadata-only verification learned. There is no stream URI, no recording list and no PTZ state here
/// because the probe has no operation that could produce them.
/// </summary>
public sealed record NvrMetadataResult(
    bool Success,
    OnvifDeviceInformation? Information,
    OnvifServices Services,
    OnvifSystemTime? SystemTime,
    IReadOnlyList<OnvifVideoProfile> Profiles,
    IReadOnlyList<OnvifVideoSource> Sources,
    IReadOnlyList<OnvifVideoEncoderConfiguration> Encoders,
    IReadOnlyList<NvrProfileRelationship> Relationships,
    IReadOnlyList<NvrMetadataRequest> Requests,
    IReadOnlyList<string> BlockedEndpoints,
    IReadOnlyList<string> UnsupportedOperations,
    int HttpAttempts,
    string? Error,
    TimeSpan Elapsed);

/// <summary>
/// Metadata-only recorder verification, used by <c>--probe-nvr-metadata</c>.
///
/// It exists so a recorder can be identified and understood before anything else is attempted, and so that the first
/// contact with production hardware can be a set of Get-only device and media queries rather than a full workflow.
/// The transport below it enforces <see cref="NvrOperationScope.MetadataOnly"/>: no stream URI, no replay, no
/// recording search, no PTZ movement, no configuration write, and no RTSP connection — there is no RTSP dependency in
/// this type at all. Requests are strictly sequential, with a digest retry per request, a hard cap on HTTP attempts and
/// an overall time limit.
/// </summary>
public sealed class NvrMetadataProbe
{
    public const int DefaultMaxHttpAttempts = 20;

    public static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromSeconds(60);

    private readonly IOnvifTransport _transport;
    private readonly int _maxHttpAttempts;
    private readonly TimeSpan _overallTimeout;

    public NvrMetadataProbe(
        IOnvifTransport transport,
        int maxHttpAttempts = DefaultMaxHttpAttempts,
        TimeSpan? overallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        if (maxHttpAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHttpAttempts), "The request budget must allow at least one attempt.");
        }

        _transport = transport;
        _maxHttpAttempts = maxHttpAttempts;
        _overallTimeout = overallTimeout ?? DefaultOverallTimeout;
    }

    public async Task<NvrMetadataResult> RunAsync(
        string host,
        int onvifPort,
        bool useTls,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var policy = new NvrAccessPolicy(host.Trim(), onvifPort, RtspPort: 554, useTls);
        var guarded = new ReadOnlyNvrTransport(_transport, policy, NvrOperationScope.MetadataOnly);
        var session = new Session(guarded, policy, _maxHttpAttempts);

        using var budget = new CancellationTokenSource(_overallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

        var stopwatch = Stopwatch.StartNew();
        var result = await session.CollectAsync(host.Trim(), onvifPort, useTls, username, password, linked.Token)
            .ConfigureAwait(false);
        stopwatch.Stop();

        return result with { Elapsed = stopwatch.Elapsed };
    }

    /// <summary>
    /// One sequential run. It is an instance of its own so the accumulated report cannot leak between probes, and every
    /// helper below records what happened before returning.
    /// </summary>
    private sealed class Session(IOnvifTransport transport, NvrAccessPolicy policy, int maxHttpAttempts)
    {
        private const int MaxDigestRetries = 1;

        private readonly List<NvrMetadataRequest> _requests = [];
        private readonly List<string> _blocked = [];
        private readonly List<string> _unsupported = [];
        private readonly List<NvrProfileRelationship> _relationships = [];
        private IReadOnlyList<OnvifVideoProfile> _profiles = [];
        private IReadOnlyList<OnvifVideoSource> _sources = [];
        private IReadOnlyList<OnvifVideoEncoderConfiguration> _encoders = [];
        private int _attempts;

        public async Task<NvrMetadataResult> CollectAsync(
            string host,
            int onvifPort,
            bool useTls,
            string? username,
            string? password,
            CancellationToken cancellationToken)
        {
            try
            {
                return await CollectCoreAsync(host, onvifPort, useTls, username, password, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Result(
                    null,
                    OnvifServices.None,
                    null,
                    "The metadata session stopped at its overall time limit. No further request was sent.");
            }
            catch (NvrAccessDeniedException exception)
            {
                return Result(null, OnvifServices.None, null, exception.Message);
            }
        }

        private async Task<NvrMetadataResult> CollectCoreAsync(
            string host,
            int onvifPort,
            bool useTls,
            string? username,
            string? password,
            CancellationToken cancellationToken)
        {
            var deviceUri = OnvifSoap.DeviceServiceUri(host, onvifPort, useTls);

            var identity = await PostAsync(
                deviceUri, "GetDeviceInformation", OnvifSoap.DeviceInformationBody(), username, password, cancellationToken)
                .ConfigureAwait(false);

            if (identity.Body is null)
            {
                return Result(
                    null,
                    OnvifServices.None,
                    null,
                    $"GetDeviceInformation did not succeed ({identity.Detail}). No further request was sent.");
            }

            OnvifDeviceInformation information;
            try
            {
                information = OnvifSoap.ParseDeviceInformation(identity.Body);
            }
            catch (InvalidDataException)
            {
                return Result(null, OnvifServices.None, null, "GetDeviceInformation returned malformed XML.");
            }

            // Capabilities and the service list are optional: a recorder that answers neither is still identifiable.
            var services = OnvifServices.None;
            var capabilities = await PostAsync(
                deviceUri, "GetCapabilities", OnvifPtzSoap.CapabilitiesBody(), username, password, cancellationToken)
                .ConfigureAwait(false);
            if (capabilities.Body is not null)
            {
                services = OnvifPtzSoap.ParseServices(capabilities.Body, deviceUri);
            }

            var list = await PostAsync(
                deviceUri, "GetServices", OnvifPtzSoap.ServicesBody(), username, password, cancellationToken)
                .ConfigureAwait(false);
            if (list.Body is not null)
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

            await PostAsync(
                deviceUri, "GetServiceCapabilities", OnvifSoap.ServiceCapabilitiesBody(), username, password, cancellationToken)
                .ConfigureAwait(false);

            OnvifSystemTime? systemTime = null;
            var time = await PostAsync(
                deviceUri, "GetSystemDateAndTime", OnvifSoap.SystemDateAndTimeBody(), username, password, cancellationToken)
                .ConfigureAwait(false);
            if (time.Body is not null)
            {
                var parsed = OnvifSoap.ParseSystemDateAndTime(time.Body);
                if (parsed.Utc is not null || parsed.Local is not null)
                {
                    systemTime = parsed;
                }
            }
            else
            {
                _unsupported.Add("GetSystemDateAndTime");
            }

            // A service XAddr on another host or port is reported, never followed. It is the recorder's own statement,
            // not an instruction: a camera address or a different management interface needs a separate approval.
            ReportOutsideEndpoints(services);

            if (services.Media is { } media && !policy.AllowsOnvifUri(media))
            {
                return Result(
                    information,
                    services,
                    systemTime,
                    $"The recorder advertised media at {Describe(media)}, which is outside the approved " +
                    $"{policy.ExpectedOnvifOrigin}. No media metadata was requested.");
            }

            var mediaUri = services.Media ?? deviceUri;

            var profiles = await PostAsync(
                mediaUri, "GetProfiles", OnvifSoap.ProfilesBody(), username, password, cancellationToken)
                .ConfigureAwait(false);
            if (profiles.Body is null)
            {
                return Result(
                    information, services, systemTime,
                    $"GetProfiles did not succeed ({profiles.Detail}). No further media request was sent.");
            }

            try
            {
                _profiles = OnvifSoap.ParseProfiles(profiles.Body);
            }
            catch (InvalidDataException)
            {
                return Result(information, services, systemTime, "GetProfiles returned malformed XML.");
            }

            if (_profiles.Count == 0)
            {
                return Result(information, services, systemTime, "The recorder reported no media profiles.");
            }

            BuildRelationships();

            var sources = await PostAsync(
                mediaUri, "GetVideoSources", OnvifSoap.VideoSourcesBody(), username, password, cancellationToken)
                .ConfigureAwait(false);
            if (sources.Body is not null)
            {
                _sources = OnvifSoap.ParseVideoSources(sources.Body);
            }
            else
            {
                _unsupported.Add("GetVideoSources");
            }

            var sourceConfigurations = await PostAsync(
                mediaUri, "GetVideoSourceConfigurations", OnvifSoap.VideoSourceConfigurationsBody(), username, password, cancellationToken)
                .ConfigureAwait(false);
            if (sourceConfigurations.Body is null)
            {
                _unsupported.Add("GetVideoSourceConfigurations");
            }

            var encoders = await PostAsync(
                mediaUri, "GetVideoEncoderConfigurations", OnvifSoap.VideoEncoderConfigurationsBody(), username, password, cancellationToken)
                .ConfigureAwait(false);
            if (encoders.Body is not null)
            {
                _encoders = OnvifSoap.ParseVideoEncoderConfigurations(encoders.Body);
            }
            else
            {
                _unsupported.Add("GetVideoEncoderConfigurations");
            }

            return Result(information, services, systemTime, null);
        }

        /// <summary>Main = heaviest pixel rate, sub = lightest distinct profile; every profile is listed with its role.</summary>
        private void BuildRelationships()
        {
            var (main, sub) = OnvifSoap.SelectStreams(_profiles);

            foreach (var profile in _profiles)
            {
                var role = profile.Token == main?.Token ? "main"
                    : profile.Token == sub?.Token ? "sub"
                    : "other";

                _relationships.Add(new NvrProfileRelationship(
                    profile.Token,
                    profile.Name,
                    role,
                    profile.SourceToken,
                    profile.EncoderToken,
                    profile.Width,
                    profile.Height,
                    profile.FrameRate,
                    profile.Encoding));
            }
        }

        private void ReportOutsideEndpoints(OnvifServices services)
        {
            (string Name, Uri? Uri)[] candidates =
            [
                ("Media", services.Media),
                ("PTZ", services.Ptz),
                ("Replay", services.Replay),
                ("Recording", services.Recording),
                ("Search", services.Search),
            ];

            foreach (var (name, uri) in candidates)
            {
                if (uri is null || policy.AllowsOnvifUri(uri))
                {
                    continue;
                }

                _blocked.Add(
                    $"{name} service advertised {Describe(uri)}, outside the approved {policy.ExpectedOnvifOrigin}; " +
                    "it was not contacted.");
            }
        }

        private NvrMetadataResult Result(
            OnvifDeviceInformation? information,
            OnvifServices services,
            OnvifSystemTime? systemTime,
            string? error)
        {
            return new NvrMetadataResult(
                Success: error is null,
                information,
                services,
                systemTime,
                _profiles,
                _sources,
                _encoders,
                _relationships,
                _requests.ToList(),
                _blocked.ToList(),
                _unsupported.Distinct(StringComparer.Ordinal).ToList(),
                _attempts,
                error,
                TimeSpan.Zero);
        }

        /// <summary>
        /// One authenticated ONVIF call, retried once with HTTP Digest. Every attempt is counted against the budget, and
        /// each outcome is recorded with the masked target only — never the body, the password or an authorization value.
        /// </summary>
        private async Task<PostResult> PostAsync(
            Uri uri,
            string operation,
            string body,
            string? username,
            string? password,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            string? authorization = null;

            for (var attempt = 0; attempt <= MaxDigestRetries; attempt++)
            {
                if (_attempts >= maxHttpAttempts)
                {
                    Record(operation, uri, "BLOCKED",
                        $"The {maxHttpAttempts}-attempt request budget was exhausted; no further request was sent.",
                        stopwatch.Elapsed);
                    return new PostResult(null, "request budget exhausted");
                }

                _attempts++;
                var envelope = OnvifSoap.BuildEnvelope(body, username, password);

                try
                {
                    var response = await transport
                        .PostAsync(uri, envelope, authorization, cancellationToken)
                        .ConfigureAwait(false);

                    if (response.StatusCode is 200 or 202)
                    {
                        Record(operation, uri, "PASS", $"HTTP {response.StatusCode}.", stopwatch.Elapsed);
                        return new PostResult(response.Body, $"HTTP {response.StatusCode}");
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
                        continue;
                    }

                    var state = response.StatusCode == 401 ? "DENIED" : "UNSUPPORTED";
                    var detail = response.StatusCode == 401
                        ? "HTTP 401: the recorder rejected the supplied credentials."
                        : $"HTTP {response.StatusCode}.";
                    Record(operation, uri, state, detail, stopwatch.Elapsed);
                    return new PostResult(null, detail);
                }
                catch (NvrAccessDeniedException exception)
                {
                    Record(operation, uri, "BLOCKED", exception.Reason, stopwatch.Elapsed);
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Record(operation, uri, "TIMEOUT", "The request was stopped at the time limit.", stopwatch.Elapsed);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    Record(operation, uri, "TIMEOUT", "The request exceeded its timeout.", stopwatch.Elapsed);
                    return new PostResult(null, "timeout");
                }
                catch (HttpRequestException exception)
                {
                    Record(operation, uri, "FAIL", exception.Message, stopwatch.Elapsed);
                    return new PostResult(null, exception.Message);
                }
            }

            Record(operation, uri, "DENIED", "Digest authentication did not succeed.", stopwatch.Elapsed);
            return new PostResult(null, "digest authentication did not succeed");
        }

        private void Record(string operation, Uri uri, string state, string detail, TimeSpan elapsed) =>
            _requests.Add(new NvrMetadataRequest(operation, Describe(uri), state, detail, elapsed));

        private static string Describe(Uri uri) =>
            $"{uri.Scheme}://{NvrSafety.MaskHost(uri.Host)}:{uri.Port}{uri.AbsolutePath}";

        private readonly record struct PostResult(string? Body, string Detail);
    }
}
