using System.Text.RegularExpressions;

namespace Camzivo.Core.Security;

/// <summary>
/// Single choke point for removing secret material from anything that can be displayed, logged or stored.
/// Every log statement and every user-facing error detail passes through <see cref="Sanitize"/>.
/// </summary>
public static partial class CredentialRedactor
{
    private const string Mask = "***";

    // Password is optional: `rtsp://user@host` leaks an account name just as surely as `user:pass@host`.
    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)(?<user>[^:@/\s]+)(?<password>:[^@/\s]*)?@")]
    private static partial Regex UriUserInfoRegex();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|apikey|api[_-]?key|auth(?:orization)?)\s*=\s*(?<value>[^&\s,;""']+)")]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(@"(?i)\bauthorization\s*:\s*(?<value>[^\r\n]+)")]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(@"(?i)\b(?:Basic|Digest)\s+(?<value>[A-Za-z0-9+/=]{6,})")]
    private static partial Regex AuthSchemeTokenRegex();

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sanitized = UriUserInfoRegex().Replace(text, MaskUserInfo);
        sanitized = QuerySecretRegex().Replace(sanitized, "$1=" + Mask);
        sanitized = AuthorizationHeaderRegex().Replace(sanitized, "Authorization: " + Mask);
        sanitized = AuthSchemeTokenRegex().Replace(sanitized, "***");
        return sanitized;
    }

    private static string MaskUserInfo(Match match)
    {
        var scheme = match.Groups["scheme"].Value;
        var user = match.Groups["user"].Value;

        return match.Groups["password"].Success
            ? $"{scheme}{user}:{Mask}@"
            : $"{scheme}{Mask}@";
    }

    /// <summary>True when the text still contains something that looks like a credential (used by tests and log guards).</summary>
    public static bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // The mask itself must not count as a secret, otherwise sanitized text would still trip the guard.
        foreach (Match match in UriUserInfoRegex().Matches(text))
        {
            if (!match.Groups["password"].Success)
            {
                return true;
            }

            var password = match.Groups["password"].Value.TrimStart(':');
            if (!password.Equals(Mask, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (Match match in QuerySecretRegex().Matches(text))
        {
            if (!match.Groups["value"].Value.Equals(Mask, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return AuthorizationHeaderRegex()
            .Matches(text)
            .Any(match => !match.Groups["value"].Value.Trim().Equals(Mask, StringComparison.Ordinal));
    }
}
