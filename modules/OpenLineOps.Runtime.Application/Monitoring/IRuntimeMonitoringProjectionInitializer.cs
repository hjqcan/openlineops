namespace OpenLineOps.Runtime.Application.Monitoring;

public interface IRuntimeMonitoringProjectionInitializer
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
