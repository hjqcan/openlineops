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
    double X,
    double Y,
    double Width,
    double Height,
    double RotationDegrees,
    string LayerId,
    string Label);
