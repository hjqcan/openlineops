using OpenLineOps.Api.Abstractions;
using OpenLineOps.Api.Health;
using OpenLineOps.Devices.Api.DependencyInjection;
using OpenLineOps.Engineering.Api.DependencyInjection;
using OpenLineOps.EventBus.DependencyInjection;
using OpenLineOps.Operations.Api.DependencyInjection;
using OpenLineOps.Operations.Infra.CrossCutting.IoC.DependencyInjection;
using OpenLineOps.Plugins.Api.DependencyInjection;
using OpenLineOps.Processes.Api.DependencyInjection;
using OpenLineOps.Projects.Api.DependencyInjection;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Topology.Api.DependencyInjection;
using OpenLineOps.Traceability.Api.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddOpenLineOpsRuntimeApi()
    .AddOpenLineOpsProcessesApi()
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
builder.Services.AddOpenLineOpsProcessesModule(builder.Configuration);
builder.Services.AddOpenLineOpsEngineeringModule(builder.Configuration);
builder.Services.AddOpenLineOpsDevicesModule(builder.Configuration);
builder.Services.AddOpenLineOpsOperationsModule(builder.Configuration);
builder.Services.AddOpenLineOpsPluginsModule(builder.Configuration);
builder.Services.AddOpenLineOpsTraceabilityModule(builder.Configuration);
builder.Services.AddOpenLineOpsEventBus(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenLineOpsReadinessHealthChecks(builder.Configuration);
builder.Services.AddOpenApi(OpenLineOpsApiVersions.Current);
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
app.UseCors("OpenLineOpsDesktop");
app.UseAuthorization();

app.MapControllers();
app.MapOpenLineOpsRuntimeRealtime();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "OpenLineOps.Api"
}))
    .WithName("GetLiveness")
    .WithGroupName(OpenLineOpsApiGroups.HealthV1)
    .WithTags("Health")
    .AllowAnonymous();

app.MapGet("/", () => Results.Redirect("/api/platform"))
    .ExcludeFromDescription();

app.Run();

public partial class Program;
