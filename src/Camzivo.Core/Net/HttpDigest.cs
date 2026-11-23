using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Camzivo.Core.Net;

/// <summary>A parsed <c>WWW-Authenticate: Digest</c> challenge.</summary>
public sealed record DigestChallenge(
    string Realm,
    string? Nonce,
    string? Opaque,
    string? Qop,
    string Algorithm,
    bool Stale);

/// <summary>
/// HTTP Digest authentication (RFC 7616 for the parts we use, which are the RFC 2617 MD5 parts that ONVIF
/// cameras actually implement).
///
/// Pure by design: the nonce count and client nonce are parameters, not clock/random reads, so the exact header can
/// be asserted in a test. Credentials are never logged; callers must not put the header in diagnostics.
/// </summary>
public static class HttpDigest
{
    public static bool TryParse(string? header, out DigestChallenge challenge)
    {
        challenge = new DigestChallenge(string.Empty, null, null, null, "MD5", false);

        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var trimmed = header.Trim();
        if (!trimmed.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitParameters(trimmed[7..]))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim().Trim('"');
            fields[key] = value;
        }

        if (!fields.TryGetValue("realm", out var realm))
        {
            return false;
        }

        challenge = new DigestChallenge(
            realm,
            fields.GetValueOrDefault("nonce"),
            fields.GetValueOrDefault("opaque"),
            fields.GetValueOrDefault("qop"),
            fields.GetValueOrDefault("algorithm") ?? "MD5",
            string.Equals(fields.GetValueOrDefault("stale"), "true", StringComparison.OrdinalIgnoreCase));

        return true;
    }

    /// <summary>Builds the <c>Authorization</c> header value for one request.</summary>
    public static string BuildAuthorization(
        DigestChallenge challenge,
        string method,
        string requestUri,
        string username,
        string password,
        string clientNonce,
        int nonceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        if (string.IsNullOrEmpty(challenge.Nonce))
        {
            throw new ArgumentException("Challenge has no nonce.", nameof(challenge));
        }

        var ha1 = Md5Hex($"{username}:{challenge.Realm}:{password}");
        var ha2 = Md5Hex($"{method.ToUpperInvariant()}:{requestUri}");
        var nc = nonceCount.ToString("x8", CultureInfo.InvariantCulture);

        var useQop = !string.IsNullOrEmpty(challenge.Qop)
                     && challenge.Qop.Contains("auth", StringComparison.OrdinalIgnoreCase);

        var response = useQop
            ? Md5Hex($"{ha1}:{challenge.Nonce}:{nc}:{clientNonce}:auth:{ha2}")
            : Md5Hex($"{ha1}:{challenge.Nonce}:{ha2}");

        var builder = new StringBuilder();
        builder.Append("Digest username=\"").Append(username).Append("\", ")
            .Append("realm=\"").Append(challenge.Realm).Append("\", ")
            .Append("nonce=\"").Append(challenge.Nonce).Append("\", ")
            .Append("uri=\"").Append(requestUri).Append("\", ")
            .Append("algorithm=").Append(challenge.Algorithm).Append(", ")
            .Append("response=\"").Append(response).Append('"');

        if (useQop)
        {
            builder.Append(", qop=auth, nc=").Append(nc).Append(", cnonce=\"").Append(clientNonce).Append('"');
        }

        if (!string.IsNullOrEmpty(challenge.Opaque))
        {
            builder.Append(", opaque=\"").Append(challenge.Opaque).Append('"');
        }

        return builder.ToString();
    }

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Splits on commas that are not inside quotes (realm values may contain commas).</summary>
    private static IEnumerable<string> SplitParameters(string input)
    {
        var start = 0;
        var quoted = false;

        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                yield return input[start..index];
                start = index + 1;
            }
        }

        if (start < input.Length)
        {
            yield return input[start..];
        }
    }
}
