using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Operations.Api.Controllers;
using OpenLineOps.Operations.Api.RuntimeIntegration;
using OpenLineOps.Runtime.Application.Events;

namespace OpenLineOps.Operations.Api.DependencyInjection;

public static class OperationsApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsOperationsApi(this IMvcBuilder mvcBuilder)
    {
        ArgumentNullException.ThrowIfNull(mvcBuilder);

        mvcBuilder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRuntimeDomainEventSubscriber, RuntimeIncidentOperationsAlarmSubscriber>());

        return mvcBuilder.AddApplicationPart(typeof(AlarmsController).Assembly);
    }
}
