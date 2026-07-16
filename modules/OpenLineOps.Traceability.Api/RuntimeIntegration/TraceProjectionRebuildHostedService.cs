using Microsoft.Extensions.Hosting;
using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class TraceProjectionRebuildOptions
{
    public const string SectionName = "OpenLineOps:Traceability:ProjectionRebuild";
    public const int DefaultPageSize = 100;

    public TraceProjectionRebuildOptions(bool enabled, int pageSize)
    {
        if (pageSize is < 1 or > ProductionRunTerminalPageRequest.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"Trace projection rebuild page size must be between 1 and {ProductionRunTerminalPageRequest.MaximumPageSize}.");
        }

        Enabled = enabled;
        PageSize = pageSize;
    }

    public bool Enabled { get; }

    public int PageSize { get; }
}

public sealed class TraceProjectionRebuildHostedService(
    ITraceProjectionRebuilder rebuilder,
    TraceProjectionRebuildOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        options.Enabled
            ? rebuilder.RebuildAsync(options.PageSize, cancellationToken).AsTask()
            : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
