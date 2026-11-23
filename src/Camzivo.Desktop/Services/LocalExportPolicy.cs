using System.IO;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Evidence stays on this Windows computer. A UNC path or a device path is rejected before any replay session starts,
/// so Camzivo cannot be pointed at a recorder share or a device namespace while exporting.
/// </summary>
public static class LocalExportPolicy
{
    public static string? Validate(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return "Choose a local output file.";
        }

        if (!Path.IsPathFullyQualified(outputPath))
        {
            return "Choose an absolute local output path.";
        }

        var full = Path.GetFullPath(outputPath);

        if (full.StartsWith(@"\\", StringComparison.Ordinal) ||
            full.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return "Evidence exports must stay on the local computer; network and device paths are not allowed.";
        }

        return null;
    }
}
