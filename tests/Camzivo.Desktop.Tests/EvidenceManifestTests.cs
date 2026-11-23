using System.IO;
using System.Text;
using Camzivo.Desktop.Services;
using Xunit;

namespace Camzivo.Desktop.Tests;

public sealed class EvidenceManifestTests
{
    [Fact]
    public async Task SidecarRecordsTheDigestInSha256SumFormatAndVerifies()
    {
        var directory = Directory.CreateTempSubdirectory("camzivo-evidence-");
        var file = Path.Combine(directory.FullName, "evidence.mp4");

        try
        {
            await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("not really an mp4, but exact bytes"));

            var manifest = await EvidenceManifestWriter.WriteAsync(
                file,
                "Entrance",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow,
                samples: 120);

            Assert.Equal(64, manifest.Sha256.Length);
            Assert.Equal(new FileInfo(file).Length, manifest.Bytes);

            var sidecar = await File.ReadAllTextAsync(file + ".sha256");
            Assert.StartsWith(manifest.Sha256 + "  evidence.mp4", sidecar);

            var json = await File.ReadAllTextAsync(file + ".json");
            Assert.Contains("\"CameraName\": \"Entrance\"", json);
            Assert.Contains("\"Samples\": 120", json);

            Assert.True(await EvidenceManifestWriter.VerifyAsync(file));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TamperingIsDetected()
    {
        var directory = Directory.CreateTempSubdirectory("camzivo-evidence-");
        var file = Path.Combine(directory.FullName, "evidence.mp4");

        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
            await EvidenceManifestWriter.WriteAsync(file, "Entrance", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);

            await File.WriteAllBytesAsync(file, [1, 2, 3, 5]);

            Assert.False(await EvidenceManifestWriter.VerifyAsync(file));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MissingFileOrSidecarFailsVerificationInsteadOfThrowing()
    {
        var directory = Directory.CreateTempSubdirectory("camzivo-evidence-");
        var file = Path.Combine(directory.FullName, "missing.mp4");

        try
        {
            Assert.False(await EvidenceManifestWriter.VerifyAsync(file));

            await File.WriteAllBytesAsync(file, [1]);
            Assert.False(await EvidenceManifestWriter.VerifyAsync(file));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WritingAManifestForAMissingFileThrows()
    {
        var directory = Directory.CreateTempSubdirectory("camzivo-evidence-");

        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => EvidenceManifestWriter.WriteAsync(
                Path.Combine(directory.FullName, "nope.mp4"),
                "Entrance",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                0));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
