using OpenLineOps.Maintenance.Domain.Identifiers;

namespace OpenLineOps.Maintenance.Domain.Assets;

public sealed class EquipmentAsset
{
    private readonly List<EquipmentFact> _facts = [];
    private readonly Dictionary<MaintenancePlanId, MaintenancePlan> _plans = [];
    private readonly Dictionary<MaintenanceTaskId, MaintenanceTask> _tasks = [];
    private readonly HashSet<string> _processedCommandIds =
        new(StringComparer.Ordinal);

    private EquipmentAsset(EquipmentAssetRegisteredFact registration)
    {
        Id = new EquipmentAssetId(registration.AssetId);
        StationId = MaintenanceGuard.RequireCanonicalText(
            registration.StationId,
            nameof(registration.StationId),
            maximumLength: 96);
        DisplayName = MaintenanceGuard.RequireCanonicalText(
            registration.DisplayName,
            nameof(registration.DisplayName));
        ProductionCritical = registration.ProductionCritical;
        RequiresCalibration = registration.RequiresCalibration;
        Apply(registration);
    }

    public EquipmentAssetId Id { get; }

    public string StationId { get; }

    public string DisplayName { get; }

    public bool ProductionCritical { get; }

    public bool RequiresCalibration { get; }

    public long TotalCycles { get; private set; }

    public decimal TotalOperatingHours { get; private set; }

    public EquipmentHealthStatus HealthStatus { get; private set; }

    public string? HealthDiagnostic { get; private set; }

    public CalibrationRecord? Calibration { get; private set; }

    public IReadOnlyCollection<MaintenancePlan> Plans =>
        _plans.Values.OrderBy(static plan => plan.Id.Value, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyCollection<MaintenanceTask> Tasks =>
        _tasks.Values.OrderBy(static task => task.Id.Value, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyCollection<EquipmentFact> Facts => _facts.AsReadOnly();

    public static EquipmentAsset Register(
        EquipmentAssetId assetId,
        string stationId,
        string displayName,
        bool productionCritical,
        bool requiresCalibration,
        string commandId,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        var registration = new EquipmentAssetRegisteredFact(
            Sequence: 1,
            FactId: CreateFactId(assetId.Value, 1),
            CommandId: RequireCommandId(commandId),
            OccurredAtUtc: MaintenanceGuard.RequireUtc(
                occurredAtUtc,
                nameof(occurredAtUtc)),
            ActorId: RequireActorId(actorId),
            AssetId: assetId.Value,
            StationId: MaintenanceGuard.RequireCanonicalText(
                stationId,
                nameof(stationId),
                maximumLength: 96),
            DisplayName: MaintenanceGuard.RequireCanonicalText(
                displayName,
                nameof(displayName)),
            ProductionCritical: productionCritical,
            RequiresCalibration: requiresCalibration);
        return new EquipmentAsset(registration);
    }

    public static EquipmentAsset Restore(
        IReadOnlyCollection<EquipmentFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0)
        {
            throw new InvalidDataException(
                "An equipment asset requires at least its registration fact.");
        }

        var ordered = facts.OrderBy(static fact => fact.Sequence).ToArray();
        if (ordered[0] is not EquipmentAssetRegisteredFact registration)
        {
            throw new InvalidDataException(
                "The first equipment fact must register the asset.");
        }

        var asset = new EquipmentAsset(registration);
        foreach (var fact in ordered.Skip(1))
        {
            asset.Apply(fact);
        }

        return asset;
    }

    public bool HasProcessed(string commandId) =>
        _processedCommandIds.Contains(RequireCommandId(commandId));

    public bool AddMaintenancePlan(
        MaintenancePlanId planId,
        string displayName,
        long? cycleInterval,
        decimal? operatingHoursInterval,
        TimeSpan? calendarInterval,
        bool blocksProduction,
        string commandId,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(planId);
        if (IsDuplicate(commandId))
        {
            return false;
        }

        if (_plans.ContainsKey(planId))
        {
            throw new InvalidOperationException(
                $"Maintenance plan {planId} already exists for asset {Id}.");
        }

        var plan = new MaintenancePlan(
            planId,
            displayName,
            cycleInterval,
            operatingHoursInterval,
            calendarInterval,
            blocksProduction,
            TotalCycles,
            TotalOperatingHours,
            calendarInterval is { } interval
                ? MaintenanceGuard.RequireUtc(
                    occurredAtUtc,
                    nameof(occurredAtUtc)).Add(interval)
                : null);
        Append(
            (sequence, factId) => new MaintenancePlanAddedFact(
                sequence,
                factId,
                RequireCommandId(commandId),
                MaintenanceGuard.RequireUtc(occurredAtUtc, nameof(occurredAtUtc)),
                RequireActorId(actorId),
                plan.Id.Value,
                plan.DisplayName,
                plan.CycleInterval,
                plan.OperatingHoursInterval,
                plan.CalendarInterval,
                plan.BlocksProduction,
                plan.BaselineCycles,
                plan.BaselineOperatingHours,
                plan.NextDueAtUtc));
        return true;
    }

    public bool RecordUsage(
        long cycleDelta,
        decimal operatingHoursDelta,
        string commandId,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        if (IsDuplicate(commandId))
        {
            return false;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(cycleDelta);
        ArgumentOutOfRangeException.ThrowIfNegative(operatingHoursDelta);
        if (cycleDelta == 0 && operatingHoursDelta == 0)
        {
            throw new ArgumentException(
                "A usage fact must advance cycles, operating hours, or both.",
                nameof(cycleDelta));
        }

        var canonicalCommandId = RequireCommandId(commandId);
        var canonicalActorId = RequireActorId(actorId);
        var timestamp = MaintenanceGuard.RequireUtc(
            occurredAtUtc,
            nameof(occurredAtUtc));
        Append(
            (sequence, factId) => new EquipmentUsageRecordedFact(
                sequence,
                factId,
                canonicalCommandId,
                timestamp,
                canonicalActorId,
                cycleDelta,
                operatingHoursDelta));
        RaiseDueTasks(canonicalCommandId, canonicalActorId, timestamp);
        return true;
    }

    public bool EvaluateDueMaintenance(
        string commandId,
        string actorId,
        DateTimeOffset evaluatedAtUtc)
    {
        if (IsDuplicate(commandId))
        {
            return false;
        }

        var canonicalCommandId = RequireCommandId(commandId);
        var canonicalActorId = RequireActorId(actorId);
        var timestamp = MaintenanceGuard.RequireUtc(
            evaluatedAtUtc,
            nameof(evaluatedAtUtc));
        Append(
            (sequence, factId) => new MaintenanceEvaluationRecordedFact(
                sequence,
                factId,
                canonicalCommandId,
                timestamp,
                canonicalActorId));
        RaiseDueTasks(canonicalCommandId, canonicalActorId, timestamp);
        return true;
    }

    public bool CompleteMaintenanceTask(
        MaintenanceTaskId taskId,
        string completionNote,
        string commandId,
        string actorId,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        if (IsDuplicate(commandId))
        {
            return false;
        }

        if (!_tasks.TryGetValue(taskId, out var task))
        {
            throw new KeyNotFoundException(
                $"Maintenance task {taskId} does not exist for asset {Id}.");
        }

        if (task.Status == MaintenanceTaskStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Maintenance task {taskId} has already been completed.");
        }

        Append(
            (sequence, factId) => new MaintenanceTaskCompletedFact(
                sequence,
                factId,
                RequireCommandId(commandId),
                MaintenanceGuard.RequireUtc(completedAtUtc, nameof(completedAtUtc)),
                RequireActorId(actorId),
                taskId.Value,
                MaintenanceGuard.RequireCanonicalText(
                    completionNote,
                    nameof(completionNote),
                    maximumLength: 500)));
        return true;
    }

    public bool RecordCalibration(
        CalibrationStatus status,
        string reference,
        DateTimeOffset? validUntilUtc,
        string commandId,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        if (IsDuplicate(commandId))
        {
            return false;
        }

        var timestamp = MaintenanceGuard.RequireUtc(
            occurredAtUtc,
            nameof(occurredAtUtc));
        if (validUntilUtc is { } validUntil)
        {
            MaintenanceGuard.RequireUtc(validUntil, nameof(validUntilUtc));
        }

        if (status == CalibrationStatus.Valid
            && (validUntilUtc is null || validUntilUtc <= timestamp))
        {
            throw new ArgumentException(
                "A valid calibration record must have a future UTC expiry.",
                nameof(validUntilUtc));
        }

        if (status == CalibrationStatus.NotRequired && RequiresCalibration)
        {
            throw new InvalidOperationException(
                $"Asset {Id} is configured to require calibration.");
        }

        Append(
            (sequence, factId) => new CalibrationStatusRecordedFact(
                sequence,
                factId,
                RequireCommandId(commandId),
                timestamp,
                RequireActorId(actorId),
                status,
                MaintenanceGuard.RequireCanonicalText(
                    reference,
                    nameof(reference)),
                validUntilUtc));
        return true;
    }

    public bool RecordHealth(
        EquipmentHealthStatus status,
        string diagnostic,
        string commandId,
        string actorId,
        DateTimeOffset occurredAtUtc)
    {
        if (IsDuplicate(commandId))
        {
            return false;
        }

        Append(
            (sequence, factId) => new EquipmentHealthRecordedFact(
                sequence,
                factId,
                RequireCommandId(commandId),
                MaintenanceGuard.RequireUtc(occurredAtUtc, nameof(occurredAtUtc)),
                RequireActorId(actorId),
                status,
                MaintenanceGuard.RequireCanonicalText(
                    diagnostic,
                    nameof(diagnostic),
                    maximumLength: 500)));
        return true;
    }

    private void RaiseDueTasks(
        string sourceCommandId,
        string actorId,
        DateTimeOffset evaluatedAtUtc)
    {
        foreach (var plan in _plans.Values
                     .OrderBy(static value => value.Id.Value, StringComparer.Ordinal))
        {
            var hasOpenTask = _tasks.Values.Any(
                task => task.PlanId == plan.Id
                    && task.Status == MaintenanceTaskStatus.Due);
            var dueReason = plan.GetDueReason(
                TotalCycles,
                TotalOperatingHours,
                evaluatedAtUtc);
            if (hasOpenTask || dueReason == MaintenanceDueReason.None)
            {
                continue;
            }

            var ordinal = _tasks.Values.Count(task => task.PlanId == plan.Id) + 1;
            var taskId = new MaintenanceTaskId(
                $"{plan.Id.Value}-T{ordinal:D6}");
            var taskCommandId = MaintenanceGuard.RequireCanonicalText(
                $"{sourceCommandId}/task/{plan.Id.Value}",
                nameof(sourceCommandId),
                maximumLength: 240);
            Append(
                (sequence, factId) => new MaintenanceTaskRaisedFact(
                    sequence,
                    factId,
                    taskCommandId,
                    evaluatedAtUtc,
                    actorId,
                    taskId.Value,
                    plan.Id.Value,
                    dueReason,
                    TotalCycles,
                    TotalOperatingHours));
        }
    }

    private bool IsDuplicate(string commandId) =>
        _processedCommandIds.Contains(RequireCommandId(commandId));

    private void Append(Func<long, string, EquipmentFact> create)
    {
        var sequence = checked(_facts.Count + 1L);
        Apply(create(sequence, CreateFactId(Id.Value, sequence)));
    }

    private void Apply(EquipmentFact fact)
    {
        var expectedSequence = checked(_facts.Count + 1L);
        if (fact.Sequence != expectedSequence
            || !string.Equals(
                fact.FactId,
                CreateFactId(Id.Value, expectedSequence),
                StringComparison.Ordinal)
            || fact.OccurredAtUtc.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(fact.ActorId)
            || string.IsNullOrWhiteSpace(fact.CommandId)
            || !_processedCommandIds.Add(fact.CommandId))
        {
            throw new InvalidDataException(
                $"Equipment fact sequence {fact.Sequence} is invalid, duplicated, "
                + "or not canonical.");
        }

        switch (fact)
        {
            case EquipmentAssetRegisteredFact registration:
                if (_facts.Count != 0
                    || !string.Equals(
                        registration.AssetId,
                        Id.Value,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Equipment registration fact does not match its aggregate.");
                }

                break;

            case MaintenancePlanAddedFact planAdded:
                var planId = new MaintenancePlanId(planAdded.PlanId);
                if (_plans.ContainsKey(planId))
                {
                    throw new InvalidDataException(
                        $"Maintenance plan {planId} is duplicated.");
                }

                _plans.Add(
                    planId,
                    new MaintenancePlan(
                        planId,
                        planAdded.DisplayName,
                        planAdded.CycleInterval,
                        planAdded.OperatingHoursInterval,
                        planAdded.CalendarInterval,
                        planAdded.BlocksProduction,
                        planAdded.BaselineCycles,
                        planAdded.BaselineOperatingHours,
                        planAdded.NextDueAtUtc));
                break;

            case EquipmentUsageRecordedFact usage:
                ArgumentOutOfRangeException.ThrowIfNegative(usage.CycleDelta);
                ArgumentOutOfRangeException.ThrowIfNegative(
                    usage.OperatingHoursDelta);
                TotalCycles = checked(TotalCycles + usage.CycleDelta);
                TotalOperatingHours += usage.OperatingHoursDelta;
                break;

            case MaintenanceEvaluationRecordedFact:
                break;

            case MaintenanceTaskRaisedFact taskRaised:
                var raisedTaskId = new MaintenanceTaskId(taskRaised.TaskId);
                var raisedPlanId = new MaintenancePlanId(taskRaised.PlanId);
                if (!_plans.ContainsKey(raisedPlanId)
                    || _tasks.ContainsKey(raisedTaskId)
                    || _tasks.Values.Any(
                        task => task.PlanId == raisedPlanId
                            && task.Status == MaintenanceTaskStatus.Due))
                {
                    throw new InvalidDataException(
                        $"Maintenance task {raisedTaskId} cannot be raised.");
                }

                _tasks.Add(
                    raisedTaskId,
                    new MaintenanceTask(
                        raisedTaskId,
                        raisedPlanId,
                        taskRaised.DueReason,
                        taskRaised.OccurredAtUtc,
                        taskRaised.DueAtCycles,
                        taskRaised.DueAtOperatingHours));
                break;

            case MaintenanceTaskCompletedFact taskCompleted:
                var completedTaskId = new MaintenanceTaskId(taskCompleted.TaskId);
                if (!_tasks.TryGetValue(completedTaskId, out var completedTask)
                    || !_plans.TryGetValue(
                        completedTask.PlanId,
                        out var completedPlan))
                {
                    throw new InvalidDataException(
                        $"Maintenance task {completedTaskId} cannot be completed.");
                }

                completedTask.Complete(
                    taskCompleted.ActorId,
                    taskCompleted.CompletionNote,
                    taskCompleted.OccurredAtUtc);
                completedPlan.ResetBaseline(
                    TotalCycles,
                    TotalOperatingHours,
                    taskCompleted.OccurredAtUtc);
                break;

            case CalibrationStatusRecordedFact calibration:
                Calibration = new CalibrationRecord(
                    calibration.Status,
                    calibration.Reference,
                    calibration.OccurredAtUtc,
                    calibration.ValidUntilUtc);
                break;

            case EquipmentHealthRecordedFact health:
                HealthStatus = health.Status;
                HealthDiagnostic = health.Diagnostic;
                break;

            default:
                throw new InvalidDataException(
                    $"Unsupported equipment fact type {fact.GetType().Name}.");
        }

        _facts.Add(fact);
    }

    private static string RequireCommandId(string value) =>
        MaintenanceGuard.RequireCanonicalText(
            value,
            nameof(value),
            maximumLength: 240);

    private static string RequireActorId(string value) =>
        MaintenanceGuard.RequireCanonicalText(
            value,
            nameof(value),
            maximumLength: 128);

    private static string CreateFactId(string assetId, long sequence) =>
        $"{assetId}:F{sequence:D12}";
}
