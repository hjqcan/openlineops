using OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

namespace OpenLineOps.Devices.Api.ExternalPrograms;

public static class ApplicationExecutableProtocolTrialPolicies
{
    public const string Disabled = "Disabled";

    public const string RestrictedHost = "RestrictedHost";

    public static bool IsRestrictedHost(string value) =>
        string.Equals(value, RestrictedHost, StringComparison.Ordinal);

    public static void RequireCurrent(string value)
    {
        if (!string.Equals(value, Disabled, StringComparison.Ordinal)
            && !IsRestrictedHost(value))
        {
            throw new InvalidOperationException(
                $"Application executable protocol trial policy must be exactly '{Disabled}' "
                + $"or '{RestrictedHost}'.");
        }
    }
}

public sealed class ExternalProgramProtocolTrialOptions
{
    public const string SectionName = "OpenLineOps:Devices:ExternalProgramTrials";

    public string ApplicationExecutablePolicy { get; init; } =
        ApplicationExecutableProtocolTrialPolicies.Disabled;

    public bool AllowsApplicationExecutable =>
        ApplicationExecutableProtocolTrialPolicies.IsRestrictedHost(
            ApplicationExecutablePolicy);

    public void Validate(ExternalProgramHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        hostOptions.Validate();
        ApplicationExecutableProtocolTrialPolicies.RequireCurrent(
            ApplicationExecutablePolicy);
        if (!AllowsApplicationExecutable)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Restricted application executable protocol trials require Windows "
                + "service identity and AppContainer enforcement.");
        }

        if (!hostOptions.RequireRestrictedHostIdentity
            || !hostOptions.RequireImmutableContentProtection
            || !hostOptions.RequireAppContainerIsolation)
        {
            throw new InvalidOperationException(
                "Application executable protocol trials may be enabled only when the external "
                + "program host requires its exact restricted service identity, immutable "
                + "content protection, and AppContainer isolation.");
        }
    }
}
