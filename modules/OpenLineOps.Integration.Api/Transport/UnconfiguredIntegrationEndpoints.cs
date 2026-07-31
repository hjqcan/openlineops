using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Domain.Messages;

namespace OpenLineOps.Integration.Api.Transport;

public sealed class IntegrationEndpointUnavailableException(string message) :
    InvalidOperationException(message);

public sealed class UnconfiguredWorkRequestHandler :
    IWorkRequestHandler,
    IWorkRequestHandlerReadiness
{
    public bool IsReady => false;

    public string UnavailabilityReason =>
        "No inbound enterprise work-request handler is configured.";

    public ValueTask<WorkResponse> HandleAsync(
        WorkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException<WorkResponse>(
            new IntegrationEndpointUnavailableException(UnavailabilityReason));
    }
}

public sealed class UnconfiguredIntegrationConnector :
    IIntegrationConnector,
    IIntegrationConnectorReadiness
{
    public bool IsReady => false;

    public string UnavailabilityReason =>
        "No outbound enterprise integration connector is configured.";

    public ValueTask SendAsync(
        IntegrationOutboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(
            new IntegrationEndpointUnavailableException(
                UnavailabilityReason));
    }
}
