namespace OpenLineOps.Projects.Application.ExternalPrograms;

public static class ExternalProgramResourceValidator
{
    private const long MaximumTimeoutMilliseconds = 24L * 60 * 60 * 1000;

    public static void ValidateDefinition(SaveExternalProgramResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExternalProgramResourceContract.PortableId(request.ResourceId, nameof(request.ResourceId));
        Canonical(request.DisplayName, nameof(request.DisplayName), 256);
        Canonical(request.CapabilityId, nameof(request.CapabilityId), 128);
        Canonical(request.CommandName, nameof(request.CommandName), 128);
        ArgumentNullException.ThrowIfNull(request.ArgumentTemplates);
        ArgumentNullException.ThrowIfNull(request.InputMappings);
        ArgumentNullException.ThrowIfNull(request.ResultMappings);
        ArgumentNullException.ThrowIfNull(request.OutcomeMapping);
        ArgumentNullException.ThrowIfNull(request.PermissionProfile);
        ArgumentNullException.ThrowIfNull(request.ExecutionLimits);

        if (request.ArgumentTemplates.Any(static item => item is null)
            || request.InputMappings.Any(static item => item is null)
            || request.ResultMappings.Any(static item => item is null))
        {
            throw new ArgumentException("External program resource collections cannot contain null items.");
        }

        if (request.ArgumentTemplates.Count > 64
            || request.InputMappings.Count > 128
            || request.ResultMappings.Count > 128)
        {
            throw new ArgumentException("External program template or mapping count exceeds the supported limit.");
        }

        var hasEntryPoint = request.EntryPoint is not null;
        var hasProviderKind = request.ProviderKind is not null;
        var hasProvider = request.ProviderKey is not null;
        if (request.LaunchKind == ExternalProgramLaunchKind.ApplicationExecutable)
        {
            if (!hasEntryPoint || hasProviderKind || hasProvider)
            {
                throw new ArgumentException(
                    "ApplicationExecutable resources require exactly one entry point and no provider key.");
            }

            ExternalProgramResourceContract.CanonicalRelativePath(
                request.EntryPoint!,
                nameof(request.EntryPoint),
                ExternalProgramResourceContract.FilesDirectoryName);
        }
        else if (request.LaunchKind == ExternalProgramLaunchKind.Provider)
        {
            if (hasEntryPoint || !hasProviderKind || !hasProvider)
            {
                throw new ArgumentException(
                    "Provider resources require exactly one provider key and no entry point.");
            }

            Canonical(request.ProviderKind!, nameof(request.ProviderKind), 64);
            Canonical(request.ProviderKey!, nameof(request.ProviderKey), 256);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(request), "External program launch kind is unsupported.");
        }

        ValidateMappings(request);
        ValidatePermissionProfile(request.PermissionProfile);
        ValidateLimits(request.ExecutionLimits);

    }

    public static void ValidateFrozenResource(ExternalProgramResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ValidateDefinition(new SaveExternalProgramResourceRequest(
            resource.ResourceId,
            resource.DisplayName,
            resource.CapabilityId,
            resource.CommandName,
            resource.LaunchKind,
            resource.EntryPoint,
            resource.ProviderKind,
            resource.ProviderKey,
            resource.ArgumentTemplates,
            resource.InputMappings,
            resource.ResultMappings,
            resource.OutcomeMapping,
            resource.PermissionProfile,
            resource.ExecutionLimits));
        ArgumentNullException.ThrowIfNull(resource.Files);
        if (!ExternalProgramResourceContract.IsSha256(resource.ContentSha256)
            || resource.UpdatedAtUtc.Offset != TimeSpan.Zero
            || resource.Files.Count == 0 && resource.LaunchKind == ExternalProgramLaunchKind.ApplicationExecutable)
        {
            throw new InvalidDataException("External program frozen resource metadata is invalid.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in resource.Files)
        {
            var path = ExternalProgramResourceContract.CanonicalRelativePath(
                file.RelativePath,
                nameof(file.RelativePath),
                ExternalProgramResourceContract.FilesDirectoryName);
            if (!paths.Add(path)
                || file.SizeBytes < 0
                || !ExternalProgramResourceContract.IsSha256(file.Sha256))
            {
                throw new InvalidDataException("External program file inventory is invalid.");
            }
        }

        const long maximumTotalFileBytes = 2L * 1024 * 1024 * 1024;
        long totalFileBytes = 0;
        if (resource.Files.Count > 256
            || resource.Files.Any(file => file.SizeBytes > 512L * 1024 * 1024))
        {
            throw new InvalidDataException("External program file inventory exceeds the supported limits.");
        }

        foreach (var file in resource.Files)
        {
            if (totalFileBytes > maximumTotalFileBytes - file.SizeBytes)
            {
                throw new InvalidDataException("External program file inventory exceeds the supported limits.");
            }

            totalFileBytes += file.SizeBytes;
        }

        if (resource.EntryPoint is not null && !paths.Contains(resource.EntryPoint))
        {
            throw new InvalidDataException(
                $"External program entry point '{resource.EntryPoint}' is absent from its file inventory.");
        }
    }

    private static void ValidateMappings(SaveExternalProgramResourceRequest request)
    {
        if (request.InputMappings.Count == 0 || request.ResultMappings.Count == 0)
        {
            throw new ArgumentException("External program input and result mappings are required.");
        }

        var inputTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in request.InputMappings)
        {
            if (!ExternalProgramResourceContract.IsSupportedInputSource(mapping.Source))
            {
                throw new ArgumentException($"External program input source '{mapping.Source}' is unsupported.");
            }

            Canonical(mapping.Target, nameof(mapping.Target), 128);
            if (!inputTargets.Add(mapping.Target))
            {
                throw new ArgumentException($"External program input target '{mapping.Target}' is duplicated.");
            }
        }

        if (request.InputMappings.All(mapping => mapping.Source != "$product.identity")
            || request.InputMappings.All(mapping => mapping.Source != "$product.model"))
        {
            throw new ArgumentException(
                "External program inputs must map Production Unit identity and product model.");
        }

        foreach (var template in request.ArgumentTemplates)
        {
            Canonical(template, nameof(request.ArgumentTemplates), 2048);
            if (!ExternalProgramResourceContract.IsSupportedArgumentTemplate(template, inputTargets))
            {
                throw new ArgumentException($"External program argument template '{template}' is unsupported.");
            }
        }

        var resultTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in request.ResultMappings)
        {
            if (!ExternalProgramResourceContract.IsSupportedResultPath(mapping.SourcePath)
                || mapping.SourcePath.Length > 256
                || !Enum.IsDefined(mapping.ValueKind))
            {
                throw new ArgumentException($"External program result path '{mapping.SourcePath}' is unsupported.");
            }

            Canonical(mapping.TargetKey, nameof(mapping.TargetKey), 128);
            if (!resultTargets.Add(mapping.TargetKey))
            {
                throw new ArgumentException($"External program result target '{mapping.TargetKey}' is duplicated.");
            }
        }

        var outcome = request.OutcomeMapping;
        if (!ExternalProgramResourceContract.IsSupportedResultPath(outcome.SourcePath)
            || outcome.SourcePath.Length > 256
            || !ExternalProgramResourceContract.IsCanonical(outcome.PassedToken)
            || !ExternalProgramResourceContract.IsCanonical(outcome.FailedToken)
            || !ExternalProgramResourceContract.IsCanonical(outcome.AbortedToken)
            || outcome.PassedToken.Length > 128
            || outcome.FailedToken.Length > 128
            || outcome.AbortedToken.Length > 128
            || outcome.PassedToken == outcome.FailedToken
            || outcome.PassedToken == outcome.AbortedToken
            || outcome.FailedToken == outcome.AbortedToken)
        {
            throw new ArgumentException("External program outcome mapping is invalid.");
        }
    }

    private static void ValidatePermissionProfile(ExternalProgramPermissionProfile profile)
    {
        if (!string.Equals(profile.ProfileName, "Restricted", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "External program permission profile must be Restricted.");
        }

        ArgumentNullException.ThrowIfNull(profile.AllowedEnvironmentVariables);
        if (profile.AllowedEnvironmentVariables.Count > 64)
        {
            throw new ArgumentException("External program environment variable count exceeds the supported limit.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in profile.AllowedEnvironmentVariables)
        {
            if (!ExternalProgramResourceContract.IsCanonical(name)
                || name.Length > 128
                || name.StartsWith("OPENLINEOPS_", StringComparison.OrdinalIgnoreCase)
                || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')
                || !names.Add(name))
            {
                throw new ArgumentException(
                    $"External program environment variable '{name}' is invalid, reserved, or duplicated.");
            }
        }
    }

    private static void ValidateLimits(ExternalProgramExecutionLimits limits)
    {
        if (limits.TimeoutMilliseconds <= 0
            || limits.TimeoutMilliseconds > MaximumTimeoutMilliseconds
            || limits.MaximumProcessCount <= 0
            || limits.MaximumProcessCount > 64
            || limits.MaximumWorkingSetBytes < 16L * 1024 * 1024
            || limits.MaximumWorkingSetBytes > 16L * 1024 * 1024 * 1024
            || limits.MaximumCpuTimeMilliseconds <= 0
            || limits.MaximumCpuTimeMilliseconds > MaximumTimeoutMilliseconds
            || limits.MaximumStandardOutputBytes <= 0
            || limits.MaximumStandardOutputBytes > 64 * 1024 * 1024
            || limits.MaximumStandardErrorBytes <= 0
            || limits.MaximumStandardErrorBytes > 64 * 1024 * 1024
            || limits.MaximumArtifactCount < 2
            || limits.MaximumArtifactCount > 1024
            || limits.MaximumArtifactBytes <= 0
            || limits.MaximumArtifactBytes > 1024L * 1024 * 1024
            || limits.MaximumTotalArtifactBytes < limits.MaximumArtifactBytes
            || limits.MaximumTotalArtifactBytes > 4L * 1024 * 1024 * 1024)
        {
            throw new ArgumentException("External program execution limits are invalid.");
        }
    }

    private static void Canonical(string value, string parameterName, int maximumLength)
    {
        if (!ExternalProgramResourceContract.IsCanonical(value) || value.Length > maximumLength)
        {
            throw new ArgumentException("External program text must be canonical and non-empty.", parameterName);
        }
    }
}
