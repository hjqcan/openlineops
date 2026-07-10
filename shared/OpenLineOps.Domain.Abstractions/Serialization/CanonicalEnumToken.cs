namespace OpenLineOps.Domain.Abstractions.Serialization;

public static class CanonicalEnumToken
{
    public static bool TryParse<TEnum>(string? token, out TEnum value)
        where TEnum : struct, Enum
    {
        if (token is not null
            && Enum.TryParse<TEnum>(token, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(token, parsed.ToString(), StringComparison.Ordinal))
        {
            value = parsed;
            return true;
        }

        value = default;
        return false;
    }

    public static string ExpectedTokens<TEnum>()
        where TEnum : struct, Enum
    {
        return string.Join(
            ", ",
            Enum.GetValues<TEnum>()
                .Select(value => value.ToString())
                .Distinct(StringComparer.Ordinal));
    }
}
