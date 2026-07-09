using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Plugins.Api.Management;
using OpenLineOps.Plugins.Api.Models;

namespace OpenLineOps.Plugins.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.PluginsV1)]
[Route(OpenLineOpsApiRoutes.Plugins)]
public sealed class PluginManagementController : ControllerBase
{
    private readonly IPluginManagementService _pluginManagementService;

    public PluginManagementController(IPluginManagementService pluginManagementService)
    {
        _pluginManagementService = pluginManagementService;
    }

    [HttpGet("overview")]
    [ProducesResponseType<PluginManagementOverviewResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PluginManagementOverviewResponse>> GetOverviewAsync(
        CancellationToken cancellationToken)
    {
        return Ok(await _pluginManagementService
            .GetOverviewAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    [HttpPost("lifecycle/start")]
    [ProducesResponseType<IReadOnlyCollection<PluginLifecycleRecordResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<PluginLifecycleRecordResponse>>> StartAsync(
        CancellationToken cancellationToken)
    {
        return Ok(await _pluginManagementService
            .StartAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    [HttpPost("lifecycle/stop")]
    [ProducesResponseType<IReadOnlyCollection<PluginLifecycleRecordResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<PluginLifecycleRecordResponse>>> StopAsync(
        CancellationToken cancellationToken)
    {
        return Ok(await _pluginManagementService
            .StopAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    [HttpGet("process-events")]
    [ProducesResponseType<IReadOnlyCollection<ExternalPluginProcessEventResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyCollection<ExternalPluginProcessEventResponse>>> ListEventsAsync(
        [FromQuery] string? pluginId,
        [FromQuery] string? kind,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(skip)] = ["Skip must be greater than or equal to zero."]
            }));
        }

        if (take is <= 0 or > 200)
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(take)] = ["Take must be between 1 and 200."]
            }));
        }

        return Ok(await _pluginManagementService
            .ListEventsAsync(pluginId, kind, skip, take, cancellationToken)
            .ConfigureAwait(false));
    }
}
