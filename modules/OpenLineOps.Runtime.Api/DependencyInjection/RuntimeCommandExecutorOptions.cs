namespace OpenLineOps.Runtime.Api.DependencyInjection;

public sealed class RuntimeCommandExecutorOptions
{
    public const string SectionName = "OpenLineOps:Runtime";

    public string CommandExecutor { get; set; } = RuntimeCommandExecutors.Simulator;
}
