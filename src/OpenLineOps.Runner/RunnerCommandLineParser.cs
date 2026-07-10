namespace OpenLineOps.Runner;

public enum RunnerParseStatus
{
    Run,
    Help,
    Error
}

public sealed record RunnerRunOptions(
    string ProjectTarget,
    string Snapshot,
    string? SerialNumber,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string? ActorId);

public sealed record RunnerParseResult(
    RunnerParseStatus Status,
    RunnerRunOptions? Options,
    string? ErrorMessage);

public static class RunnerCommandLineParser
{
    private static readonly HashSet<string> ValueOptions = new(
        ["snapshot", "serial", "batch", "fixture", "device", "actor"],
        StringComparer.OrdinalIgnoreCase);

    public static RunnerParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 1 && IsHelpCommand(arguments[0]))
        {
            return Help();
        }

        if (arguments.Any(IsHelpOption))
        {
            return Help();
        }

        if (arguments.Count == 0)
        {
            return Error("A command is required.");
        }

        if (!string.Equals(arguments[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            return Error($"Unknown command '{arguments[0]}'.");
        }

        string? projectTarget = null;
        var optionValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parseOptions = true;

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (parseOptions && string.Equals(argument, "--", StringComparison.Ordinal))
            {
                parseOptions = false;
                continue;
            }

            if (parseOptions && argument.StartsWith("--", StringComparison.Ordinal))
            {
                var separatorIndex = argument.IndexOf('=');
                var optionName = separatorIndex < 0
                    ? argument[2..]
                    : argument[2..separatorIndex];
                if (!ValueOptions.Contains(optionName))
                {
                    return Error($"Unknown option '--{optionName}'.");
                }

                if (optionValues.ContainsKey(optionName))
                {
                    return Error($"Option '--{optionName}' may only be specified once.");
                }

                string optionValue;
                if (separatorIndex >= 0)
                {
                    optionValue = argument[(separatorIndex + 1)..];
                }
                else
                {
                    if (index + 1 >= arguments.Count
                        || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Error($"Missing value for '--{optionName}'.");
                    }

                    optionValue = arguments[++index];
                }

                if (string.IsNullOrWhiteSpace(optionValue))
                {
                    return Error($"Value for '--{optionName}' cannot be empty.");
                }

                optionValues.Add(optionName, optionValue);
                continue;
            }

            if (projectTarget is not null)
            {
                return Error($"Unexpected argument '{argument}'.");
            }

            if (string.IsNullOrWhiteSpace(argument))
            {
                return Error("Project target cannot be empty.");
            }

            projectTarget = argument;
        }

        if (projectTarget is null)
        {
            return Error("A project directory or .oloproj path is required.");
        }

        return new RunnerParseResult(
            RunnerParseStatus.Run,
            new RunnerRunOptions(
                projectTarget,
                GetOption(optionValues, "snapshot") ?? "active",
                GetOption(optionValues, "serial"),
                GetOption(optionValues, "batch"),
                GetOption(optionValues, "fixture"),
                GetOption(optionValues, "device"),
                GetOption(optionValues, "actor")),
            ErrorMessage: null);
    }

    private static string? GetOption(Dictionary<string, string> values, string name)
    {
        return values.TryGetValue(name, out var value) ? value : null;
    }

    private static bool IsHelpCommand(string argument)
    {
        return string.Equals(argument, "help", StringComparison.OrdinalIgnoreCase)
            || IsHelpOption(argument);
    }

    private static bool IsHelpOption(string argument)
    {
        return string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase);
    }

    private static RunnerParseResult Help()
    {
        return new RunnerParseResult(RunnerParseStatus.Help, Options: null, ErrorMessage: null);
    }

    private static RunnerParseResult Error(string message)
    {
        return new RunnerParseResult(RunnerParseStatus.Error, Options: null, message);
    }
}
