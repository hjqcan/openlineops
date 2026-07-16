using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed class ProductionRouteLayout
{
    public const int MinimumCoordinate = 0;

    public const int MaximumCoordinate = 100_000;

    private readonly IReadOnlyCollection<OperationCanvasPosition> _operationPositions;

    public ProductionRouteLayout(IEnumerable<OperationCanvasPosition> operationPositions)
    {
        ArgumentNullException.ThrowIfNull(operationPositions);
        var positions = operationPositions.ToArray();
        if (positions.Any(static position => position is null))
        {
            throw new ArgumentException(
                "Production route layout cannot contain null positions.",
                nameof(operationPositions));
        }

        var operationIds = positions.Select(position => position.OperationId.Value).ToArray();
        if (operationIds.Distinct(StringComparer.Ordinal).Count() != operationIds.Length)
        {
            throw new ArgumentException(
                "Production route layout operation ids must be unique.",
                nameof(operationPositions));
        }

        if (operationIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != operationIds.Length)
        {
            throw new ArgumentException(
                "Production route layout cannot contain operation ids that differ only by case.",
                nameof(operationPositions));
        }

        _operationPositions = Array.AsReadOnly(positions
            .OrderBy(position => position.OperationId.Value, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyCollection<OperationCanvasPosition> OperationPositions => _operationPositions;
}

public sealed class OperationCanvasPosition
{
    public OperationCanvasPosition(OperationDefinitionId operationId, int x, int y)
    {
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
        X = EnsureCoordinate(x, nameof(x));
        Y = EnsureCoordinate(y, nameof(y));
    }

    public OperationDefinitionId OperationId { get; }

    public int X { get; }

    public int Y { get; }

    private static int EnsureCoordinate(int value, string parameterName)
    {
        if (value is < ProductionRouteLayout.MinimumCoordinate
            or > ProductionRouteLayout.MaximumCoordinate)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Production route coordinates must be between {ProductionRouteLayout.MinimumCoordinate} and {ProductionRouteLayout.MaximumCoordinate}.");
        }

        return value;
    }
}
