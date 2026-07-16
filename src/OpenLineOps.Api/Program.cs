using OpenLineOps.Api;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Api.Health;
using OpenLineOps.Api.Security;
using OpenLineOps.Devices.Api.DependencyInjection;
using OpenLineOps.Engineering.Api.DependencyInjection;
using OpenLineOps.EventBus.DependencyInjection;
using OpenLineOps.Operations.Api.DependencyInjection;
using OpenLineOps.Operations.Infra.CrossCutting.IoC.DependencyInjection;
using OpenLineOps.Plugins.Api.DependencyInjection;
using OpenLineOps.Processes.Api.DependencyInjection;
using OpenLineOps.Production.Api.DependencyInjection;
using OpenLineOps.Projects.Api.DependencyInjection;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Topology.Api.DependencyInjection;
using OpenLineOps.Traceability.Api.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
var desktopProcessHandshake = DesktopProcessHandshake.FromEnvironment();
using var desktopParentProcessLifetime = DesktopParentProcessLifetime.FromEnvironment(
    desktopProcessHandshake is not null);

HttpsOrLoopbackMiddleware.ValidateConfiguredUrls(builder.Configuration);

builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<OpenLineOpsSecurityOptions>,
    OpenLineOpsSecurityOptionsValidator>();
builder.Services
    .AddOptions<OpenLineOpsSecurityOptions>()
    .Bind(builder.Configuration.GetSection(OpenLineOpsSecurityOptions.SectionName))
    .ValidateOnStart();
builder.Services
    .AddAuthentication(OpenLineOpsApiSecurity.AuthenticationScheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
        OpenLineOpsBearerAuthenticationHandler>(
        OpenLineOpsApiSecurity.AuthenticationScheme,
        static _ => { });
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
    .AddOpenLineOpsRuntimeApi()
    .AddOpenLineOpsProcessesApi()
    .AddOpenLineOpsProductionApi()
    .AddOpenLineOpsDevicesApi()
    .AddOpenLineOpsEngineeringApi()
    .AddOpenLineOpsOperationsApi()
    .AddOpenLineOpsPluginsApi()
    .AddOpenLineOpsProjectsApi()
    .AddOpenLineOpsTopologyApi()
    .AddOpenLineOpsTraceabilityApi();
builder.Services.AddOpenLineOpsProjectsModule();
builder.Services.AddOpenLineOpsTopologyModule();
builder.Services.AddOpenLineOpsRuntimeModule(builder.Configuration);
builder.Services.AddOpenLineOpsProcessesModule();
builder.Services.AddOpenLineOpsProductionModule();
builder.Services.AddOpenLineOpsEngineeringModule();
builder.Services.AddOpenLineOpsDevicesModule(builder.Configuration);
builder.Services.AddOpenLineOpsOperationsModule(builder.Configuration);
builder.Services.AddOpenLineOpsPluginsModule(builder.Configuration);
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
var desktopParentMonitor = desktopParentProcessLifetime?.MonitorAsync(app.Lifetime);
if (desktopProcessHandshake is not null)
{
    await desktopProcessHandshake.PublishBoundEndpointAsync(app, app.Lifetime.ApplicationStopping);
}
await app.WaitForShutdownAsync();
if (desktopParentMonitor is not null)
{
    await desktopParentMonitor;
}

public partial class Program;
