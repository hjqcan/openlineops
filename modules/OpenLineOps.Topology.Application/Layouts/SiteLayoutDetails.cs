namespace OpenLineOps.Topology.Application.Layouts;

public sealed record SiteLayoutDetails(
    string LayoutId,
    string TopologyId,
    string DisplayName,
    double CanvasWidth,
    double CanvasHeight,
    string Units,
    IReadOnlyCollection<SiteLayoutElementDetails> Elements);

public sealed record SiteLayoutElementDetails(
    string ElementId,
    string Kind,
    string TargetKind,
    string TargetId,
    string? ParentElementId,
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    int ZIndex,
    IReadOnlyDictionary<string, string> Style);
