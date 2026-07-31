using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Persistence;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Domain.Identifiers;
using OpenLineOps.Maintenance.Domain.Readiness;

namespace OpenLineOps.Maintenance.Application.Services;

public sealed class EquipmentMaintenanceService(
    IEquipmentAssetRepository repository) : IEquipmentMaintenanceService
{
    public const int MaximumConcurrencyAttempts = 8;

    public async ValueTask<Result<EquipmentAssetDetails>> RegisterAsync(
        RegisterEquipmentAssetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EquipmentAsset asset;
        try
        {
            asset = EquipmentAsset.Register(
                new EquipmentAssetId(command.AssetId),
                command.StationId,
                command.DisplayName,
                command.ProductionCritical,
                command.RequiresCalibration,
                command.CommandId,
                command.ActorId,
                command.OccurredAtUtc);
        }
        catch (ArgumentException exception)
        {
            return Validation<EquipmentAssetDetails>(exception.Message);
        }

        var added = await repository.TryAddAsync(asset, cancellationToken)
            .ConfigureAwait(false);
        if (added == EquipmentAssetAddResult.Added)
        {
            return Result.Success(ToDetails(asset, asset.Facts.Count));
        }

        var existing = await repository.GetByIdAsync(asset.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null
            && FindCommandFact(existing.Asset, command.CommandId)
                is EquipmentAssetRegisteredFact registration
            && RegistrationMatches(registration, command))
        {
            return Result.Success(ToDetails(existing.Asset, existing.Revision));
        }

        return Conflict<EquipmentAssetDetails>(
            "Maintenance.AssetAlreadyExists",
            $"Equipment asset {command.AssetId} already exists.");
    }

    public async ValueTask<Result<EquipmentAssetDetails>> GetAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        EquipmentAssetId id;
        try
        {
            id = new EquipmentAssetId(assetId);
        }
        catch (ArgumentException exception)
        {
            return Validation<EquipmentAssetDetails>(exception.Message);
        }

        var entry = await repository.GetByIdAsync(id, cancellationToken)
            .ConfigureAwait(false);
        return entry is null
            ? NotFound<EquipmentAssetDetails>(id)
            : Result.Success(ToDetails(entry.Asset, entry.Revision));
    }

    public ValueTask<Result<EquipmentAssetDetails>> AddPlanAsync(
        AddMaintenancePlanCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            command.AssetId,
            command.CommandId,
            fact => fact is MaintenancePlanAddedFact plan
                && PlanMatches(plan, command),
            asset => asset.AddMaintenancePlan(
                new MaintenancePlanId(command.PlanId),
                command.DisplayName,
                command.CycleInterval,
                command.OperatingHoursInterval,
                command.CalendarInterval,
                command.BlocksProduction,
                command.CommandId,
                command.ActorId,
                command.OccurredAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<EquipmentAssetDetails>> RecordUsageAsync(
        RecordEquipmentUsageCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            command.AssetId,
            command.CommandId,
            fact => fact is EquipmentUsageRecordedFact usage
                && usage.CycleDelta == command.CycleDelta
                && usage.OperatingHoursDelta == command.OperatingHoursDelta
                && EnvelopeMatches(
                    usage,
                    command.ActorId,
                    command.OccurredAtUtc),
            asset => asset.RecordUsage(
                command.CycleDelta,
                command.OperatingHoursDelta,
                command.CommandId,
                command.ActorId,
                command.OccurredAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<EquipmentAssetDetails>> EvaluateMaintenanceAsync(
        EvaluateMaintenanceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            command.AssetId,
            command.CommandId,
            fact => fact is MaintenanceEvaluationRecordedFact evaluation
                && EnvelopeMatches(
                    evaluation,
                    command.ActorId,
                    command.EvaluatedAtUtc),
            asset => asset.EvaluateDueMaintenance(
                command.CommandId,
                command.ActorId,
                command.EvaluatedAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<EquipmentAssetDetails>> CompleteTaskAsync(
        CompleteMaintenanceTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            command.AssetId,
            command.CommandId,
            fact => fact is MaintenanceTaskCompletedFact completed
                && string.Equals(
                    completed.TaskId,
                    command.TaskId,
                    StringComparison.Ordinal)
                && string.Equals(
                    completed.CompletionNote,
                    command.CompletionNote,
                    StringComparison.Ordinal)
                && EnvelopeMatches(
                    completed,
                    command.ActorId,
                    command.CompletedAtUtc),
            asset => asset.CompleteMaintenanceTask(
                new MaintenanceTaskId(command.TaskId),
                command.CompletionNote,
                command.CommandId,
                command.ActorId,
                command.CompletedAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<EquipmentAssetDetails>> RecordCalibrationAsync(
        RecordCalibrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            command.AssetId,
            command.CommandId,
            fact => fact is CalibrationStatusRecordedFact calibration
                && calibration.Status == command.Status
                && string.Equals(
                    calibration.Reference,
                    command.Reference,
                    StringComparison.Ordinal)
                && calibration.ValidUntilUtc == command.ValidUntilUtc
                && EnvelopeMatches(
                    calibration,
                    command.ActorId,
                    command.OccurredAtUtc),
            asset => asset.RecordCalibration(
                command.Status,
                command.Reference,
                command.ValidUntilUtc,
                command.CommandId,
                command.ActorId,
                command.OccurredAtUtc),
            cancellationToken);
    }

    public ValueTask<Result<EquipmentAssetDetails>> RecordHealthAsync(
        RecordEquipmentHealthCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(
            command.AssetId,
            command.CommandId,
            fact => fact is EquipmentHealthRecordedFact health
                && health.Status == command.Status
                && string.Equals(
                    health.Diagnostic,
                    command.Diagnostic,
                    StringComparison.Ordinal)
                && EnvelopeMatches(
                    health,
                    command.ActorId,
                    command.OccurredAtUtc),
            asset => asset.RecordHealth(
                command.Status,
                command.Diagnostic,
                command.CommandId,
                command.ActorId,
                command.OccurredAtUtc),
            cancellationToken);
    }

    public async ValueTask<Result<StationProductionStartDecision>>
        EvaluateProductionStartAsync(
            string stationId,
            DateTimeOffset evaluatedAtUtc,
            CancellationToken cancellationToken = default)
    {
        if (!IsCanonical(stationId, 96))
        {
            return Validation<StationProductionStartDecision>(
                "Station ID must be non-empty canonical text.");
        }

        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            return Validation<StationProductionStartDecision>(
                "Production-start evaluation time must use the UTC offset.");
        }

        var entries = await repository.ListByStationAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        var decision = ProductionStartEvaluator.Evaluate(
            entries.Select(static entry => entry.Asset),
            evaluatedAtUtc);
        return Result.Success(
            new StationProductionStartDecision(
                stationId,
                evaluatedAtUtc,
                decision.Allowed,
                decision.Blocks,
                entries
                    .OrderBy(
                        static entry => entry.Asset.Id.Value,
                        StringComparer.Ordinal)
                    .Select(
                        static entry => new EquipmentAssetRevision(
                            entry.Asset.Id.Value,
                            entry.Revision))
                    .ToArray()));
    }

    private async ValueTask<Result<EquipmentAssetDetails>> MutateAsync(
        string assetId,
        string commandId,
        Func<EquipmentFact, bool> replayMatches,
        Func<EquipmentAsset, bool> mutation,
        CancellationToken cancellationToken)
    {
        EquipmentAssetId id;
        try
        {
            id = new EquipmentAssetId(assetId);
        }
        catch (ArgumentException exception)
        {
            return Validation<EquipmentAssetDetails>(exception.Message);
        }

        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return NotFound<EquipmentAssetDetails>(id);
            }

            var replay = FindCommandFact(entry.Asset, commandId);
            if (replay is not null)
            {
                return replayMatches(replay)
                    ? Result.Success(ToDetails(entry.Asset, entry.Revision))
                    : Conflict<EquipmentAssetDetails>(
                        "Maintenance.CommandConflict",
                        $"Command {commandId} was already used with different content.");
            }

            try
            {
                if (!mutation(entry.Asset))
                {
                    throw new InvalidOperationException(
                        "A new maintenance command unexpectedly produced no fact.");
                }
            }
            catch (ArgumentException exception)
            {
                return Validation<EquipmentAssetDetails>(exception.Message);
            }
            catch (KeyNotFoundException exception)
            {
                return Conflict<EquipmentAssetDetails>(
                    "Maintenance.SubjectNotFound",
                    exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return Conflict<EquipmentAssetDetails>(
                    "Maintenance.InvalidState",
                    exception.Message);
            }

            try
            {
                var revision = await repository.SaveAsync(
                        entry.Asset,
                        entry.Revision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Success(ToDetails(entry.Asset, revision));
            }
            catch (EquipmentAssetConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
        }

        return Conflict<EquipmentAssetDetails>(
            "Maintenance.ConcurrencyConflict",
            $"Equipment asset {assetId} kept changing; retry the operation.");
    }

    private static EquipmentFact? FindCommandFact(
        EquipmentAsset asset,
        string commandId) =>
        asset.Facts.FirstOrDefault(
            fact => string.Equals(
                fact.CommandId,
                commandId,
                StringComparison.Ordinal));

    private static bool RegistrationMatches(
        EquipmentAssetRegisteredFact fact,
        RegisterEquipmentAssetCommand command) =>
        string.Equals(fact.AssetId, command.AssetId, StringComparison.Ordinal)
        && string.Equals(fact.StationId, command.StationId, StringComparison.Ordinal)
        && string.Equals(
            fact.DisplayName,
            command.DisplayName,
            StringComparison.Ordinal)
        && fact.ProductionCritical == command.ProductionCritical
        && fact.RequiresCalibration == command.RequiresCalibration
        && EnvelopeMatches(fact, command.ActorId, command.OccurredAtUtc);

    private static bool PlanMatches(
        MaintenancePlanAddedFact fact,
        AddMaintenancePlanCommand command) =>
        string.Equals(fact.PlanId, command.PlanId, StringComparison.Ordinal)
        && string.Equals(
            fact.DisplayName,
            command.DisplayName,
            StringComparison.Ordinal)
        && fact.CycleInterval == command.CycleInterval
        && fact.OperatingHoursInterval == command.OperatingHoursInterval
        && fact.CalendarInterval == command.CalendarInterval
        && fact.BlocksProduction == command.BlocksProduction
        && EnvelopeMatches(fact, command.ActorId, command.OccurredAtUtc);

    private static bool EnvelopeMatches(
        EquipmentFact fact,
        string actorId,
        DateTimeOffset occurredAtUtc) =>
        string.Equals(fact.ActorId, actorId, StringComparison.Ordinal)
        && fact.OccurredAtUtc == occurredAtUtc;

    private static EquipmentAssetDetails ToDetails(
        EquipmentAsset asset,
        long revision) =>
        new(
            asset.Id.Value,
            revision,
            asset.StationId,
            asset.DisplayName,
            asset.ProductionCritical,
            asset.RequiresCalibration,
            asset.TotalCycles,
            asset.TotalOperatingHours,
            asset.HealthStatus,
            asset.HealthDiagnostic,
            asset.Calibration is null
                ? null
                : new CalibrationDetails(
                    asset.Calibration.Status,
                    asset.Calibration.Reference,
                    asset.Calibration.RecordedAtUtc,
                    asset.Calibration.ValidUntilUtc),
            asset.Plans
                .Select(
                    static plan => new MaintenancePlanDetails(
                        plan.Id.Value,
                        plan.DisplayName,
                        plan.CycleInterval,
                        plan.OperatingHoursInterval,
                        plan.CalendarInterval,
                        plan.BlocksProduction,
                        plan.BaselineCycles,
                        plan.BaselineOperatingHours,
                        plan.NextDueAtUtc))
                .ToArray(),
            asset.Tasks
                .Select(
                    static task => new MaintenanceTaskDetails(
                        task.Id.Value,
                        task.PlanId.Value,
                        task.DueReason,
                        task.DueAtUtc,
                        task.DueAtCycles,
                        task.DueAtOperatingHours,
                        task.Status,
                        task.CompletedAtUtc,
                        task.CompletedBy,
                        task.CompletionNote))
                .ToArray());

    private static bool IsCanonical(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl);

    private static Result<T> NotFound<T>(EquipmentAssetId id) =>
        Result.Failure<T>(
            ApplicationError.NotFound(
                "Maintenance.AssetNotFound",
                $"Equipment asset {id} was not found."));

    private static Result<T> Validation<T>(string message) =>
        Result.Failure<T>(
            ApplicationError.Validation(
                "Maintenance.InvalidCommand",
                message));

    private static Result<T> Conflict<T>(string code, string message) =>
        Result.Failure<T>(ApplicationError.Conflict(code, message));
}
