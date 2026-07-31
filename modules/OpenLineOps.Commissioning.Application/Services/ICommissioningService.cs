using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Commissioning.Application.Contracts;

namespace OpenLineOps.Commissioning.Application.Services;

public interface ICommissioningService
{
    ValueTask<Result<CommissioningSessionDetails>> StartAsync(
        StartCommissioningSessionCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> RenewLeaseAsync(
        RenewCommissioningLeaseCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> RecordDiagnosticAccessAsync(
        CommissioningSubjectCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> StartSignalMonitoringAsync(
        CommissioningSubjectCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> AuthorizeManualCommandAsync(
        AuthorizeCommissioningManualCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> AuthorizeFlowStepAsync(
        CommissioningSubjectCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> SetBreakpointAsync(
        SetCommissioningBreakpointCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> RecoverInterruptedActionAsync(
        RecoverCommissioningActionCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> ResolveRecoveryAsync(
        ResolveCommissioningRecoveryCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> CompleteAsync(
        string sessionId,
        string actorId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<CommissioningSessionDetails>> AbortAsync(
        AbortCommissioningSessionCommand command,
        CancellationToken cancellationToken = default);
}
