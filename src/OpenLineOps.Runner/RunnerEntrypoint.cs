using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Devices.Api.DependencyInjection;
using OpenLineOps.Engineering.Api.DependencyInjection;
using OpenLineOps.Plugins.Api.DependencyInjection;
using OpenLineOps.Processes.Api.DependencyInjection;
using OpenLineOps.Projects.Api.DependencyInjection;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Topology.Api.DependencyInjection;

namespace OpenLineOps.Runner;

public static class RunnerEntrypoint
{
    public const string UsageText = """
        OpenLineOps Runner - one-shot immutable Project Snapshot execution

        Usage:
          OpenLineOps.Runner run <project-directory-or-.oloproj> [--snapshot <id|active>] [--serial <value>] [--batch <value>] [--fixture <value>] [--device <value>] [--actor <value>]

        Notes:
          --snapshot defaults to "active".
          Runtime configuration uses appsettings.json, appsettings.<environment>.json, and environment variables.
          Only snapshots with an immutable release descriptor can run; editable application source is never a fallback.

        Stable exit codes:
          0   completed successfully
          2   command-line usage error
          3   project manifest could not be opened
          4   requested/active snapshot could not be selected
          5   selected snapshot has no immutable release
          6   immutable release or runtime start was rejected
          7   runtime session reached a non-completed terminal state
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
            var configuration = BuildConfiguration(currentDirectory);
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddOpenLineOpsProjectsModule();
            services.AddOpenLineOpsTopologyModule();
            services.AddOpenLineOpsRuntimeModule(configuration);
            services.AddOpenLineOpsProcessesModule(configuration);
            services.AddOpenLineOpsEngineeringModule(configuration);
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

    private static IConfigurationRoot BuildConfiguration(string currentDirectory)
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        var baseDirectory = AppContext.BaseDirectory;
        var builder = new ConfigurationBuilder();

        AddJsonFiles(builder, baseDirectory, environmentName);
        if (!string.Equals(
                Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            AddJsonFiles(builder, currentDirectory, environmentName);
        }

        return builder
            .AddEnvironmentVariables()
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
