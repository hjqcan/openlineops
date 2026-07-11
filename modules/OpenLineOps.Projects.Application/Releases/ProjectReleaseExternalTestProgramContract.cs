using System.Diagnostics.CodeAnalysis;

namespace OpenLineOps.Projects.Application.Releases;

public static class ProjectReleaseExternalTestProgramContract
{
    public const string AdapterIdProperty = "externalTestProgramAdapterId";

    public const string InvocationSchema = "openlineops.external-test-invocation";

    private static readonly HashSet<string> SupportedInputSources = new(StringComparer.Ordinal)
    {
        "$product.identity",
        "$product.model",
        "$product.inputKey",
        "$run.id",
        "$line.id",
        "$operation.id",
        "$operation.attempt",
        "$session.id",
        "$station.id",
        "$lot.id",
        "$carrier.id",
        "$fixture.id",
        "$device.id",
        "$configuration.id",
        "$step.id",
        "$command.id",
        "$command.name",
        "$node.id",
        "$action.id",
        "$capability.id",
        "$project.id",
        "$application.id",
        "$snapshot.id",
        "$target.kind",
        "$target.id"
    };

    public static bool IsSupportedInputSource(string? source)
    {
        return source is not null && SupportedInputSources.Contains(source);
    }

    public static bool IsSupportedArgumentTemplate(
        string? template,
        IEnumerable<string> inputTargets)
    {
        if (!IsCanonical(template))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(inputTargets);
        var supportedPlaceholders = SupportedInputSources
            .Select(source => source[1..])
            .Concat(inputTargets.Select(target => $"input.{target}"))
            .ToHashSet(StringComparer.Ordinal);
        var cursor = 0;
        while (cursor < template.Length)
        {
            var opening = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (opening < 0)
            {
                return true;
            }

            var closing = template.IndexOf("}}", opening + 2, StringComparison.Ordinal);
            if (closing < 0)
            {
                return false;
            }

            var placeholder = template[(opening + 2)..closing];
            if (!IsCanonical(placeholder) || !supportedPlaceholders.Contains(placeholder))
            {
                return false;
            }

            cursor = closing + 2;
        }

        return true;
    }

    public static bool IsSupportedResultPath(string? sourcePath)
    {
        if (!IsCanonical(sourcePath) || !sourcePath.StartsWith("$.", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = sourcePath[2..].Split('.');
        return segments.Length > 0 && segments.All(IsCanonical);
    }

    public static bool IsSupportedOutcomeMapping(
        ProjectReleaseExternalTestProgramOutcomeMapping? mapping)
    {
        return mapping is not null
            && IsSupportedResultPath(mapping.SourcePath)
            && IsCanonical(mapping.PassedToken)
            && IsCanonical(mapping.FailedToken)
            && IsCanonical(mapping.AbortedToken)
            && !string.Equals(mapping.PassedToken, mapping.FailedToken, StringComparison.Ordinal)
            && !string.Equals(mapping.PassedToken, mapping.AbortedToken, StringComparison.Ordinal)
            && !string.Equals(mapping.FailedToken, mapping.AbortedToken, StringComparison.Ordinal);
    }

    private static bool IsCanonical([NotNullWhen(true)] string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !char.IsWhiteSpace(value[0])
               && !char.IsWhiteSpace(value[^1]);
    }
}
