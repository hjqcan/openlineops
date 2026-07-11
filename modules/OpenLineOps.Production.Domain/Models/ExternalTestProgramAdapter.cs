using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public static class ExternalTestProgramInputSources
{
    public const string ProductIdentity = "$product.identity";

    public const string ProductModel = "$product.model";
}

public enum ExternalTestProgramLaunchKind
{
    ApplicationExecutable = 1,
    Provider = 2
}

public sealed record ExternalTestProgramInputMapping
{
    public ExternalTestProgramInputMapping(string source, string target)
    {
        Source = ProductionIdGuard.NotBlank(source, nameof(source));
        Target = ProductionIdGuard.NotBlank(target, nameof(target));
    }

    public string Source { get; }

    public string Target { get; }
}

public sealed record ExternalTestProgramResultMapping
{
    public ExternalTestProgramResultMapping(string sourcePath, string targetKey)
    {
        SourcePath = ProductionIdGuard.NotBlank(sourcePath, nameof(sourcePath));
        TargetKey = ProductionIdGuard.NotBlank(targetKey, nameof(targetKey));
    }

    public string SourcePath { get; }

    public string TargetKey { get; }
}

public sealed record ExternalTestProgramOutcomeMapping
{
    public ExternalTestProgramOutcomeMapping(
        string sourcePath,
        string passedToken,
        string failedToken,
        string abortedToken)
    {
        SourcePath = ProductionIdGuard.NotBlank(sourcePath, nameof(sourcePath));
        PassedToken = ProductionIdGuard.NotBlank(passedToken, nameof(passedToken));
        FailedToken = ProductionIdGuard.NotBlank(failedToken, nameof(failedToken));
        AbortedToken = ProductionIdGuard.NotBlank(abortedToken, nameof(abortedToken));

        if (PassedToken == FailedToken
            || PassedToken == AbortedToken
            || FailedToken == AbortedToken)
        {
            throw new ArgumentException(
                "External test program outcome tokens must be pairwise distinct exact values.");
        }
    }

    public string SourcePath { get; }

    public string PassedToken { get; }

    public string FailedToken { get; }

    public string AbortedToken { get; }
}

public sealed class ExternalTestProgramAdapter : Entity<ExternalTestProgramAdapterId>
{
    public const string InvocationPayloadAdapterIdProperty = "externalTestProgramAdapterId";

    private readonly List<string> _argumentTemplates;
    private readonly List<ExternalTestProgramInputMapping> _inputMappings;
    private readonly List<ExternalTestProgramResultMapping> _resultMappings;

    private ExternalTestProgramAdapter(
        ExternalTestProgramAdapterId id,
        string displayName,
        string capabilityId,
        string commandName,
        string? executable,
        string? providerKey,
        IEnumerable<string> argumentTemplates,
        IEnumerable<ExternalTestProgramInputMapping> inputMappings,
        IEnumerable<ExternalTestProgramResultMapping> resultMappings,
        ExternalTestProgramOutcomeMapping outcomeMapping,
        TimeSpan timeout)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "External test program timeout must be positive.");
        }

        if (timeout.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "External test program timeout must use whole-millisecond precision.");
        }

        DisplayName = ProductionIdGuard.NotBlank(displayName, nameof(displayName));
        CapabilityId = ProductionIdGuard.NotBlank(capabilityId, nameof(capabilityId));
        CommandName = ProductionIdGuard.NotBlank(commandName, nameof(commandName));
        Executable = NormalizeExecutable(executable);
        ProviderKey = providerKey is null
            ? null
            : ProductionIdGuard.NotBlank(providerKey, nameof(providerKey));
        if ((Executable is null) == (ProviderKey is null))
        {
            throw new ArgumentException(
                "External test program must define exactly one Application executable or provider key.");
        }

        _argumentTemplates = argumentTemplates
            .Select(argument => ProductionIdGuard.NotBlank(argument, nameof(argumentTemplates)))
            .ToList();
        var inputMappingItems = inputMappings.ToList();
        var resultMappingItems = resultMappings.ToList();
        if (inputMappingItems.Any(static mapping => mapping is null)
            || resultMappingItems.Any(static mapping => mapping is null))
        {
            throw new ArgumentException(
                "External test program mappings cannot contain null items.");
        }

        _inputMappings = inputMappingItems
            .OrderBy(mapping => mapping.Target, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.Source, StringComparer.Ordinal)
            .ToList();
        _resultMappings = resultMappingItems
            .OrderBy(mapping => mapping.TargetKey, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.SourcePath, StringComparer.Ordinal)
            .ToList();
        EnsureMappingsAreValid(_inputMappings, _resultMappings);
        OutcomeMapping = outcomeMapping ?? throw new ArgumentNullException(nameof(outcomeMapping));
        Timeout = timeout;
    }

    public string DisplayName { get; }

    public string CapabilityId { get; }

    public string CommandName { get; }

    public ExternalTestProgramLaunchKind LaunchKind => Executable is null
        ? ExternalTestProgramLaunchKind.Provider
        : ExternalTestProgramLaunchKind.ApplicationExecutable;

    public string? Executable { get; }

    public string? ProviderKey { get; }

    public IReadOnlyCollection<string> ArgumentTemplates => _argumentTemplates.AsReadOnly();

    public IReadOnlyCollection<ExternalTestProgramInputMapping> InputMappings => _inputMappings.AsReadOnly();

    public IReadOnlyCollection<ExternalTestProgramResultMapping> ResultMappings => _resultMappings.AsReadOnly();

    public ExternalTestProgramOutcomeMapping OutcomeMapping { get; }

    public TimeSpan Timeout { get; }

    public static ExternalTestProgramAdapter Create(
        ExternalTestProgramAdapterId id,
        string displayName,
        string capabilityId,
        string commandName,
        string? executable,
        string? providerKey,
        IEnumerable<string> argumentTemplates,
        IEnumerable<ExternalTestProgramInputMapping> inputMappings,
        IEnumerable<ExternalTestProgramResultMapping> resultMappings,
        ExternalTestProgramOutcomeMapping outcomeMapping,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(argumentTemplates);
        ArgumentNullException.ThrowIfNull(inputMappings);
        ArgumentNullException.ThrowIfNull(resultMappings);

        return new ExternalTestProgramAdapter(
            id,
            displayName,
            capabilityId,
            commandName,
            executable,
            providerKey,
            argumentTemplates,
            inputMappings,
            resultMappings,
            outcomeMapping,
            timeout);
    }

    private static string? NormalizeExecutable(string? executable)
    {
        if (executable is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(executable)
            || Path.IsPathRooted(executable)
            || executable.Contains('\\')
            || char.IsWhiteSpace(executable[0])
            || char.IsWhiteSpace(executable[^1]))
        {
            throw new ArgumentException(
                "External test executable must be a canonical Application-relative path.",
                nameof(executable));
        }

        var segments = executable.Split('/');
        if (segments.Length < 2
            || !string.Equals(segments[0], "programs", StringComparison.Ordinal)
            || segments.Skip(1).Any(segment => !IsPortableSegment(segment)))
        {
            throw new ArgumentException(
                "External test executable must be under programs/ inside the portable Application.",
                nameof(executable));
        }

        return executable;
    }

    private static bool IsPortableSegment(string segment)
    {
        try
        {
            return string.Equals(
                ProductionIdGuard.PortablePathSegment(segment, nameof(segment)),
                segment,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void EnsureMappingsAreValid(
        List<ExternalTestProgramInputMapping> inputMappings,
        List<ExternalTestProgramResultMapping> resultMappings)
    {
        if (inputMappings.Count == 0 || resultMappings.Count == 0)
        {
            throw new ArgumentException("External test program input and result mappings are required.");
        }

        if (inputMappings.Select(mapping => mapping.Target).Distinct(StringComparer.Ordinal).Count()
            != inputMappings.Count)
        {
            throw new ArgumentException("External test program input mapping targets must be unique.");
        }

        if (resultMappings.Select(mapping => mapping.TargetKey).Distinct(StringComparer.Ordinal).Count()
            != resultMappings.Count)
        {
            throw new ArgumentException("External test program result mapping targets must be unique.");
        }

        if (inputMappings.All(mapping => mapping.Source != ExternalTestProgramInputSources.ProductIdentity)
            || inputMappings.All(mapping => mapping.Source != ExternalTestProgramInputSources.ProductModel))
        {
            throw new ArgumentException(
                "External test program input mappings must include product identity and product model.");
        }
    }
}
