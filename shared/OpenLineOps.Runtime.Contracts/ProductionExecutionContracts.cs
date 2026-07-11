using System.Globalization;

namespace OpenLineOps.Runtime.Contracts;

public enum ExecutionStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    TimedOut = 4,
    Canceled = 5,
    Rejected = 6
}

public enum ResultJudgement
{
    Passed = 1,
    Failed = 2,
    Aborted = 3,
    Unknown = 4,
    NotApplicable = 5
}

public enum ProductDisposition
{
    InProcess = 0,
    Completed = 1,
    Nonconforming = 2,
    Held = 3,
    Scrapped = 4
}

public enum ProductionContextValueKind
{
    Text = 1,
    Boolean = 2,
    WholeNumber = 3,
    FixedPoint = 4,
    DateTimeUtc = 5
}

public sealed record ProductionContextValue
{
    public ProductionContextValue(ProductionContextValueKind kind, string canonicalValue)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported context value kind.");
        }

        if (string.IsNullOrWhiteSpace(canonicalValue)
            || char.IsWhiteSpace(canonicalValue[0])
            || char.IsWhiteSpace(canonicalValue[^1]))
        {
            throw new ArgumentException(
                "Production Context value must be canonical non-empty text.",
                nameof(canonicalValue));
        }

        Kind = kind;
        CanonicalValue = canonicalValue;
        ValidateCanonicalValue();
    }

    public ProductionContextValueKind Kind { get; }

    public string CanonicalValue { get; }

    private void ValidateCanonicalValue()
    {
        var valid = Kind switch
        {
            ProductionContextValueKind.Text => true,
            ProductionContextValueKind.Boolean => CanonicalValue is "true" or "false",
            ProductionContextValueKind.WholeNumber => long.TryParse(
                CanonicalValue,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var integer)
                && string.Equals(
                    integer.ToString(CultureInfo.InvariantCulture),
                    CanonicalValue,
                    StringComparison.Ordinal),
            ProductionContextValueKind.FixedPoint => decimal.TryParse(
                CanonicalValue,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out _),
            ProductionContextValueKind.DateTimeUtc => DateTimeOffset.TryParseExact(
                CanonicalValue,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp)
                && timestamp.Offset == TimeSpan.Zero,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"Value '{CanonicalValue}' is not canonical for {Kind}.",
                nameof(CanonicalValue));
        }
    }
}
