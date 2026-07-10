using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record ProductionRunTerminalDomainEvent(ProductionRunSnapshot Run)
    : DomainEvent("ProductionRun.Terminal");
