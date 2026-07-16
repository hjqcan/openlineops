using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Tests;

public sealed class ApiMetadataTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private readonly OpenLineOpsApiWebApplicationFactory _factory;

    public ApiMetadataTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void ControllersUseBoundedContextApiExplorerGroups()
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

    [Fact]
    public void ControllerAndRuntimeHubEndpointsDeclareExplicitNamedAuthorizationPolicies()
    {
        var endpointDataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var protectedEndpoints = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsControllerOrRuntimeHubRoute(endpoint.RoutePattern.RawText))
            .ToArray();
        var allowedPolicies = new HashSet<string>(StringComparer.Ordinal)
        {
            OpenLineOpsApiSecurity.EngineeringPolicy,
            OpenLineOpsApiSecurity.OperatorPolicy,
            OpenLineOpsApiSecurity.SafetyPolicy,
            OpenLineOpsApiSecurity.SafetyConfirmationPolicy,
            OpenLineOpsApiSecurity.StationAgentPolicy
        };

        Assert.NotEmpty(protectedEndpoints);
        foreach (var endpoint in protectedEndpoints)
        {
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());

            var authorizeData = endpoint.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .ToArray();
            Assert.NotEmpty(authorizeData);
            Assert.All(authorizeData, metadata =>
            {
                Assert.False(string.IsNullOrWhiteSpace(metadata.Policy));
                Assert.Contains(metadata.Policy!, allowedPolicies);
            });
        }
    }

    private static bool IsControllerOrRuntimeHubRoute(string? route) =>
        route is not null
        && (route.StartsWith("api/", StringComparison.Ordinal)
            || route.StartsWith("hubs/runtime-progress", StringComparison.Ordinal));
}
