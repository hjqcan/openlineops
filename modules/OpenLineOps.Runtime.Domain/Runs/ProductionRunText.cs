namespace OpenLineOps.Runtime.Domain.Runs;

internal static class ProductionRunText
{
    public static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;
    }

    public static string? Optional(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return Required(value, parameterName);
    }
}
