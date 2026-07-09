using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Devices.Api.Controllers;

namespace OpenLineOps.Devices.Api.DependencyInjection;

public static class DevicesApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsDevicesApi(this IMvcBuilder mvcBuilder)
    {
        return mvcBuilder.AddApplicationPart(typeof(DeviceConfigurationController).Assembly);
    }
}
