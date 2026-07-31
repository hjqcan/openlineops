using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public interface ITraceProjectionRebuilder
{
    ValueTask<TraceProjectionRebuildResult> RebuildAsync(
        int pageSize,
        CancellationToken cancellationToken = default);
}

public sealed record TraceProjectionRebuildResult(
    int ScannedRunCount,
    int CreatedRecordCount,
    int ExistingRecordCount,
    int PageCount);

public sealed class TraceProjectionRebuilder(
    IProductionRunRepository productionRuns,
    ProductionRunTraceDomainEventSubscriber subscriber) : ITraceProjectionRebuilder
{
    public async ValueTask<TraceProjectionRebuildResult> RebuildAsync(
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var request = new ProductionRunTerminalPageRequest(pageSize);
        var scannedRunCount = 0;
        var createdRecordCount = 0;
        var existingRecordCount = 0;
        var pageCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await productionRuns
                .ListTerminalAsync(request, cancellationToken)
                .ConfigureAwait(false);
            pageCount = checked(pageCount + 1);

            foreach (var entry in page.Items)
            {
                var outcome = await subscriber
                    .ProjectAsync(entry, cancellationToken)
                    .ConfigureAwait(false);
                scannedRunCount = checked(scannedRunCount + 1);
                if (outcome == TraceRecordProjectionOutcome.Created)
                {
                    createdRecordCount = checked(createdRecordCount + 1);
                }
                else
                {
                    existingRecordCount = checked(existingRecordCount + 1);
                }
            }

            if (page.Next is null)
            {
                break;
            }

            if (page.Items.Count == 0 || Equals(request.After, page.Next))
            {
                throw new InvalidDataException(
                    "Terminal Production Run pagination returned a non-advancing cursor.");
            }

            request = new ProductionRunTerminalPageRequest(pageSize, page.Next);
        }

        return new TraceProjectionRebuildResult(
            scannedRunCount,
            createdRecordCount,
            existingRecordCount,
            pageCount);
    }
}
