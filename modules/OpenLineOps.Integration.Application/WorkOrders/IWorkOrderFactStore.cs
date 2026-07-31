using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.WorkOrders;

namespace OpenLineOps.Integration.Application.WorkOrders;

public interface IWorkOrderFactStore
{
    ValueTask AppendAsync(
        IEnumerable<WorkOrderFact> facts,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkOrderFact>> ListAsync(
        WorkOrderId workOrderId,
        CancellationToken cancellationToken = default);
}
