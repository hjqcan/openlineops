using OpenLineOps.Api.Abstractions;
using OpenLineOps.Topology.Api.Models;
using OpenLineOps.Topology.Application.Layouts;
using ApiAddElementRequest = OpenLineOps.Topology.Api.Models.AddSiteLayoutElementRequest;
using ApiCreateLayoutRequest = OpenLineOps.Topology.Api.Models.CreateSiteLayoutRequest;
using ApiUpdateGeometryRequest = OpenLineOps.Topology.Api.Models.UpdateSiteLayoutElementGeometryRequest;
using ApiUpdatePresentationRequest = OpenLineOps.Topology.Api.Models.UpdateSiteLayoutElementPresentationRequest;

namespace OpenLineOps.Topology.Api.Controllers;

internal static class SiteLayoutApiContract
{
    public static SiteLayoutResponse ToResponse(SiteLayoutDetails layout)
    {
        return new SiteLayoutResponse(
            layout.LayoutId,
            layout.TopologyId,
            layout.DisplayName,
            layout.CanvasWidth,
            layout.CanvasHeight,
            layout.Units,
            layout.Elements.Select(element => new SiteLayoutElementResponse(
                element.ElementId,
                element.Kind,
                new LayoutTargetResponse(element.TargetKind, element.TargetId),
                element.ParentElementId,
                element.X,
                element.Y,
                element.Width,
                element.Height,
                element.RotationDegrees,
                element.ZIndex,
                element.Style)).ToArray(),
            EditorDocumentConcurrency.ComputeRevision(layout));
    }

    public static Dictionary<string, string[]> Validate(ApiCreateLayoutRequest? request)
    {
        var errors = AutomationTopologyApiContract.NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AutomationTopologyApiContract.AddRequired(errors, nameof(request.LayoutId), request.LayoutId);
        AutomationTopologyApiContract.AddRequired(errors, nameof(request.TopologyId), request.TopologyId);
        AutomationTopologyApiContract.AddRequired(errors, nameof(request.DisplayName), request.DisplayName);
        AddPositive(errors, nameof(request.CanvasWidth), request.CanvasWidth);
        AddPositive(errors, nameof(request.CanvasHeight), request.CanvasHeight);
        AutomationTopologyApiContract.AddRequired(errors, nameof(request.Units), request.Units);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiAddElementRequest? request)
    {
        var errors = AutomationTopologyApiContract.NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AutomationTopologyApiContract.AddRequired(errors, nameof(request.ElementId), request.ElementId);
        AutomationTopologyApiContract.AddRequired(errors, nameof(request.Kind), request.Kind);
        if (request.Target is null)
        {
            errors[nameof(request.Target)] = ["Object is required."];
        }
        else
        {
            AutomationTopologyApiContract.AddRequired(errors, "Target.Kind", request.Target.Kind);
            AutomationTopologyApiContract.AddRequired(errors, "Target.TargetId", request.Target.TargetId);
        }

        AddNumber(errors, nameof(request.X), request.X);
        AddNumber(errors, nameof(request.Y), request.Y);
        AddPositive(errors, nameof(request.Width), request.Width);
        AddPositive(errors, nameof(request.Height), request.Height);
        AddNumber(errors, nameof(request.RotationDegrees), request.RotationDegrees);
        if (request.ZIndex is null)
        {
            errors[nameof(request.ZIndex)] = ["Value is required."];
        }

        if (request.Style is null)
        {
            errors[nameof(request.Style)] = ["Object is required."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiUpdateGeometryRequest? request)
    {
        var errors = AutomationTopologyApiContract.NewErrors(request);
        if (request is null)
        {
            return errors;
        }

        AddNumber(errors, nameof(request.X), request.X);
        AddNumber(errors, nameof(request.Y), request.Y);
        AddPositive(errors, nameof(request.Width), request.Width);
        AddPositive(errors, nameof(request.Height), request.Height);
        AddNumber(errors, nameof(request.RotationDegrees), request.RotationDegrees);
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApiUpdatePresentationRequest? request)
    {
        var errors = AutomationTopologyApiContract.NewErrors(request);
        if (request is not null && request.ZIndex is null && request.Style is null)
        {
            errors["request"] = ["At least one of ZIndex or Style is required."];
        }

        return errors;
    }

    private static void AddNumber(Dictionary<string, string[]> errors, string key, double? value)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            errors[key] = ["Value must be finite."];
        }
    }

    private static void AddPositive(Dictionary<string, string[]> errors, string key, double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || value <= 0)
        {
            errors[key] = ["Value must be positive and finite."];
        }
    }
}
