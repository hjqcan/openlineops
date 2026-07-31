using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Tests;

public sealed class CommissioningHostApiTests(
    OpenLineOpsApiWebApplicationFactory factory)
    : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private static readonly string[] DiagnosticCapabilities =
        ["DeviceDiagnostics"];

    private static readonly string[] MonitoringCapabilities =
        ["DeviceDiagnostics", "SignalMonitoring"];

    [Fact]
    public async Task HostRegistersCommissioningApiAndBindsAuthenticatedEngineer()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station.commissioning.{suffix}";
        var sessionId = $"session.{suffix}";
        var route = $"/api/stations/{stationId}/commissioning/sessions";
        using var engineering = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);
        using var operatorClient = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);

        using var created = await engineering.PostAsJsonAsync(route, new
        {
            sessionId,
            authorizedRole = OpenLineOpsApiSecurity.EngineeringRole,
            leaseId = $"lease.{suffix}",
            fencingToken = 1001,
            durationSeconds = 3600,
            capabilities = MonitoringCapabilities
        });
        using var createdDocument = await JsonDocument.ParseAsync(
            await created.Content.ReadAsStreamAsync());
        using var operatorRead = await operatorClient.GetAsync(
            $"{route}/{sessionId}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(
            ApiTestAuthentication.EngineeringActorId,
            createdDocument.RootElement.GetProperty("requestedBy").GetString());
        Assert.Equal(
            stationId,
            createdDocument.RootElement.GetProperty("stationId").GetString());
        Assert.Equal(1001, createdDocument.RootElement
            .GetProperty("fencingToken")
            .GetInt64());
        Assert.Equal(HttpStatusCode.Forbidden, operatorRead.StatusCode);
    }

    [Fact]
    public async Task CommissioningHostRejectsUnknownRequestMembers()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = $"station.strict.{suffix}";
        using var engineering = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);

        using var response = await engineering.PostAsJsonAsync(
            $"/api/stations/{stationId}/commissioning/sessions",
            new
            {
                sessionId = $"session.{suffix}",
                authorizedRole = OpenLineOpsApiSecurity.EngineeringRole,
                leaseId = $"lease.{suffix}",
                fencingToken = 1002,
                durationSeconds = 3600,
                capabilities = DiagnosticCapabilities,
                requestedBy = "spoofed-actor"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
