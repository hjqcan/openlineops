using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Production.Domain.Models;

public sealed record OperationInputMapping
{
    public OperationInputMapping(
        string targetInputKey,
        OperationDefinitionId sourceOperationId,
        string sourceOutputKey,
        ProductionContextValueKind expectedValueKind)
    {
        TargetInputKey = CanonicalKey(targetInputKey, nameof(targetInputKey));
        SourceOperationId = sourceOperationId
            ?? throw new ArgumentNullException(nameof(sourceOperationId));
        SourceOutputKey = CanonicalKey(sourceOutputKey, nameof(sourceOutputKey));
        if (!Enum.IsDefined(expectedValueKind))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedValueKind));
        }

        ExpectedValueKind = expectedValueKind;
    }

    public string TargetInputKey { get; }

    public OperationDefinitionId SourceOperationId { get; }

    public string SourceOutputKey { get; }

    public ProductionContextValueKind ExpectedValueKind { get; }

    private static string CanonicalKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Production Context keys must be canonical text of at most 256 characters.",
                parameterName);
        }

        return value;
    }
}
