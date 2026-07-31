using OpenLineOps.Api;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Api.Health;
using OpenLineOps.Api.Integrations;
using OpenLineOps.Api.Security;
using OpenLineOps.Commissioning.Api.DependencyInjection;
using OpenLineOps.Devices.Api.DependencyInjection;
using OpenLineOps.Engineering.Api.DependencyInjection;
using OpenLineOps.EventBus.DependencyInjection;
using OpenLineOps.Integration.Api.DependencyInjection;
using OpenLineOps.Maintenance.Api.DependencyInjection;
using OpenLineOps.Maintenance.Api.RuntimeIntegration;
using OpenLineOps.Operations.Api.DependencyInjection;
using OpenLineOps.Operations.Infra.CrossCutting.IoC.DependencyInjection;
using OpenLineOps.Operations.Metrics.Api.DependencyInjection;
using OpenLineOps.Plugins.Api.DependencyInjection;
using OpenLineOps.Processes.Api.DependencyInjection;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Production.Api.DependencyInjection;
using OpenLineOps.Projects.Api.DependencyInjection;
using OpenLineOps.Quality.Api.DependencyInjection;
using OpenLineOps.Recipes.Api.DependencyInjection;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Topology.Api.DependencyInjection;
using OpenLineOps.Traceability.Api.DependencyInjection;

var desktopProcessHandshake = DesktopProcessHandshake.FromEnvironment();
using var desktopParentProcessLifetime = DesktopParentProcessLifetime.FromEnvironment(
    desktopProcessHandshake is not null);
var desktopProcessTreeLifetime = desktopProcessHandshake is null
    ? null
    : WindowsCurrentProcessTreeLifetime.BindCurrentProcess();
var builder = WebApplication.CreateBuilder(args);

HttpsOrLoopbackMiddleware.ValidateConfiguredUrls(builder.Configuration);

builder.Services.AddOpenLineOpsAuthentication(builder.Configuration);
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(
        OpenLineOpsApiSecurity.EngineeringPolicy,
        policy => policy.RequireRole(OpenLineOpsApiSecurity.EngineeringRole))
    .AddPolicy(
        OpenLineOpsApiSecurity.OperatorPolicy,
        policy => policy.RequireRole(OpenLineOpsApiSecurity.OperatorRole))
    .AddPolicy(
        OpenLineOpsApiSecurity.SafetyPolicy,
        policy => policy.RequireRole(OpenLineOpsApiSecurity.SafetyRole))
    .AddPolicy(
        OpenLineOpsApiSecurity.SafetyConfirmationPolicy,
        policy => policy.RequireRole(
            OpenLineOpsApiSecurity.SafetyRole,
            OpenLineOpsApiSecurity.OperatorRole))
    .AddPolicy(
        OpenLineOpsApiSecurity.StationAgentPolicy,
        policy => policy.RequireRole(OpenLineOpsApiSecurity.StationAgentRole))
    .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole(
            OpenLineOpsApiSecurity.EngineeringRole,
            OpenLineOpsApiSecurity.OperatorRole)
        .Build())
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole(
            OpenLineOpsApiSecurity.EngineeringRole,
            OpenLineOpsApiSecurity.OperatorRole)
        .Build());

builder.Services
    .AddControllers()
    .AddOpenLineOpsCommissioningApi()
    .AddOpenLineOpsRuntimeApi()
    .AddOpenLineOpsProcessesApi()
    .AddOpenLineOpsProductionApi()
    .AddOpenLineOpsDevicesApi()
    .AddOpenLineOpsEngineeringApi()
    .AddOpenLineOpsIntegrationApi()
    .AddOpenLineOpsMaintenanceApi()
    .AddOpenLineOpsOperationsApi()
    .AddOpenLineOpsOperationsMetricsApi()
    .AddOpenLineOpsPluginsApi()
    .AddOpenLineOpsProjectsApi()
    .AddOpenLineOpsQualityApi()
    .AddOpenLineOpsRecipesApi()
    .AddOpenLineOpsTopologyApi()
    .AddOpenLineOpsTraceabilityApi();
builder.Services.AddOpenLineOpsProjectsModule();
builder.Services.AddOpenLineOpsTopologyModule();
builder.Services.AddOpenLineOpsCommissioningModule(builder.Configuration);
builder.Services.AddOpenLineOpsRuntimeModule(builder.Configuration);
builder.Services.AddOpenLineOpsProcessesModule();
builder.Services.AddOpenLineOpsProductionModule();
builder.Services.AddOpenLineOpsEngineeringModule();
builder.Services.AddOpenLineOpsIntegrationModule(builder.Configuration);
builder.Services.AddOpenLineOpsMaintenanceModule(builder.Configuration);
builder.Services.AddOpenLineOpsMaintenanceRuntimeGate();
builder.Services.AddOpenLineOpsDevicesModule(builder.Configuration);
builder.Services.AddOpenLineOpsOperationsModule(builder.Configuration);
builder.Services.AddOpenLineOpsOperationsMetricsModule(builder.Configuration);
builder.Services.AddOpenLineOpsPluginsModule(builder.Configuration);
builder.Services.AddOpenLineOpsQualityModule(builder.Configuration);
builder.Services.AddOpenLineOpsRecipesModule(builder.Configuration);
builder.Services.AddOpenLineOpsRecipeRuntimeIntegration();
builder.Services.AddOpenLineOpsTraceabilityModule(builder.Configuration);
builder.Services.AddOpenLineOpsEventBus(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenLineOpsReadinessHealthChecks(builder.Configuration);
builder.Services.AddOpenApi(OpenLineOpsApiDocument.Name);
builder.Services.AddCors(options =>
{
    options.AddPolicy("OpenLineOpsDesktop", policy =>
    {
        var allowedOrigins = builder.Configuration
            .GetSection("OpenLineOps:Desktop:AllowedOrigins")
            .Get<string[]>()
            ?? [
                "http://127.0.0.1:5173",
                "http://localhost:5173"
            ];

        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();
var desktopParentMonitor = desktopParentProcessLifetime?.MonitorAsync(app.Lifetime);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseMiddleware<HttpsOrLoopbackMiddleware>();
app.UseCors("OpenLineOpsDesktop");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapOpenLineOpsRuntimeRealtime();
app.MapHealthChecks("/health/ready");
app.MapGet("/health/live", Results.NoContent)
    .WithName("GetLiveness")
    .WithGroupName(OpenLineOpsApiGroups.Health)
    .WithTags("Health")
    .AllowAnonymous();
if (desktopProcessHandshake is not null)
{
    app.MapGet(
            DesktopProcessHandshake.Endpoint,
            (HttpRequest request, HttpResponse response) =>
                desktopProcessHandshake.Prove(request, response))
        .ExcludeFromDescription()
        .AllowAnonymous();
}

app.MapGet("/", () => Results.Redirect("/api/platform"))
    .ExcludeFromDescription();

await app.StartAsync();
if (desktopProcessHandshake is not null)
{
    await desktopProcessHandshake.PublishBoundEndpointAsync(app, app.Lifetime.ApplicationStopping);
}
await app.WaitForShutdownAsync();
if (desktopParentMonitor is not null)
{
    await desktopParentMonitor;
}
GC.KeepAlive(desktopProcessTreeLifetime);

public partial class Program;
