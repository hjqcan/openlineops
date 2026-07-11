namespace OpenLineOps.Runtime.Infrastructure.Execution;

public sealed class StationExecutionOptions
{
    public const string SectionName = "OpenLineOps:Runtime:StationExecution";

    public string Provider { get; set; } = StationExecutionProviders.Agent;
}

public static class StationExecutionProviders
{
    public const string Agent = "Agent";
    public const string InProcess = "InProcess";

    public static StationExecutionProvider Parse(string? value) => value switch
    {
        Agent => StationExecutionProvider.Agent,
        InProcess => StationExecutionProvider.InProcess,
        _ => throw new InvalidOperationException(
            $"Unsupported Station execution provider '{value}'. Expected exactly "
            + $"'{Agent}' or '{InProcess}'.")
    };
}

public enum StationExecutionProvider
{
    Agent,
    InProcess
}
