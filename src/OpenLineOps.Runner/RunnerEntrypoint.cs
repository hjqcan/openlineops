using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Devices.Api.DependencyInjection;
using OpenLineOps.Engineering.Api.DependencyInjection;
using OpenLineOps.Plugins.Api.DependencyInjection;
using OpenLineOps.Processes.Api.DependencyInjection;
using OpenLineOps.Production.Api.DependencyInjection;
using OpenLineOps.Projects.Api.DependencyInjection;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Topology.Api.DependencyInjection;
using OpenLineOps.Traceability.Api.DependencyInjection;

namespace OpenLineOps.Runner;

public static class RunnerEntrypoint
{
    public const string UsageText = """
        OpenLineOps Runner - one-shot immutable Production Line execution

        Usage:
          OpenLineOps.Runner run <project-directory-or-.oloproj> --production-unit <identity> --actor <actor-id> [--snapshot <id|active>] [--run-id <guid>] [--lot <value>] [--carrier <value>] [--slot <value>] [--fixture <value>] [--device <value>]

        Notes:
          --snapshot defaults to "active".
          --run-id defaults to a newly generated Production Run id; provide it to make retries idempotent.
          Runtime configuration uses appsettings.json, appsettings.<environment>.json, and environment variables.
          Every Operation executes through its Station Agent from the signed immutable package.
          Concurrent products coordinate through Station, Slot, Fixture, and Device fencing leases.
          Interrupted non-idempotent hardware work enters RecoveryRequired and is never replayed automatically.

        Stable exit codes:
          0   completed successfully
          2   command-line usage error
          3   project manifest could not be opened
          4   requested/active snapshot could not be selected
          5   selected snapshot has no immutable release
          6   immutable release or Production Run start was rejected
          7   Production Run failed
          8   canceled
          70  unexpected internal/configuration error
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        string currentDirectory,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentNullException.ThrowIfNull(output);

        var parseResult = RunnerCommandLineParser.Parse(arguments);
        if (parseResult.Status == RunnerParseStatus.Help)
        {
            await output.WriteLineAsync(UsageText).ConfigureAwait(false);
            return RunnerExitCodes.Success;
        }

        if (parseResult.Status == RunnerParseStatus.Error)
        {
            await RunnerJsonOutputWriter
                .WriteAsync(
                    output,
                    RunnerJsonOutput.Failed(
                        RunnerExitCodes.UsageError,
                        target: string.Empty,
                        "Runner.UsageError",
                        parseResult.ErrorMessage!))
                .ConfigureAwait(false);
            return RunnerExitCodes.UsageError;
        }

        try
        {
            var configuration = BuildConfiguration(parseResult.Options!, currentDirectory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddOpenLineOpsProjectsModule();
            services.AddOpenLineOpsTopologyModule();
            services.AddOpenLineOpsRuntimeModule(configuration);
            services.AddOpenLineOpsTraceabilityModule(configuration);
            services.AddOpenLineOpsProcessesModule();
            services.AddOpenLineOpsProductionModule();
            services.AddOpenLineOpsEngineeringModule();
            services.AddOpenLineOpsPluginsModule(configuration);
            services.AddOpenLineOpsDevicesModule(configuration);
            services.AddScoped<RunnerCommand>();

            await using var serviceProvider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true
                });
            await using var scope = serviceProvider.CreateAsyncScope();
            var command = scope.ServiceProvider.GetRequiredService<RunnerCommand>();

            return await command
                .RunAsync(parseResult.Options!, currentDirectory, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RunnerJsonOutputWriter
                .WriteAsync(
                    output,
                    RunnerJsonOutput.Failed(
                        RunnerExitCodes.Canceled,
                        parseResult.Options!.ProjectTarget,
                        "Runner.Canceled",
                        "Runner execution was canceled."))
                .ConfigureAwait(false);
            return RunnerExitCodes.Canceled;
        }
        catch (Exception exception)
        {
            await RunnerJsonOutputWriter
                .WriteAsync(
                    output,
                    RunnerJsonOutput.Failed(
                        RunnerExitCodes.InternalError,
                        parseResult.Options!.ProjectTarget,
                        "Runner.InternalError",
                        exception.Message))
                .ConfigureAwait(false);
            return RunnerExitCodes.InternalError;
        }
    }

    private static IConfigurationRoot BuildConfiguration(
        RunnerRunOptions options,
        string currentDirectory)
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        var baseDirectory = AppContext.BaseDirectory;
        string projectDirectory;
        try
        {
            projectDirectory = ProjectExecutionDataDirectory.ProjectDirectoryFromTarget(
                options.ProjectTarget,
                currentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            // RunnerCommand owns the machine-readable project-target error. This
            // provisional configuration scope is never used to open a project.
            projectDirectory = currentDirectory;
        }
        var runnerDataDirectory = ProjectExecutionDataDirectory.ForProjectDirectory(projectDirectory);
        var builder = new ConfigurationBuilder();

        AddJsonFiles(builder, baseDirectory, environmentName);
        if (!string.Equals(
                Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            AddJsonFiles(builder, projectDirectory, environmentName);
        }

        return builder
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Traceability:Persistence:Provider"] = "Sqlite",
                ["OpenLineOps:Traceability:Persistence:ConnectionString"] = null,
                ["OpenLineOps:Traceability:Persistence:DatabasePath"] = Path.Combine(
                    runnerDataDirectory,
                    "openlineops-traceability.sqlite"),
                ["OpenLineOps:Traceability:ArtifactStorage:Provider"] = "FileSystem",
                ["OpenLineOps:Traceability:ArtifactStorage:RootPath"] = Path.Combine(
                    runnerDataDirectory,
                    "trace-artifacts")
            })
            .Build();
    }

    private static void AddJsonFiles(
        IConfigurationBuilder builder,
        string directory,
        string environmentName)
    {
        builder.AddJsonFile(
            Path.Combine(directory, "appsettings.json"),
            optional: true,
            reloadOnChange: false);
        builder.AddJsonFile(
            Path.Combine(directory, $"appsettings.{environmentName}.json"),
            optional: true,
            reloadOnChange: false);
    }
}
