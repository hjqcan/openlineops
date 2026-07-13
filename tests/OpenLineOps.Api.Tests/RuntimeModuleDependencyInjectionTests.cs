using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Runtime.Api.HostedServices;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Infrastructure.Commands;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Runtime.Infrastructure.Scripting;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Api.Tests;

public sealed class RuntimeModuleDependencyInjectionTests
{
    [Fact]
    public void AddOpenLineOpsRuntimeModuleRegistersFormalProductionRunLifecycle()
    {
        var services = new ServiceCollection();

        services.AddOpenLineOpsRuntimeModule(CreateLocalConfiguration());

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        Assert.IsType<InMemoryProductionRunRepository>(
            serviceProvider.GetRequiredService<IProductionRunRepository>());
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IProductionRunRunner));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IProductionRunCoordinator));
        Assert.Same(
            serviceProvider.GetRequiredService<IProductionRunRepository>(),
            serviceProvider.GetRequiredService<IProductionRunExecutionPlanRepository>());
        Assert.IsType<InMemoryResourceLeaseRepository>(
            serviceProvider.GetRequiredService<IResourceLeaseRepository>());
        Assert.IsType<ProductionLineRuntimeStateReader>(
            scope.ServiceProvider.GetRequiredService<IProductionLineRuntimeStateReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProductionRunRecoveryService>());
        Assert.Same(
            serviceProvider.GetRequiredService<IRuntimeSessionRepository>(),
            serviceProvider.GetRequiredService<IRuntimeMonitoringStore>());
        Assert.IsType<InMemoryRuntimeSessionRepository>(
            serviceProvider.GetRequiredService<IRuntimeMonitoringStore>());
        Assert.Same(
            serviceProvider.GetRequiredService<IRuntimeMonitoringService>(),
            serviceProvider.GetRequiredService<IRuntimeMonitoringProjectionInitializer>());
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToArray();
        var monitoringIndex = Array.FindIndex(
            hostedServices,
            service => service is RuntimeMonitoringProjectionStartupHostedService);
        var recoveryIndex = Array.FindIndex(
            hostedServices,
            service => service.GetType().Name == "ProductionRunStartupRecoveryHostedService");
        Assert.True(monitoringIndex >= 0);
        Assert.True(recoveryIndex > monitoringIndex);
        Assert.Contains(
            hostedServices,
            service => service is ProductionRunCoordinatorHostedService);
        Assert.IsType<RuntimeDomainEventPublisher>(
            serviceProvider.GetRequiredService<OpenLineOps.Runtime.Application.Events.IRuntimeDomainEventPublisher>());
        Assert.IsType<DisabledStationSafetyGateway>(
            serviceProvider.GetRequiredService<IStationEmergencyStopGateway>());
    }

    [Fact]
    public void ExplicitLocalExecutionResolvesOnlyTheInProcessDispatcherAndSafetyController()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRuntimeCommandExecutor, SuccessfulRuntimeCommandExecutor>();
        services.AddOpenLineOpsRuntimeModule(CreateLocalConfiguration());
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        Assert.IsType<InProcessStationOperationDispatcher>(
            scope.ServiceProvider.GetRequiredService<IStationOperationDispatcher>());
        Assert.IsType<InProcessStationSafetyController>(
            scope.ServiceProvider.GetRequiredService<IStationSafetyController>());
    }

    [Fact]
    public void AddOpenLineOpsRuntimeModuleUsesSqliteForSessionsAndProductionRunsTogether()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-runtime-di-{Guid.NewGuid():N}.sqlite");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Persistence:Provider"] = RuntimeSessionPersistenceProviders.Sqlite,
                ["OpenLineOps:Runtime:Persistence:ConnectionString"] = $"Data Source={databasePath}",
                ["OpenLineOps:Runtime:Coordination:Provider"] = ProductionCoordinationPersistenceProviders.Sqlite,
                ["OpenLineOps:Runtime:Coordination:SqliteDatabasePath"] = databasePath + ".production",
                ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddOpenLineOpsRuntimeModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        Assert.IsType<SqliteRuntimeSessionRepository>(
            serviceProvider.GetRequiredService<IRuntimeSessionRepository>());
        Assert.Same(
            serviceProvider.GetRequiredService<IRuntimeSessionRepository>(),
            serviceProvider.GetRequiredService<IRuntimeMonitoringStore>());
        Assert.IsType<SqliteProductionRunRepository>(
            serviceProvider.GetRequiredService<IProductionRunRepository>());
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToArray();
        var leaseIndex = Array.FindIndex(
            hostedServices,
            service => service is SqliteRuntimeStoreLeaseHostedService);
        var monitoringIndex = Array.FindIndex(
            hostedServices,
            service => service is RuntimeMonitoringProjectionStartupHostedService);
        var recoveryIndex = Array.FindIndex(
            hostedServices,
            service => service is ProductionRunStartupRecoveryHostedService);
        Assert.True(leaseIndex >= 0);
        Assert.True(monitoringIndex > leaseIndex);
        Assert.True(recoveryIndex > monitoringIndex);
    }

    [Fact]
    public async Task RuntimeMonitoringHostedServiceFailsClosedUntilDurableRebuildCompletes()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(CreateLocalConfiguration());
        using var serviceProvider = services.BuildServiceProvider();
        var monitoring = serviceProvider.GetRequiredService<IRuntimeMonitoringService>();
        var scope = new RuntimeMonitoringScope(
            "project-hosted",
            "application-hosted",
            "snapshot-hosted",
            "topology-hosted");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await monitoring.GetStationStatusesAsync(scope));

        var hosted = Assert.Single(
            serviceProvider.GetServices<IHostedService>(),
            service => service is RuntimeMonitoringProjectionStartupHostedService);
        await hosted.StartAsync(CancellationToken.None);

        Assert.Empty(await monitoring.GetStationStatusesAsync(scope));
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SqliteRuntimeStoreLeaseRejectsASecondHostAndCanBeReacquiredAfterStop()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-runtime-host-lease-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "runtime.sqlite");
        var connectionString = $"Data Source={databasePath};Pooling=False";

        try
        {
            using var first = new SqliteRuntimeStoreExclusiveLease(connectionString);
            using var second = new SqliteRuntimeStoreExclusiveLease(connectionString);

            Assert.Throws<InvalidOperationException>(first.EnsureAcquired);
            await first.AcquireAsync();
            first.EnsureAcquired();
            Assert.True(File.Exists(first.LockFilePath));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await second.AcquireAsync());
            Assert.Contains("already owned by another host", exception.Message, StringComparison.Ordinal);

            first.Release();
            Assert.Throws<InvalidOperationException>(first.EnsureAcquired);
            await second.AcquireAsync();
            second.Release();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=file:runtime?mode=memory&cache=shared")]
    [InlineData("Data Source=runtime;Mode=Memory;Cache=Shared")]
    public void AddOpenLineOpsRuntimeModuleRejectsTransientSqliteConnectionStrings(
        string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Persistence:Provider"] = RuntimeSessionPersistenceProviders.Sqlite,
                ["OpenLineOps:Runtime:Persistence:ConnectionString"] = connectionString
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddOpenLineOpsRuntimeModule(configuration));
        Assert.Contains("file-backed database", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenLineOpsRuntimeModuleDoesNotRegisterAConfigurableCommandExecutor()
    {
        var services = new ServiceCollection();

        services.AddOpenLineOpsRuntimeModule(CreateLocalConfiguration());

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IRuntimeCommandExecutor));
    }

    [Fact]
    public void AddOpenLineOpsRuntimeModuleUsesOnlyProcessIsolatedPythonExecutionByDefault()
    {
        var services = new ServiceCollection();

        services.AddOpenLineOpsRuntimeModule(CreateLocalConfiguration());

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<PythonScriptRuntimeOptions>();
        var executor = serviceProvider.GetRequiredService<IRuntimeScriptExecutor>();

        Assert.Equal(PythonScriptRuntimeExecutionModes.ProcessIsolated, options.ExecutionMode);
        Assert.IsType<ProcessIsolatedPythonScriptRuntimeScriptExecutor>(executor);
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IRuntimeScriptExecutor));
    }

    [Fact]
    public void AddOpenLineOpsRuntimeModuleBindsPythonScriptWorkerSandboxOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Scripting:Python:ExecutionMode"] = "ProcessIsolated",
                ["OpenLineOps:Runtime:Scripting:Python:WorkerFileName"] = "dotnet",
                ["OpenLineOps:Runtime:Scripting:Python:WorkerArguments"] = "\"OpenLineOps.ScriptWorker.dll\"",
                ["OpenLineOps:Runtime:Scripting:Python:WorkerWorkingDirectory"] = "workers",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:RequireLeastPrivilegeExecution"] = "true",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:IsolationMode"] = "Container",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:ContainerRuntimeExecutable"] = "podman",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:ContainerImage"] = "openlineops/script-worker:1.0.0",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:ContainerWorkspacePath"] = "/worker",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:LeastPrivilegeIdentity"] = "10001:10001",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:AdditionalContainerRunArguments:0"] = "--pull=never",
                ["OpenLineOps:Runtime:Persistence:Provider"] = RuntimeSessionPersistenceProviders.InMemory,
                ["OpenLineOps:Runtime:Coordination:Provider"] = ProductionCoordinationPersistenceProviders.InMemory,
                ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<PythonScriptRuntimeOptions>();

        Assert.Equal("ProcessIsolated", options.ExecutionMode);
        Assert.Equal("dotnet", options.WorkerFileName);
        Assert.Equal("\"OpenLineOps.ScriptWorker.dll\"", options.WorkerArguments);
        Assert.Equal("workers", options.WorkerWorkingDirectory);
        Assert.True(options.Sandbox.RequireLeastPrivilegeExecution);
        Assert.Equal(PythonScriptWorkerIsolationModes.Container, options.Sandbox.IsolationMode);
        Assert.Equal("podman", options.Sandbox.ContainerRuntimeExecutable);
        Assert.Equal("openlineops/script-worker:1.0.0", options.Sandbox.ContainerImage);
        Assert.Equal("/worker", options.Sandbox.ContainerWorkspacePath);
        Assert.Equal("10001:10001", options.Sandbox.LeastPrivilegeIdentity);
        Assert.Contains("--pull=never", options.Sandbox.AdditionalContainerRunArguments);
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("Memory")]
    [InlineData("Postgres")]
    [InlineData("PostgreSql")]
    [InlineData("PostgreSQL")]
    [InlineData("postgresql")]
    public void AddOpenLineOpsRuntimeModuleRejectsNonCanonicalPersistenceProviderTokens(string provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Persistence:Provider"] = provider
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsRuntimeModule(configuration));

        Assert.Contains(
            $"Expected exactly '{RuntimeSessionPersistenceProviders.Sqlite}' "
            + $"or '{RuntimeSessionPersistenceProviders.InMemory}'",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Trusted")]
    [InlineData("InProcess")]
    [InlineData("InProcessTrusted")]
    [InlineData("processisolated")]
    [InlineData("Worker")]
    [InlineData("")]
    public void AddOpenLineOpsRuntimeModuleRejectsNonCanonicalPythonExecutionMode(string executionMode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Scripting:Python:ExecutionMode"] = executionMode
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsRuntimeModule(configuration));

        Assert.Contains("Expected exactly", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Docker")]
    [InlineData("Podman")]
    [InlineData("container")]
    [InlineData("RunAs")]
    [InlineData("")]
    public void AddOpenLineOpsRuntimeModuleRejectsNonCanonicalPythonIsolationMode(string isolationMode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:IsolationMode"] = isolationMode
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsRuntimeModule(configuration));

        Assert.Contains("Expected exactly", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    public void AddOpenLineOpsRuntimeModuleRejectsInvalidPythonSandboxBoolean(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:ContainerMountReadOnly"] = value
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsRuntimeModule(configuration));

        Assert.Contains("exactly 'true' or 'false'", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration CreateLocalConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenLineOps:Runtime:Persistence:Provider"] = RuntimeSessionPersistenceProviders.InMemory,
            ["OpenLineOps:Runtime:Coordination:Provider"] = ProductionCoordinationPersistenceProviders.InMemory,
            ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
            ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess"
        })
        .Build();

    private sealed class SuccessfulRuntimeCommandExecutor : IRuntimeCommandExecutor
    {
        public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed());
        }
    }
}
