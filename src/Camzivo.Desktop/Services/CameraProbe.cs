using System.Diagnostics;
using System.Runtime.Versioning;
using Camzivo.Core.Devices;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Streaming;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Data.Devices;
using Camzivo.Data.Net;

namespace Camzivo.Desktop.Services;

/// <summary>The four onboarding stages the operator sees, plus what was discovered.</summary>
public sealed record CameraProbeResult(
    bool Success,
    IReadOnlyList<StageResult> Stages,
    Diagnostic? Failure,
    string? ValidationError,
    ConnectionTestResult? Test);

/// <summary>
/// The Add Camera workflow: validate → reach/auth/stream (ONVIF or the supplied RTSP URL) → decode one real frame.
///
/// The last stage is deliberately the same decoder the wall uses. A reachable IP address, a successful SOAP login
/// and even a valid SDP are all *claims*; only a decoded frame proves the stream can be watched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CameraProbe(IConnectionTester tester)
{
    public async Task<CameraProbeResult> RunAsync(
        CameraRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = DeviceOnboarding.Validate(request);
        if (validation is not null)
        {
            return new CameraProbeResult(false, [], null, validation, null);
        }

        var address = request.Address.Trim();
        var isRtsp = address.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);

        if (isRtsp)
        {
            return await ProbeRtspAsync(request, new Uri(address), cancellationToken).ConfigureAwait(false);
        }

        var endpoint = new DeviceEndpoint(
            address,
            request.OnvifPort ?? 80,
            request.RtspPort ?? 554,
            request.UseTls);

        var test = await tester.TestAsync(endpoint, request.Username, request.Password, cancellationToken)
            .ConfigureAwait(false);

        if (!test.Success || test.MainStream is null)
        {
            return new CameraProbeResult(false, test.Stages, test.Failure, null, test);
        }

        var stages = test.Stages.ToList();
        var frameStage = await ProveFrameAsync(test.MainStream, request.Username, request.Password, cancellationToken)
            .ConfigureAwait(false);

        stages.Add(frameStage);

        return new CameraProbeResult(
            frameStage.Success,
            stages,
            frameStage.Success ? null : ConnectionDiagnostics.Explain(frameStage.Failure, frameStage.Detail),
            null,
            test);
    }

    private async Task<CameraProbeResult> ProbeRtspAsync(
        CameraRegistrationRequest request,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var stages = new List<StageResult>();

        var reach = await new TcpRtspEndpointProbe().ProbeAsync(uri, cancellationToken).ConfigureAwait(false);
        stages.Add(new StageResult(
            ConnectionStage.Reach,
            reach is null,
            reach ?? StreamFailure.None,
            reach is null ? $"TCP {uri.Host}:{uri.Port} accepted a connection" : $"TCP {uri.Host}:{uri.Port} did not answer",
            TimeSpan.Zero));

        if (reach is not null)
        {
            return new CameraProbeResult(false, stages, ConnectionDiagnostics.Explain(reach.Value), null, null);
        }

        // RTSP authenticates as part of the exchange, so the sign-in stage is proven by the stream stage succeeding.
        stages.Add(new StageResult(
            ConnectionStage.Authenticate,
            true,
            StreamFailure.None,
            string.IsNullOrWhiteSpace(request.Username) ? "No credentials supplied" : "Credentials sent with the RTSP request",
            TimeSpan.Zero));

        stages.Add(new StageResult(ConnectionStage.Profiles, true, StreamFailure.None, "Stream address supplied directly", TimeSpan.Zero));

        var frameStage = await ProveFrameAsync(uri, request.Username, request.Password, cancellationToken).ConfigureAwait(false);
        stages.Add(frameStage);

        var test = new ConnectionTestResult(
            frameStage.Success,
            stages,
            null,
            [],
            uri,
            null,
            frameStage.Success ? null : ConnectionDiagnostics.Explain(frameStage.Failure, frameStage.Detail));

        return new CameraProbeResult(
            frameStage.Success,
            stages,
            frameStage.Success ? null : test.Failure,
            null,
            test);
    }

    private static async Task<StageResult> ProveFrameAsync(
        Uri uri,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var session = new H264StreamSession();
        session.Start(new RtspSessionOptions(uri, username, password));

        var gotFrame = await session.WaitForFirstFrameAsync(TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        stopwatch.Stop();

        if (!gotFrame)
        {
            var failure = session.Status.Failure == StreamFailure.None ? StreamFailure.NoVideo : session.Status.Failure;
            return new StageResult(ConnectionStage.Stream, false, failure, session.Status.Detail, stopwatch.Elapsed);
        }

        var telemetry = session.Telemetry;
        return new StageResult(
            ConnectionStage.Stream,
            true,
            StreamFailure.None,
            $"Decoded {telemetry.Width}×{telemetry.Height} with {telemetry.DecoderName}",
            stopwatch.Elapsed);
    }
}
