using System.IO;
using Camzivo.Core.Recording;
using Camzivo.Core.Streaming.Replay;
using Camzivo.Core.Streaming.Rtsp;
using Camzivo.Core.Streaming.Video;

namespace Camzivo.Desktop.Services;

public sealed record ExportRequest(
    string CameraName,
    RtspSessionOptions Transport,
    RecordingSegment Segment,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string OutputPath);

public sealed record ExportProgress(string Stage, double Fraction, DateTimeOffset PositionUtc);

public sealed record ExportResult(bool Success, string? Path, string? Error, long Bytes, int Samples, TimeSpan Duration);

/// <summary>
/// Writes selected recordings to an MP4 evidence file.
///
/// It is a separate session from live and from the playback page: cancelling an export cannot disturb either. Samples
/// go through the real parser and muxer (length-prefixed NALs, avcC, sample table); the file is written to a
/// <c>.part</c> path and only renamed after it is complete and non-empty, so a cancelled or interrupted export never
/// leaves something that looks like evidence.
/// </summary>
public sealed class EvidenceExporter(Func<IVideoDecoder> decoderFactory)
{
    private const int MaxExportMinutes = 240;

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EndUtc <= request.StartUtc)
        {
            return Fail("The export range is empty.");
        }

        if (request.EndUtc - request.StartUtc > TimeSpan.FromMinutes(MaxExportMinutes))
        {
            return Fail($"An export can cover at most {MaxExportMinutes} minutes.");
        }

        var segmentStart = request.StartUtc < request.Segment.StartUtc ? request.Segment.StartUtc : request.StartUtc;
        var segmentEnd = request.EndUtc > request.Segment.EndUtc ? request.Segment.EndUtc : request.EndUtc;

        if (segmentEnd <= segmentStart)
        {
            return Fail("The requested range is outside the recording.");
        }

        if (LocalExportPolicy.Validate(request.OutputPath) is { } pathError)
        {
            return Fail(pathError);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (string.IsNullOrEmpty(directory))
        {
            return Fail("The output folder is not valid.");
        }

        Directory.CreateDirectory(directory);

        var partPath = request.OutputPath + ".part";
        var started = DateTimeOffset.UtcNow;
        var samples = 0;
        Mp4Writer? writer = null;
        ReplaySession? session = null;
        FileStream? file = null;
        var sync = new object();

        try
        {
            file = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024);

            session = new ReplaySession(decoderFactory);
            var parameterSets = Array.Empty<byte[]>();
            var width = 0;
            var height = 0;

            session.AccessUnitReady += (accessUnit, timestamp) =>
            {
                lock (sync)
                {
                    if (writer is null)
                    {
                        parameterSets = session.ParameterSets.ToArray();
                        var sps = parameterSets.FirstOrDefault(nal => (nal[0] & 0x1F) == 7);
                        var pps = parameterSets.FirstOrDefault(nal => (nal[0] & 0x1F) == 8);

                        if (sps is null || pps is null)
                        {
                            return;
                        }

                        width = session.DecodedWidth > 0 ? session.DecodedWidth : 640;
                        height = session.DecodedHeight > 0 ? session.DecodedHeight : 360;
                        writer = new Mp4Writer(file, sps, pps, width, height);
                    }

                    writer.AddSample(accessUnit, timestamp);
                    samples++;
                }
            };

            await session.ConnectAsync(
                request.Transport,
                request.Segment.StartUtc,
                request.Segment.EndUtc,
                segmentStart,
                1.0,
                cancellationToken).ConfigureAwait(false);

            var total = (segmentEnd - segmentStart).TotalSeconds;

            while (!cancellationToken.IsCancellationRequested)
            {
                var position = session.Status.PositionUtc;
                var stage = writer is null ? "Waiting for video" : "Writing samples";
                progress?.Report(new ExportProgress(stage, Math.Clamp((position - segmentStart).TotalSeconds / total, 0, 1), position));

                if (position >= segmentEnd || session.Status.State == ReplayState.Ended)
                {
                    break;
                }

                if (session.Status.State == ReplayState.Failed)
                {
                    return Fail(session.Status.Detail);
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (sync)
            {
                if (writer is null || samples == 0)
                {
                    return Fail("The recorder sent no video for this range.");
                }

                writer.Complete();
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            file.Dispose();
            file = null;

            if (new FileInfo(partPath).Length < 1024)
            {
                return Fail("The exported file is empty.");
            }

            File.Move(partPath, request.OutputPath, overwrite: true);

            var bytes = new FileInfo(request.OutputPath).Length;
            progress?.Report(new ExportProgress("Complete", 1, segmentEnd));

            return new ExportResult(true, request.OutputPath, null, bytes, samples, DateTimeOffset.UtcNow - started);
        }
        catch (OperationCanceledException)
        {
            return Fail("The export was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or RtspException or VideoDecoderException or InvalidOperationException)
        {
            return Fail(exception.Message);
        }
        finally
        {
            if (session is not null)
            {
                await session.StopAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }

            file?.Dispose();

            try
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
            catch (IOException)
            {
                // A locked leftover is reported by the next export attempt, not hidden.
            }

            _ = started;
            _ = samples;
        }
    }

    private static ExportResult Fail(string error) => new(false, null, error, 0, 0, TimeSpan.Zero);
}
