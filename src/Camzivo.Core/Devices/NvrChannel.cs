using System.Text.RegularExpressions;
using Camzivo.Core.Streaming;

namespace Camzivo.Core.Devices;

/// <summary>Whether a device is one camera or a recorder exposing several channels.</summary>
public enum DeviceKind
{
    Camera = 0,
    Nvr = 1,
}

/// <summary>One video channel of a recorder, as persisted. Identical to a standalone camera except for its parent.</summary>
public sealed record NvrChannel(
    Guid Id,
    Guid NvrId,
    int Channel,
    string Name,
    string? ProfileToken,
    string? SubProfileToken,
    string? MainUri,
    string? SubUri,
    bool Enabled,
    int SortOrder,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? SourceToken = null)
{
    public StreamUriSet Uris => new(MainUri, SubUri);

    public string DisplayName => $"{Channel} · {Name}";
}

/// <summary>A profile on a recorder, already resolved to a stream URI, before it becomes a channel.</summary>
public sealed record DiscoveredChannel(
    int Channel,
    string Name,
    string ProfileToken,
    string? SubProfileToken,
    string? MainUri,
    string? SubUri,
    string? SourceToken = null)
{
    public StreamUriSet Uris => new(MainUri, SubUri);
}

/// <summary>
/// Turns the profile list of a recorder into channels.
///
/// There is no standard ONVIF call that says "list the channels of this recorder": media profiles are the closest
/// thing, and vendors differ. Dahua names them per channel ("Channel1 Main"/"Channel 1 Sub"), other devices use the
/// profile token. The rule here is deliberately conservative:
///
/// 1. A channel number is read from the profile name or token when one is present ("Channel 3", "CH03", "3").
/// 2. Profiles that do not name a channel are numbered in the order the device returned them.
/// 3. Profiles sharing a channel number are paired: the heaviest is main, the lightest distinct one is sub.
///
/// Anything the device does not describe stays "unverified": the operator sees a channel list before importing, so a
/// wrong guess is visible rather than silently streamed.
/// </summary>
public static partial class NvrChannelPlanner
{
    public static IReadOnlyList<DiscoveredChannel> Plan(
        IReadOnlyList<Onvif.OnvifVideoProfile> profiles,
        Func<string, Uri?> streamUriForProfile)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(streamUriForProfile);

        var candidates = profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Token))
            .ToList();

        var measured = candidates
            .Where(profile => profile.Width > 0 && profile.Height > 0)
            .ToList();

        // Dahua recorders omit <Resolution> from GetProfiles — the resolution is only in GetVideoEncoderConfigurations
        // — so filtering on it turned an eight-channel recorder into "no channels" while another client listed all
        // eight. When no profile reports a resolution, profiles that still describe video (an encoder token, a source
        // token or an encoding) are numbered instead; the channel number, not the resolution, is what an import needs.
        var usable = measured.Count > 0
            ? measured
            : candidates
                .Where(profile =>
                    !string.IsNullOrWhiteSpace(profile.EncoderToken) ||
                    !string.IsNullOrWhiteSpace(profile.SourceToken) ||
                    !string.IsNullOrWhiteSpace(profile.Encoding))
                .ToList();

        if (usable.Count == 0)
        {
            return [];
        }

        // The stream address the recorder hands back is the strongest evidence available: Dahua writes the channel
        // and the stream type into it (?channel=3&subtype=0). Reading those beats guessing from profile names, which
        // turned one 16-channel recorder into 28 phantom channels whose "sub" streams pointed at channels 42-46 —
        // addresses the recorder answered with 503 because they do not exist. Profiles whose URL names no channel
        // still fall back to the name/token rules below, so cameras behave exactly as before.
        var entries = new List<(int Channel, Onvif.OnvifVideoProfile Profile, int? Subtype, Uri? Uri)>();
        var unnumbered = new List<Onvif.OnvifVideoProfile>();

        foreach (var profile in usable)
        {
            var uri = streamUriForProfile(profile.Token);
            if (ReadQueryNumber(uri, "channel") is > 0 and <= 512 and var channel)
            {
                entries.Add((channel, profile, ReadQueryNumber(uri, "subtype"), uri));
            }
            else
            {
                unnumbered.Add(profile);
            }
        }

        if (unnumbered.Count > 0)
        {
            var numbered = new List<(int Channel, Onvif.OnvifVideoProfile Profile)>();

            foreach (var profile in unnumbered)
            {
                var channel = ReadChannelNumber(profile.Name) ?? ReadChannelNumber(profile.Token);
                numbered.Add((channel ?? 0, profile));
            }

            // Profiles that carry no channel number keep their device order, continuing after the highest explicit one.
            var highestExplicit = numbered.Where(pair => pair.Channel > 0).Select(pair => pair.Channel).DefaultIfEmpty(0).Max();
            var next = highestExplicit + 1;

            // Some recorders return one main and one sub profile per channel with no channel number anywhere (Dahua
            // variants do this). Numbering each profile separately would turn eight cameras into sixteen tiles. Pair
            // an alternating main/sub sequence only when the whole device uses that shape; a mixed list keeps the old
            // conservative numbering so an explicit channel is never silently merged with an unnumbered profile.
            if (highestExplicit == 0 && TryPairUnnumberedStreams(unnumbered, out var pairs))
            {
                numbered = pairs;
            }
            else
            {
                for (var index = 0; index < numbered.Count; index++)
                {
                    if (numbered[index].Channel == 0)
                    {
                        numbered[index] = (next++, numbered[index].Profile);
                    }
                }
            }

            foreach (var (channel, profile) in numbered)
            {
                entries.Add((channel, profile, null, streamUriForProfile(profile.Token)));
            }
        }

        var channels = new List<DiscoveredChannel>();

        foreach (var group in entries.GroupBy(entry => entry.Channel).OrderBy(group => group.Key))
        {
            // subtype 0 is the main stream; the smallest higher subtype is the sub stream. Profiles with no subtype
            // in their URL fall back to the heaviest/lowest pixel rate, as before.
            var ordered = group
                .OrderBy(entry => entry.Subtype ?? int.MaxValue)
                .ThenByDescending(entry => entry.Profile.PixelRate)
                .ToList();

            var main = ordered[0];
            var subIndex = ordered.FindIndex(entry => entry.Profile.Token != main.Profile.Token);
            var sub = subIndex >= 0 ? ordered[subIndex] : default;

            channels.Add(new DiscoveredChannel(
                group.Key,
                CleanName(main.Profile.Name, group.Key),
                main.Profile.Token,
                subIndex >= 0 ? sub.Profile.Token : null,
                main.Uri?.AbsoluteUri,
                subIndex >= 0 ? sub.Uri?.AbsoluteUri : null));
        }

        return channels;
    }

    /// <summary>Reads an integer query parameter (Dahua: <c>channel</c>, <c>subtype</c>) from a stream URI.</summary>
    private static int? ReadQueryNumber(Uri? uri, string key)
    {
        if (uri is null || string.IsNullOrEmpty(uri.Query))
        {
            return null;
        }

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || !part.AsSpan(0, separator).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(part[(separator + 1)..], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Reads a 1-based channel number out of a profile name or token, or null when there is none.</summary>
    public static int? ReadChannelNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ChannelPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["n"].Value, out var value) && value is > 0 and <= 512 ? value : null;
    }

    private static bool TryPairUnnumberedStreams(
        IReadOnlyList<Onvif.OnvifVideoProfile> profiles,
        out List<(int Channel, Onvif.OnvifVideoProfile Profile)> numbered)
    {
        numbered = [];
        var roles = profiles.Select(profile => ReadStreamRole(profile.Name) ?? ReadStreamRole(profile.Token)).ToList();

        if (profiles.Count < 2 || profiles.Count % 2 != 0 ||
            roles.Any(role => role is null) ||
            roles.Where((role, index) => index % 2 == 0).Any(role => role != StreamRole.Main) ||
            roles.Where((role, index) => index % 2 == 1).Any(role => role != StreamRole.Sub))
        {
            return false;
        }

        var channel = 1;
        for (var index = 0; index < profiles.Count; index += 2)
        {
            numbered.Add((channel, profiles[index]));
            numbered.Add((channel, profiles[index + 1]));
            channel++;
        }

        return true;
    }

    private enum StreamRole
    {
        Main,
        Sub,
    }

    private static StreamRole? ReadStreamRole(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (SubStreamToken().IsMatch(text))
        {
            return StreamRole.Sub;
        }

        return MainStreamToken().IsMatch(text) ? StreamRole.Main : null;
    }

    private static string CleanName(string? profileName, int channel)
    {
        var name = profileName?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return $"Channel {channel}";
        }

        // "Channel1 Main" / "Camera 3 - Sub" -> the channel identity is already shown separately.
        name = SubStreamSuffix().Replace(name, string.Empty).Trim();

        return name.Length == 0 ? $"Channel {channel}" : name;
    }

    [GeneratedRegex(@"(?:channel|ch|camera|cam|profile)?[\s_\-]*(?<n>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"[\s_\-]*(main|sub|substream|sub-stream|mainstream|main-stream)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubStreamSuffix();

    [GeneratedRegex(@"(^|[\s_\-])(sub|substream|sub-stream)([\s_\-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SubStreamToken();

    [GeneratedRegex(@"(^|[\s_\-])(main|mainstream|main-stream)([\s_\-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MainStreamToken();
}
