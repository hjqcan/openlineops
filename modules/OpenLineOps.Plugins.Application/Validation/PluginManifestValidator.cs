using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Compatibility;

namespace OpenLineOps.Plugins.Application.Validation;

public sealed class PluginManifestValidator : IPluginManifestValidator
{
    private readonly PluginCompatibilityOptions _options;

    public PluginManifestValidator(PluginCompatibilityOptions? options = null)
    {
        _options = options ?? PluginCompatibilityOptions.Default;
    }

    public PluginValidationReport Validate(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        List<PluginValidationIssue> issues = [];

        AddRequiredStringIssue(issues, manifest.Id, "Plugin.ManifestIdRequired", "Plugin id is required.");
        AddRequiredStringIssue(issues, manifest.Name, "Plugin.NameRequired", "Plugin name is required.");
        AddRequiredStringIssue(issues, manifest.Version, "Plugin.VersionRequired", "Plugin version is required.");
        AddRequiredStringIssue(issues, manifest.EntryAssembly, "Plugin.EntryAssemblyRequired", "Plugin entry assembly is required.");
        AddRequiredStringIssue(issues, manifest.EntryType, "Plugin.EntryTypeRequired", "Plugin entry type is required.");
        AddRequiredStringIssue(
            issues,
            manifest.RuntimeIdentifier,
            "Plugin.RuntimeIdentifierRequired",
            "Plugin runtime identifier is required.");
        AddRequiredStringIssue(
            issues,
            manifest.AbiVersion,
            "Plugin.AbiVersionRequired",
            "Plugin ABI version is required.");
        AddCanonicalStringIssue(issues, manifest.Id, "Plugin.ManifestIdNonCanonical", "Plugin id");
        AddCanonicalStringIssue(issues, manifest.Name, "Plugin.NameNonCanonical", "Plugin name");
        AddCanonicalStringIssue(issues, manifest.Version, "Plugin.VersionNonCanonical", "Plugin version");
        AddCanonicalStringIssue(
            issues,
            manifest.EntryAssembly,
            "Plugin.EntryAssemblyNonCanonical",
            "Plugin entry assembly");
        AddCanonicalStringIssue(issues, manifest.EntryType, "Plugin.EntryTypeNonCanonical", "Plugin entry type");
        AddCanonicalStringIssue(
            issues,
            manifest.ContractVersion,
            "Plugin.ContractVersionNonCanonical",
            "Plugin contract version");
        AddCanonicalStringIssue(
            issues,
            manifest.MinimumPlatformVersion,
            "Plugin.MinimumPlatformVersionNonCanonical",
            "Plugin minimum platform version");
        AddCanonicalStringIssue(
            issues,
            manifest.RuntimeIdentifier,
            "Plugin.RuntimeIdentifierNonCanonical",
            "Plugin runtime identifier");
        AddCanonicalStringIssue(
            issues,
            manifest.AbiVersion,
            "Plugin.AbiVersionNonCanonical",
            "Plugin ABI version");
        ValidateEntryAssemblyPath(issues, manifest.EntryAssembly);

        if (!Enum.IsDefined(manifest.Kind))
        {
            issues.Add(new PluginValidationIssue(
                "Plugin.KindUnsupported",
                $"Plugin kind '{manifest.Kind}' is not supported."));
        }

        AddVersionIssueIfInvalid(
            issues,
            manifest.Version,
            "Plugin.VersionInvalid",
            "Plugin version must be a valid semantic version.");

        ValidateContractVersion(issues, manifest.ContractVersion);
        ValidateMinimumPlatformVersion(issues, manifest.MinimumPlatformVersion);
        ValidateCapabilities(issues, manifest.Capabilities);
        ValidateDeviceCommands(issues, manifest.Capabilities, manifest.DeviceCommands);
        ValidateProcessCommands(issues, manifest.Kind, manifest.Capabilities, manifest.ProcessCommands);

        return new PluginValidationReport(manifest, issues);
    }

    private void ValidateContractVersion(List<PluginValidationIssue> issues, string contractVersion)
    {
        AddRequiredStringIssue(
            issues,
            contractVersion,
            "Plugin.ContractVersionRequired",
            "Plugin contract version is required.");

        if (!TryParseVersion(contractVersion, out var declaredVersion))
        {
            issues.Add(new PluginValidationIssue(
                "Plugin.ContractVersionInvalid",
                "Plugin contract version must be a valid semantic version."));

            return;
        }

        if (!TryParseVersion(_options.ContractVersion, out var supportedVersion)
            || declaredVersion.Major != supportedVersion.Major)
        {
            issues.Add(new PluginValidationIssue(
                "Plugin.ContractVersionUnsupported",
                $"Plugin contract version {contractVersion} is incompatible with supported contract version {_options.ContractVersion}."));
        }
    }

    private void ValidateMinimumPlatformVersion(List<PluginValidationIssue> issues, string minimumPlatformVersion)
    {
        AddRequiredStringIssue(
            issues,
            minimumPlatformVersion,
            "Plugin.MinimumPlatformVersionRequired",
            "Plugin minimum platform version is required.");

        if (!TryParseVersion(minimumPlatformVersion, out var minimumVersion))
        {
            issues.Add(new PluginValidationIssue(
                "Plugin.MinimumPlatformVersionInvalid",
                "Plugin minimum platform version must be a valid semantic version."));

            return;
        }

        if (!TryParseVersion(_options.PlatformVersion, out var platformVersion)
            || minimumVersion > platformVersion)
        {
            issues.Add(new PluginValidationIssue(
                "Plugin.PlatformVersionIncompatible",
                $"Plugin requires platform version {minimumPlatformVersion}, but current platform version is {_options.PlatformVersion}."));
        }
    }

    private static void ValidateCapabilities(
        List<PluginValidationIssue> issues,
        IReadOnlyCollection<string>? capabilities)
    {
        if (capabilities is null || capabilities.Count == 0)
        {
            issues.Add(new PluginValidationIssue(
                "Plugin.CapabilitiesRequired",
                "Plugin must declare at least one capability."));

            return;
        }

        var canonicalCapabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.CapabilityInvalid",
                    "Plugin capability cannot be empty."));

                continue;
            }

            if (!IsCanonicalString(capability))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.CapabilityNonCanonical",
                    $"Plugin capability '{capability}' must not contain surrounding whitespace."));
                continue;
            }

            if (!canonicalCapabilities.Add(capability))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.CapabilityDuplicate",
                    $"Plugin capability '{capability}' is declared more than once."));
            }
        }
    }

    private static void ValidateDeviceCommands(
        List<PluginValidationIssue> issues,
        IReadOnlyCollection<string>? capabilities,
        IReadOnlyCollection<PluginDeviceCommandDefinition>? deviceCommands)
    {
        if (deviceCommands is null || deviceCommands.Count == 0)
        {
            return;
        }

        var declaredCapabilities = new HashSet<string>(
            capabilities?
                .Where(IsCanonicalString)
            ?? [],
            StringComparer.Ordinal);
        var commandIds = new HashSet<string>(StringComparer.Ordinal);
        var commandNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in deviceCommands)
        {
            if (command is null)
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.DeviceCommandInvalid",
                    "Plugin device command cannot be empty."));

                continue;
            }

            AddRequiredStringIssue(
                issues,
                command.Id,
                "Plugin.DeviceCommandIdRequired",
                "Plugin device command id is required.");
            AddRequiredStringIssue(
                issues,
                command.Capability,
                "Plugin.DeviceCommandCapabilityRequired",
                "Plugin device command capability is required.");
            AddRequiredStringIssue(
                issues,
                command.CommandName,
                "Plugin.DeviceCommandNameRequired",
                "Plugin device command name is required.");

            var idIsCanonical = AddCommandStringIssue(
                issues,
                command.Id,
                "Plugin.DeviceCommandIdNonCanonical",
                "Plugin device command id");
            var capabilityIsCanonical = AddCommandStringIssue(
                issues,
                command.Capability,
                "Plugin.DeviceCommandCapabilityNonCanonical",
                "Plugin device command capability");
            var commandNameIsCanonical = AddCommandStringIssue(
                issues,
                command.CommandName,
                "Plugin.DeviceCommandNameNonCanonical",
                "Plugin device command name");

            if (idIsCanonical && !commandIds.Add(command.Id))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.DeviceCommandDuplicate",
                    $"Plugin device command '{command.Id}' is declared more than once."));
            }

            if (capabilityIsCanonical
                && commandNameIsCanonical
                && !commandNames.Add($"{command.Capability}:{command.CommandName}"))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.DeviceCommandNameDuplicate",
                    $"Plugin device command '{command.CommandName}' for capability '{command.Capability}' is declared more than once."));
            }

            if (capabilityIsCanonical
                && !declaredCapabilities.Contains(command.Capability))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.DeviceCommandCapabilityMissing",
                    $"Plugin device command '{command.Id}' references capability '{command.Capability}', but that capability is not declared."));
            }

            if (command.TimeoutMilliseconds <= 0)
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.DeviceCommandTimeoutInvalid",
                    "Plugin device command timeout must be positive."));
            }

            if (command.MaxRetries < 0)
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.DeviceCommandRetriesInvalid",
                    "Plugin device command max retries cannot be negative."));
            }
        }
    }

    private static void ValidateProcessCommands(
        List<PluginValidationIssue> issues,
        PluginKind pluginKind,
        IReadOnlyCollection<string>? capabilities,
        IReadOnlyCollection<PluginProcessCommandDefinition>? processCommands)
    {
        if (processCommands is null || processCommands.Count == 0)
        {
            return;
        }

        if (pluginKind != PluginKind.ProcessNode)
        {
            issues.Add(new PluginValidationIssue(
                "Plugin.ProcessCommandKindInvalid",
                "Plugin process commands require plugin kind ProcessNode."));
        }

        var declaredCapabilities = new HashSet<string>(
            capabilities?
                .Where(IsCanonicalString)
            ?? [],
            StringComparer.Ordinal);
        var commandIds = new HashSet<string>(StringComparer.Ordinal);
        var commandNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in processCommands)
        {
            if (command is null)
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.ProcessCommandInvalid",
                    "Plugin process command cannot be empty."));

                continue;
            }

            AddRequiredStringIssue(
                issues,
                command.Id,
                "Plugin.ProcessCommandIdRequired",
                "Plugin process command id is required.");
            AddRequiredStringIssue(
                issues,
                command.Capability,
                "Plugin.ProcessCommandCapabilityRequired",
                "Plugin process command capability is required.");
            AddRequiredStringIssue(
                issues,
                command.CommandName,
                "Plugin.ProcessCommandNameRequired",
                "Plugin process command name is required.");

            var idIsCanonical = AddCommandStringIssue(
                issues,
                command.Id,
                "Plugin.ProcessCommandIdNonCanonical",
                "Plugin process command id");
            var capabilityIsCanonical = AddCommandStringIssue(
                issues,
                command.Capability,
                "Plugin.ProcessCommandCapabilityNonCanonical",
                "Plugin process command capability");
            var commandNameIsCanonical = AddCommandStringIssue(
                issues,
                command.CommandName,
                "Plugin.ProcessCommandNameNonCanonical",
                "Plugin process command name");

            if (idIsCanonical && !commandIds.Add(command.Id))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.ProcessCommandDuplicate",
                    $"Plugin process command '{command.Id}' is declared more than once."));
            }

            if (capabilityIsCanonical
                && commandNameIsCanonical
                && !commandNames.Add($"{command.Capability}:{command.CommandName}"))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.ProcessCommandNameDuplicate",
                    $"Plugin process command '{command.CommandName}' for capability '{command.Capability}' is declared more than once."));
            }

            if (capabilityIsCanonical
                && !declaredCapabilities.Contains(command.Capability))
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.ProcessCommandCapabilityMissing",
                    $"Plugin process command '{command.Id}' references capability '{command.Capability}', but that capability is not declared."));
            }

            if (command.TimeoutMilliseconds <= 0)
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.ProcessCommandTimeoutInvalid",
                    "Plugin process command timeout must be positive."));
            }

            if (command.MaxRetries < 0)
            {
                issues.Add(new PluginValidationIssue(
                    "Plugin.ProcessCommandRetriesInvalid",
                    "Plugin process command max retries cannot be negative."));
            }
        }
    }

    private static void AddRequiredStringIssue(
        List<PluginValidationIssue> issues,
        string? value,
        string code,
        string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new PluginValidationIssue(code, message));
        }
    }

    private static void AddCanonicalStringIssue(
        List<PluginValidationIssue> issues,
        string? value,
        string code,
        string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(value) && !IsCanonicalString(value))
        {
            issues.Add(new PluginValidationIssue(
                code,
                $"{fieldName} must not contain surrounding whitespace."));
        }
    }

    private static bool AddCommandStringIssue(
        List<PluginValidationIssue> issues,
        string? value,
        string code,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IsCanonicalString(value))
        {
            return true;
        }

        issues.Add(new PluginValidationIssue(
            code,
            $"{fieldName} must not contain surrounding whitespace."));
        return false;
    }

    private static bool IsCanonicalString(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    private static void ValidateEntryAssemblyPath(
        List<PluginValidationIssue> issues,
        string? entryAssembly)
    {
        if (!IsCanonicalString(entryAssembly))
        {
            return;
        }

        var canonicalEntryAssembly = entryAssembly!;
        if (Path.IsPathRooted(canonicalEntryAssembly)
            || canonicalEntryAssembly.Contains('\\', StringComparison.Ordinal)
            || canonicalEntryAssembly.Any(char.IsControl)
            || canonicalEntryAssembly.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            issues.Add(new PluginValidationIssue(
                "Plugin.EntryAssemblyPathNonCanonical",
                "Plugin entry assembly must be a canonical forward-slash package-relative path."));
        }
    }

    private static void AddVersionIssueIfInvalid(
        List<PluginValidationIssue> issues,
        string value,
        string code,
        string message)
    {
        if (!string.IsNullOrWhiteSpace(value) && !TryParseVersion(value, out _))
        {
            issues.Add(new PluginValidationIssue(code, message));
        }
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        return Version.TryParse(value, out version!);
    }
}
