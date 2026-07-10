using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Operations;

namespace OpenLineOps.Topology.Domain.Layouts;

public sealed class SiteLayout : AggregateRoot<SiteLayoutId>
{
    private readonly List<SiteLayoutElement> _elements = [];

    private SiteLayout(
        SiteLayoutId id,
        AutomationTopologyId topologyId,
        string displayName,
        double canvasWidth,
        double canvasHeight,
        string units)
        : base(id)
    {
        if (!double.IsFinite(canvasWidth) || canvasWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), "Canvas width must be positive and finite.");
        }

        if (!double.IsFinite(canvasHeight) || canvasHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasHeight), "Canvas height must be positive and finite.");
        }

        TopologyId = topologyId;
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Units = TopologyIdGuard.NotBlank(units, nameof(units));
    }

    public AutomationTopologyId TopologyId { get; }

    public string DisplayName { get; }

    public double CanvasWidth { get; }

    public double CanvasHeight { get; }

    public string Units { get; }

    public IReadOnlyCollection<SiteLayoutElement> Elements => _elements.AsReadOnly();

    public static SiteLayout Create(
        SiteLayoutId id,
        AutomationTopologyId topologyId,
        string displayName,
        double canvasWidth,
        double canvasHeight,
        string units = "mm")
    {
        return new SiteLayout(id, topologyId, displayName, canvasWidth, canvasHeight, units);
    }

    public TopologyOperationResult AddElement(SiteLayoutElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (_elements.Any(candidate => candidate.Id == element.Id))
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutElementAlreadyExists",
                $"Layout element {element.Id} already exists in site layout {Id}.");
        }

        if (_elements.Any(candidate => candidate.Target == element.Target))
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutTargetAlreadyPlaced",
                $"Layout target {element.Target.Kind}:{element.Target.TargetId} is already placed in site layout {Id}.");
        }

        var hierarchy = ValidateElementHierarchy(element);
        if (!hierarchy.Succeeded)
        {
            return hierarchy;
        }

        var bounds = ValidateElementBounds(element, element.X, element.Y, element.Width, element.Height, element.RotationDegrees);
        if (!bounds.Succeeded)
        {
            return bounds;
        }

        _elements.Add(element);
        return TopologyOperationResult.Accepted("Layout element added.");
    }

    public TopologyOperationResult UpdateElementGeometry(
        LayoutElementId elementId,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees)
    {
        var element = _elements.SingleOrDefault(candidate => candidate.Id == elementId);
        if (element is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutElementNotFound",
                $"Layout element {elementId} was not found in site layout {Id}.");
        }

        var bounds = ValidateElementBounds(element, x, y, width, height, rotationDegrees);
        if (!bounds.Succeeded)
        {
            return bounds;
        }

        foreach (var child in _elements.Where(candidate => candidate.ParentElementId == elementId))
        {
            if (!FitsInside(child.X, child.Y, child.Width, child.Height, child.RotationDegrees, width, height))
            {
                return TopologyOperationResult.Rejected(
                    "Topology.LayoutChildOutOfBounds",
                    $"Layout element {elementId} cannot be resized because child {child.Id} would leave its bounds.");
            }
        }

        element.UpdateGeometry(x, y, width, height, rotationDegrees);
        return TopologyOperationResult.Accepted("Layout element geometry updated.");
    }

    public TopologyOperationResult UpdateElementPresentation(
        LayoutElementId elementId,
        int? zIndex,
        IReadOnlyDictionary<string, string>? style)
    {
        var element = _elements.SingleOrDefault(candidate => candidate.Id == elementId);
        if (element is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutElementNotFound",
                $"Layout element {elementId} was not found in site layout {Id}.");
        }

        if (zIndex is null && style is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutPresentationRequired",
                "At least one of zIndex or style must be supplied.");
        }

        element.UpdatePresentation(zIndex, style);
        return TopologyOperationResult.Accepted("Layout element presentation updated.");
    }

    public TopologyOperationResult RemoveElementSubtree(LayoutElementId elementId)
    {
        if (_elements.All(candidate => candidate.Id != elementId))
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutElementNotFound",
                $"Layout element {elementId} was not found in site layout {Id}.");
        }

        RemoveElementSubtrees([elementId]);
        return TopologyOperationResult.Accepted("Layout element subtree removed.");
    }

    public int RemoveElementsByTargets(IReadOnlySet<LayoutTargetReference> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var roots = _elements
            .Where(element => targets.Contains(element.Target))
            .Select(element => element.Id)
            .ToArray();
        return RemoveElementSubtrees(roots);
    }

    private int RemoveElementSubtrees(IEnumerable<LayoutElementId> roots)
    {
        var removedIds = roots.ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var child in _elements.Where(candidate => candidate.ParentElementId is not null
                         && removedIds.Contains(candidate.ParentElementId)))
            {
                changed |= removedIds.Add(child.Id);
            }
        }

        return _elements.RemoveAll(element => removedIds.Contains(element.Id));
    }

    private TopologyOperationResult ValidateElementHierarchy(SiteLayoutElement element)
    {
        if (!KindMatchesTarget(element.Kind, element.Target.Kind))
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutKindTargetMismatch",
                $"Layout element kind {element.Kind} cannot represent target kind {element.Target.Kind}.");
        }

        if (element.ParentElementId is null)
        {
            return element.Kind == LayoutElementKind.SystemShape
                ? TopologyOperationResult.Accepted()
                : TopologyOperationResult.Rejected(
                    "Topology.LayoutRootMustBeSystem",
                    "Only SystemShape elements may be placed at the canvas root.");
        }

        var parent = _elements.SingleOrDefault(candidate => candidate.Id == element.ParentElementId);
        if (parent is null)
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutParentMissing",
                $"Parent layout element {element.ParentElementId} must exist before child {element.Id} is added.");
        }

        var allowed = element.Kind switch
        {
            LayoutElementKind.SystemShape => parent.Kind == LayoutElementKind.SystemShape,
            LayoutElementKind.GroupRegion => parent.Kind == LayoutElementKind.SystemShape,
            LayoutElementKind.SlotShape => parent.Kind == LayoutElementKind.GroupRegion,
            _ => false
        };

        return allowed
            ? TopologyOperationResult.Accepted()
            : TopologyOperationResult.Rejected(
                "Topology.LayoutParentKindInvalid",
                $"Layout element {element.Kind} cannot be placed inside {parent.Kind}.");
    }

    private TopologyOperationResult ValidateElementBounds(
        SiteLayoutElement element,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees)
    {
        if (!double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || !double.IsFinite(rotationDegrees)
            || width <= 0
            || height <= 0)
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutGeometryInvalid",
                "Layout geometry must contain finite coordinates, a finite rotation, and positive dimensions.");
        }

        var containerWidth = CanvasWidth;
        var containerHeight = CanvasHeight;
        if (element.ParentElementId is not null)
        {
            var parent = _elements.SingleOrDefault(candidate => candidate.Id == element.ParentElementId);
            if (parent is null)
            {
                return TopologyOperationResult.Rejected(
                    "Topology.LayoutParentMissing",
                    $"Parent layout element {element.ParentElementId} does not exist.");
            }

            containerWidth = parent.Width;
            containerHeight = parent.Height;
        }

        return FitsInside(x, y, width, height, rotationDegrees, containerWidth, containerHeight)
            ? TopologyOperationResult.Accepted()
            : TopologyOperationResult.Rejected(
                "Topology.LayoutElementOutOfBounds",
                $"Layout element {element.Id} geometry must stay inside its parent container.");
    }

    private static bool KindMatchesTarget(LayoutElementKind elementKind, LayoutTargetKind targetKind)
    {
        return (elementKind, targetKind) switch
        {
            (LayoutElementKind.SystemShape, LayoutTargetKind.System) => true,
            (LayoutElementKind.GroupRegion, LayoutTargetKind.SlotGroup) => true,
            (LayoutElementKind.SlotShape, LayoutTargetKind.Slot) => true,
            _ => false
        };
    }

    private static bool FitsInside(
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees,
        double containerWidth,
        double containerHeight)
    {
        var radians = rotationDegrees * Math.PI / 180d;
        var halfRotatedWidth = Math.Abs(Math.Cos(radians)) * width / 2d
            + Math.Abs(Math.Sin(radians)) * height / 2d;
        var halfRotatedHeight = Math.Abs(Math.Sin(radians)) * width / 2d
            + Math.Abs(Math.Cos(radians)) * height / 2d;
        var centerX = x + width / 2d;
        var centerY = y + height / 2d;

        return centerX - halfRotatedWidth >= 0
            && centerY - halfRotatedHeight >= 0
            && centerX + halfRotatedWidth <= containerWidth
            && centerY + halfRotatedHeight <= containerHeight;
    }
}
