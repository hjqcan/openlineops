namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalPluginSandboxOptions
{
    public bool RequireTrustedPackage { get; set; }

    public Dictionary<string, string> TrustedEntryAssemblySha256 { get; } = new(StringComparer.Ordinal);

    public bool RequireSignedPackage { get; set; }

    public string PackageSignatureFileName { get; set; } = "openlineops-plugin.signature.json";

    public Dictionary<string, string> TrustedPackageSigningPublicKeys { get; } = new(StringComparer.Ordinal);

    public bool RequireLeastPrivilegeExecution { get; set; }

    public string IsolationMode { get; set; } = ExternalPluginIsolationModes.ExternalProcess;

    public string? LeastPrivilegeIdentity { get; set; }

    public string? LeastPrivilegeLauncherExecutable { get; set; }

    public string? LeastPrivilegeArgumentsTemplate { get; set; }

    public bool LeastPrivilegeNoInteractivePrompt { get; set; } = true;

    public string? ContainerImage { get; set; }

    public string? ContainerRuntimeExecutable { get; set; }

    public string ContainerPackagePath { get; set; } = "/openlineops/plugin";

    public string? ContainerWorkingDirectory { get; set; }

    public string ContainerExecutablePath { get; set; } = "dotnet";

    public string ContainerArgumentsTemplate { get; set; } =
        "\"{EntryAssemblyPath}\" --openlineops-plugin-host --manifest \"{ManifestPath}\"";

    public string ContainerNetwork { get; set; } = "none";

    public bool ContainerNoNewPrivileges { get; set; } = true;

    public bool ContainerDropAllCapabilities { get; set; } = true;

    public bool ContainerReadOnlyRootFilesystem { get; set; }

    public int ContainerPidsLimit { get; set; } = 128;

    public List<string> AdditionalContainerRunArguments { get; } = [];

    public TimeSpan MaxCommandTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public bool TerminateProcessOnCommandTimeout { get; set; } = true;

    public bool HasLeastPrivilegeExecution
    {
        get
        {
            return !string.IsNullOrWhiteSpace(LeastPrivilegeIdentity)
                || !string.IsNullOrWhiteSpace(ContainerImage)
                || ExternalPluginIsolationModes.IsContainer(IsolationMode);
        }
    }
}

public static class ExternalPluginIsolationModes
{
    public const string ExternalProcess = "ExternalProcess";
    public const string LeastPrivilegeIdentity = "LeastPrivilegeIdentity";
    public const string Container = "Container";

    public static ExternalPluginIsolationMode Parse(string? value)
    {
        return value switch
        {
            ExternalProcess => ExternalPluginIsolationMode.ExternalProcess,
            LeastPrivilegeIdentity => ExternalPluginIsolationMode.LeastPrivilegeIdentity,
            Container => ExternalPluginIsolationMode.Container,
            _ => throw new InvalidOperationException(
                $"Unsupported external plugin isolation mode '{value}'. Expected exactly "
                + $"'{ExternalProcess}', '{LeastPrivilegeIdentity}', or '{Container}'.")
        };
    }

    public static bool IsLeastPrivilegeIdentity(string value)
    {
        return string.Equals(value, LeastPrivilegeIdentity, StringComparison.Ordinal);
    }

    public static bool IsContainer(string value)
    {
        return string.Equals(value, Container, StringComparison.Ordinal);
    }
}

public enum ExternalPluginIsolationMode
{
    ExternalProcess,
    LeastPrivilegeIdentity,
    Container
}
