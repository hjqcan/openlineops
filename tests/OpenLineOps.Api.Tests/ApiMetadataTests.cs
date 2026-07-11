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

        Assert.Contains(OpenLineOpsApiGroups.Platform, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.Engineering, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.Plugins, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.Processes, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.Production, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.Runtime, groupNames);
        Assert.Contains(OpenLineOpsApiGroups.Traceability, groupNames);
    }
}
