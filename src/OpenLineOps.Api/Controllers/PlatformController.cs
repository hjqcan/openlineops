using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Platform)]
[Route(OpenLineOpsApiRoutes.Platform)]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class PlatformController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PlatformInfoResponse>(StatusCodes.Status200OK)]
    public ActionResult<PlatformInfoResponse> Get()
    {
        var assembly = typeof(Program).Assembly.GetName();

        return Ok(new PlatformInfoResponse(
            ProductName: "OpenLineOps",
            ServiceName: assembly.Name ?? "OpenLineOps.Api",
            Version: assembly.Version?.ToString() ?? "0.0.0",
            Runtime: RuntimeInformation.FrameworkDescription,
            Environment: HttpContext.RequestServices
                .GetRequiredService<IHostEnvironment>()
                .EnvironmentName));
    }
}
