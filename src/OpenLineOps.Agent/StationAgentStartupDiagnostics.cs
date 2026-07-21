using System.Collections;
using System.Text.RegularExpressions;

namespace OpenLineOps.Agent;

internal static partial class StationAgentStartupDiagnostics
{
    private const int MaximumEventLogMessageLength = 4_096;

    public static string CreateEventLogFailureMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = $"OpenLineOps Station Agent startup failed ({exception.GetType().Name}): {exception.Message}";
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is not string name
                || variable.Value is not string value
                || value.Length < 4
                || !IsSensitiveEnvironmentName(name))
            {
                continue;
            }

            message = message.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        }

        message = CredentialUri().Replace(message, "$1[REDACTED]@");
        var normalized = new string(message
            .Select(static character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        return normalized.Length <= MaximumEventLogMessageLength
            ? normalized
            : normalized[..MaximumEventLogMessageLength];
    }

    private static bool IsSensitiveEnvironmentName(string name) =>
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("URI", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"\b([a-z][a-z0-9+.-]*://)[^/@\s:]+:[^/@\s]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUri();
}
