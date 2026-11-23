using System.Collections.Concurrent;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Net;
using Camzivo.Core.Recording;
using Camzivo.Data.Net;

namespace Camzivo.Data.Recording;

public sealed record ReplayLookup(Uri? ReplayUri, RecordingServices Services, string? Error)
{
    public static ReplayLookup Failed(string error) => new(null, RecordingServices.None, error);
}

/// <summary>What the Playback page needs from a recorder; the seam tests script.</summary>
public interface IRecordingCatalog
{
    Task<RecordingAvailability> SearchAsync(
        Guid cameraId,
        Uri deviceServiceUri,
        string? username,
        string? password,
        DateOnly localDate,
        TimeZoneInfo zone,
        string? sourceToken,
        CancellationToken cancellationToken,
        int rtspPort = 554);

    Task<ReplayLookup> GetReplayAsync(
        Uri deviceServiceUri,
        string? username,
        string? password,
        string recordingToken,
        CancellationToken cancellationToken,
        int rtspPort = 554);
}

/// <summary>
/// Profile G recording search over the same ONVIF transport as onboarding.
///
/// A device is never assumed to support recordings: the service addresses are read first, and a device that advertises
/// only media is reported as unsupported rather than searched and shown empty. Search results are real recorder
/// answers only — an empty list is displayed as "no recordings", never filled in with a guess.
/// </summary>
public sealed class OnvifRecordingCatalog(IOnvifTransport transport) : IRecordingCatalog
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(30);
    private const int MaxResultPages = 8;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, RecordingServices Services)> _services = new(StringComparer.Ordinal);

    public async Task<RecordingAvailability> SearchAsync(
        Guid cameraId,
        Uri deviceServiceUri,
        string? username,
        string? password,
        DateOnly localDate,
        TimeZoneInfo zone,
        string? sourceToken,
        CancellationToken cancellationToken,
        int rtspPort = 554)
    {
        ArgumentNullException.ThrowIfNull(deviceServiceUri);
        ArgumentNullException.ThrowIfNull(zone);

        var policy = NvrAccessPolicy.ForService(deviceServiceUri, rtspPort);
        var onvif = new ReadOnlyNvrTransport(transport, policy);

        var (startUtc, endUtc) = RecordingAvailability.DayBounds(localDate, zone);
        var windowError = RecordingWindow.Validate(startUtc, endUtc);
        if (windowError is not null)
        {
            return new RecordingAvailability(cameraId, localDate, [], zone.Id, DateTimeOffset.UtcNow, Error: windowError);
        }

        var services = await ResolveServicesAsync(onvif, deviceServiceUri, username, password, cancellationToken).ConfigureAwait(false);

        if (!services.HasAny)
        {
            return Unsupported(cameraId, localDate, zone, "This device does not advertise the ONVIF recording services.");
        }

        if (services.Search is null || services.Replay is null)
        {
            return Unsupported(
                cameraId,
                localDate,
                zone,
                services.Search is null
                    ? "This device has no ONVIF search service, so recordings cannot be listed."
                    : "This device has no ONVIF replay service, so recordings cannot be played.");
        }

        if (!policy.AllowsOnvifUri(services.Search) || !policy.AllowsOnvifUri(services.Replay))
        {
            return Unsupported(
                cameraId,
                localDate,
                zone,
                $"This recorder advertised Profile G at an unapproved host or port ({NvrSafety.Redact(services.Search)} / {NvrSafety.Redact(services.Replay)}). Nothing was contacted; approve the endpoint explicitly.");
        }

        var findResponse = await PostAsync(
                onvif,
                services.Search,
                OnvifRecordingSoap.FindRecordingsBody(startUtc, endUtc, sourceToken),
                username,
                password,
                cancellationToken)
            .ConfigureAwait(false);

        if (findResponse is null)
        {
            return Failed(cameraId, localDate, zone, "The recorder did not accept the recording search.");
        }

        string? searchToken;
        try
        {
            (searchToken, _) = OnvifRecordingSoap.ParseSearchStarted(findResponse);
        }
        catch (InvalidDataException exception)
        {
            return Failed(cameraId, localDate, zone, exception.Message);
        }

        if (searchToken is null)
        {
            return Failed(cameraId, localDate, zone, "The recorder returned no search token.");
        }

        var segments = new List<RecordingSegment>();

        try
        {
            for (var page = 0; page < MaxResultPages; page++)
            {
                var resultsXml = await PostAsync(
                        onvif,
                        services.Search,
                        OnvifRecordingSoap.GetRecordingSearchResultsBody(searchToken),
                        username,
                        password,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (resultsXml is null)
                {
                    return Failed(cameraId, localDate, zone, "The recorder stopped answering the search.");
                }

                var result = OnvifRecordingSoap.ParseSearchResults(resultsXml);
                segments.AddRange(result.Segments);

                if (result.Completed)
                {
                    break;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            return Failed(cameraId, localDate, zone, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(cameraId, localDate, zone, "The recording search timed out.");
        }
        finally
        {
            // Releasing the search is polite and bounded; a device that has already forgotten it is not an error.
            _ = EndSearchQuietlyAsync(onvif, services.Search, searchToken, username, password);
        }

        var clipped = OnvifRecordingSoap.Merge(segments)
            .Where(segment => segment.EndUtc > startUtc && segment.StartUtc < endUtc)
            .ToList();

        return new RecordingAvailability(cameraId, localDate, clipped, zone.Id, DateTimeOffset.UtcNow);
    }

    public async Task<ReplayLookup> GetReplayAsync(
        Uri deviceServiceUri,
        string? username,
        string? password,
        string recordingToken,
        CancellationToken cancellationToken,
        int rtspPort = 554)
    {
        ArgumentNullException.ThrowIfNull(deviceServiceUri);

        var policy = NvrAccessPolicy.ForService(deviceServiceUri, rtspPort);
        var onvif = new ReadOnlyNvrTransport(transport, policy);
        var services = await ResolveServicesAsync(onvif, deviceServiceUri, username, password, cancellationToken).ConfigureAwait(false);

        if (services.Replay is null)
        {
            return new ReplayLookup(
                null,
                services,
                services.HasAny
                    ? "This device has no ONVIF replay service."
                    : "This device does not advertise ONVIF recordings.");
        }

        if (!policy.AllowsOnvifUri(services.Replay))
        {
            return new ReplayLookup(
                null,
                services,
                $"This recorder advertised replay at an unapproved host or port ({NvrSafety.Redact(services.Replay)}). Nothing was contacted; approve the endpoint explicitly.");
        }

        var xml = await PostAsync(
                onvif,
                services.Replay,
                OnvifRecordingSoap.GetReplayUriBody(recordingToken),
                username,
                password,
                cancellationToken)
            .ConfigureAwait(false);

        if (xml is null)
        {
            return new ReplayLookup(null, services, "The recorder did not answer the replay request.");
        }

        try
        {
            var uri = OnvifRecordingSoap.ParseReplayUri(xml);
            if (uri is null)
            {
                return new ReplayLookup(null, services, "The recorder returned no replay address.");
            }

            if (!policy.AllowsStreamUri(uri))
            {
                return new ReplayLookup(
                    null,
                    services,
                    $"The recorder returned a replay address outside {policy.ExpectedRtspOrigin} ({NvrSafety.Redact(uri)}). Nothing was opened; approve the endpoint explicitly.");
            }

            return new ReplayLookup(uri, services, null);
        }
        catch (InvalidDataException exception)
        {
            return new ReplayLookup(null, services, exception.Message);
        }
    }

    private async Task<RecordingServices> ResolveServicesAsync(
        IOnvifTransport onvif,
        Uri deviceServiceUri,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var key = $"{deviceServiceUri}|{username}";

        if (_services.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheLifetime)
        {
            return cached.Services;
        }

        var xml = await PostAsync(onvif, deviceServiceUri, OnvifPtzSoap.ServicesBody(), username, password, cancellationToken)
            .ConfigureAwait(false);

        if (xml is null)
        {
            // GetServices can be refused by older devices; GetCapabilities is the fallback, not a retry storm.
            xml = await PostAsync(onvif, deviceServiceUri, OnvifPtzSoap.CapabilitiesBody(), username, password, cancellationToken)
                .ConfigureAwait(false);
        }

        var services = xml is null ? RecordingServices.None : OnvifRecordingSoap.ParseServices(xml, deviceServiceUri);

        _services[key] = (DateTimeOffset.UtcNow, services);
        _ = FailureLifetime;
        return services;
    }

    private async Task EndSearchQuietlyAsync(IOnvifTransport onvif, Uri search, string token, string? username, string? password)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _ = await PostAsync(onvif, search, OnvifRecordingSoap.EndSearchBody(token), username, password, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
            _ = exception;
        }
    }

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

    private static RecordingAvailability Unsupported(Guid cameraId, DateOnly date, TimeZoneInfo zone, string reason) =>
        new(cameraId, date, [], zone.Id, DateTimeOffset.UtcNow, reason);

    private static RecordingAvailability Failed(Guid cameraId, DateOnly date, TimeZoneInfo zone, string error) =>
        new(cameraId, date, [], zone.Id, DateTimeOffset.UtcNow, Error: error);
}
