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

        _elements.Add(element);

        return TopologyOperationResult.Accepted("Layout element added.");
    }
}
