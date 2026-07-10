using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Tests;

public sealed class ApiMetadataTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiMetadataTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void ControllersUseBoundedContextV1ApiExplorerGroups()
    {
        var provider = _factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        var groupNames = provider.ApiDescriptionGroups.Items
            .Select(group => group.GroupName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(OpenLineOpsApiGroups.PlatformV1, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.EngineeringV1, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.PluginsV1, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.ProcessesV1, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.ProductionV1, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.RuntimeV1, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.TraceabilityV1, groupNames);
    }
}
