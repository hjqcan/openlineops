using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Maintenance.Api;
using OpenLineOps.Maintenance.Api.Controllers;
using OpenLineOps.Maintenance.Api.DependencyInjection;
using OpenLineOps.Maintenance.Api.Models;
using OpenLineOps.Maintenance.Application.Persistence;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Infrastructure.Persistence;

namespace OpenLineOps.Maintenance.Tests;

public sealed class MaintenanceApiTests
{
    [Fact]
    public void ControllersExposeOneRouteFamilyAndSeparatedPolicies()
    {
        AssertControllerPolicy(
            typeof(MaintenanceEngineeringController),
            OpenLineOpsApiSecurity.EngineeringPolicy);
        AssertControllerPolicy(
            typeof(MaintenanceOperatorController),
            OpenLineOpsApiSecurity.OperatorPolicy);
        AssertControllerPolicy(
            typeof(MaintenanceStationAgentController),
            OpenLineOpsApiSecurity.StationAgentPolicy);
    }

    [Fact]
    public void MvcRegistrationUsesStrictJsonAndDiscoversControllers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddOpenLineOpsMaintenanceApi();
        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value
            .JsonSerializerOptions;
        var parts = provider.GetRequiredService<ApplicationPartManager>();

        Assert.False(options.PropertyNameCaseInsensitive);
        Assert.True(options.RespectNullableAnnotations);
        Assert.Equal(
            JsonUnmappedMemberHandling.Disallow,
            options.UnmappedMemberHandling);
        Assert.Contains(
            parts.ApplicationParts,
            part => part.Name
                == typeof(MaintenanceEngineeringController).Assembly
                    .GetName()
                    .Name);
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<RegisterEquipmentAssetRequest>(
                """
                {
                  "assetId": "asset-a",
                  "stationId": "station-a",
                  "displayName": "Tester",
                  "productionCritical": true,
                  "requiresCalibration": true,
                  "occurredAtUtc": "2026-07-31T00:00:00.0000000+00:00",
                  "unexpected": true
                }
                """,
                options));
    }

    [Fact]
    public async Task StationAgentWritesRequireMatchingAssetStationClaim()
    {
        using var harness = new ApiHarness();
        harness.SetPrincipal(
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        _ = Created(
            await harness.Engineering.RegisterAssetAsync(
                new RegisterEquipmentAssetRequest(
                    "asset-a",
                    "station-a",
                    "Tester",
                    ProductionCritical: false,
                    RequiresCalibration: false,
                    MaintenanceTestContext.Now),
                "register-a",
                CancellationToken.None));
        harness.SetPrincipal(
            "agent-b",
            OpenLineOpsApiSecurity.StationAgentRole,
            "station-b");

        var usageDenied = await harness.StationAgent.RecordUsageAsync(
            "asset-a",
            new RecordEquipmentUsageRequest(
                1,
                0.1m,
                MaintenanceTestContext.Now.AddMinutes(1)),
            "usage-denied",
            CancellationToken.None);
        var evaluationDenied =
            await harness.StationAgent.EvaluateMaintenanceDueAsync(
                "asset-a",
                new EvaluateMaintenanceDueRequest(
                    MaintenanceTestContext.Now.AddMinutes(1)),
                "evaluation-denied",
                CancellationToken.None);
        var healthDenied = await harness.StationAgent.RecordHealthAsync(
            "asset-a",
            new RecordEquipmentHealthRequest(
                "Healthy",
                "Online",
                MaintenanceTestContext.Now.AddMinutes(1)),
            "health-denied",
            CancellationToken.None);

        Assert.IsType<ForbidResult>(usageDenied.Result);
        Assert.IsType<ForbidResult>(evaluationDenied.Result);
        Assert.IsType<ForbidResult>(healthDenied.Result);

        harness.SetPrincipal(
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            "station-a");
        var accepted = await harness.StationAgent.RecordUsageAsync(
            "asset-a",
            new RecordEquipmentUsageRequest(
                1,
                0.1m,
                MaintenanceTestContext.Now.AddMinutes(1)),
            "usage-accepted",
            CancellationToken.None);
        Assert.Equal(1, Ok(accepted).TotalCycles);
    }

    [Fact]
    public async Task ApiMapsValidationNotFoundAndConflictToCanonicalStatuses()
    {
        using var harness = new ApiHarness();
        harness.SetPrincipal(
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        var validation = await harness.Engineering.RegisterAssetAsync(
            new RegisterEquipmentAssetRequest(
                " ",
                "station-a",
                "Tester",
                ProductionCritical: false,
                RequiresCalibration: false,
                MaintenanceTestContext.Now),
            "bad-register",
            CancellationToken.None);
        _ = Created(
            await harness.Engineering.RegisterAssetAsync(
                new RegisterEquipmentAssetRequest(
                    "asset-a",
                    "station-a",
                    "Tester",
                    ProductionCritical: false,
                    RequiresCalibration: false,
                    MaintenanceTestContext.Now),
                "register-a",
                CancellationToken.None));
        var conflict = await harness.Engineering.RegisterAssetAsync(
            new RegisterEquipmentAssetRequest(
                "asset-a",
                "station-a",
                "Changed",
                ProductionCritical: false,
                RequiresCalibration: false,
                MaintenanceTestContext.Now),
            "register-conflict",
            CancellationToken.None);

        harness.SetPrincipal(
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole);
        var missing = await harness.Operator.GetAssetAsync(
            "missing-asset",
            CancellationToken.None);

        AssertProblem(validation, StatusCodes.Status400BadRequest);
        AssertProblem(missing, StatusCodes.Status404NotFound);
        AssertProblem(conflict, StatusCodes.Status409Conflict);
    }

    [Fact]
    public void DefaultModuleUsesFileSqliteAndTestOverrideIsExplicit()
    {
        using var directory = new TestDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{MaintenancePersistenceOptions.SectionName}:DatabasePath"] =
                        directory.DatabasePath
                })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsMaintenanceModule(configuration);
        using (var provider = services.BuildServiceProvider(
                   new ServiceProviderOptions { ValidateScopes = true }))
        {
            Assert.IsType<SqliteEquipmentAssetRepository>(
                provider.GetRequiredService<IEquipmentAssetRepository>());
        }

        var testServices = new ServiceCollection();
        testServices.AddOpenLineOpsMaintenanceModule(configuration);
        testServices.AddOpenLineOpsMaintenanceInMemoryForTests();
        using var testProvider = testServices.BuildServiceProvider();
        Assert.IsType<InMemoryEquipmentAssetRepository>(
            testProvider.GetRequiredService<IEquipmentAssetRepository>());
    }

    [Fact]
    public void ModuleRejectsInMemorySqliteConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{MaintenancePersistenceOptions.SectionName}:ConnectionString"] =
                        "Data Source=:memory:"
                })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsMaintenanceModule(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IEquipmentAssetRepository>());
    }

    private static void AssertControllerPolicy(
        Type controllerType,
        string expectedPolicy)
    {
        var authorization = Assert.Single(
            controllerType.GetCustomAttributes<AuthorizeAttribute>());
        var route = Assert.Single(
            controllerType.GetCustomAttributes<RouteAttribute>());
        Assert.Equal(expectedPolicy, authorization.Policy);
        Assert.Equal(MaintenanceApiRoutes.Root, route.Template);
    }

    private static EquipmentAssetResponse Created(
        ActionResult<EquipmentAssetResponse> result)
    {
        var created = Assert.IsType<CreatedResult>(result.Result);
        return Assert.IsType<EquipmentAssetResponse>(created.Value);
    }

    private static EquipmentAssetResponse Ok(
        ActionResult<EquipmentAssetResponse> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<EquipmentAssetResponse>(ok.Value);
    }

    private static void AssertProblem<T>(
        ActionResult<T> result,
        int expectedStatus)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        _ = Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private sealed class ApiHarness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        private readonly FixedClock _clock =
            new FixedClock(MaintenanceTestContext.Now);

        public ApiHarness()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(_clock);
            services.AddSingleton<IClock>(
                static provider => provider.GetRequiredService<FixedClock>());
            services.AddOpenLineOpsMaintenanceInMemoryForTests();
            services.AddControllers().AddOpenLineOpsMaintenanceApi();
            _provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateScopes = true });
            _scope = _provider.CreateScope();
            var service = _scope.ServiceProvider
                .GetRequiredService<IEquipmentMaintenanceService>();
            Engineering = new MaintenanceEngineeringController(service);
            Operator = new MaintenanceOperatorController(service, _clock);
            StationAgent = new MaintenanceStationAgentController(service);
        }

        public MaintenanceEngineeringController Engineering { get; }

        public MaintenanceOperatorController Operator { get; }

        public MaintenanceStationAgentController StationAgent { get; }

        public void SetPrincipal(
            string actorId,
            string role,
            string? stationId = null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, actorId),
                new(ClaimTypes.Role, role)
            };
            if (stationId is not null)
            {
                claims.Add(
                    new Claim(
                        OpenLineOpsApiSecurity.StationIdClaim,
                        stationId));
            }

            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        claims,
                        authenticationType: "maintenance-tests"))
            };
            Engineering.ControllerContext =
                new ControllerContext { HttpContext = context };
            Operator.ControllerContext =
                new ControllerContext { HttpContext = context };
            StationAgent.ControllerContext =
                new ControllerContext { HttpContext = context };
        }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
