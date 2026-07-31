using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Projects.Application.ExternalPrograms;

public static class ExternalProgramResourceContract
{
    private static readonly SearchValues<char> InvalidPortablePathCharacters =
        SearchValues.Create("<>\"|?*");

    public const string ResourceDirectoryName = "external-programs";
    public const string DescriptorFileName = "resource.json";
    public const string FilesDirectoryName = "files";
    public const string ResourceKind = "OpenLineOps.ExternalProgramResource";
    public const string Schema = "openlineops.external-program-resource";
    public const string InvocationSchema = "openlineops.external-program-invocation";
    public const string ResourceIdProperty = "externalProgramResourceId";
    public const string ProductionInputSourcePrefix = "$production.";
    public const int MaximumFrozenFileCount = 256;
    public const int MaximumFrozenDirectoryCount = 1_024;
    public const long MaximumFrozenFileBytes = 512L * 1024 * 1024;
    public const long MaximumFrozenTotalBytes = 2L * 1024 * 1024 * 1024;

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

    public static string PortableId(string value, string parameterName)
    {
        if (!IsCanonical(value)
            || value.Length > 96
            || value is "." or ".."
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException(
                "External program resource id must start with an ASCII letter or digit and use only letters, digits, dot, dash, or underscore.",
                parameterName);
        }

        return value;
    }

    public static string CanonicalRelativePath(
        string value,
        string parameterName,
        string? requiredFirstSegment = null)
    {
        if (!IsCanonical(value)
            || value.Length > 1_024
            || !value.IsNormalized(NormalizationForm.FormC)
            || Path.IsPathRooted(value)
            || value.Contains('\\')
            || value.Contains(':'))
        {
            throw new ArgumentException(
                "External program file path must be a canonical forward-slash relative path.",
                parameterName);
        }

        var segments = value.Split('/');
        if (segments.Any(segment => !IsCanonical(segment)
                || segment.Length > 255
                || segment is "." or ".."
                || segment.EndsWith('.')
                || segment.AsSpan().ContainsAny(InvalidPortablePathCharacters)
                || IsWindowsReservedPathSegment(segment))
            || requiredFirstSegment is not null
            && !string.Equals(segments[0], requiredFirstSegment, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                requiredFirstSegment is null
                    ? "External program file path contains an invalid segment."
                    : $"External program file path must be under {requiredFirstSegment}/.",
                parameterName);
        }

        return string.Join('/', segments);
    }

    public static bool IsSupportedApplicationExecutableEntryPoint(string? value)
    {
        if (value is null || !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            _ = CanonicalRelativePath(value, nameof(value), FilesDirectoryName);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static long AccumulateFrozenFileBytes(
        int currentFileCount,
        long accumulatedBytes,
        long nextFileBytes)
    {
        if (currentFileCount < 0
            || currentFileCount >= MaximumFrozenFileCount
            || accumulatedBytes < 0
            || accumulatedBytes > MaximumFrozenTotalBytes
            || nextFileBytes < 0
            || nextFileBytes > MaximumFrozenFileBytes
            || nextFileBytes > MaximumFrozenTotalBytes - accumulatedBytes)
        {
            throw new InvalidDataException("External program file inventory exceeds the supported limits.");
        }

        return accumulatedBytes + nextFileBytes;
    }

    private static bool IsWindowsReservedPathSegment(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || IsNumberedWindowsDeviceName(stem, "COM")
            || IsNumberedWindowsDeviceName(stem, "LPT");
    }

    private static bool IsNumberedWindowsDeviceName(string stem, string prefix)
    {
        return stem.Length == prefix.Length + 1
            && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && stem[^1] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }

    public static bool IsSupportedInputSource(string? source) =>
        source is not null
        && (SupportedInputSources.Contains(source)
            || TryGetProductionInputKey(source, out _));

    public static bool TryGetProductionInputKey(
        string? source,
        [NotNullWhen(true)] out string? inputKey)
    {
        inputKey = null;
        if (!IsCanonical(source)
            || !source.StartsWith(ProductionInputSourcePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = source[ProductionInputSourcePrefix.Length..];
        if (!IsCanonical(candidate) || candidate.Length > 256)
        {
            return false;
        }

        inputKey = candidate;
        return true;
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
            if (template[cursor] == '}')
            {
                return false;
            }

            if (template[cursor] != '{')
            {
                cursor++;
                continue;
            }

            if (cursor + 1 >= template.Length || template[cursor + 1] != '{')
            {
                return false;
            }

            var closing = template.IndexOf("}}", cursor + 2, StringComparison.Ordinal);
            if (closing < 0)
            {
                return false;
            }

            var placeholder = template[(cursor + 2)..closing];
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

    public static bool IsCanonical([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1])
        && !value.Any(char.IsControl);

    public static bool IsSha256(string value) =>
        value.Length == 64
        && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
        && value.All(Uri.IsHexDigit);

    public static ExternalProgramResourceReference ReadReference(string? inputPayload)
    {
        if (string.IsNullOrWhiteSpace(inputPayload))
        {
            return ExternalProgramResourceReference.None;
        }

        try
        {
            using var document = JsonDocument.Parse(inputPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ExternalProgramResourceReference.None;
            }

            var matches = document.RootElement.EnumerateObject()
                .Where(property => string.Equals(property.Name, ResourceIdProperty, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
            {
                return ExternalProgramResourceReference.None;
            }

            if (matches.Length != 1
                || matches[0].Value.ValueKind != JsonValueKind.String
                || !IsCanonical(matches[0].Value.GetString()))
            {
                return ExternalProgramResourceReference.Malformed;
            }

            try
            {
                return new ExternalProgramResourceReference(
                    PortableId(matches[0].Value.GetString()!, ResourceIdProperty),
                    IsMalformed: false);
            }
            catch (ArgumentException)
            {
                return ExternalProgramResourceReference.Malformed;
            }
        }
        catch (JsonException)
        {
            // Device-command inputs are opaque provider payloads unless they are
            // a JSON object that explicitly declares our reserved resource id.
            // A non-JSON payload therefore cannot be an external-program
            // reference and must remain routable as an ordinary command.
            return ExternalProgramResourceReference.None;
        }
    }
}

public readonly record struct ExternalProgramResourceReference(string? ResourceId, bool IsMalformed)
{
    public static ExternalProgramResourceReference None => new(null, IsMalformed: false);

    public static ExternalProgramResourceReference Malformed => new(null, IsMalformed: true);
}

public enum ExternalProgramLaunchKind
{
    ApplicationExecutable = 1,
    Provider = 2
}

public sealed record ExternalProgramInputMapping(string Source, string Target);

public sealed record ExternalProgramResultMapping(
    string SourcePath,
    string TargetKey,
    ProductionContextValueKind ValueKind);

public sealed record ExternalProgramOutcomeMapping(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);

public sealed record ExternalProgramPermissionProfile(
    string ProfileName,
    bool NetworkAccessAllowed,
    IReadOnlyCollection<string> AllowedEnvironmentVariables);

public sealed record ExternalProgramExecutionLimits(
    long TimeoutMilliseconds,
    int MaximumProcessCount,
    long MaximumWorkingSetBytes,
    long MaximumCpuTimeMilliseconds,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    int MaximumArtifactCount,
    long MaximumArtifactBytes,
    long MaximumTotalArtifactBytes);

public sealed record ExternalProgramResourceFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ExternalProgramResource(
    string ResourceId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    ExternalProgramLaunchKind LaunchKind,
    string? EntryPoint,
    string? ProviderKind,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalProgramInputMapping> InputMappings,
    IReadOnlyCollection<ExternalProgramResultMapping> ResultMappings,
    ExternalProgramOutcomeMapping OutcomeMapping,
    ExternalProgramPermissionProfile PermissionProfile,
    ExternalProgramExecutionLimits ExecutionLimits,
    IReadOnlyCollection<ExternalProgramResourceFile> Files,
    string ContentSha256,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveExternalProgramResourceRequest(
    string ResourceId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    ExternalProgramLaunchKind LaunchKind,
    string? EntryPoint,
    string? ProviderKind,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalProgramInputMapping> InputMappings,
    IReadOnlyCollection<ExternalProgramResultMapping> ResultMappings,
    ExternalProgramOutcomeMapping OutcomeMapping,
    ExternalProgramPermissionProfile PermissionProfile,
    ExternalProgramExecutionLimits ExecutionLimits);

public sealed record ExternalProgramFileUpload(
    string ResourceRelativePath,
    Stream Content,
    long SizeBytes,
    string ExpectedSha256);

public sealed record ExternalProgramProtocolTrialRequest(
    IReadOnlyDictionary<string, ExternalProgramTrialInputValue> Inputs);

public enum ExternalProgramTrialInputKind
{
    Text = 1,
    IntegralNumber = 2,
    FractionalNumber = 3,
    Logical = 4
}

public sealed record ExternalProgramTrialInputValue(
    ExternalProgramTrialInputKind Kind,
    string CanonicalValue);

public sealed record ExternalProgramProtocolTrialResult(
    string ResourceId,
    string LaunchKind,
    string ContentSha256,
    string ExecutionStatus,
    string Judgement,
    string? ResultPayload,
    string? FailureReason,
    IReadOnlyCollection<ExternalProgramTrialArtifact> Artifacts);

public sealed record ExternalProgramTrialArtifact(
    string Name,
    string Kind,
    string? MediaType,
    long SizeBytes,
    string Sha256);
