using System.Collections.ObjectModel;
using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.Layouts;

public sealed class SiteLayoutElement : Entity<LayoutElementId>
{
    private SiteLayoutElement(
        LayoutElementId id,
        LayoutElementKind kind,
        LayoutTargetReference target,
        LayoutElementId? parentElementId,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees,
        int zIndex,
        IReadOnlyDictionary<string, string> style)
        : base(id)
    {
        EnsureFinite(x, nameof(x));
        EnsureFinite(y, nameof(y));
        EnsurePositiveFinite(width, nameof(width));
        EnsurePositiveFinite(height, nameof(height));
        EnsureFinite(rotationDegrees, nameof(rotationDegrees));

        Kind = kind;
        Target = target;
        ParentElementId = parentElementId;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
        ZIndex = zIndex;
        Style = new ReadOnlyDictionary<string, string>(CopyStyle(style));
    }

    public LayoutElementKind Kind { get; }

    public LayoutTargetReference Target { get; }

    public LayoutElementId? ParentElementId { get; }

    public double X { get; private set; }

    public double Y { get; private set; }

    public double Width { get; private set; }

    public double Height { get; private set; }

    public double RotationDegrees { get; private set; }

    public int ZIndex { get; private set; }

    public IReadOnlyDictionary<string, string> Style { get; private set; }

    public static SiteLayoutElement Create(
        LayoutElementId id,
        LayoutElementKind kind,
        LayoutTargetReference target,
        LayoutElementId? parentElementId,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees,
        int zIndex,
        IReadOnlyDictionary<string, string>? style = null)
    {
        return new SiteLayoutElement(
            id,
            kind,
            target,
            parentElementId,
            x,
            y,
            width,
            height,
            rotationDegrees,
            zIndex,
            style ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    internal void UpdateGeometry(
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees)
    {
        EnsureFinite(x, nameof(x));
        EnsureFinite(y, nameof(y));
        EnsurePositiveFinite(width, nameof(width));
        EnsurePositiveFinite(height, nameof(height));
        EnsureFinite(rotationDegrees, nameof(rotationDegrees));

        X = x;
        Y = y;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
    }

    internal void UpdatePresentation(
        int? zIndex,
        IReadOnlyDictionary<string, string>? style)
    {
        var validatedStyle = style is null
            ? null
            : new ReadOnlyDictionary<string, string>(CopyStyle(style));

        if (zIndex.HasValue)
        {
            ZIndex = zIndex.Value;
        }

        if (validatedStyle is not null)
        {
            Style = validatedStyle;
        }
    }

    private static Dictionary<string, string> CopyStyle(IReadOnlyDictionary<string, string> style)
    {
        ArgumentNullException.ThrowIfNull(style);

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in style)
        {
            var key = TopologyIdGuard.NotBlank(pair.Key, "style key");
            var value = TopologyIdGuard.NotBlank(pair.Value, $"style value for {key}");
            if (!copy.TryAdd(key, value))
            {
                throw new ArgumentException($"Duplicate style key '{key}'.", nameof(style));
            }
        }

        return copy;
    }

    private static void EnsureFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Layout geometry must be finite.");
        }
    }

    private static void EnsurePositiveFinite(double value, string parameterName)
    {
        EnsureFinite(value, parameterName);
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Layout dimensions must be positive.");
        }
    }
}
