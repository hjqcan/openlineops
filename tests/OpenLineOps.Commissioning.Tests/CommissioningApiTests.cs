using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Commissioning.Api.Controllers;
using OpenLineOps.Commissioning.Api.DependencyInjection;
using OpenLineOps.Commissioning.Api.Models;
using OpenLineOps.Commissioning.Application.Persistence;
using OpenLineOps.Commissioning.Application.Security;
using OpenLineOps.Commissioning.Application.Services;
using OpenLineOps.Commissioning.Domain.Sessions;
using OpenLineOps.Commissioning.Infrastructure.Persistence;

namespace OpenLineOps.Commissioning.Tests;

public sealed class CommissioningApiTests
{
    [Fact]
    public void ControllersSeparateEngineeringAndStationAgentPolicies()
    {
        var engineering = Assert.Single(
            typeof(CommissioningEngineeringController)
                .GetCustomAttributes<AuthorizeAttribute>());
        var stationAgent = Assert.Single(
            typeof(CommissioningStationAgentController)
                .GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal(OpenLineOpsApiSecurity.EngineeringPolicy, engineering.Policy);
        Assert.Equal(OpenLineOpsApiSecurity.StationAgentPolicy, stationAgent.Policy);
    }

    [Fact]
    public async Task StartUsesAuthenticatedActorAndRejectsUnheldRole()
    {
        using var harness = new ApiHarness();
        harness.SetPrincipal(
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);

        var denied = await harness.Engineering.StartAsync(
            "station-a",
            StartRequest("session-denied") with
            {
                AuthorizedRole = "CommissioningEngineer"
            },
            CancellationToken.None);
        var accepted = await harness.Engineering.StartAsync(
            "station-a",
            StartRequest("session-a"),
            CancellationToken.None);

        AssertStatus(denied, StatusCodes.Status409Conflict);
        var response = Created(accepted);
        Assert.Equal("engineer-a", response.RequestedBy);
        Assert.Equal(OpenLineOpsApiSecurity.EngineeringRole, response.AuthorizedRole);
    }

    [Fact]
    public async Task OnlyOneActiveSessionCanHoldAStationLease()
    {
        using var harness = new ApiHarness();
        harness.SetPrincipal(
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);

        var first = await harness.Engineering.StartAsync(
            "station-a",
            StartRequest("session-a"),
            CancellationToken.None);
        var conflict = await harness.Engineering.StartAsync(
            "station-a",
            StartRequest("session-b"),
            CancellationToken.None);
        var otherStation = await harness.Engineering.StartAsync(
            "station-b",
            StartRequest("session-c"),
            CancellationToken.None);

        _ = Created(first);
        AssertStatus(conflict, StatusCodes.Status409Conflict);
        _ = Created(otherStation);
    }

    [Fact]
    public async Task SessionCapabilitiesGateDebugOperations()
    {
        using var harness = new ApiHarness();
        harness.SetPrincipal(
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        _ = Created(await harness.Engineering.StartAsync(
            "station-a",
            StartRequest(
                "session-a",
                [nameof(CommissioningCapability.DeviceDiagnostics)]),
            CancellationToken.None));

        var diagnostic = await harness.Engineering.RecordDiagnosticAccessAsync(
            "station-a",
            "session-a",
            new CommissioningSubjectRequest(41, "device-a"),
            CancellationToken.None);
        var flowStep = await harness.Engineering.AuthorizeFlowStepAsync(
            "station-a",
            "session-a",
            new CommissioningSubjectRequest(41, "node-a"),
            CancellationToken.None);

        Assert.Equal(
            nameof(CommissioningAuditKind.DiagnosticAccessed),
            Ok(diagnostic).AuditTrail.Last().Kind);
        AssertStatus(flowStep, StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task ManualCommandAndBreakpointEnforceFenceSafetyModeAndWhitelist()
    {
        using var harness = new ApiHarness();
        harness.SetPrincipal(
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        _ = Created(await harness.Engineering.StartAsync(
            "station-a",
            StartRequest("session-a"),
            CancellationToken.None));

        var staleFence = await harness.Engineering.AuthorizeManualCommandAsync(
            "station-a",
            "session-a",
            new AuthorizeCommissioningManualCommandRequest(
                40,
                "command-stale",
                "Maintenance",
                "Normal",
                true),
            CancellationToken.None);
        var safetyCritical = await harness.Engineering.AuthorizeManualCommandAsync(
            "station-a",
            "session-a",
            new AuthorizeCommissioningManualCommandRequest(
                41,
                "command-safety",
                "Simulation",
                "SafetyCritical",
                true),
            CancellationToken.None);
        var accepted = await harness.Engineering.AuthorizeManualCommandAsync(
            "station-a",
            "session-a",
            new AuthorizeCommissioningManualCommandRequest(
                41,
                "command-clamp",
                "Maintenance",
                "Motion",
                true),
            CancellationToken.None);
        var breakpointDenied = await harness.Engineering.SetBreakpointAsync(
            "station-a",
            "session-a",
            new SetCommissioningBreakpointRequest(
                41,
                "node-a",
                "Setup",
                false),
            CancellationToken.None);
        var breakpointAccepted = await harness.Engineering.SetBreakpointAsync(
            "station-a",
            "session-a",
            new SetCommissioningBreakpointRequest(
                41,
                "node-b",
                "Simulation",
                false),
            CancellationToken.None);

        AssertStatus(staleFence, StatusCodes.Status409Conflict);
        AssertStatus(safetyCritical, StatusCodes.Status409Conflict);
        Assert.Equal(
            nameof(CommissioningAuditKind.ManualCommandAuthorized),
            Ok(accepted).AuditTrail.Last().Kind);
        AssertStatus(breakpointDenied, StatusCodes.Status409Conflict);
        Assert.Equal(
            nameof(CommissioningAuditKind.BreakpointSet),
            Ok(breakpointAccepted).AuditTrail.Last().Kind);
    }

    [Fact]
    public async Task NonIdempotentRecoveryRequiresStationAgentFenceAndHumanDisposition()
    {
        using var harness = new ApiHarness();
        harness.SetPrincipal(
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        _ = Created(await harness.Engineering.StartAsync(
            "station-a",
            StartRequest("session-a"),
            CancellationToken.None));

        harness.SetPrincipal(
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            stationId: "station-b");
        var wrongStation = await harness.StationAgent.RecoverInterruptedActionAsync(
            "station-a",
            "session-a",
            new RecoverCommissioningActionRequest(
                41,
                "action-clamp",
                "NonIdempotent"),
            CancellationToken.None);

        harness.SetPrincipal(
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            stationId: "station-a");
        var staleFence = await harness.StationAgent.RecoverInterruptedActionAsync(
            "station-a",
            "session-a",
            new RecoverCommissioningActionRequest(
                40,
                "action-clamp",
                "NonIdempotent"),
            CancellationToken.None);
        var interrupted = await harness.StationAgent.RecoverInterruptedActionAsync(
            "station-a",
            "session-a",
            new RecoverCommissioningActionRequest(
                41,
                "action-clamp",
                "NonIdempotent"),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(wrongStation.Result);
        AssertStatus(staleFence, StatusCodes.Status409Conflict);
        Assert.Equal(
            nameof(CommissioningSessionStatus.RecoveryRequired),
            Ok(interrupted).Status);

        harness.SetPrincipal(
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        var replay = await harness.Engineering.ResolveRecoveryAsync(
            "station-a",
            "session-a",
            new ResolveCommissioningRecoveryRequest(41, "Replay"),
            CancellationToken.None);
        var skip = await harness.Engineering.ResolveRecoveryAsync(
            "station-a",
            "session-a",
            new ResolveCommissioningRecoveryRequest(41, "Skip"),
            CancellationToken.None);

        AssertStatus(replay, StatusCodes.Status409Conflict);
        Assert.Equal(nameof(CommissioningSessionStatus.Active), Ok(skip).Status);
        Assert.Equal(
            nameof(CommissioningAuditKind.RecoveryResolved),
            Ok(skip).AuditTrail.Last().Kind);
    }

    [Fact]
    public async Task ModuleCompositionRegistersApplicationPartClaimsPolicyAndSqlite()
    {
        using var database = new TemporaryDatabase();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{CommissioningPersistenceOptions.SectionName}:Provider"] =
                    CommissioningPersistenceOptions.SqliteProvider,
                [$"{CommissioningPersistenceOptions.SectionName}:DatabasePath"] =
                    database.DatabasePath
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenLineOpsCommissioningModule(configuration);
        services.AddControllers().AddOpenLineOpsCommissioningApi();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true
            });
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = CreateHttpContext(
            "engineer-a",
            [OpenLineOpsApiSecurity.EngineeringRole],
            stationId: null);
        using var scope = provider.CreateScope();

        var repository = scope.ServiceProvider
            .GetRequiredService<ICommissioningSessionRepository>();
        var accessPolicy = scope.ServiceProvider
            .GetRequiredService<ICommissioningAccessPolicy>();
        var service = scope.ServiceProvider
            .GetRequiredService<ICommissioningService>();
        var parts = provider.GetRequiredService<ApplicationPartManager>();
        var result = await service.StartAsync(
            new(
                "session-a",
                "station-a",
                "engineer-a",
                OpenLineOpsApiSecurity.EngineeringRole,
                "lease.session-a",
                41,
                TimeSpan.FromMinutes(30),
                [CommissioningCapability.DeviceDiagnostics]));

        Assert.IsType<SqliteCommissioningSessionRepository>(repository);
        Assert.Equal(
            "ClaimsCommissioningAccessPolicy",
            accessPolicy.GetType().Name);
        Assert.Contains(
            parts.ApplicationParts,
            part => part.Name == typeof(CommissioningEngineeringController)
                .Assembly.GetName().Name);
        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(database.DatabasePath));
    }

    private static StartCommissioningSessionRequest StartRequest(
        string sessionId,
        IReadOnlyCollection<string>? capabilities = null) =>
        new(
            sessionId,
            OpenLineOpsApiSecurity.EngineeringRole,
            $"lease.{sessionId}",
            41,
            DurationSeconds: 3600,
            capabilities ?? Enum.GetNames<CommissioningCapability>());

    private static CommissioningSessionResponse Created(
        ActionResult<CommissioningSessionResponse> actionResult)
    {
        var created = Assert.IsType<CreatedResult>(actionResult.Result);
        return Assert.IsType<CommissioningSessionResponse>(created.Value);
    }

    private static CommissioningSessionResponse Ok(
        ActionResult<CommissioningSessionResponse> actionResult)
    {
        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        return Assert.IsType<CommissioningSessionResponse>(ok.Value);
    }

    private static void AssertStatus(
        ActionResult<CommissioningSessionResponse> actionResult,
        int expectedStatus)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(actionResult.Result);
        Assert.Equal(expectedStatus, result.StatusCode);
        _ = Assert.IsType<ProblemDetails>(result.Value);
    }

    private static DefaultHttpContext CreateHttpContext(
        string actorId,
        IReadOnlyCollection<string> roles,
        string? stationId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, actorId)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        if (stationId is not null)
        {
            claims.Add(new Claim(OpenLineOpsApiSecurity.StationIdClaim, stationId));
        }

        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    claims,
                    authenticationType: "commissioning-tests"))
        };
    }

    private sealed class ApiHarness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        private readonly IHttpContextAccessor _accessor;

        public ApiHarness()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{CommissioningPersistenceOptions.SectionName}:Provider"] =
                        CommissioningPersistenceOptions.InMemoryProvider
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOpenLineOpsCommissioningModule(configuration);
            services.AddControllers().AddOpenLineOpsCommissioningApi();
            _provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateScopes = true
                });
            _scope = _provider.CreateScope();
            _accessor = _provider.GetRequiredService<IHttpContextAccessor>();
            var service = _scope.ServiceProvider
                .GetRequiredService<ICommissioningService>();
            Engineering = new CommissioningEngineeringController(service);
            StationAgent = new CommissioningStationAgentController(service);
        }

        public CommissioningEngineeringController Engineering { get; }

        public CommissioningStationAgentController StationAgent { get; }

        public void SetPrincipal(
            string actorId,
            string role,
            string? stationId = null)
        {
            var context = CreateHttpContext(actorId, [role], stationId);
            _accessor.HttpContext = context;
            Engineering.ControllerContext = new ControllerContext
            {
                HttpContext = context
            };
            StationAgent.ControllerContext = new ControllerContext
            {
                HttpContext = context
            };
        }

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-commissioning-api-{Guid.NewGuid():N}");

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "commissioning.sqlite");
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
