using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Data.Devices;
using Camzivo.Data.Net;

namespace Camzivo.Desktop.Services;

/// <summary>
/// The operator-facing metadata-only verification workflow (<c>--probe-nvr-metadata</c>).
///
/// It is deliberately smaller than <see cref="NvrAcceptanceProbe"/>: no stream is opened, no recording is searched and
/// no PTZ or configuration write is possible, because the only transport it can reach is scoped to
/// <see cref="NvrMetadataOperations"/>. It stops as soon as the metadata is collected and writes a local report.
///
/// Usage:
///   Camzivo.Desktop.exe --probe-nvr-metadata artifacts/nvr-metadata --nvr-host 192.168.1.108 --nvr-user admin
///   --nvr-metadata-authorized --nvr-confirm 192.168.1.108
///   CAMZIVO_NVR_PASSWORD=... (environment, never a command-line argument)
/// </summary>
internal static class NvrMetadataProbeCommand
{
    public static async Task<int> RunAsync(
        string outputDirectory,
        string? host,
        string? username,
        string? password,
        int onvifPort,
        bool useTls,
        bool metadataAuthorized,
        string? confirmedHost)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);

        if (LocalExportPolicy.Validate(Path.Combine(outputDirectory, "nvr-metadata.json")) is { } localError)
        {
            Console.Error.WriteLine($"BLOCKED: {localError}");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);

        var rows = new List<(string Name, string State, string Detail)>();

        void Add(string name, string state, string detail)
        {
            rows.Add((name, state, detail));
            Console.WriteLine($"{state,-12} {name,-30} {detail}");
        }

        var scheme = useTls ? "https" : "http";

        if (string.IsNullOrWhiteSpace(password))
        {
            Add("credentials", "BLOCKED", "No NVR password was supplied in CAMZIVO_NVR_PASSWORD.");
            Add("network.plan", "BLOCKED",
                $"No request will be sent. Intended endpoint: {host ?? "not supplied"}; {scheme} port {onvifPort}; RTSP not used.");
            WriteReport(outputDirectory, host, scheme, onvifPort, rows, null, "blocked",
                "No NVR credentials supplied; no hardware claim is made.");
            return 2;
        }

        var access = NvrMetadataAccessGate.Evaluate(host, onvifPort, useTls, metadataAuthorized, confirmedHost);
        if (!access.Allowed)
        {
            Add("authorization", "BLOCKED", access.Detail);
            Add("network.plan", "BLOCKED",
                $"No request will be sent. Intended endpoint: {host ?? "not supplied"}; {scheme} port {onvifPort}; RTSP not used.");
            WriteReport(outputDirectory, host, scheme, onvifPort, rows, null, "blocked",
                "Metadata-only hardware access was not authorized; no NVR was contacted.");
            return 2;
        }

        Add("authorization", "PASS", access.Detail);
        Add("network.plan", "APPROVED",
            $"host={host!.Trim()} scheme={scheme} port={onvifPort}; sequential ONVIF requests only; no RTSP connection");
        Add("allowlist", "ENFORCED",
            $"approved={string.Join(",", NvrMetadataOperations.Approved.OrderBy(name => name, StringComparer.Ordinal))}");
        Add("never-sent", "ENFORCED",
            $"excluded={string.Join(",", NvrMetadataOperations.Excluded)}");
        Add("limits", "ENFORCED",
            $"maxHttpAttempts={NvrMetadataProbe.DefaultMaxHttpAttempts} overallTimeout={NvrMetadataProbe.DefaultOverallTimeout.TotalSeconds:F0}s " +
            "digestRetriesPerRequest=1 redirects=disabled");

        NvrMetadataResult? result = null;
        try
        {
            using var http = new HttpOnvifTransport();
            result = await new NvrMetadataProbe(http)
                .RunAsync(host.Trim(), onvifPort, useTls, username, password)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or OperationCanceledException
                                              or NvrAccessDeniedException
                                              or InvalidOperationException
                                              or IOException
                                              or UriFormatException)
        {
            Add("probe.error", "FAIL", $"{exception.GetType().Name}: {exception.Message}");
        }

        if (result is null)
        {
            WriteReport(outputDirectory, host, scheme, onvifPort, rows, null, "failed",
                "The metadata session did not complete; see the report.");
            return 1;
        }

        Add("identity", result.Information is null ? "FAIL" : "PASS",
            result.Information is null
                ? "No device information was returned."
                : $"manufacturer={result.Information.Manufacturer ?? "?"} model={result.Information.Model ?? "?"} " +
                  $"firmware={result.Information.FirmwareVersion ?? "?"} serial=omitted");
        Add("services", result.Services.Names.Count > 0 ? "PASS" : "UNSUPPORTED",
            result.Services.Names.Count > 0 ? string.Join(",", result.Services.Names) : "No service namespaces were advertised.");
        Add("system-time", result.SystemTime?.Utc is not null ? "PASS" : "UNSUPPORTED",
            result.SystemTime is null
                ? "No system time was returned."
                : $"utc={result.SystemTime.Utc:O} local={result.SystemTime.Local ?? "?"} tz={result.SystemTime.TimeZone ?? "not returned"}");
        Add("profiles", result.Profiles.Count > 0 ? "PASS" : "UNSUPPORTED", $"{result.Profiles.Count} profile(s)");
        Add("video-sources", result.UnsupportedOperations.Contains("GetVideoSources") ? "UNSUPPORTED" : "PASS",
            result.UnsupportedOperations.Contains("GetVideoSources")
                ? "GetVideoSources was not accepted by the recorder."
                : $"{result.Sources.Count} video source(s)");
        Add("source-configurations",
            result.UnsupportedOperations.Contains("GetVideoSourceConfigurations") ? "UNSUPPORTED" : "PASS",
            result.UnsupportedOperations.Contains("GetVideoSourceConfigurations")
                ? "GetVideoSourceConfigurations was not accepted by the recorder."
                : "GetVideoSourceConfigurations answered");
        Add("encoder-configurations",
            result.UnsupportedOperations.Contains("GetVideoEncoderConfigurations") ? "UNSUPPORTED" : "PASS",
            result.UnsupportedOperations.Contains("GetVideoEncoderConfigurations")
                ? "GetVideoEncoderConfigurations was not accepted by the recorder."
                : $"{result.Encoders.Count} encoder configuration(s)");

        foreach (var relationship in result.Relationships)
        {
            Add($"profile.{relationship.Role}",
                relationship.Width > 0 && relationship.Height > 0 ? "PASS" : "UNSUPPORTED",
                $"{relationship.ProfileName ?? "unnamed"} {relationship.Width}x{relationship.Height} " +
                $"{relationship.Encoding ?? "?"} @{relationship.FrameRate?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?"}fps " +
                $"source={relationship.SourceToken ?? "unmapped"} encoder={relationship.EncoderToken ?? "unmapped"}");
        }

        foreach (var blocked in result.BlockedEndpoints)
        {
            Add("blocked.endpoint", "BLOCKED", blocked);
        }

        Add("requests", result.Success ? "PASS" : "FAIL",
            $"{result.HttpAttempts} HTTP attempt(s) in {result.Elapsed.TotalSeconds:F1}s; " +
            $"{result.Requests.Count(request => request.State == "PASS")} answered");

        if (result.Error is { Length: > 0 } error)
        {
            Add("result", "FAIL", error);
        }

        var outcome = result.Success ? "passed" : "failed";
        WriteReport(outputDirectory, host, scheme, onvifPort, rows, result, outcome,
            result.Success
                ? "Metadata-only verification completed. No stream, recording, PTZ or configuration request was sent."
                : result.Error ?? "Metadata-only verification failed.");

        return result.Success ? 0 : 1;
    }

    /// <summary>Writes the local report, a host-masked shareable copy and a plain summary.</summary>
    private static void WriteReport(
        string directory,
        string? host,
        string scheme,
        int onvifPort,
        IReadOnlyList<(string Name, string State, string Detail)> rows,
        NvrMetadataResult? result,
        string outcome,
        string summary)
    {
        var local = BuildDocument(rows, result, host, scheme, onvifPort, outcome, summary, shareable: false);
        var shareable = BuildDocument(rows, result, host, scheme, onvifPort, outcome, summary, shareable: true);
        var options = new JsonSerializerOptions { WriteIndented = true };

        File.WriteAllText(Path.Combine(directory, "nvr-metadata.json"), JsonSerializer.Serialize(local, options));
        File.WriteAllText(Path.Combine(directory, "nvr-metadata-shareable.json"), JsonSerializer.Serialize(shareable, options));
        File.WriteAllLines(
            Path.Combine(directory, "summary.txt"),
            rows.Select(row => $"{row.State,-12} {row.Name,-30} {row.Detail}")
                .Append(string.Empty)
                .Append($"outcome: {outcome}")
                .Append(summary));

        Console.WriteLine();
        Console.WriteLine($"outcome: {outcome}");
        Console.WriteLine(summary);
        Console.WriteLine($"report: {Path.Combine(directory, "nvr-metadata.json")}");
        Console.WriteLine($"shareable report: {Path.Combine(directory, "nvr-metadata-shareable.json")}");
    }

    /// <summary>
    /// The report intentionally has no serial number, password, authorization header or credential URL. The shareable
    /// copy also masks the host and replaces device tokens with positional references.
    /// </summary>
    private static object BuildDocument(
        IReadOnlyList<(string Name, string State, string Detail)> rows,
        NvrMetadataResult? result,
        string? host,
        string scheme,
        int onvifPort,
        string outcome,
        string summary,
        bool shareable)
    {
        var displayHost = NvrSafety.MaskHost(host);

        string Endpoint(Uri? uri) => uri is null
            ? "none"
            : $"{uri.Scheme}://{(shareable ? NvrSafety.MaskHost(uri.Host) : uri.Host)}:{uri.Port}{uri.AbsolutePath}";

        var relationships = result?.Relationships.Select((relationship, index) => new
        {
            index = index + 1,
            profile = shareable ? $"profile-{index + 1}" : relationship.ProfileToken,
            name = relationship.ProfileName,
            role = relationship.Role,
            source = shareable
                ? relationship.SourceToken is null ? "none" : "linked"
                : relationship.SourceToken ?? "none",
            encoder = shareable
                ? relationship.EncoderToken is null ? "none" : "linked"
                : relationship.EncoderToken ?? "none",
            resolution = relationship.Width > 0 && relationship.Height > 0
                ? $"{relationship.Width}x{relationship.Height}"
                : "unknown",
            frameRate = relationship.FrameRate,
            encoding = relationship.Encoding,
        }).ToList();

        return new
        {
            generatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            mode = "metadata-only",
            outcome,
            summary,
            authorization = shareable ? "permitted for this run only" : "explicit --nvr-metadata-authorized with matching --nvr-confirm",
            network = new
            {
                host = shareable ? displayHost : host?.Trim() ?? "not supplied",
                scheme,
                onvifPort,
                rtsp = "not used",
            },
            boundary = new
            {
                approvedOperations = NvrMetadataOperations.Approved.OrderBy(name => name, StringComparer.Ordinal),
                neverSent = NvrMetadataOperations.Excluded,
                maxHttpAttempts = NvrMetadataProbe.DefaultMaxHttpAttempts,
                overallTimeoutSeconds = NvrMetadataProbe.DefaultOverallTimeout.TotalSeconds,
                digestRetriesPerRequest = 1,
                redirects = "disabled",
                rtspOpened = false,
                recordingOperations = false,
                configurationWrites = false,
                stoppedAfterMetadata = true,
            },
            identity = result?.Information is { } information
                ? new
                {
                    manufacturer = information.Manufacturer,
                    model = information.Model,
                    firmwareVersion = information.FirmwareVersion,
                    serialNumber = "omitted",
                }
                : null,
            services = result is null
                ? null
                : new
                {
                    advertised = result.Services.Names,
                    media = Endpoint(result.Services.Media),
                    ptz = Endpoint(result.Services.Ptz),
                    replay = Endpoint(result.Services.Replay),
                    recording = Endpoint(result.Services.Recording),
                    search = Endpoint(result.Services.Search),
                    summary = result.Services.Summary,
                },
            systemTime = result?.SystemTime is { } time
                ? new { utc = time.Utc?.ToString("O", CultureInfo.InvariantCulture), local = time.Local, timeZone = time.TimeZone }
                : null,
            counts = new
            {
                profiles = result?.Profiles.Count ?? 0,
                videoSources = result?.Sources.Count ?? 0,
                encoderConfigurations = result?.Encoders.Count ?? 0,
                httpAttempts = result?.HttpAttempts ?? 0,
                elapsedMs = result is null ? 0 : (long)result.Elapsed.TotalMilliseconds,
            },
            profiles = result?.Profiles.Select(profile => new
            {
                token = shareable ? "omitted" : profile.Token,
                name = profile.Name,
                encoding = profile.Encoding,
                resolution = profile.ResolutionText,
                frameRate = profile.FrameRate,
                sourceToken = shareable ? (profile.SourceToken is null ? "none" : "linked") : profile.SourceToken,
                encoderToken = shareable ? (profile.EncoderToken is null ? "none" : "linked") : profile.EncoderToken,
                hasAudio = profile.HasAudio,
            }).ToList(),
            videoSources = result?.Sources.Select(source => new
            {
                token = shareable ? "omitted" : source.Token,
                name = source.Name,
            }).ToList(),
            encoderConfigurations = result?.Encoders.Select(encoder => new
            {
                token = shareable ? "omitted" : encoder.Token,
                name = encoder.Name,
                encoding = encoder.Encoding,
                resolution = encoder.Width > 0 && encoder.Height > 0 ? $"{encoder.Width}x{encoder.Height}" : "unknown",
                frameRate = encoder.FrameRate,
            }).ToList(),
            relationships,
            blockedEndpoints = result?.BlockedEndpoints,
            unsupportedOperations = result?.UnsupportedOperations,
            requests = result?.Requests.Select(request => new
            {
                operation = request.Operation,
                target = request.Target,
                state = request.State,
                detail = request.Detail,
                elapsedMs = (long)request.Elapsed.TotalMilliseconds,
            }).ToList(),
            error = result?.Error,
            steps = rows.Select(row => new { step = row.Name, state = row.State, detail = row.Detail }),
        };
    }
}
