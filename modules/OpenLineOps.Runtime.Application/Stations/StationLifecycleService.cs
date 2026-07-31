using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Stations;

public enum StationLifecycleCommand
{
    Reset,
    Start,
    Complete,
    Hold,
    Unhold,
    Suspend,
    Unsuspend,
    Stop,
    Abort,
    Clear,
    Acknowledge
}

public sealed class StationLifecycleService(
    IStationLifecycleRepository repository,
    IClock clock)
{
    public const int MaximumConcurrencyAttempts = 8;

    public async ValueTask<Result<StationLifecyclePersistenceEntry>> GetAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        var entry = await repository.GetByIdAsync(stationId, cancellationToken)
            .ConfigureAwait(false);
        return entry is null
            ? NotFound(stationId)
            : Result.Success(entry);
    }

    public async ValueTask<Result<StationLifecyclePersistenceEntry>> CreateAsync(
        StationId stationId,
        StationMode mode,
        StationReadiness readiness,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(readiness);
        _ = Required(reason, nameof(reason));
        var station = StationLifecycle.Create(
            stationId,
            mode,
            readiness,
            actorId,
            clock.UtcNow);
        if (!await repository.TryAddAsync(station, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<StationLifecyclePersistenceEntry>(
                ApplicationError.Conflict(
                    "Runtime.StationLifecycleAlreadyExists",
                    $"Station lifecycle {stationId} already exists."));
        }

        return Result.Success(new StationLifecyclePersistenceEntry(station, revision: 0));
    }

    public ValueTask<Result<StationLifecyclePersistenceEntry>> ChangeModeAsync(
        StationId stationId,
        StationMode mode,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            stationId,
            actorId,
            reason,
            StationCommandGrant.ChangeMode,
            (station, authorization, occurredAtUtc) =>
                station.ChangeMode(mode, authorization, reason, occurredAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<StationLifecyclePersistenceEntry>> UpdateReadinessAsync(
        StationId stationId,
        StationReadiness readiness,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        return MutateAsync(
            stationId,
            actorId,
            reason,
            StationCommandGrant.ReportReadiness,
            (station, authorization, occurredAtUtc) =>
                station.UpdateReadiness(readiness, authorization, reason, occurredAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<StationLifecyclePersistenceEntry>> CommandAsync(
        StationId stationId,
        StationLifecycleCommand command,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                "Station lifecycle command is invalid.");
        }

        var grant = GrantFor(command);
        return MutateAsync(
            stationId,
            actorId,
            reason,
            grant,
            (station, authorization, occurredAtUtc) =>
                Execute(station, command, authorization, reason, occurredAtUtc),
            cancellationToken);
    }

    private async ValueTask<Result<StationLifecyclePersistenceEntry>> MutateAsync(
        StationId stationId,
        string actorId,
        string reason,
        StationCommandGrant grant,
        Func<
            StationLifecycle,
            StationCommandAuthorization,
            DateTimeOffset,
            RuntimeOperationResult> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(mutation);
        _ = Required(reason, nameof(reason));
        var authorization = new StationCommandAuthorization(actorId, grant);

        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return NotFound(stationId);
            }

            var result = mutation(entry.Station, authorization, clock.UtcNow);
            if (!result.Succeeded)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(result.Code, result.Message));
            }

            try
            {
                var nextRevision = await repository.SaveAsync(
                        entry.Station,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Success(new StationLifecyclePersistenceEntry(
                    entry.Station,
                    nextRevision));
            }
            catch (StationLifecycleConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
            catch (StationLifecycleConcurrencyException)
            {
                return Result.Failure<StationLifecyclePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationLifecycleConcurrencyConflict",
                        $"Station lifecycle {stationId} kept changing while the command was "
                        + "being applied; retry the command."));
            }
        }

        throw new InvalidOperationException(
            "Station lifecycle concurrency retry loop exited unexpectedly.");
    }

    private static RuntimeOperationResult Execute(
        StationLifecycle station,
        StationLifecycleCommand command,
        StationCommandAuthorization authorization,
        string reason,
        DateTimeOffset occurredAtUtc)
    {
        return command switch
        {
            StationLifecycleCommand.Reset =>
                station.Reset(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Start =>
                station.Start(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Complete =>
                station.Complete(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Hold =>
                station.Hold(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Unhold =>
                station.Unhold(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Suspend =>
                station.Suspend(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Unsuspend =>
                station.Unsuspend(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Stop =>
                station.Stop(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Abort =>
                station.Abort(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Clear =>
                station.Clear(authorization, reason, occurredAtUtc),
            StationLifecycleCommand.Acknowledge =>
                station.AcknowledgeTransition(authorization, reason, occurredAtUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
    }

    private static StationCommandGrant GrantFor(StationLifecycleCommand command)
    {
        return command switch
        {
            StationLifecycleCommand.Reset => StationCommandGrant.Reset,
            StationLifecycleCommand.Start => StationCommandGrant.Start,
            StationLifecycleCommand.Complete => StationCommandGrant.Complete,
            StationLifecycleCommand.Hold => StationCommandGrant.Hold,
            StationLifecycleCommand.Unhold => StationCommandGrant.Unhold,
            StationLifecycleCommand.Suspend => StationCommandGrant.Suspend,
            StationLifecycleCommand.Unsuspend => StationCommandGrant.Unsuspend,
            StationLifecycleCommand.Stop => StationCommandGrant.Stop,
            StationLifecycleCommand.Abort => StationCommandGrant.Abort,
            StationLifecycleCommand.Clear => StationCommandGrant.Clear,
            StationLifecycleCommand.Acknowledge =>
                StationCommandGrant.AcknowledgeTransition,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
    }

    private static Result<StationLifecyclePersistenceEntry> NotFound(StationId stationId)
    {
        return Result.Failure<StationLifecyclePersistenceEntry>(
            ApplicationError.NotFound(
                "Runtime.StationLifecycleNotFound",
                $"Station lifecycle {stationId} was not found."));
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }
}
