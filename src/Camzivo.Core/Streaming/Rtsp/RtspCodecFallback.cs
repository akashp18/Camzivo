namespace Camzivo.Core.Streaming.Rtsp;

/// <summary>
/// Picks the next stream of the same channel when the negotiated video codec cannot be decoded.
///
/// A Dahua recorder labels its streams only by <c>subtype</c> in the URL (0 main, 1 sub, 2 third), and the three may
/// be encoded differently — an installer often leaves a sub stream on H.264 for compatibility while the main stream
/// runs H.265. Since this build decodes H.264, a tile that would otherwise fail can still show video by asking for
/// the channel's next stream. Deriving the candidate from the URL keeps the fallback where the knowledge lives and
/// avoids threading a second address through every caller.
/// </summary>
public static class RtspCodecFallback
{
    /// <summary>The next stream of the same channel, or null when the channel has no further stream to offer.</summary>
    public static Uri? Next(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var next = NextSubtype(ReadSubtype(uri));
        return next is null ? null : WithSubtype(uri, next.Value);
    }

    /// <summary>Human name for a subtype, used in the tile detail so a downgrade is visible, not silent.</summary>
    public static string Describe(Uri uri) => ReadSubtype(uri) switch
    {
        1 => "sub stream",
        2 => "third stream",
        _ => "main stream",
    };

    private static int? NextSubtype(int? subtype) => subtype switch
    {
        0 => 1,
        1 => 2,
        _ => null,
    };

    private static int? ReadSubtype(Uri uri)
    {
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = parameter.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (parameter[..separator].Equals("subtype", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parameter[(separator + 1)..], out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static Uri WithSubtype(Uri uri, int subtype)
    {
        var parameters = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter =>
            {
                var separator = parameter.IndexOf('=');
                return separator > 0 &&
                       parameter[..separator].Equals("subtype", StringComparison.OrdinalIgnoreCase)
                    ? $"subtype={subtype}"
                    : parameter;
            });

        var builder = new UriBuilder(uri) { Query = string.Join('&', parameters) };
        return builder.Uri;
    }
}
