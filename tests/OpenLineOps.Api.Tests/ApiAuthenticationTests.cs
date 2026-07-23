using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Api.Security;

namespace OpenLineOps.Api.Tests;

public sealed class ApiAuthenticationTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private readonly OpenLineOpsApiWebApplicationFactory _factory;

    public ApiAuthenticationTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ProtectedApiRequiresCredentialWhileMinimalLivenessRemainsAnonymous()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var protectedResponse = await client.GetAsync("/api/platform");
        using var readiness = await client.GetAsync("/health/ready");
        using var liveness = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, readiness.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, liveness.StatusCode);
        Assert.Empty(await liveness.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DesktopProcessProofEndpointDoesNotExistWithoutExplicitStudioLaunch()
    {
        using var client = _factory.CreateAuthenticatedClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/health/desktop-process");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InvalidCredentialIsUnauthorizedAndWrongRoleIsForbidden()
    {
        using var invalidClient = _factory.CreateClient();
        ApiTestAuthentication.Authenticate(
            invalidClient,
            Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        using var invalid = await invalidClient.GetAsync("/api/platform");
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

        using var operatorClient = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);
        using var forbidden = await operatorClient.PostAsJsonAsync(
            "/api/automation-project-workspaces",
            new
            {
                projectId = "project.authz-forbidden",
                displayName = "Forbidden",
                projectPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                defaultApplicationId = "application.main",
                defaultApplicationName = "Main"
            });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task ActorBodySpoofIsRejectedAndRawTraceInjectionDoesNotExist()
    {
        using var client = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);
        var productionUnitId = Guid.NewGuid();
        using var spoof = await client.PostAsJsonAsync("/api/production-units", new
        {
            productionUnitId,
            productModelId = "product.auth-boundary",
            identityKey = "serialNumber",
            identityValue = $"AUTH-{productionUnitId:N}",
            lotId = (string?)null,
            actorId = "spoofed.actor",
            occurredAtUtc = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero)
        });
        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode);

        using var trace = await client.PostAsJsonAsync(
            "/api/traceability/records",
            new { actorId = ApiTestAuthentication.OperatorActorId });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, trace.StatusCode);
    }

    [Fact]
    public async Task EmergencyStopRequiresSafetyPolicyAndUsesSafetyActorClaim()
    {
        var body = new
        {
            messageId = "91111111-1111-1111-1111-111111111111",
            idempotencyKey = "91111111-1111-1111-1111-111111111112",
            projectId = "project.auth-safety",
            applicationId = "application.auth-safety",
            projectSnapshotId = "snapshot.auth-safety",
            reason = "Guard circuit opened.",
            requestedAtUtc = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture)
        };
        const string path = "/api/operations/stations/station.auth-safety/emergency-stop";

        foreach (var forbiddenToken in new[]
                 {
                     ApiTestAuthentication.EngineeringToken,
                     ApiTestAuthentication.OperatorToken,
                     ApiTestAuthentication.StationAgentToken
                 })
        {
            using var forbiddenClient = _factory.CreateAuthenticatedClient(
                token: forbiddenToken);
            using var forbidden = await forbiddenClient.PostAsJsonAsync(path, body);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using var safetyClient = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.SafetyToken);
        using var authorized = await safetyClient.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.NotFound, authorized.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, authorized.StatusCode);
    }

    [Theory]
    [InlineData("/api/platform", HttpStatusCode.OK)]
    [InlineData("/api/devices/definitions", HttpStatusCode.OK)]
    [InlineData("/api/devices/instances", HttpStatusCode.OK)]
    [InlineData(
        "/api/automation-projects/project.authz/snapshots/snapshot.authz/production-run-context",
        HttpStatusCode.NotFound)]
    [InlineData("/api/operations/active-runs", HttpStatusCode.OK)]
    [InlineData("/api/operations/lines/line.authz/state", HttpStatusCode.OK)]
    [InlineData(
        "/api/production-runs/81111111-1111-1111-1111-111111111111",
        HttpStatusCode.NotFound)]
    [InlineData(
        "/api/runtime/monitoring/stations?projectId=project.authz&applicationId=application.authz&projectSnapshotId=snapshot.authz&topologyId=topology.authz",
        HttpStatusCode.OK)]
    [InlineData("/api/runtime/sessions/recovery-plan", HttpStatusCode.OK)]
    [InlineData("/api/traceability/records", HttpStatusCode.OK)]
    [InlineData(
        "/api/traceability/production-units/81111111-1111-1111-1111-111111111111/material-lifecycle",
        HttpStatusCode.NotFound)]
    [InlineData(
        "/api/traceability/read-models/station-dashboard?stationSystemId=station.authz",
        HttpStatusCode.OK)]
    [InlineData(
        "/api/traceability/station-safety-evidence?projectId=project.authz&applicationId=application.authz",
        HttpStatusCode.OK)]
    [InlineData("/api/traceability/artifacts/missing-authz-artifact.bin", HttpStatusCode.NotFound)]
    public async Task DedicatedSafetyAndStationAgentCredentialsCannotReadOrdinaryDomainSurfaces(
        string path,
        HttpStatusCode operatorStatus)
    {
        foreach (var forbiddenToken in new[]
                 {
                     ApiTestAuthentication.SafetyToken,
                     ApiTestAuthentication.StationAgentToken
                 })
        {
            using var forbiddenClient = _factory.CreateAuthenticatedClient(
                token: forbiddenToken);
            using var forbiddenResponse = await forbiddenClient.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        }

        using var operatorClient = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);
        using var operatorResponse = await operatorClient.GetAsync(path);
        Assert.Equal(operatorStatus, operatorResponse.StatusCode);
    }

    [Fact]
    public async Task DedicatedSafetyAndStationAgentCredentialsCannotConnectToRuntimeProgressHub()
    {
        const string negotiate = "/hubs/runtime-progress/negotiate?negotiateVersion=1";
        foreach (var forbiddenToken in new[]
                 {
                     ApiTestAuthentication.SafetyToken,
                     ApiTestAuthentication.StationAgentToken
                 })
        {
            using var forbiddenClient = _factory.CreateAuthenticatedClient(
                token: forbiddenToken);
            using var forbiddenResponse = await forbiddenClient.PostAsync(
                negotiate,
                new StringContent(string.Empty));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        }

        using var operatorClient = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);
        using var operatorResponse = await operatorClient.PostAsync(
            negotiate,
            new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.OK, operatorResponse.StatusCode);
    }

    [Fact]
    public async Task ReadinessRequiresAnEngineeringOrOperatorCredential()
    {
        foreach (var forbiddenToken in new[]
                 {
                     ApiTestAuthentication.SafetyToken,
                     ApiTestAuthentication.StationAgentToken
                 })
        {
            using var forbiddenClient = _factory.CreateAuthenticatedClient(
                token: forbiddenToken);
            using var forbiddenResponse = await forbiddenClient.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        }

        foreach (var allowedToken in new[]
                 {
                     ApiTestAuthentication.EngineeringToken,
                     ApiTestAuthentication.OperatorToken
                 })
        {
            using var allowedClient = _factory.CreateAuthenticatedClient(
                token: allowedToken);
            using var allowedResponse = await allowedClient.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        }
    }

    [Fact]
    public async Task CleartextRemoteRequestIsRejectedBeforeApiDispatch()
    {
        using var client = _factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://coordinator.example/api/platform");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void SecurityConfigurationRejectsCombinedSafetyRole()
    {
        var result = new OpenLineOpsSecurityOptionsValidator().Validate(
            null,
            new OpenLineOpsSecurityOptions
            {
                Callers =
                [
                    Caller("standard", OpenLineOpsApiSecurity.EngineeringRole),
                    Caller(
                        "unsafe-safety",
                        OpenLineOpsApiSecurity.SafetyRole,
                        OpenLineOpsApiSecurity.OperatorRole)
                ]
            });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "Safety must use a dedicated credential",
                StringComparison.Ordinal));
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "at least one dedicated Safety-only credential",
                StringComparison.Ordinal));
    }

    [Fact]
    public void SecurityConfigurationRequiresSafetyOnlyCredential()
    {
        var result = new OpenLineOpsSecurityOptionsValidator().Validate(
            null,
            new OpenLineOpsSecurityOptions
            {
                Callers =
                [
                    Caller(
                        "standard",
                        OpenLineOpsApiSecurity.EngineeringRole,
                        OpenLineOpsApiSecurity.OperatorRole)
                ]
            });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "at least one dedicated Safety-only credential",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AgentRabbitMqConfigurationRequiresDedicatedStationAgentCredential()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:StationExecution:Provider"] = "Agent",
                ["OpenLineOps:Runtime:AgentTransport:Provider"] = "RabbitMq"
            })
            .Build();
        var result = new OpenLineOpsSecurityOptionsValidator(configuration).Validate(
            null,
            new OpenLineOpsSecurityOptions
            {
                Callers = [Caller("safety", OpenLineOpsApiSecurity.SafetyRole)]
            });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "dedicated StationAgent credential",
                StringComparison.Ordinal));
    }

    [Fact]
    public void InProcessDisabledConfigurationDoesNotRequireStationAgentCredential()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess",
                ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled"
            })
            .Build();
        var result = new OpenLineOpsSecurityOptionsValidator(configuration).Validate(
            null,
            new OpenLineOpsSecurityOptions
            {
                Callers = [Caller("safety", OpenLineOpsApiSecurity.SafetyRole)]
            });

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    private static OpenLineOpsCallerCredentialOptions Caller(
        string credentialId,
        params string[] roles) => new()
        {
            CredentialId = credentialId,
            ActorId = $"actor.{credentialId}",
            TokenSha256 = new string(credentialId.StartsWith("unsafe", StringComparison.Ordinal) ? 'b' : 'a', 64),
            Roles = [.. roles]
        };
}
