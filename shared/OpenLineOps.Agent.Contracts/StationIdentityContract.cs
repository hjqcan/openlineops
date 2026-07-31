namespace OpenLineOps.Agent.Contracts;

public static class StationIdentityContract
{
    public const int MaximumLength = 128;

    public static bool IsCanonical(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumLength
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '@' or '-');

    public static string Require(string? value, string parameterName) =>
        IsCanonical(value)
            ? value!
            : throw new ArgumentException(
                $"{parameterName} must be a canonical ASCII Station identity no longer than {MaximumLength} characters.",
                parameterName);

    public static void RequireMessage(string? value, string fieldName)
    {
        if (!IsCanonical(value))
        {
            throw new InvalidDataException(
                $"{fieldName} must be a canonical ASCII Station identity no longer than {MaximumLength} characters.");
        }
    }
}
