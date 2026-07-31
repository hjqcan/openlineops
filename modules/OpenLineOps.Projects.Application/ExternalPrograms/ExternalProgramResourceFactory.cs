using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Projects.Application.ExternalPrograms;

public static class ExternalProgramResourceFactory
{
    public static ExternalProgramResource Create(
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramResourceFile> files,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);
        ExternalProgramResourceValidator.ValidateDefinition(request);
        if (updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("External program resource timestamps must use UTC.", nameof(updatedAtUtc));
        }

        if (request.EntryPoint is not null
            && files.All(file => !string.Equals(file.RelativePath, request.EntryPoint, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"External program entry point '{request.EntryPoint}' was not imported into the resource.");
        }

        var resource = new ExternalProgramResource(
            request.ResourceId,
            request.DisplayName,
            request.CapabilityId,
            request.CommandName,
            request.LaunchKind,
            request.EntryPoint,
            request.ProviderKind,
            request.ProviderKey,
            request.ArgumentTemplates.ToArray(),
            request.InputMappings.ToArray(),
            request.ResultMappings.ToArray(),
            request.OutcomeMapping,
            request.PermissionProfile with
            {
                AllowedEnvironmentVariables = request.PermissionProfile.AllowedEnvironmentVariables
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            },
            request.ExecutionLimits,
            files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(),
            string.Empty,
            updatedAtUtc);
        resource = resource with { ContentSha256 = ComputeContentSha256(resource) };
        ExternalProgramResourceValidator.ValidateFrozenResource(resource);
        return resource;
    }

    public static string ComputeContentSha256(ExternalProgramResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var canonical = new StringBuilder();
        Append(canonical, resource.ResourceId);
        Append(canonical, resource.DisplayName);
        Append(canonical, resource.CapabilityId);
        Append(canonical, resource.CommandName);
        Append(canonical, resource.LaunchKind.ToString());
        Append(canonical, resource.EntryPoint ?? string.Empty);
        Append(canonical, resource.ProviderKind ?? string.Empty);
        Append(canonical, resource.ProviderKey ?? string.Empty);
        foreach (var item in resource.ArgumentTemplates)
        {
            Append(canonical, item);
        }

        foreach (var item in resource.InputMappings.OrderBy(item => item.Target, StringComparer.Ordinal))
        {
            Append(canonical, item.Source);
            Append(canonical, item.Target);
        }

        foreach (var item in resource.ResultMappings.OrderBy(item => item.TargetKey, StringComparer.Ordinal))
        {
            Append(canonical, item.SourcePath);
            Append(canonical, item.TargetKey);
            Append(canonical, item.ValueKind.ToString());
        }

        Append(canonical, resource.OutcomeMapping.SourcePath);
        Append(canonical, resource.OutcomeMapping.PassedToken);
        Append(canonical, resource.OutcomeMapping.FailedToken);
        Append(canonical, resource.OutcomeMapping.AbortedToken);
        Append(canonical, resource.PermissionProfile.ProfileName);
        Append(canonical, resource.PermissionProfile.NetworkAccessAllowed.ToString(CultureInfo.InvariantCulture));
        foreach (var name in resource.PermissionProfile.AllowedEnvironmentVariables.Order(StringComparer.Ordinal))
        {
            Append(canonical, name);
        }

        Append(canonical, resource.ExecutionLimits.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumProcessCount.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumWorkingSetBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumCpuTimeMilliseconds.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumStandardOutputBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumStandardErrorBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumArtifactCount.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumArtifactBytes.ToString(CultureInfo.InvariantCulture));
        Append(canonical, resource.ExecutionLimits.MaximumTotalArtifactBytes.ToString(CultureInfo.InvariantCulture));
        foreach (var file in resource.Files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            Append(canonical, file.RelativePath);
            Append(canonical, file.SizeBytes.ToString(CultureInfo.InvariantCulture));
            Append(canonical, file.Sha256);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
}
