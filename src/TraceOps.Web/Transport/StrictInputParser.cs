using System.Globalization;
using System.Text.RegularExpressions;
using TraceOps.Domain;

namespace TraceOps.Web.Transport;

internal static partial class StrictInputParser
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    public static bool IsCanonicalTrackingNumber(string? value)
    {
        if (value is null || value.Length is < 1 or > 32)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'A' and <= 'Z')
                and not (>= '0' and <= '9')
                and not '-')
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryParseStatus(string? value, out ShipmentStatus status)
    {
        foreach (var name in Enum.GetNames<ShipmentStatus>())
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                status = Enum.Parse<ShipmentStatus>(name, ignoreCase: false);
                return true;
            }
        }

        status = default;
        return false;
    }

    public static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        if (value is null || !TimestampPattern().IsMatch(value))
        {
            timestamp = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    public static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
