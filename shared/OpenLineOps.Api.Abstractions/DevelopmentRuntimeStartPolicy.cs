namespace OpenLineOps.Api.Abstractions;

public static class DevelopmentRuntimeStartPolicy
{
    public const string EnabledConfigurationKey = "OpenLineOps:Runtime:DevelopmentStarts:Enabled";

    public const string DisabledErrorCode = "Runtime.DevelopmentStartEndpointDisabled";

    public const string DisabledErrorMessage =
        "This compatibility runtime-start endpoint is disabled. It can be enabled only in the Development or Test environment with the explicit development-start flag.";

    public static bool IsAllowed(string? environmentName, string? configuredValue)
    {
        var isAllowedEnvironment =
            string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase);

        return isAllowedEnvironment
            && bool.TryParse(configuredValue, out var explicitlyEnabled)
            && explicitlyEnabled;
    }
}
