using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.Layouts;

public sealed class SiteLayoutElement : Entity<LayoutElementId>
{
    private SiteLayoutElement(
        LayoutElementId id,
        LayoutElementKind kind,
        LayoutTargetReference target,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees,
        string layerId,
        string label)
        : base(id)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Layout element width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Layout element height must be positive.");
        }

        Kind = kind;
        Target = target;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
        LayerId = TopologyIdGuard.NotBlank(layerId, nameof(layerId));
        Label = TopologyIdGuard.NotBlank(label, nameof(label));
    }

    public LayoutElementKind Kind { get; }

    public LayoutTargetReference Target { get; }

    public double X { get; private set; }

    public double Y { get; private set; }

    public double Width { get; private set; }

    public double Height { get; private set; }

    public double RotationDegrees { get; private set; }

    public string LayerId { get; }

    public string Label { get; }

    public static SiteLayoutElement Create(
        LayoutElementId id,
        LayoutElementKind kind,
        LayoutTargetReference target,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees,
        string layerId,
        string label)
    {
        return new SiteLayoutElement(id, kind, target, x, y, width, height, rotationDegrees, layerId, label);
    }

    internal void UpdateGeometry(
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Layout element width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Layout element height must be positive.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
    }
}
