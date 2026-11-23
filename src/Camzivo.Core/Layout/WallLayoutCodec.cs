using System.Text;
using System.Text.Json;

namespace Camzivo.Core.Layout;

/// <summary>
/// Serialises the camera wall (which camera sits in which slot) so an operator's arrangement survives a restart.
///
/// Rules that make this safe to run against a file written by another build or edited by hand:
/// unknown JSON is refused rather than guessed at, a missing/duplicate slot becomes empty instead of duplicating
/// a decoder, and the slot count is capped.
/// </summary>
public static class WallLayoutCodec
{
    public const int CurrentVersion = 1;

    public const int MaxSlots = 256;

    public static string Encode(IReadOnlyList<Guid?> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count > MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slots), slots.Count, $"A wall cannot hold more than {MaxSlots} slots.");
        }

        var builder = new StringBuilder(slots.Count * 40 + 32);
        builder.Append("{\"version\":").Append(CurrentVersion).Append(",\"slots\":[");

        for (var index = 0; index < slots.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            if (slots[index] is { } id)
            {
                builder.Append('"').Append(id.ToString("D")).Append('"');
            }
            else
            {
                builder.Append("null");
            }
        }

        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>
    /// Parses a stored wall. Returns false only for structurally unusable input (not JSON, wrong version, not an
    /// array, too many slots); individual unusable entries decay to empty slots.
    /// </summary>
    public static bool TryDecode(string? text, out Guid?[] slots)
    {
        slots = [];

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                version.GetInt32() != CurrentVersion ||
                !root.TryGetProperty("slots", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var length = array.GetArrayLength();
            if (length > MaxSlots)
            {
                return false;
            }

            var result = new Guid?[length];
            var seen = new HashSet<Guid>();

            for (var index = 0; index < length; index++)
            {
                var element = array[index];
                if (element.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                // A hand-edited or foreign value must not throw; it simply becomes an empty slot.
                if (!Guid.TryParse(element.GetString(), out var id) || !seen.Add(id))
                {
                    continue;
                }

                result[index] = id;
            }

            slots = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
