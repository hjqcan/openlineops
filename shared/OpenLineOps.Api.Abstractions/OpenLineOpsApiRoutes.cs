namespace OpenLineOps.Api.Abstractions;

public static class OpenLineOpsApiRoutes
{
    public const string Platform = "api/platform";
    public const string Devices = "api/devices";
    public const string Engineering = "api/engineering";
    public const string OperationsAlarms = "api/operations/alarms";
    public const string Plugins = "api/plugins";
    public const string AutomationProjects = "api/automation-projects";
    public const string AutomationProjectWorkspaces = "api/automation-project-workspaces";
    public const string ProcessBlocklyBlocks = "api/process-blocks";
    public const string ProcessDefinitions = "api/process-definitions";
    public const string RuntimeMonitoring = "api/runtime/monitoring";
    public const string RuntimeSessions = "api/runtime/sessions";
    public const string AutomationTopologies = "api/automation-topologies";
    public const string SiteLayouts = "api/site-layouts";
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
    public const string Traceability = "api/traceability";
}
