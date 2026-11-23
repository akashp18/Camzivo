using Camzivo.Data.Storage;
using Xunit;

namespace Camzivo.Data.Tests;

/// <summary>
/// The log is the only thing that survives a crash, so these tests check the properties support relies on:
/// the line is written, secrets are redacted, the tail is newest-first, and the crash marker is consumed once.
/// </summary>
public class AppLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "camzivo-log-" + Guid.NewGuid().ToString("N"));

    public AppLogTests() => AppLog.Initialize(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void WritesTimestampedLinesToTheCurrentFile()
    {
        AppLog.Info("shell started");

        var path = AppLog.CurrentPath;
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var content = File.ReadAllText(path!);
        Assert.Contains("INFO", content);
        Assert.Contains("shell started", content);
    }

    [Fact]
    public void ExceptionsAreRecordedWithTypeAndMessage()
    {
        AppLog.Error("camera failed", new InvalidOperationException("socket closed"));

        var content = File.ReadAllText(AppLog.CurrentPath!);
        Assert.Contains("ERROR", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("socket closed", content);
    }

    [Fact]
    public void TheInjectedRedactorMasksCredentials()
    {
        AppLog.Initialize(_root, text => text.Replace("hunter2", "***", StringComparison.Ordinal));
        AppLog.Warn("login rtsp://admin:hunter2@10.0.0.5/stream failed");

        var content = File.ReadAllText(AppLog.CurrentPath!);
        Assert.DoesNotContain("hunter2", content);
        Assert.Contains("***", content);
    }

    [Fact]
    public void TailReturnsNewestFirst()
    {
        AppLog.Info("first");
        AppLog.Info("second");

        var tail = AppLog.Tail(10);

        Assert.Contains(tail, line => line.Contains("second", StringComparison.Ordinal));
        Assert.True(
            tail.ToList().FindIndex(line => line.Contains("second", StringComparison.Ordinal)) <
            tail.ToList().FindIndex(line => line.Contains("first", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheCrashMarkerIsReportedExactlyOnce()
    {
        AppLog.WriteCrashMarker("fatal: AccessViolationException");

        var first = AppLog.ReadAndClearCrashMarker();
        var second = AppLog.ReadAndClearCrashMarker();

        Assert.NotNull(first);
        Assert.Contains("AccessViolationException", first);
        Assert.Null(second);
    }

    [Fact]
    public void AnUnwritableRootDisablesLoggingInsteadOfThrowing()
    {
        AppLog.Initialize(Path.Combine(_root, "nested", "\0invalid"));

        AppLog.Info("must not throw");

        Assert.Null(AppLog.ReadAndClearCrashMarker());
    }
}
