using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Application.Extensions;

public interface IApplicationExtensionPackageService
{
    ValueTask<Result<IReadOnlyCollection<ApplicationExtensionPackageDetails>>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    ValueTask<Result<ApplicationExtensionPackageDetails>> ImportAsync(
        ProjectApplicationWorkspaceScope scope,
        ImportApplicationExtensionPackageRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<Result> RemoveAsync(
        ProjectApplicationWorkspaceScope scope,
        string pluginId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<IReadOnlyCollection<ApplicationExtensionPackageDetails>>> ValidateAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);
}

public sealed record ImportApplicationExtensionPackageRequest(
    string PortableId,
    Stream ZipContent,
    long CompressedSizeBytes);

public sealed record ApplicationExtensionPackageDetails(
    string PortableId,
    ProjectApplicationPluginPackageReference Reference,
    PluginPackageDescriptor Package,
    PluginValidationReport Validation);
