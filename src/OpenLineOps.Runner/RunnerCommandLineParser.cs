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
    Guid ProductionRunId,
    Guid ProductionUnitId,
    string ProductionUnitIdentityValue,
    string ActorId);

public sealed record RunnerParseResult(
    RunnerParseStatus Status,
    RunnerRunOptions? Options,
    string? ErrorMessage);

public static class RunnerCommandLineParser
{
    private static readonly HashSet<string> ValueOptions = new(
        [
            "snapshot",
            "run-id",
            "production-unit-id",
            "identity",
            "actor"
        ],
        StringComparer.Ordinal);

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

        if (!string.Equals(arguments[0], "run", StringComparison.Ordinal))
        {
            return Error($"Unknown command '{arguments[0]}'.");
        }

        string? projectTarget = null;
        var optionValues = new Dictionary<string, string>(StringComparer.Ordinal);
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

                if (char.IsWhiteSpace(optionValue[0]) || char.IsWhiteSpace(optionValue[^1]))
                {
                    return Error($"Value for '--{optionName}' must not have leading or trailing whitespace.");
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

            if (char.IsWhiteSpace(argument[0]) || char.IsWhiteSpace(argument[^1]))
            {
                return Error("Project target must not have leading or trailing whitespace.");
            }

            projectTarget = argument;
        }

        if (projectTarget is null)
        {
            return Error("A project directory or .oloproj path is required.");
        }

        var productionUnitIdentityValue = GetOption(optionValues, "identity");
        if (productionUnitIdentityValue is null)
        {
            return Error("Option '--identity' is required.");
        }

        if (!Guid.TryParseExact(
                GetOption(optionValues, "production-unit-id"),
                "D",
                out var productionUnitId)
            || productionUnitId == Guid.Empty)
        {
            return Error("Option '--production-unit-id' must be a non-empty GUID in D format.");
        }

        var actorId = GetOption(optionValues, "actor");
        if (actorId is null)
        {
            return Error("Option '--actor' is required.");
        }

        var productionRunId = Guid.NewGuid();
        var productionRunIdText = GetOption(optionValues, "run-id");
        if (productionRunIdText is not null
            && (!Guid.TryParseExact(productionRunIdText, "D", out productionRunId)
                || productionRunId == Guid.Empty))
        {
            return Error("Value for '--run-id' must be a non-empty GUID in D format.");
        }

        return new RunnerParseResult(
            RunnerParseStatus.Run,
            new RunnerRunOptions(
                projectTarget,
                GetOption(optionValues, "snapshot") ?? "active",
                productionRunId,
                productionUnitId,
                productionUnitIdentityValue,
                actorId),
            ErrorMessage: null);
    }

    private static string? GetOption(Dictionary<string, string> values, string name)
    {
        return values.TryGetValue(name, out var value) ? value : null;
    }

    private static bool IsHelpCommand(string argument)
    {
        return string.Equals(argument, "help", StringComparison.Ordinal)
            || IsHelpOption(argument);
    }

    private static bool IsHelpOption(string argument)
    {
        return string.Equals(argument, "--help", StringComparison.Ordinal)
            || string.Equals(argument, "-h", StringComparison.Ordinal);
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
