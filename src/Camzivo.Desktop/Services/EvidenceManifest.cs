using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Camzivo.Desktop.Services;

/// <summary>What was exported, from where and for which recorded range. Written next to the evidence file.</summary>
public sealed record EvidenceManifest(
    string FilePath,
    string Sha256,
    long Bytes,
    int Samples,
    string CameraName,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    DateTimeOffset CreatedUtc,
    string TimeZone);

/// <summary>
/// Writes a SHA-256 sidecar and a JSON manifest next to an export.
///
/// An MP4 that cannot be hashed later is not evidence. The sidecar records the digest, the recorded range and the
/// camera, so a file can be shown to be the exact bytes that were produced — and the export record in SQLite points at
/// the same facts instead of at a file name alone.
/// </summary>
public static class EvidenceManifestWriter
{
    public static async Task<EvidenceManifest> WriteAsync(
        string filePath,
        string cameraName,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        int samples,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The evidence file does not exist.", filePath);
        }

        string hash;

        await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
        {
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            hash = Convert.ToHexString(digest);
        }

        var manifest = new EvidenceManifest(
            info.FullName,
            hash,
            info.Length,
            samples,
            cameraName,
            rangeStartUtc,
            rangeEndUtc,
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Local.Id);

        // "<hash>  <file>" is the format sha256sum -c understands.
        await File.WriteAllTextAsync(
            info.FullName + ".sha256",
            $"{hash}  {info.Name}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(
            new
            {
                manifest.FilePath,
                manifest.Sha256,
                manifest.Bytes,
                manifest.Samples,
                manifest.CameraName,
                RangeStartUtc = manifest.RangeStartUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                RangeEndUtc = manifest.RangeEndUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                manifest.CreatedUtc,
                manifest.TimeZone,
                durationSeconds = duration?.TotalSeconds,
            },
            new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(info.FullName + ".json", json, cancellationToken).ConfigureAwait(false);

        return manifest;
    }

    /// <summary>Recomputes the digest and compares it with the sidecar. Used to verify an export later.</summary>
    public static async Task<bool> VerifyAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var sidecar = filePath + ".sha256";
        if (!File.Exists(filePath) || !File.Exists(sidecar))
        {
            return false;
        }

        var expected = (await File.ReadAllTextAsync(sidecar, cancellationToken).ConfigureAwait(false))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return string.Equals(Convert.ToHexString(digest), expected, StringComparison.OrdinalIgnoreCase);
    }
}
