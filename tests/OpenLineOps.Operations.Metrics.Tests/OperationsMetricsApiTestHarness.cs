using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Operations.Metrics.Api.DependencyInjection;
using OpenLineOps.Operations.Metrics.Infrastructure.Persistence;

namespace OpenLineOps.Operations.Metrics.Tests;

internal sealed class OperationsMetricsApiTestHarness : IAsyncDisposable
{
    private const string TestAuthenticationScheme =
        "OperationsMetricsApiTest";

    private readonly WebApplication _application;
    private readonly string _directory;

    private OperationsMetricsApiTestHarness(
        WebApplication application,
        string directory)
    {
        _application = application;
        _directory = directory;
        Client = application.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<OperationsMetricsApiTestHarness> CreateAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-operations-metrics-api-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "metrics.sqlite"),
            Pooling = false
        }.ToString();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{OperationsMetricsPersistenceOptions.SectionName}:ConnectionString"] =
                    connectionString
            })
            .Build();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication(TestAuthenticationScheme)
            .AddScheme<
                AuthenticationSchemeOptions,
                HeaderAuthenticationHandler>(
                TestAuthenticationScheme,
                static _ => { });
        builder.Services
            .AddAuthorizationBuilder()
            .AddPolicy(
                OpenLineOpsApiSecurity.EngineeringPolicy,
                static policy => policy.RequireRole(
                    OpenLineOpsApiSecurity.EngineeringRole))
            .AddPolicy(
                OpenLineOpsApiSecurity.OperatorPolicy,
                static policy => policy.RequireRole(
                    OpenLineOpsApiSecurity.OperatorRole))
            .AddPolicy(
                OpenLineOpsApiSecurity.StationAgentPolicy,
                static policy => policy.RequireRole(
                    OpenLineOpsApiSecurity.StationAgentRole))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        builder.Services
            .AddControllers()
            .AddOpenLineOpsOperationsMetricsApi();
        builder.Services.AddOpenLineOpsOperationsMetricsModule(configuration);
        builder.Services.RemoveAll<TimeProvider>();
        builder.Services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(
                OperationsMetricsTestData.BaseUtc.AddHours(12)));
        builder.Services.AddProblemDetails();

        var application = builder.Build();
        application.UseExceptionHandler();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapControllers();
        await application.StartAsync();
        return new OperationsMetricsApiTestHarness(application, directory);
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string actorId,
        string role,
        object? body = null,
        string? stationId = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(HeaderAuthenticationHandler.ActorHeader, actorId);
        request.Headers.Add(HeaderAuthenticationHandler.RoleHeader, role);
        if (stationId is not null)
        {
            request.Headers.Add(
                HeaderAuthenticationHandler.StationHeader,
                stationId);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> SendRawJsonAsync(
        HttpMethod method,
        string path,
        string actorId,
        string role,
        string json,
        string? stationId = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(HeaderAuthenticationHandler.ActorHeader, actorId);
        request.Headers.Add(HeaderAuthenticationHandler.RoleHeader, role);
        if (stationId is not null)
        {
            request.Headers.Add(
                HeaderAuthenticationHandler.StationHeader,
                stationId);
        }

        request.Content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        public const string ActorHeader = "X-Test-Actor";
        public const string RoleHeader = "X-Test-Role";
        public const string StationHeader = "X-Test-Station";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(
                    ActorHeader,
                    out var actorValues)
                || !Request.Headers.TryGetValue(
                    RoleHeader,
                    out var roleValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, actorValues.ToString()),
                new(ClaimTypes.Role, roleValues.ToString())
            };
            if (Request.Headers.TryGetValue(
                    StationHeader,
                    out var stationValues))
            {
                claims.Add(new Claim(
                    OpenLineOpsApiSecurity.StationIdClaim,
                    stationValues.ToString()));
            }

            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims, TestAuthenticationScheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(
                    principal,
                    TestAuthenticationScheme)));
        }
    }
}
