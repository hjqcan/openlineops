namespace OpenLineOps.Topology.Application.Layouts;

public sealed record CreateSiteLayoutRequest(
    string LayoutId,
    string TopologyId,
    string DisplayName,
    double CanvasWidth,
    double CanvasHeight,
    string Units);

public sealed record AddSiteLayoutElementRequest(
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

public sealed record UpdateSiteLayoutElementGeometryRequest(
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees);

public sealed record UpdateSiteLayoutElementPresentationRequest(
    int? ZIndex,
    IReadOnlyDictionary<string, string>? Style);
