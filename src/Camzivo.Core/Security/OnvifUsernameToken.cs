using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Camzivo.Core.Security;

/// <summary>
/// WS-Security UsernameToken (PasswordDigest) as ONVIF devices require. ONVIF does not accept a plaintext password
/// element in practice, and sending one would be a needless exposure on the wire.
///
/// digest = Base64(SHA1(nonce || created || password)) — the nonce and timestamp are parameters so the output is
/// deterministic and testable.
/// </summary>
public static class OnvifUsernameToken
{
    public const string WsseNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";

    public const string WsuNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    public const string PasswordDigestType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";

    public const string Base64EncodingType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    /// <summary>Builds the digest value for a given nonce, timestamp and password.</summary>
    public static string PasswordDigest(string password, ReadOnlySpan<byte> nonce, DateTimeOffset created)
    {
        var createdText = CreatedText(created);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var createdBytes = Encoding.UTF8.GetBytes(createdText);

        var buffer = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        nonce.CopyTo(buffer);
        createdBytes.CopyTo(buffer, nonce.Length);
        passwordBytes.CopyTo(buffer, nonce.Length + createdBytes.Length);

        return Convert.ToBase64String(SHA1.HashData(buffer));
    }

    public static string CreatedText(DateTimeOffset created) =>
        created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Full <c>&lt;wsse:Security&gt;</c> header. Caller supplies the deterministic nonce and timestamp.</summary>
    public static string SecurityHeader(string username, string password, ReadOnlySpan<byte> nonce, DateTimeOffset created)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var digest = PasswordDigest(password, nonce, created);
        var nonceBase64 = Convert.ToBase64String(nonce);
        var createdText = CreatedText(created);

        // XML-escape the username: an account can legitimately contain '&' or '<'.
        var safeUser = System.Security.SecurityElement.Escape(username);

        return $"""
            <wsse:Security xmlns:wsse="{WsseNamespace}" xmlns:wsu="{WsuNamespace}">
              <wsse:UsernameToken>
                <wsse:Username>{safeUser}</wsse:Username>
                <wsse:Password Type="{PasswordDigestType}">{digest}</wsse:Password>
                <wsse:Nonce EncodingType="{Base64EncodingType}">{nonceBase64}</wsse:Nonce>
                <wsu:Created>{createdText}</wsu:Created>
              </wsse:UsernameToken>
            </wsse:Security>
            """;
    }
}
