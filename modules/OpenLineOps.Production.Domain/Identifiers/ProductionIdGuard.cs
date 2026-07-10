namespace OpenLineOps.Production.Domain.Identifiers;

internal static class ProductionIdGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier values cannot be blank.", parameterName);
        }

        return value.Trim();
    }

    public static string PortablePathSegment(string value, string parameterName)
    {
        var normalized = NotBlank(value, parameterName);
        if (!string.Equals(value, normalized, StringComparison.Ordinal)
            || normalized is "." or ".."
            || normalized.Length > 128
            || normalized.EndsWith('.')
            || IsReservedWindowsName(normalized)
            || normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException(
                "Value must be a portable path segment containing at most 128 letters, digits, '.', '-' or '_' characters and cannot be an operating-system reserved name.",
                parameterName);
        }

        return normalized;
    }

    private static bool IsReservedWindowsName(string value)
    {
        var baseName = value.Split('.', 2)[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return baseName.Length == 4
            && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && baseName[3] is >= '1' and <= '9';
    }
}
