using System.Text;

namespace OpenLineOps.ProcessIsolation;

internal static class WindowsCommandLine
{
    public static string Build(string executablePath, IReadOnlyCollection<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder();
        AppendArgument(builder, executablePath);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(
                builder,
                argument ?? throw new ArgumentException(
                    "External program arguments cannot contain null values.",
                    nameof(arguments)));
        }

        return builder.ToString();
    }

    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var builder = new StringBuilder();
        AppendArgument(builder, argument);
        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0
            && !argument.Any(character => character is ' ' or '\t' or '"'))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', checked(backslashCount * 2 + 1));
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(character);
        }

        builder.Append('\\', checked(backslashCount * 2));
        builder.Append('"');
    }
}
