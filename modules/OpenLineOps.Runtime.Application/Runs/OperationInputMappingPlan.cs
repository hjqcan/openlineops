using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Application.Runs;

public sealed record OperationInputMappingPlan
{
    public OperationInputMappingPlan(
        string targetInputKey,
        string sourceOperationId,
        string sourceOutputKey,
        ProductionContextValueKind expectedValueKind)
    {
        TargetInputKey = Required(targetInputKey, nameof(targetInputKey));
        SourceOperationId = Required(sourceOperationId, nameof(sourceOperationId));
        SourceOutputKey = Required(sourceOutputKey, nameof(sourceOutputKey));
        if (!Enum.IsDefined(expectedValueKind))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedValueKind));
        }

        ExpectedValueKind = expectedValueKind;
    }

    public string TargetInputKey { get; }

    public string SourceOperationId { get; }

    public string SourceOutputKey { get; }

    public ProductionContextValueKind ExpectedValueKind { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || value.Length > 256
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
        || value.Any(char.IsControl)
            ? throw new ArgumentException(
                "Production Context mapping fields must be canonical text of at most 256 characters.",
                parameterName)
            : value;
}
