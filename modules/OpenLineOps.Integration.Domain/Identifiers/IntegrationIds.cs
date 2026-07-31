namespace OpenLineOps.Integration.Domain.Identifiers;

public sealed record WorkOrderId
{
    public WorkOrderId(string value)
    {
        Value = IntegrationGuard.RequiredCanonical(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record WorkRequestId
{
    public WorkRequestId(string value)
    {
        Value = IntegrationGuard.RequiredCanonical(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record WorkResponseId
{
    public WorkResponseId(string value)
    {
        Value = IntegrationGuard.RequiredCanonical(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record WorkOrderFactId
{
    public WorkOrderFactId(string value)
    {
        Value = IntegrationGuard.RequiredCanonical(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}
