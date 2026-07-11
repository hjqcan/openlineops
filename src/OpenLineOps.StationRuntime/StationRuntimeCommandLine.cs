namespace OpenLineOps.StationRuntime;

internal sealed record StationRuntimeCommandLine(string RequestFilePath, string ResultFilePath)
{
    public static StationRuntimeCommandLine Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 5
            || !string.Equals(arguments[0], "execute-operation", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "--request-file", StringComparison.Ordinal)
            || !string.Equals(arguments[3], "--result-file", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Expected exactly: execute-operation --request-file <path> --result-file <path>.");
        }

        return new StationRuntimeCommandLine(
            AbsolutePath(arguments[2], "request file"),
            AbsolutePath(arguments[4], "result file"));
    }

    private static string AbsolutePath(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Any(char.IsControl)
            || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException($"Station Runtime {description} path must be canonical and absolute.");
        }

        return Path.GetFullPath(value);
    }
}
