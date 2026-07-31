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
using OpenLineOps.Integration.Api.DependencyInjection;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.Messages;

namespace OpenLineOps.Integration.Api.Tests;

internal sealed class IntegrationApiTestHarness : IAsyncDisposable
{
    private const string TestAuthenticationScheme = "IntegrationApiTest";
    private readonly WebApplication _application;
    private readonly string _directory;

    private IntegrationApiTestHarness(
        WebApplication application,
        string directory)
    {
        _application = application;
        _directory = directory;
        Client = application.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _application.Services;

    public static async Task<IntegrationApiTestHarness> CreateAsync(
        IWorkRequestHandler? workRequestHandler = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-integration-api-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "integration.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{IntegrationPersistenceOptions.SectionName}:ConnectionString"] =
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
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
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
            .AddOpenLineOpsIntegrationApi();
        builder.Services.AddOpenLineOpsIntegrationModule(configuration);
        builder.Services.RemoveAll<TimeProvider>();
        builder.Services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(TestData.ReceivedAtUtc));
        if (workRequestHandler is not null)
        {
            builder.Services.RemoveAll<IWorkRequestHandler>();
            builder.Services.AddSingleton(workRequestHandler);
        }

        builder.Services.AddProblemDetails();
        var application = builder.Build();
        application.UseExceptionHandler();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapControllers();
        await application.StartAsync();
        return new IntegrationApiTestHarness(application, directory);
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
            request.Headers.Add(HeaderAuthenticationHandler.StationHeader, stationId);
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
            request.Headers.Add(HeaderAuthenticationHandler.StationHeader, stationId);
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
            if (!Request.Headers.TryGetValue(ActorHeader, out var actorValues)
                || !Request.Headers.TryGetValue(RoleHeader, out var roleValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var actor = actorValues.ToString();
            var role = roleValues.ToString();
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, actor),
                new(ClaimTypes.Role, role)
            };
            if (Request.Headers.TryGetValue(StationHeader, out var stationValues))
            {
                claims.Add(new Claim(
                    OpenLineOpsApiSecurity.StationIdClaim,
                    stationValues.ToString()));
            }

            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims, TestAuthenticationScheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, TestAuthenticationScheme)));
        }
    }
}

internal sealed class ReadyWorkRequestHandler :
    IWorkRequestHandler,
    IWorkRequestHandlerReadiness
{
    private int _invocationCount;

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public bool IsReady => true;

    public string? UnavailabilityReason => null;

    public ValueTask<WorkResponse> HandleAsync(
        WorkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _invocationCount);
        return ValueTask.FromResult(new WorkResponse(
            new WorkResponseId($"response-{request.Id.Value}"),
            request.Id,
            request.WorkOrderId,
            WorkResponseStatus.Accepted,
            request.SourceSystem,
            request.OccurredAtUtc.AddSeconds(1),
            """{"accepted":true}"""));
    }
}

internal static class TestData
{
    public static readonly DateTimeOffset OccurredAtUtc =
        new(2026, 7, 31, 2, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset ReceivedAtUtc =
        OccurredAtUtc.AddSeconds(5);

    public static object WorkRequest(
        string requestId = "request-001",
        string stationId = "station-a",
        int quantity = 4) =>
        new
        {
            workRequestId = requestId,
            workOrderId = "order-001",
            kind = "CreateOrUpdate",
            stationId,
            occurredAtUtc = OccurredAtUtc,
            payload = new
            {
                productModelId = "model-a",
                quantity
            }
        };
}
