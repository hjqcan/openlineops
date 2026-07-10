namespace OpenLineOps.Runtime.Domain.Runs;

public sealed record DutIdentity
{
    public DutIdentity(string modelId, string inputKey, string value)
    {
        ModelId = Required(modelId, nameof(modelId));
        InputKey = Required(inputKey, nameof(inputKey));
        Value = Required(value, nameof(value));
    }

    public string ModelId { get; }

    public string InputKey { get; }

    public string Value { get; }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;
    }
}
