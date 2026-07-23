using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Projects.Tests;

public sealed class ExternalProgramResourceServiceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DefinitionTrialBuildsCanonicalProviderResourceWithoutPersistence()
    {
        var repository = new RejectingRepository();
        var executor = new RecordingTrialExecutor();
        var service = CreateService(repository, executor);

        var result = await service.TrialDefinitionAsync(
            "project",
            "application",
            ProviderDefinition(),
            new ExternalProgramProtocolTrialRequest(new Dictionary<string, ExternalProgramTrialInputValue>
            {
                ["identity"] = new(ExternalProgramTrialInputKind.Text, "board-001")
            }));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, repository.CallCount);
        var resource = Assert.IsType<ExternalProgramResource>(executor.Resource);
        Assert.Equal("extension-trial-loopback", resource.ResourceId);
        Assert.Empty(resource.Files);
        Assert.Equal(Timestamp, resource.UpdatedAtUtc);
        Assert.True(ExternalProgramResourceContract.IsSha256(resource.ContentSha256));
        Assert.Equal(
            ExternalProgramResourceFactory.ComputeContentSha256(resource),
            resource.ContentSha256);
        Assert.Equal("board-001", executor.Request!.Inputs["identity"].CanonicalValue);
    }

    [Fact]
    public async Task DefinitionTrialRejectsExecutableDefinitionWithoutRepositoryOrExecutorUse()
    {
        var repository = new RejectingRepository();
        var executor = new RecordingTrialExecutor();
        var service = CreateService(repository, executor);
        var executable = ProviderDefinition() with
        {
            LaunchKind = ExternalProgramLaunchKind.ApplicationExecutable,
            EntryPoint = "files/vendor.exe",
            ProviderKind = null,
            ProviderKey = null
        };

        var result = await service.TrialDefinitionAsync(
            "project",
            "application",
            executable,
            new ExternalProgramProtocolTrialRequest(
                new Dictionary<string, ExternalProgramTrialInputValue>()));

        Assert.True(result.IsFailure);
        Assert.Equal(
            "Validation.Projects.ExternalProgramDefinitionTrialProviderRequired",
            result.Error.Code);
        Assert.Equal(0, repository.CallCount);
        Assert.Null(executor.Resource);
    }

    private static ExternalProgramResourceService CreateService(
        RejectingRepository repository,
        RecordingTrialExecutor executor)
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project",
            "application",
            Path.GetTempPath(),
            "applications/application/application.oloapp");
        return new ExternalProgramResourceService(
            new ScopeResolver(scope),
            repository,
            executor,
            new UnusedUsageInspector(),
            new FixedClock());
    }

    private static SaveExternalProgramResourceRequest ProviderDefinition() => new(
        "extension-trial-loopback",
        "Loopback protocol trial",
        "device.loopback",
        "Echo",
        ExternalProgramLaunchKind.Provider,
        EntryPoint: null,
        ProviderKind: "PluginCommand",
        ProviderKey: "openlineops.samples.loopback-device",
        ArgumentTemplates: [],
        InputMappings: [
            new ExternalProgramInputMapping("$product.identity", "identity"),
            new ExternalProgramInputMapping("$product.model", "model")
        ],
        ResultMappings: [
            new ExternalProgramResultMapping(
                "$.deviceInstanceId",
                "extension.trial.result",
                ProductionContextValueKind.Text)
        ],
        new ExternalProgramOutcomeMapping(
            "$.deviceInstanceId",
            "loopback-device-01",
            "Failed",
            "Aborted"),
        new ExternalProgramPermissionProfile("Restricted", false, []),
        new ExternalProgramExecutionLimits(
            10_000,
            1,
            128 * 1024 * 1024,
            10_000,
            1_024 * 1_024,
            1_024 * 1_024,
            2,
            1_024 * 1_024,
            2 * 1_024 * 1_024));

    private sealed class ScopeResolver(ProjectApplicationWorkspaceScope scope)
        : IProjectApplicationWorkspaceScopeResolver
    {
        public ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
            string projectId,
            string applicationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectApplicationWorkspaceScope?>(
                projectId == scope.ProjectId && applicationId == scope.ApplicationId ? scope : null);
    }

    private sealed class RejectingRepository : IExternalProgramResourceRepository
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyCollection<ExternalProgramResource>> ListAsync(
            ProjectApplicationWorkspaceScope scope,
            CancellationToken cancellationToken = default) => Reject<IReadOnlyCollection<ExternalProgramResource>>();

        public ValueTask<ExternalProgramResource?> GetAsync(
            ProjectApplicationWorkspaceScope scope,
            string resourceId,
            CancellationToken cancellationToken = default) => Reject<ExternalProgramResource?>();

        public ValueTask<ExternalProgramResource> SaveDefinitionAsync(
            ProjectApplicationWorkspaceScope scope,
            SaveExternalProgramResourceRequest request,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken = default) => Reject<ExternalProgramResource>();

        public ValueTask<ExternalProgramResource> ImportDirectoryAsync(
            ProjectApplicationWorkspaceScope scope,
            SaveExternalProgramResourceRequest request,
            IReadOnlyCollection<ExternalProgramFileUpload> files,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken = default) => Reject<ExternalProgramResource>();

        public ValueTask DeleteAsync(
            ProjectApplicationWorkspaceScope scope,
            string resourceId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("The unsaved definition trial must not call the repository.");
        }

        private ValueTask<T> Reject<T>()
        {
            CallCount++;
            throw new InvalidOperationException("The unsaved definition trial must not call the repository.");
        }
    }

    private sealed class RecordingTrialExecutor : IExternalProgramTrialExecutor
    {
        public ExternalProgramResource? Resource { get; private set; }

        public ExternalProgramProtocolTrialRequest? Request { get; private set; }

        public ValueTask<Result<ExternalProgramProtocolTrialResult>> ExecuteAsync(
            ProjectApplicationWorkspaceScope scope,
            ExternalProgramResource resource,
            ExternalProgramProtocolTrialRequest request,
            CancellationToken cancellationToken = default)
        {
            Resource = resource;
            Request = request;
            return ValueTask.FromResult(Result.Success(new ExternalProgramProtocolTrialResult(
                resource.ResourceId,
                resource.LaunchKind.ToString(),
                resource.ContentSha256,
                "Completed",
                "Passed",
                "{}",
                null,
                [])));
        }
    }

    private sealed class UnusedUsageInspector : IExternalProgramResourceUsageInspector
    {
        public ValueTask<bool> IsReferencedAsync(
            ProjectApplicationWorkspaceScope scope,
            string resourceId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The unsaved definition trial must not inspect persisted usages.");
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }
}
