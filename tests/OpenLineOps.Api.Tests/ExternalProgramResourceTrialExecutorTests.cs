using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Devices.Api.ExternalPrograms;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;
using OpenLineOps.Plugins.Application.Trials;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Api.Tests;

public sealed class ExternalProgramResourceTrialExecutorTests
{
    [Fact]
    public async Task DisabledExecutableTrialRejectsBeforeReadingContentOrInvokingHost()
    {
        var host = new RecordingExternalProgramHost();
        var provider = new RecordingProviderTrialRunner();
        var executor = new ExternalProgramResourceTrialExecutor(
            host,
            provider,
            new ExternalProgramProtocolTrialOptions(),
            PermissiveHostOptions());

        var result = await executor.ExecuteAsync(
            Scope(),
            Resource(ExternalProgramLaunchKind.ApplicationExecutable),
            TrialRequest());

        Assert.True(result.IsFailure);
        Assert.Equal(
            "Conflict.Projects.ApplicationExecutableProtocolTrialDisabled",
            result.Error.Code);
        Assert.Equal(0, host.InvocationCount);
        Assert.Equal(0, provider.InvocationCount);
    }

    [Fact]
    public async Task DisabledExecutablePolicyDoesNotDisableProviderProtocolTrial()
    {
        var host = new RecordingExternalProgramHost();
        var provider = new RecordingProviderTrialRunner();
        var executor = new ExternalProgramResourceTrialExecutor(
            host,
            provider,
            new ExternalProgramProtocolTrialOptions(),
            PermissiveHostOptions());

        var result = await executor.ExecuteAsync(
            Scope(),
            Resource(ExternalProgramLaunchKind.Provider),
            TrialRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal("Completed", result.Value.ExecutionStatus);
        Assert.Equal("Passed", result.Value.Judgement);
        Assert.Equal(0, host.InvocationCount);
        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public void RestrictedExecutableTrialCannotBeConstructedWithPermissiveHost()
    {
        var trialOptions = new ExternalProgramProtocolTrialOptions
        {
            ApplicationExecutablePolicy =
                ApplicationExecutableProtocolTrialPolicies.RestrictedHost
        };

        if (!OperatingSystem.IsWindows())
        {
            _ = Assert.Throws<PlatformNotSupportedException>(() =>
                new ExternalProgramResourceTrialExecutor(
                    new RecordingExternalProgramHost(),
                    new RecordingProviderTrialRunner(),
                    trialOptions,
                    PermissiveHostOptions()));
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ExternalProgramResourceTrialExecutor(
                new RecordingExternalProgramHost(),
                new RecordingProviderTrialRunner(),
                trialOptions,
                PermissiveHostOptions()));

        Assert.Contains(
            "exact restricted service identity, immutable content protection, and AppContainer isolation",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static ProjectApplicationWorkspaceScope Scope() => new(
        "project.main",
        "application.main",
        Path.Combine(Path.GetTempPath(), "openlineops-disabled-protocol-trial"),
        "applications/main/main.oloapp");

    private static ExternalProgramResource Resource(ExternalProgramLaunchKind launchKind) => new(
        "program.vendor",
        "Vendor program",
        "device.vendor",
        "Run",
        launchKind,
        launchKind == ExternalProgramLaunchKind.ApplicationExecutable
            ? "files/vendor.exe"
            : null,
        launchKind == ExternalProgramLaunchKind.Provider ? "PluginCommand" : null,
        launchKind == ExternalProgramLaunchKind.Provider ? "plugin.vendor" : null,
        [],
        [
            new ExternalProgramInputMapping("$product.identity", "identity"),
            new ExternalProgramInputMapping("$product.model", "model")
        ],
        [
            new ExternalProgramResultMapping(
                "$.outcome",
                "trial.outcome",
                ProductionContextValueKind.Text)
        ],
        new ExternalProgramOutcomeMapping("$.outcome", "Passed", "Failed", "Aborted"),
        new ExternalProgramPermissionProfile("Restricted", false, []),
        new ExternalProgramExecutionLimits(
            30_000,
            1,
            128L * 1024 * 1024,
            30_000,
            1024 * 1024,
            1024 * 1024,
            2,
            1024 * 1024,
            2L * 1024 * 1024),
        launchKind == ExternalProgramLaunchKind.ApplicationExecutable
            ? [new ExternalProgramResourceFile("files/vendor.exe", 1, new string('a', 64))]
            : [],
        new string('b', 64),
        DateTimeOffset.UnixEpoch);

    private static ExternalProgramProtocolTrialRequest TrialRequest() => new(
        new Dictionary<string, ExternalProgramTrialInputValue>
        {
            ["identity"] = new(ExternalProgramTrialInputKind.Text, "board-001"),
            ["model"] = new(ExternalProgramTrialInputKind.Text, "sample-board")
        });

    private static ExternalProgramHostOptions PermissiveHostOptions() => new()
    {
        RequireRestrictedHostIdentity = false,
        RequireImmutableContentProtection = false,
        RequireAppContainerIsolation = false
    };

    private sealed class RecordingExternalProgramHost : IExternalProgramHost
    {
        public int InvocationCount { get; private set; }

        public ValueTask<ExternalProgramExecutionResult> ExecuteAsync(
            ExternalProgramExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            throw new InvalidOperationException("The executable host must not be invoked.");
        }
    }

    private sealed class RecordingProviderTrialRunner : IPluginProviderTrialRunner
    {
        public int InvocationCount { get; private set; }

        public ValueTask<PluginProviderTrialResult> ExecuteAsync(
            ProjectApplicationWorkspaceScope scope,
            PluginProviderTrialRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.FromResult(new PluginProviderTrialResult(
                PluginProviderTrialOutcome.Completed,
                "{\"outcome\":\"Passed\"}",
                null));
        }
    }
}
