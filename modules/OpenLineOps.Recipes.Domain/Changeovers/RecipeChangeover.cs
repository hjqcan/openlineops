namespace OpenLineOps.Recipes.Domain.Changeovers;

public enum RecipeChangeoverState
{
    CurrentUnitCompletion = 0,
    LineClearance = 1,
    Download = 2,
    ReadbackVerified = 3,
    FirstArticleConfirmed = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7
}

public sealed record RecipeChangeoverAuditEntry(
    long Revision,
    string Action,
    RecipeChangeoverState State,
    string Evidence,
    int? WorkInProgressCount,
    bool? LineClearanceConfirmed,
    DateTimeOffset OccurredAtUtc,
    string ActorId);

public sealed record RecipeChangeover
{
    public RecipeChangeover(
        Guid changeoverId,
        Guid assignmentId,
        string recipeId,
        string versionId,
        string productModelId,
        string stationId,
        DateTimeOffset startedAtUtc,
        string startedBy,
        long revision = 1,
        RecipeChangeoverState state = RecipeChangeoverState.CurrentUnitCompletion,
        Guid? deploymentId = null,
        IReadOnlyCollection<RecipeChangeoverAuditEntry>? audit = null)
    {
        if (changeoverId == Guid.Empty)
        {
            throw new ArgumentException("Changeover id is required.", nameof(changeoverId));
        }

        if (assignmentId == Guid.Empty)
        {
            throw new ArgumentException("Assignment id is required.", nameof(assignmentId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ChangeoverId = changeoverId;
        AssignmentId = assignmentId;
        RecipeId = RecipeValueGuard.Required(recipeId, nameof(recipeId));
        VersionId = RecipeValueGuard.Required(versionId, nameof(versionId));
        ProductModelId = RecipeValueGuard.Required(productModelId, nameof(productModelId));
        StationId = RecipeValueGuard.Required(stationId, nameof(stationId));
        StartedAtUtc = RecipeValueGuard.Utc(startedAtUtc, nameof(startedAtUtc));
        StartedBy = RecipeValueGuard.Required(startedBy, nameof(startedBy));
        Revision = revision;
        State = state;
        DeploymentId = deploymentId;
        Audit = (audit ?? [
                new RecipeChangeoverAuditEntry(
                    1,
                    "Started",
                    RecipeChangeoverState.CurrentUnitCompletion,
                    "Changeover started.",
                    null,
                    null,
                    StartedAtUtc,
                    StartedBy)
            ])
            .OrderBy(static entry => entry.Revision)
            .ToArray();
        if (Audit.Count == 0
            || Audit.Last().Revision != Revision
            || Audit.Last().State != State)
        {
            throw new ArgumentException(
                "Changeover audit does not match its current state.",
                nameof(audit));
        }

        ValidateAudit();
    }

    public Guid ChangeoverId { get; }

    public Guid AssignmentId { get; }

    public string RecipeId { get; }

    public string VersionId { get; }

    public string ProductModelId { get; }

    public string StationId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public string StartedBy { get; }

    public long Revision { get; init; }

    public RecipeChangeoverState State { get; init; }

    public Guid? DeploymentId { get; init; }

    public IReadOnlyCollection<RecipeChangeoverAuditEntry> Audit { get; init; }

    public bool IsTerminal => State is RecipeChangeoverState.Completed
        or RecipeChangeoverState.Failed
        or RecipeChangeoverState.Cancelled;

    public RecipeChangeover Advance(
        RecipeChangeoverState target,
        string evidence,
        DateTimeOffset occurredAtUtc,
        string actorId,
        Guid? deploymentId = null,
        int? workInProgressCount = null,
        bool? lineClearanceConfirmed = null)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException("A terminal changeover cannot advance.");
        }

        var expected = State switch
        {
            RecipeChangeoverState.CurrentUnitCompletion => RecipeChangeoverState.LineClearance,
            RecipeChangeoverState.LineClearance => RecipeChangeoverState.Download,
            RecipeChangeoverState.Download => RecipeChangeoverState.ReadbackVerified,
            RecipeChangeoverState.ReadbackVerified => RecipeChangeoverState.FirstArticleConfirmed,
            RecipeChangeoverState.FirstArticleConfirmed => RecipeChangeoverState.Completed,
            _ => throw new InvalidOperationException("Changeover state cannot advance.")
        };
        if (target != expected)
        {
            throw new InvalidOperationException(
                $"Changeover must advance from {State} to {expected}; {target} is out of sequence.");
        }

        var canonicalEvidence = RecipeValueGuard.Required(evidence, nameof(evidence));
        if (target == RecipeChangeoverState.LineClearance
            && (workInProgressCount != 0 || lineClearanceConfirmed != true))
        {
            throw new InvalidOperationException(
                "Line clearance requires explicit confirmation and a WIP count of zero.");
        }

        if (target != RecipeChangeoverState.LineClearance
            && (workInProgressCount is not null || lineClearanceConfirmed is not null))
        {
            throw new ArgumentException(
                "Structured line-clearance evidence is only valid for LineClearance.");
        }

        var nextDeploymentId = DeploymentId;
        if (target == RecipeChangeoverState.Download)
        {
            if (deploymentId is null || deploymentId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Download transition requires a deployment id.",
                    nameof(deploymentId));
            }

            nextDeploymentId = deploymentId;
        }
        else if (deploymentId is not null && deploymentId != DeploymentId)
        {
            throw new ArgumentException(
                "Deployment id can only be assigned during Download.",
                nameof(deploymentId));
        }

        return Append(
            target,
            "Advanced",
            canonicalEvidence,
            occurredAtUtc,
            actorId,
            nextDeploymentId,
            workInProgressCount,
            lineClearanceConfirmed);
    }

    public RecipeChangeover Fail(
        string reason,
        DateTimeOffset occurredAtUtc,
        string actorId) =>
        Terminate(
            RecipeChangeoverState.Failed,
            "Failed",
            reason,
            occurredAtUtc,
            actorId);

    public RecipeChangeover Cancel(
        string reason,
        DateTimeOffset occurredAtUtc,
        string actorId) =>
        Terminate(
            RecipeChangeoverState.Cancelled,
            "Cancelled",
            reason,
            occurredAtUtc,
            actorId);

    private RecipeChangeover Terminate(
        RecipeChangeoverState terminal,
        string action,
        string reason,
        DateTimeOffset occurredAtUtc,
        string actorId)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException("A terminal changeover cannot change state.");
        }

        return Append(
            terminal,
            action,
            RecipeValueGuard.Required(reason, nameof(reason)),
            occurredAtUtc,
            actorId,
            DeploymentId,
            null,
            null);
    }

    private RecipeChangeover Append(
        RecipeChangeoverState state,
        string action,
        string evidence,
        DateTimeOffset occurredAtUtc,
        string actorId,
        Guid? deploymentId,
        int? workInProgressCount,
        bool? lineClearanceConfirmed)
    {
        var canonicalOccurredAtUtc = RecipeValueGuard.Utc(
            occurredAtUtc,
            nameof(occurredAtUtc));
        if (canonicalOccurredAtUtc < Audit.Last().OccurredAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredAtUtc),
                "Changeover audit time cannot precede the previous fact.");
        }

        var nextRevision = checked(Revision + 1);
        var nextAudit = Audit.Append(new RecipeChangeoverAuditEntry(
                nextRevision,
                action,
                state,
                evidence,
                workInProgressCount,
                lineClearanceConfirmed,
                canonicalOccurredAtUtc,
                RecipeValueGuard.Required(actorId, nameof(actorId))))
            .ToArray();
        return this with
        {
            Revision = nextRevision,
            State = state,
            DeploymentId = deploymentId,
            Audit = nextAudit
        };
    }

    private void ValidateAudit()
    {
        if (Audit.Count != Revision)
        {
            throw new ArgumentException(
                "Changeover audit revision count is discontinuous.",
                nameof(Audit));
        }

        var entries = Audit.ToArray();
        if (entries[0].Revision != 1
            || entries[0].Action != "Started"
            || entries[0].State != RecipeChangeoverState.CurrentUnitCompletion
            || entries[0].OccurredAtUtc != StartedAtUtc
            || !string.Equals(entries[0].ActorId, StartedBy, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Changeover start audit is invalid.",
                nameof(Audit));
        }

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Revision != index + 1
                || entry.OccurredAtUtc.Offset != TimeSpan.Zero
                || string.IsNullOrWhiteSpace(entry.ActorId)
                || string.IsNullOrWhiteSpace(entry.Evidence)
                || (index > 0
                    && entry.OccurredAtUtc < entries[index - 1].OccurredAtUtc))
            {
                throw new ArgumentException(
                    "Changeover audit contains an invalid or out-of-order fact.",
                    nameof(Audit));
            }

            if (entry.State == RecipeChangeoverState.LineClearance)
            {
                if (entry.WorkInProgressCount != 0
                    || entry.LineClearanceConfirmed != true)
                {
                    throw new ArgumentException(
                        "Changeover line-clearance audit does not prove WIP zero.",
                        nameof(Audit));
                }
            }
            else if (entry.WorkInProgressCount is not null
                || entry.LineClearanceConfirmed is not null)
            {
                throw new ArgumentException(
                    "Changeover audit attaches line-clearance evidence to another state.",
                    nameof(Audit));
            }

            if (index == 0)
            {
                continue;
            }

            var previous = entries[index - 1].State;
            var normalTransition = (previous, entry.State) is
                (RecipeChangeoverState.CurrentUnitCompletion, RecipeChangeoverState.LineClearance)
                or (RecipeChangeoverState.LineClearance, RecipeChangeoverState.Download)
                or (RecipeChangeoverState.Download, RecipeChangeoverState.ReadbackVerified)
                or (RecipeChangeoverState.ReadbackVerified, RecipeChangeoverState.FirstArticleConfirmed)
                or (RecipeChangeoverState.FirstArticleConfirmed, RecipeChangeoverState.Completed);
            var termination = previous is not (
                    RecipeChangeoverState.Completed
                    or RecipeChangeoverState.Failed
                    or RecipeChangeoverState.Cancelled)
                && entry.State is RecipeChangeoverState.Failed
                    or RecipeChangeoverState.Cancelled;
            if ((!normalTransition && !termination)
                || (normalTransition
                    && !string.Equals(
                        entry.Action,
                        "Advanced",
                        StringComparison.Ordinal))
                || (entry.State == RecipeChangeoverState.Failed
                    && !string.Equals(
                        entry.Action,
                        "Failed",
                        StringComparison.Ordinal))
                || (entry.State == RecipeChangeoverState.Cancelled
                    && !string.Equals(
                        entry.Action,
                        "Cancelled",
                        StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "Changeover audit contains an invalid state transition.",
                    nameof(Audit));
            }
        }

        var requiresDeployment = State is
            RecipeChangeoverState.Download
            or RecipeChangeoverState.ReadbackVerified
            or RecipeChangeoverState.FirstArticleConfirmed
            or RecipeChangeoverState.Completed;
        var forbidsDeployment = State is
            RecipeChangeoverState.CurrentUnitCompletion
            or RecipeChangeoverState.LineClearance;
        if ((requiresDeployment && DeploymentId is null)
            || (forbidsDeployment && DeploymentId is not null))
        {
            throw new ArgumentException(
                "Changeover deployment identity is inconsistent with its state.",
                nameof(DeploymentId));
        }
    }
}
