namespace OpenLineOps.Operations.Domain.Identifiers;

internal static class OperationsIdGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier values cannot be blank.", parameterName);
        }

        return value.Trim();
    }
}
