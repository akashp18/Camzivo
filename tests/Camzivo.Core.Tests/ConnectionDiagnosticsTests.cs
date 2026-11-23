using Camzivo.Core.Diagnostics;
using Xunit;

namespace Camzivo.Core.Tests;

public class ConnectionDiagnosticsTests
{
    public static TheoryData<StreamFailure> AllFailures()
    {
        var data = new TheoryData<StreamFailure>();
        foreach (var failure in Enum.GetValues<StreamFailure>())
        {
            data.Add(failure);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFailures))]
    public void EveryFailureExplainsItselfAndOffersAnAction(StreamFailure failure)
    {
        var diagnostic = ConnectionDiagnostics.Explain(failure);

        Assert.Equal(failure, diagnostic.Code);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Title));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Explanation));
        Assert.NotEmpty(diagnostic.Actions);
        Assert.All(diagnostic.Actions, action => Assert.False(string.IsNullOrWhiteSpace(action)));
    }

    [Fact]
    public void UnauthorizedMentionsTheOnvifAccountTrap()
    {
        var diagnostic = ConnectionDiagnostics.Explain(StreamFailure.Unauthorized);

        Assert.Contains("ONVIF", diagnostic.Actions[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetailIsRedactedBeforeItCanReachTheUi()
    {
        var diagnostic = ConnectionDiagnostics.Explain(
            StreamFailure.Unauthorized,
            "DESCRIBE rtsp://admin:supersecret@192.168.18.11:554/live -> 401");

        Assert.NotNull(diagnostic.Detail);
        Assert.DoesNotContain("supersecret", diagnostic.Detail, StringComparison.Ordinal);
        Assert.Contains("***", diagnostic.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankDetailBecomesNull()
    {
        Assert.Null(ConnectionDiagnostics.Explain(StreamFailure.Timeout, "   ").Detail);
    }

    [Fact]
    public void ExplainAllSkipsTheNoneCode()
    {
        var diagnostics = ConnectionDiagnostics.ExplainAll([StreamFailure.None, StreamFailure.Timeout, StreamFailure.Forbidden]);

        Assert.Equal(2, diagnostics.Count);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == StreamFailure.None);
    }

    [Fact]
    public void UnknownFailureStillProducesASupportableMessage()
    {
        var diagnostic = ConnectionDiagnostics.Explain(StreamFailure.Unknown, "opaque");

        Assert.Contains("support", string.Join(' ', diagnostic.Actions), StringComparison.OrdinalIgnoreCase);
    }
}
