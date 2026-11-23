using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Camzivo.Core.Streaming.Video;

namespace Camzivo.Desktop.Services;

/// <summary>Writes the measured hardware capability of this machine, so acceleration claims rest on evidence.</summary>
[SupportedOSPlatform("windows")]
public static class HardwareCapabilityReport
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);

        var capability = HardwareCapabilityProbe.Probe();
        var json = JsonSerializer.Serialize(capability, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(directory, "hardware.json");

        File.WriteAllText(path, json);
        Console.WriteLine(json);
        Console.WriteLine($"hardware capability written to {path}");

        return 0;
    }
}
