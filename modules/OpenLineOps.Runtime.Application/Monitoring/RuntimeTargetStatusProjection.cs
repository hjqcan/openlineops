using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using CommandExecutionStatus = OpenLineOps.Runtime.Contracts.ExecutionStatus;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record RuntimeTargetStatusProjection(
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    ProductionRunId ProductionRunId,
    string ProductionLineDefinitionId,
    string OperationId,
    int OperationAttempt,
    string StationSystemId,
    ProductionUnitIdentity ProductionUnitIdentity,
    string RuntimeStationId,
    RuntimeSessionId SessionId,
    string ActionId,
    string TargetKind,
    string TargetId,
    CommandExecutionStatus CommandStatus,
    DateTimeOffset LastTransitionAtUtc,
    bool IsTerminal,
    string? FailureReason)
{
    public static RuntimeTargetStatusProjection FromCommandStatusChanged(
        RuntimeSession session,
        RuntimeCommandStatusChangedDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (session.Id != domainEvent.SessionId)
        {
            throw new InvalidOperationException(
                $"Runtime command event session {domainEvent.SessionId} does not match session {session.Id}.");
        }

        var command = session.Commands.SingleOrDefault(candidate => candidate.Id == domainEvent.CommandId)
            ?? throw new InvalidOperationException(
                $"Runtime command {domainEvent.CommandId} was not found in session {session.Id}.");
        var isTerminal = IsTerminalStatus(domainEvent.ToStatus);

        return new RuntimeTargetStatusProjection(
            session.TraceMetadata.ProjectId,
            session.TraceMetadata.ApplicationId,
            session.TraceMetadata.ProjectSnapshotId,
            session.TraceMetadata.TopologyId,
            session.TraceMetadata.ProductionRunId,
            session.TraceMetadata.ProductionLineDefinitionId,
            session.TraceMetadata.OperationId,
            session.TraceMetadata.OperationAttempt,
            session.TraceMetadata.StationSystemId,
            session.TraceMetadata.ProductionUnitIdentity,
            session.StationId.Value,
            session.Id,
            command.ActionId.Value,
            command.TargetKind,
            command.TargetId,
            domainEvent.ToStatus,
            GetTransitionAtUtc(command, domainEvent.ToStatus),
            isTerminal,
            isTerminal && domainEvent.ToStatus != CommandExecutionStatus.Completed
                ? command.FailureReason
                : null);
    }

    private static DateTimeOffset GetTransitionAtUtc(
        RuntimeCommand command,
        CommandExecutionStatus status)
    {
        return status switch
        {
            CommandExecutionStatus.Pending => command.CreatedAtUtc,
            CommandExecutionStatus.Running => command.StartedAtUtc
                ?? MissingTransitionTimestamp(command, status),
            CommandExecutionStatus.Completed
                or CommandExecutionStatus.Failed
                or CommandExecutionStatus.TimedOut
                or CommandExecutionStatus.Canceled
                or CommandExecutionStatus.Rejected => command.CompletedAtUtc
                    ?? MissingTransitionTimestamp(command, status),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported runtime command status.")
        };
    }

    private static DateTimeOffset MissingTransitionTimestamp(
        RuntimeCommand command,
        CommandExecutionStatus status)
    {
        throw new InvalidOperationException(
            $"Runtime command {command.Id} has status {status} without its transition timestamp.");
    }

    private static bool IsTerminalStatus(CommandExecutionStatus status)
    {
        return status is CommandExecutionStatus.Completed
            or CommandExecutionStatus.Failed
            or CommandExecutionStatus.TimedOut
            or CommandExecutionStatus.Canceled
            or CommandExecutionStatus.Rejected;
    }
}
