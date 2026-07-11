namespace OpenLineOps.Runtime.Domain.Runs;

public sealed record ProductionUnitIdentity
{
    public ProductionUnitIdentity(string modelId, string inputKey, string value)
    {
        ModelId = ProductionRunText.Required(modelId, nameof(modelId));
        InputKey = ProductionRunText.Required(inputKey, nameof(inputKey));
        Value = ProductionRunText.Required(value, nameof(value));
    }

    public string ModelId { get; }

    public string InputKey { get; }

    public string Value { get; }
}
