namespace OpenLineOps.Api.Abstractions;

public static class OpenLineOpsApiRoutes
{
    public const string Platform = "api/platform";
    public const string Devices = "api/devices";
    public const string OperationsAlarms = "api/operations/alarms";
    public const string Plugins = "api/plugins";
    public const string AutomationProjects = "api/automation-projects";
    public const string AutomationProjectWorkspaces = "api/automation-project-workspaces";
    public const string RuntimeMonitoring = "api/runtime/monitoring";
    public const string ProductionRuns = "api/production-runs";
    public const string ProductionUnits = "api/production-units";
    public const string ProductionLots = "api/production-lots";
    public const string ProductionCarriers = "api/production-carriers";
    public const string SlotOccupancies = "api/slot-occupancies";
    public const string MaterialGenealogy = "api/material-genealogy";
    public const string OperationsActiveRuns = "api/operations/active-runs";
    public const string OperationsLineState = "api/operations/lines/{lineId}/state";
    public const string OperationsStationEmergencyStop =
        "api/operations/stations/{stationSystemId}/emergency-stop";
    public const string OperationsSafetyEvents = "api/operations/safety-events";
    public const string RuntimeSessions = "api/runtime/sessions";
    public const string ProjectApplicationTopologies =
        AutomationProjects + "/{projectId}/applications/{applicationId}/topologies";
    public const string ProjectApplicationSiteLayouts =
        AutomationProjects + "/{projectId}/applications/{applicationId}/layouts";
    public const string ProjectApplicationProcesses =
        AutomationProjects + "/{projectId}/applications/{applicationId}/processes";
    public const string ProjectApplicationProcessBlocklyBlocks =
        AutomationProjects + "/{projectId}/applications/{applicationId}/process-blocks";
    public const string ProjectApplicationEngineering =
        AutomationProjects + "/{projectId}/applications/{applicationId}/engineering";
    public const string ProjectApplicationProductionLines =
        AutomationProjects + "/{projectId}/applications/{applicationId}/production-lines";
    public const string ProjectApplicationExternalPrograms =
        AutomationProjects + "/{projectId}/applications/{applicationId}/external-programs";
    public const string ProjectSnapshotProductionRunContext =
        AutomationProjects + "/{projectId}/snapshots/{snapshotId}/production-run-context";
    public const string Traceability = "api/traceability";
}
