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
        if (canvasWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), "Canvas width must be positive.");
        }

        if (canvasHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasHeight), "Canvas height must be positive.");
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

        var boundsResult = ValidateGeometry(element.X, element.Y, element.Width, element.Height);
        if (!boundsResult.Succeeded)
        {
            return boundsResult;
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

        var boundsResult = ValidateGeometry(x, y, width, height);
        if (!boundsResult.Succeeded)
        {
            return boundsResult;
        }

        element.UpdateGeometry(x, y, width, height, rotationDegrees);

        return TopologyOperationResult.Accepted("Layout element geometry updated.");
    }

    private TopologyOperationResult ValidateGeometry(
        double x,
        double y,
        double width,
        double height)
    {
        if (width <= 0 || height <= 0)
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutElementSizeInvalid",
                "Layout element width and height must be positive.");
        }

        if (x < 0 || y < 0 || x + width > CanvasWidth || y + height > CanvasHeight)
        {
            return TopologyOperationResult.Rejected(
                "Topology.LayoutElementOutOfBounds",
                $"Layout element geometry must stay inside site layout {Id}.");
        }

        return TopologyOperationResult.Accepted();
    }
}
