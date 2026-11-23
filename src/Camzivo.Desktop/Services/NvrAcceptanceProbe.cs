using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Recording;
using Camzivo.Core.Streaming;
using Camzivo.Core.Streaming.Replay;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Core.Streaming.Video;
using Camzivo.Data.Devices;
using Camzivo.Data.Net;
using Camzivo.Data.Recording;
using Camzivo.Data.Repositories;
using Camzivo.Data.Sqlite;
using Camzivo.Data.Storage;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Real Dahua/NVR acceptance. This is intentionally a separate, read-only workflow: it never edits the recorder,
/// its schedules, storage or recordings. It writes a BLOCKED report unless the password, explicit authorization and
/// matching exact-host confirmation are all present; simulator results are never presented as hardware validation.
///
/// Usage:
///   Camzivo.Desktop.exe --probe-nvr artifacts/nvr --nvr-host 192.168.1.108 --nvr-user admin
///   --nvr-authorized --nvr-confirm 192.168.1.108
///   CAMZIVO_NVR_PASSWORD=... (environment, never a command-line argument)
/// </summary>
internal static class NvrAcceptanceProbe
{
    public static async Task<int> RunAsync(
        string outputDirectory,
        string? host,
        string? username,
        string? password,
        int onvifPort,
        int rtspPort,
        bool authorized,
        string? confirmedHost)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (LocalExportPolicy.Validate(Path.Combine(outputDirectory, "nvr-acceptance.json")) is { } localError)
        {
            Console.Error.WriteLine($"BLOCKED: {localError}");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);

        var rows = new List<(string Name, string State, string Detail)>();

        void Add(string name, string state, string detail)
        {
            rows.Add((name, state, detail));
            Console.WriteLine($"{state,-10} {name,-28} {detail}");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            Add("hardware", "BLOCKED", "No NVR password was supplied in CAMZIVO_NVR_PASSWORD.");
            Write(outputDirectory, rows, "blocked", "No NVR credentials supplied; no hardware claim is made.");
            return 2;
        }

        var access = NvrHardwareAccessGate.Evaluate(host, onvifPort, rtspPort, authorized, confirmedHost);
        if (!access.Allowed)
        {
            Add("hardware", "BLOCKED", access.Detail);
            Add("network.plan", "BLOCKED",
                $"No request will be sent. Intended exact endpoint: {host ?? "not supplied"}; ONVIF {onvifPort}; RTSP {rtspPort}.");
            Write(outputDirectory, rows, "blocked", "Hardware access was not authorized; no NVR was contacted.");
            return 2;
        }

        Add("authorization", "PASS", access.Detail);
        Add("network.plan", "APPROVED",
            $"host={host!.Trim()} onvif={onvifPort} rtsp={rtspPort}; one camera; bounded short recording interval; operations={string.Join(",", NvrReadOnlyOperations.Approved.OrderBy(name => name))}");

        var endpoint = new DeviceEndpoint(host.Trim(), onvifPort, rtspPort, UseTls: false);
        using var http = new HttpOnvifTransport();
        var discovery = new NvrDiscovery(http, new TcpRtspEndpointProbe());
        NvrDiscoveryResult? found = null;
        Device? channelDevice = null;
        ReplaySession? replay = null;

        try
        {
            found = await discovery.DiscoverAsync(endpoint, username, password).ConfigureAwait(false);
            Add("connect+discover", found.Success ? "PASS" : "FAIL",
                found.Success
                    ? $"{found.Information?.Manufacturer} {found.Information?.Model} fw={found.Information?.FirmwareVersion} serial={found.Information?.SerialNumber} channels={found.Channels.Count}"
                    : found.Failure?.Explanation ?? found.ValidationError ?? "Discovery failed.");

            if (!found.Success)
            {
                Write(outputDirectory, rows, "failed", found.Failure?.Explanation ?? "The NVR could not be discovered.");
                return 1;
            }

            Add("identity", "PASS",
                $"mfr={found.Information?.Manufacturer ?? "?"} model={found.Information?.Model ?? "?"} fw={found.Information?.FirmwareVersion ?? "?"}");
            Add("profile-g-services", found.Services.CanSearchAndReplay ? "PASS" : "UNSUPPORTED",
                found.Services.CanSearchAndReplay
                    ? $"search={found.Services.Search} replay={found.Services.Replay}"
                    : $"services: {found.Services.Summary}");
            Add("timezone", found.SystemTime?.TimeZone is { } ? "PASS" : "UNKNOWN",
                $"utc={found.SystemTime?.Utc:O} local={found.SystemTime?.Local} tz={found.SystemTime?.TimeZone ?? "not returned"}");

            foreach (var line in found.Channels)
            {
                Add($"channel.{line.Channel}", line.MainUri is null ? "UNSUPPORTED" : "PASS",
                    $"{line.Name} source={line.SourceToken ?? "unmapped"} main={Redact(line.MainUri)} sub={(line.SubUri is null ? "none" : "present")}");
            }

            var first = found.Channels.FirstOrDefault(channel => channel.MainUri is not null);
            if (first is null)
            {
                Write(outputDirectory, rows, "failed", "No channel returned a live stream address.");
                return 1;
            }

            var profileDirectory = Path.Combine(outputDirectory, "profile");
            Directory.CreateDirectory(profileDirectory);
            var databasePath = Path.Combine(profileDirectory, "camzivo.db");
            MigrationRunner.Migrate(databasePath);
            var factory = new SqliteConnectionFactory(databasePath);
            var import = await new NvrOnboarding(
                new DeviceRepository(factory),
                new CameraRepository(factory),
                new DpapiSecretStore(Path.Combine(profileDirectory, "secrets")))
                .ImportAsync(
                    new NvrRegistrationRequest(
                        $"NVR {host.Trim()}", host.Trim(), username, password, false, onvifPort, rtspPort),
                    found,
                    found.Channels.Select(channel => channel.Channel).ToArray(),
                    DateTimeOffset.UtcNow).ConfigureAwait(false);

            Add("channels.import", import.Success ? "PASS" : "FAIL",
                import.Success
                    ? $"imported={import.Imported} updated={import.Updated} skipped={import.Skipped.Count} device={import.Device?.Id}"
                    : import.Error ?? "Channel import failed.");

            if (import.Device is null)
            {
                Write(outputDirectory, rows, "failed", "Channels could not be imported into the temporary profile.");
                return 1;
            }

            // A genuinely new factory/repository pair is the restart check: nothing is reused from the import instance.
            var reopenedFactory = new SqliteConnectionFactory(databasePath);
            var restored = await new DeviceRepository(reopenedFactory).GetAsync(import.Device.Id).ConfigureAwait(false);
            var restoredChannels = await new CameraRepository(reopenedFactory)
                .GetForDeviceAsync(import.Device.Id).ConfigureAwait(false);
            var secretRefs = new DpapiSecretStore(Path.Combine(profileDirectory, "secrets"));
            var passwordRestored = restored?.SecretRef is { } secretRef
                ? secretRefs.Load(secretRef) is not null
                : false;

            Add("restart.persistence", restored is not null && restoredChannels.Count > 0 ? "PASS" : "FAIL",
                $"device={restored?.DisplayName ?? "missing"} channels={restoredChannels.Count} passwordRestored={passwordRestored} firmware={restored?.Firmware ?? "?"} source={(restoredChannels.FirstOrDefault()?.SourceToken ?? "unmapped")}");

            channelDevice = restored ?? new Device(
                Guid.NewGuid(), null, host.Trim(), host.Trim(), onvifPort, rtspPort, onvifPort, false,
                username, null, found.Information?.Manufacturer, found.Information?.Model, true,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                first.MainUri, first.SubUri, DeviceKind.Camera,
                found.Information?.FirmwareVersion, found.Information?.SerialNumber,
                found.SystemTime?.TimeZone, null, DateTimeOffset.UtcNow, first.SourceToken);

            await using (var live = new H264StreamSession())
            {
                live.Start(new RtspSessionOptions(new Uri(first.MainUri!), username, password));
                var gotFrame = await live.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Add("live.decode", gotFrame ? "PASS" : "FAIL",
                    gotFrame
                        ? $"{live.Telemetry.Width}×{live.Telemetry.Height} via {live.Telemetry.DecoderName}"
                        : live.Status.Detail);
            }

            var deviceUri = OnvifSoap.DeviceServiceUri(host.Trim(), onvifPort, useTls: false);
            var catalog = new OnvifRecordingCatalog(http);
            var yesterday = DateOnly.FromDateTime(DateTime.Now.AddDays(-1));
            var yesterdayAvailability = await catalog.SearchAsync(
                channelDevice.Id, deviceUri, username, password, yesterday, TimeZoneInfo.Local,
                first.SourceToken, CancellationToken.None, rtspPort).ConfigureAwait(false);

            Add("recording-search.yesterday",
                yesterdayAvailability.IsSupported && !yesterdayAvailability.Failed ? "PASS" : "UNSUPPORTED",
                yesterdayAvailability.UnsupportedReason ?? yesterdayAvailability.Error ??
                $"{yesterdayAvailability.Segments.Count} segment(s) on {yesterday:yyyy-MM-dd}; source={first.SourceToken ?? "unmapped"}");

            // A recorder can legitimately have no footage yesterday. Today is a second factual search, not a
            // substitute for yesterday; both results are reported.
            var today = DateOnly.FromDateTime(DateTime.Now);
            var availability = yesterdayAvailability.Segments.Count > 0
                ? yesterdayAvailability
                : await catalog.SearchAsync(
                    channelDevice.Id, deviceUri, username, password, today, TimeZoneInfo.Local,
                    first.SourceToken, CancellationToken.None, rtspPort).ConfigureAwait(false);

            if (!ReferenceEquals(availability, yesterdayAvailability))
            {
                Add("recording-search.today", availability.IsSupported && !availability.Failed ? "PASS" : "UNSUPPORTED",
                    availability.UnsupportedReason ?? availability.Error ??
                    $"{availability.Segments.Count} segment(s) on {today:yyyy-MM-dd}; source={first.SourceToken ?? "unmapped"}");
            }

            var segment = availability.Segments.FirstOrDefault();
            if (segment is null)
            {
                Write(outputDirectory, rows, "partial", "Connection/live passed; no usable recording was returned.");
                return 1;
            }

            var lookup = await catalog.GetReplayAsync(deviceUri, username, password, segment.RecordingToken, CancellationToken.None, rtspPort)
                .ConfigureAwait(false);
            Add("replay-lookup", lookup.ReplayUri is not null ? "PASS" : "FAIL", lookup.Error ?? Redact(lookup.ReplayUri?.ToString()));

            if (lookup.ReplayUri is not null)
            {
                replay = new ReplaySession(() => new MediaFoundationH264Decoder());
                var transport = new RtspSessionOptions(lookup.ReplayUri, username, password, Timeout: TimeSpan.FromSeconds(20));
                await replay.ConnectAsync(transport, segment.StartUtc, segment.EndUtc, segment.StartUtc, 1, CancellationToken.None)
                    .ConfigureAwait(false);

                await WaitAsync(
                    () => replay.Status.State is ReplayState.Playing or ReplayState.Ended && replay.FramesPresented > 0,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Add("playback.1x", replay.FramesPresented > 0 ? "PASS" : "FAIL",
                    $"state={replay.Status.State} position={replay.Status.PositionUtc:O} decoded={replay.FramesDecoded} presented={replay.FramesPresented}");

                foreach (var speed in new[] { 2d, 4d, 6d, 8d, 16d })
                {
                    var change = await replay.ChangeRateAsync(transport, speed, CancellationToken.None).ConfigureAwait(false);
                    if (!change.Accepted)
                    {
                        Add($"playback.{speed:0}x", "UNSUPPORTED", change.Detail);
                        continue;
                    }

                    var before = replay.Status.PositionUtc;
                    var clock = Stopwatch.GetTimestamp();
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    var elapsed = Stopwatch.GetElapsedTime(clock);
                    var measured = (replay.Status.PositionUtc - before).TotalSeconds / elapsed.TotalSeconds;
                    Add($"playback.{speed:0}x", PlaybackRates.IsDelivered(speed, measured) ? "PASS" : "FAIL",
                        $"requested={speed:0}× measured={measured:F2}× decoded={replay.FramesDecoded} presented={replay.FramesPresented}");
                }

                var exporter = new EvidenceExporter(() => new MediaFoundationH264Decoder());
                var exportStart = segment.StartUtc + TimeSpan.FromMinutes(1);
                var exportEnd = exportStart + TimeSpan.FromSeconds(10) < segment.EndUtc
                    ? exportStart + TimeSpan.FromSeconds(10)
                    : segment.EndUtc;
                var exportPath = Path.Combine(outputDirectory, "real-nvr-evidence.mp4");
                var export = await exporter.ExportAsync(
                    new ExportRequest(channelDevice.DisplayName, transport, segment, exportStart, exportEnd, exportPath),
                    null,
                    CancellationToken.None).ConfigureAwait(false);

                Add("export.mp4", export.Success && File.Exists(exportPath) ? "PASS" : "FAIL",
                    export.Success
                        ? $"{export.Samples} samples · {export.Bytes} bytes · {export.Path}"
                        : export.Error ?? "Export failed.");
            }

            var failed = rows.Count(row => row.State == "FAIL");
            Write(outputDirectory, rows, failed == 0 ? "passed" : "failed",
                failed == 0 ? "Real NVR workflow completed." : $"{failed} acceptance step(s) failed.");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or OperationCanceledException
                                              or NvrAccessDeniedException
                                              or InvalidOperationException
                                              or IOException
                                              or UriFormatException)
        {
            Add("probe.error", "FAIL", $"{exception.GetType().Name}: {exception.Message}");
            Write(outputDirectory, rows, "failed", exception.Message);
            return 1;
        }
        finally
        {
            if (replay is not null)
            {
                await replay.StopAsync().ConfigureAwait(false);
                await replay.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static string Redact(string? uri) =>
        string.IsNullOrWhiteSpace(uri) ? "none" : Camzivo.Core.Streaming.RtspUriBuilder.Redact(new Uri(uri));

    private static void Write(string directory, IReadOnlyList<(string Name, string State, string Detail)> rows, string outcome, string summary)
    {
        var report = new
        {
            generatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            outcome,
            summary,
            hardware = rows.FirstOrDefault(row => row.Name == "hardware").Detail,
            steps = rows.Select(row => new { step = row.Name, state = row.State, detail = row.Detail }),
        };

        File.WriteAllText(
            Path.Combine(directory, "nvr-acceptance.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(
            Path.Combine(directory, "summary.txt"),
            rows.Select(row => $"{row.State,-10} {row.Name,-28} {row.Detail}"));
    }
}
