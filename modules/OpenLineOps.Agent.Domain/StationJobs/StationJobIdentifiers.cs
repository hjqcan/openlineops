namespace OpenLineOps.Agent.Domain.StationJobs;

public readonly record struct StationJobId
{
    public StationJobId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Station job id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct StationOperationRunId
{
    public StationOperationRunId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                "Operation run id must be canonical non-empty text.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
