using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenLineOps.ReleaseManifest;

internal static partial class ReleaseManifestContract
{
    public const string Product = "OpenLineOps";

    public static void ValidateVersion(string? value)
    {
        if (value is null
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !SemanticVersionRegex().IsMatch(value))
        {
            throw new InvalidOperationException(
                "Release version must be one canonical semantic-version value.");
        }
    }

    public static void ValidateCommit(string? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length is not (40 or 64)
            || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "Release commit must be a lowercase 40- or 64-character Git object id.");
        }
    }

    public static void ValidateGeneratedAtUtc(string? value)
    {
        if (value is null
            || !DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || !string.Equals(value, parsed.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Release generatedAtUtc must be a canonical round-trip UTC timestamp.");
        }
    }

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-(?:(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\\+(?:[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SemanticVersionRegex();
}
