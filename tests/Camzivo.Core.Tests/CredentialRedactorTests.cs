using Camzivo.Core.Security;
using Xunit;

namespace Camzivo.Core.Tests;

public class CredentialRedactorTests
{
    [Theory]
    [InlineData("rtsp://admin:secret@192.168.18.11:554/live", "rtsp://admin:***@192.168.18.11:554/live")]
    [InlineData("http://user:p%40ss@cam.local/onvif/device_service", "http://user:***@cam.local/onvif/device_service")]
    public void UriUserInformationIsMasked(string input, string expected)
    {
        Assert.Equal(expected, CredentialRedactor.Sanitize(input));
    }

    [Fact]
    public void UserInformationWithoutAPasswordIsStillMasked()
    {
        Assert.Equal("rtsp://***@cam.local/live", CredentialRedactor.Sanitize("rtsp://admin@cam.local/live"));
        Assert.True(CredentialRedactor.ContainsSecret("rtsp://admin@cam.local/live"));
    }

    [Fact]
    public void PlainTextWithoutCredentialsIsNotMistakenForOne()
    {
        // A slash after the host must not be read as user information.
        Assert.Equal("https://example.com/a@b", CredentialRedactor.Sanitize("https://example.com/a@b"));
        Assert.False(CredentialRedactor.ContainsSecret("https://example.com/a@b"));
    }

    [Fact]
    public void QuerySecretsAreMasked()
    {
        var sanitized = CredentialRedactor.Sanitize("GET /cgi-bin/x?user=admin&password=hunter2&channel=1");

        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.Contains("password=***", sanitized, StringComparison.Ordinal);
        Assert.Contains("channel=1", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationHeadersAreMasked()
    {
        var sanitized = CredentialRedactor.Sanitize("Authorization: Digest username=\"admin\", response=\"abc123\"");

        Assert.Equal("Authorization: ***", sanitized);
    }

    [Fact]
    public void BareSchemeTokensAreMasked()
    {
        var sanitized = CredentialRedactor.Sanitize("header Basic YWRtaW46c2VjcmV0");

        Assert.DoesNotContain("YWRtaW46c2VjcmV0", sanitized, StringComparison.Ordinal);
        Assert.Contains("***", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void NonSecretTextIsUnchanged()
    {
        const string message = "Camera 12 reconnected after 3 attempts at 14:02:11";

        Assert.Equal(message, CredentialRedactor.Sanitize(message));
    }

    [Fact]
    public void ContainsSecretDetectsLeaksBeforeTheyAreLogged()
    {
        Assert.True(CredentialRedactor.ContainsSecret("rtsp://admin:secret@cam/live"));
        Assert.True(CredentialRedactor.ContainsSecret("password=abc"));
        Assert.False(CredentialRedactor.ContainsSecret(CredentialRedactor.Sanitize("rtsp://admin:secret@cam/live")));
    }

    [Fact]
    public void NullAndEmptyAreSafe() 
    {
        Assert.Equal(string.Empty, CredentialRedactor.Sanitize(null));
        Assert.Equal(string.Empty, CredentialRedactor.Sanitize(string.Empty));
        Assert.False(CredentialRedactor.ContainsSecret(null));
    }
}
