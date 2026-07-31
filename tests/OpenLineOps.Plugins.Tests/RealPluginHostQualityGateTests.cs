using System.Collections.Concurrent;
using System.Text.Json;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Trials;
using OpenLineOps.Plugins.Application.Validation;
using OpenLineOps.Plugins.Infrastructure.Discovery;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;
using OpenLineOps.Plugins.Infrastructure.Trials;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Tests;

public sealed class RealPluginHostQualityGateTests
{
    [Theory]
    [InlineData("Passed")]
    [InlineData("Failed")]
    public async Task QualityGateRunsInRealPluginHostAndKeepsProductJudgementSeparate(
        string resultJudgement)
    {
        using var workspace = await RealHostWorkspace.CreateAsync();
        var eventSink = new CapturingEventSink();
        var hostOptions = workspace.CreateHostOptions();
        var providerRunner = new ExternalProcessPluginProviderTrialRunner(
            new FileSystemPluginPackageCatalog(workspace.ManifestStore),
            new PluginManifestValidator(),
            new SystemDiagnosticsExternalPluginProcessRunner(hostOptions, eventSink),
            new ExternalPluginProcessRegistry(),
            hostOptions,
            eventSink);

        var result = await providerRunner.ExecuteAsync(
            workspace.Scope,
            new PluginProviderTrialRequest(
                ExternalProcessPluginProviderTrialRunner.ProcessProviderKind,
                "quality-gate.evaluate",
                "production.quality-gate",
                "Evaluate",
                JsonSerializer.Serialize(new
                {
                    resultJudgement,
                    detail = "real-host-integration"
                }),
                5000));

        Assert.Equal(PluginProviderTrialOutcome.Completed, result.Outcome);
        Assert.Null(result.FailureReason);
        using var payload = JsonDocument.Parse(result.ResultPayload!);
        Assert.Equal("Completed", payload.RootElement.GetProperty("executionStatus").GetString());
        Assert.Equal(resultJudgement, payload.RootElement.GetProperty("resultJudgement").GetString());
        Assert.Contains(eventSink.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.Started
            && processEvent.ProjectId == workspace.Scope.ProjectId
            && processEvent.ApplicationId == workspace.Scope.ApplicationId
            && processEvent.PluginId == "openlineops.samples.quality-gate"
            && processEvent.PackageContentSha256 == workspace.PackageContentSha256);
        Assert.Contains(eventSink.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.ProcessExited
            && processEvent.ProjectId == workspace.Scope.ProjectId
            && processEvent.ApplicationId == workspace.Scope.ApplicationId
            && processEvent.PluginId == "openlineops.samples.quality-gate"
            && processEvent.PackageContentSha256 == workspace.PackageContentSha256
            && processEvent.Detail == "0");
        Assert.DoesNotContain(eventSink.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.ProcessKilled);
    }

    [Fact]
    public async Task InvalidQualityGatePayloadIsAnExecutionFailureFromRealPluginHost()
    {
        using var workspace = await RealHostWorkspace.CreateAsync();
        var eventSink = new CapturingEventSink();
        var hostOptions = workspace.CreateHostOptions();
        var providerRunner = new ExternalProcessPluginProviderTrialRunner(
            new FileSystemPluginPackageCatalog(workspace.ManifestStore),
            new PluginManifestValidator(),
            new SystemDiagnosticsExternalPluginProcessRunner(hostOptions, eventSink),
            new ExternalPluginProcessRegistry(),
            hostOptions,
            eventSink);

        var result = await providerRunner.ExecuteAsync(
            workspace.Scope,
            new PluginProviderTrialRequest(
                ExternalProcessPluginProviderTrialRunner.ProcessProviderKind,
                "openlineops.samples.quality-gate",
                "production.quality-gate",
                "Evaluate",
                "not-json",
                5000));

        Assert.Equal(PluginProviderTrialOutcome.Failed, result.Outcome);
        Assert.Null(result.ResultPayload);
        Assert.Equal("Quality-gate input must be valid JSON.", result.FailureReason);
        Assert.Contains(eventSink.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.ProcessExited
            && processEvent.ProjectId == workspace.Scope.ProjectId
            && processEvent.ApplicationId == workspace.Scope.ApplicationId
            && processEvent.PluginId == "openlineops.samples.quality-gate"
            && processEvent.PackageContentSha256 == workspace.PackageContentSha256
            && processEvent.Detail == "0");
        Assert.DoesNotContain(eventSink.Events, processEvent =>
            processEvent.Kind == ExternalPluginProcessEventKind.ProcessKilled);
    }

    private sealed class CapturingEventSink : IExternalPluginProcessEventSink
    {
        private readonly ConcurrentQueue<ExternalPluginProcessEvent> _events = [];

        public IReadOnlyCollection<ExternalPluginProcessEvent> Events => _events.ToArray();

        public void Record(ExternalPluginProcessEvent processEvent) => _events.Enqueue(processEvent);
    }

    private sealed class RealHostWorkspace : IDisposable
    {
        private RealHostWorkspace(
            string root,
            string hostExecutablePath,
            ProjectApplicationWorkspaceScope scope,
            string packageContentSha256)
        {
            Root = root;
            HostExecutablePath = hostExecutablePath;
            Scope = scope;
            PackageContentSha256 = packageContentSha256;
        }

        public string Root { get; }

        public string HostExecutablePath { get; }

        public ProjectApplicationWorkspaceScope Scope { get; }

        public string PackageContentSha256 { get; }

        public FileSystemAutomationProjectManifestStore ManifestStore { get; } = new();

        public static async Task<RealHostWorkspace> CreateAsync()
        {
            var repositoryRoot = FindRepositoryRoot();
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Test build configuration could not be resolved.");
            var hostExecutablePath = Path.Combine(
                repositoryRoot,
                "src",
                "OpenLineOps.PluginHost",
                "bin",
                configuration,
                "net10.0",
                OperatingSystem.IsWindows() ? "OpenLineOps.PluginHost.exe" : "OpenLineOps.PluginHost");
            var sampleRoot = Path.Combine(
                repositoryRoot,
                "samples",
                "plugins",
                "OpenLineOps.SamplePlugins.QualityGate");
            var sampleAssemblyPath = Path.Combine(
                sampleRoot,
                "bin",
                configuration,
                "net10.0",
                "OpenLineOps.SamplePlugins.QualityGate.dll");
            if (!File.Exists(hostExecutablePath))
            {
                throw new FileNotFoundException(
                    "The real OpenLineOps.PluginHost executable was not built for the test configuration.",
                    hostExecutablePath);
            }

            if (!File.Exists(sampleAssemblyPath))
            {
                throw new FileNotFoundException(
                    "The Quality Gate sample plugin was not built for the test configuration.",
                    sampleAssemblyPath);
            }

            var root = Path.Combine(
                Path.GetTempPath(),
                "openlineops-real-plugin-host",
                Guid.NewGuid().ToString("N"));
            var scope = new ProjectApplicationWorkspaceScope(
                "project.real-host",
                "application.real-host",
                root,
                "applications/application.real-host/application.real-host.oloapp");
            var packagePath = Path.Combine(scope.PluginsRootPath, "quality-gate");
            Directory.CreateDirectory(packagePath);
            File.Copy(
                Path.Combine(sampleRoot, "manifest.json"),
                Path.Combine(packagePath, "manifest.json"));
            File.Copy(
                sampleAssemblyPath,
                Path.Combine(packagePath, "OpenLineOps.SamplePlugins.QualityGate.dll"));
            await File.WriteAllTextAsync(
                scope.ApplicationProjectFilePath,
                """
                {
                  "schemaVersion": "openlineops.automation-application",
                  "formatVersion": 1,
                  "kind": "OpenLineOps.AutomationApplication",
                  "product": "OpenLineOps",
                  "applicationId": "application.real-host",
                  "displayName": "Real Plugin Host",
                  "resourceLayoutVersion": 1,
                  "topologyId": null,
                  "processDefinitionIds": [],
                  "pluginPackageReferences": []
                }
                """);

            var descriptor = await FileSystemPluginPackageInspector.InspectAsync(packagePath);
            var workspace = new RealHostWorkspace(
                root,
                hostExecutablePath,
                scope,
                descriptor.PackageContentSha256);
            await workspace.ManifestStore.ReplaceAsync(
                scope,
                [
                    new ProjectApplicationPluginPackageReference(
                        descriptor.Manifest.Id,
                        descriptor.Manifest.Version,
                        ProjectApplicationPluginPackageReferenceContract.ManifestPath("quality-gate"),
                        descriptor.PackageContentSha256)
                ]);
            return workspace;
        }

        public ExternalProcessPluginHostOptions CreateHostOptions() => new()
        {
            ExecutablePath = HostExecutablePath,
            ArgumentsTemplate =
                "--openlineops-plugin-host --manifest \"{ManifestPath}\" --entry \"{EntryAssemblyPath}\" --type \"{EntryType}\"",
            StartupProbeDelay = TimeSpan.FromMilliseconds(100),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            Sandbox = new ExternalPluginSandboxOptions
            {
                MaxCommandTimeout = TimeSpan.FromSeconds(5),
                TerminateProcessOnCommandTimeout = true
            }
        };

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "OpenLineOps.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("OpenLineOps repository root was not found.");
        }
    }
}
