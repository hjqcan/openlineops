using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OpenLineOps.Api.Abstractions;

[AttributeUsage(AttributeTargets.Method)]
public sealed class DevelopmentRuntimeStartOnlyAttribute : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var services = context.HttpContext.RequestServices;
        var hostEnvironment = services.GetRequiredService<IHostEnvironment>();
        var configuration = services.GetRequiredService<IConfiguration>();
        if (DevelopmentRuntimeStartPolicy.IsAllowed(
                hostEnvironment.EnvironmentName,
                configuration[DevelopmentRuntimeStartPolicy.EnabledConfigurationKey]))
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = DevelopmentRuntimeStartPolicy.DisabledErrorCode,
            Detail = DevelopmentRuntimeStartPolicy.DisabledErrorMessage
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}
