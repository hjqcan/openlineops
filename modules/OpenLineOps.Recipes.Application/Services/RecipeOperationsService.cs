using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Recipes.Application.Contracts;
using OpenLineOps.Recipes.Application.Fencing;
using OpenLineOps.Recipes.Application.Hashing;
using OpenLineOps.Recipes.Application.Persistence;
using OpenLineOps.Recipes.Application.Readiness;
using OpenLineOps.Recipes.Application.Recipes;
using OpenLineOps.Recipes.Domain.Assignments;
using OpenLineOps.Recipes.Domain.Changeovers;
using OpenLineOps.Recipes.Domain.Deployments;
using OpenLineOps.Recipes.Domain.Recipes;

namespace OpenLineOps.Recipes.Application.Services;

public sealed class RecipeOperationsService(
    IRecipeOperationsStore store,
    IReleasedRecipeRevisionSource revisionSource,
    IStationFencingTokenValidator fencingTokenValidator,
    IClock clock)
    : IRecipeOperationsService,
      IRecipeProductionReadinessGate,
      IStationRecipeAuthorityResolver
{
    public async ValueTask<Result<RecipeRevisionSnapshot>> GetReleasedRevisionAsync(
        string recipeId,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipeId) || string.IsNullOrWhiteSpace(versionId))
        {
            return Failure<RecipeRevisionSnapshot>(
                "Recipe.Identity.Required",
                "Recipe and version identity are required.");
        }

        var revision = await revisionSource
            .GetReleasedAsync(recipeId, versionId, cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
        {
            return Result.Failure<RecipeRevisionSnapshot>(ApplicationError.NotFound(
                "Recipe.ReleasedRevision.NotFound",
                $"Released recipe {recipeId}/{versionId} was not found."));
        }

        var computedHash = RecipeConfigurationHasher.Compute(
            revision.RecipeId,
            revision.VersionId,
            revision.Parameters);
        if (!string.Equals(
                revision.ConfigurationSha256,
                computedHash,
                StringComparison.Ordinal))
        {
            return Result.Failure<RecipeRevisionSnapshot>(ApplicationError.Conflict(
                "Recipe.ReleasedRevision.Integrity",
                "Released recipe configuration hash does not match its parameters."));
        }

        return Result.Success(revision);
    }

    public async ValueTask<Result<RecipeAssignment>> CreateAssignmentAsync(
        CreateRecipeAssignmentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var idempotency = Idempotency(
                "recipe.assignment.create",
                command.CommandId,
                command.AssignmentId,
                command.RecipeId,
                command.VersionId,
                command.ProductModelId,
                command.StationId,
                command.EffectiveFromUtc,
                command.EffectiveUntilUtc,
                command.ActorId);
            var replay = await ReplayAsync<RecipeAssignment>(
                    idempotency,
                    "assignment",
                    command.AssignmentId,
                    "Recipe.Assignment",
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }

            var revision = await GetReleasedRevisionAsync(
                    command.RecipeId,
                    command.VersionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (revision.IsFailure)
            {
                return Result.Failure<RecipeAssignment>(revision.Error);
            }

            var assignment = new RecipeAssignment(
                command.AssignmentId,
                revision.Value,
                command.ProductModelId,
                command.StationId,
                command.EffectiveFromUtc,
                command.EffectiveUntilUtc,
                clock.UtcNow,
                command.ActorId);
            var stored = await store
                .CreateAssignmentAsync(assignment, idempotency, cancellationToken)
                .ConfigureAwait(false);
            return MapWrite(
                stored,
                "Recipe.Assignment",
                "Assignment overlaps an existing station/product effective interval.");
        }
        catch (ArgumentException exception)
        {
            return Invalid<RecipeAssignment>(exception);
        }
    }

    public async ValueTask<Result<RecipeAssignment>> GetAssignmentAsync(
        Guid assignmentId,
        CancellationToken cancellationToken = default)
    {
        var assignment = await store
            .GetAssignmentAsync(assignmentId, cancellationToken)
            .ConfigureAwait(false);
        return assignment is null
            ? Result.Failure<RecipeAssignment>(ApplicationError.NotFound(
                "Recipe.Assignment.NotFound",
                $"Recipe assignment {assignmentId:D} was not found."))
            : Result.Success(assignment);
    }

    public async ValueTask<Result<IReadOnlyCollection<RecipeAssignment>>> ListAssignmentsAsync(
        string? recipeId = null,
        string? stationId = null,
        string? productModelId = null,
        CancellationToken cancellationToken = default)
    {
        var assignments = await store
            .ListAssignmentsAsync(
                recipeId,
                stationId,
                productModelId,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(assignments);
    }

    public async ValueTask<Result<RecipeDeployment>> CreateDeploymentAsync(
        CreateRecipeDeploymentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var idempotency = Idempotency(
                "recipe.deployment.create",
                command.Command.CommandId,
                command.DeploymentId,
                command.AssignmentId,
                command.RecipeId,
                command.StationId,
                command.Command.FencingToken,
                command.Command.DeadlineUtc,
                command.ActorId);
            var replay = await ReplayAsync<RecipeDeployment>(
                    idempotency,
                    "deployment",
                    command.DeploymentId,
                    "Recipe.Deployment",
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }

            var assignmentResult = await GetAssignmentAsync(
                    command.AssignmentId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (assignmentResult.IsFailure)
            {
                return Result.Failure<RecipeDeployment>(assignmentResult.Error);
            }

            var assignment = assignmentResult.Value;
            if (!string.Equals(assignment.RecipeId, command.RecipeId, StringComparison.Ordinal)
                || !string.Equals(assignment.StationId, command.StationId, StringComparison.Ordinal))
            {
                return Conflict<RecipeDeployment>(
                    "Recipe.Deployment.AssignmentMismatch",
                    "Deployment route and station do not match the assignment.");
            }

            if (!assignment.IsEffectiveAt(clock.UtcNow))
            {
                return Conflict<RecipeDeployment>(
                    "Recipe.Assignment.NotEffective",
                    "Recipe assignment is not effective at deployment time.");
            }

            if (command.Command.DeadlineUtc <= clock.UtcNow)
            {
                return Conflict<RecipeDeployment>(
                    "Recipe.Deployment.DeadlineExpired",
                    "Deployment deadline has expired.");
            }

            if (!await fencingTokenValidator.IsCurrentAsync(
                    assignment.StationId,
                    command.Command.FencingToken,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return Conflict<RecipeDeployment>(
                    "Recipe.Deployment.StaleFencingToken",
                    "Deployment fencing token is not the station's current lease token.");
            }

            var revision = await GetReleasedRevisionAsync(
                    assignment.RecipeId,
                    assignment.VersionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (revision.IsFailure)
            {
                return Result.Failure<RecipeDeployment>(revision.Error);
            }

            if (!string.Equals(
                    assignment.ConfigurationSha256,
                    revision.Value.ConfigurationSha256,
                    StringComparison.Ordinal))
            {
                return Conflict<RecipeDeployment>(
                    "Recipe.Deployment.ConfigurationMismatch",
                    "Released recipe no longer matches the assigned immutable configuration.");
            }

            var deployment = new RecipeDeployment(
                command.DeploymentId,
                assignment,
                revision.Value,
                command.Command,
                clock.UtcNow,
                command.ActorId);
            var stored = await store
                .CreateDeploymentAsync(deployment, idempotency, cancellationToken)
                .ConfigureAwait(false);
            return MapWrite(stored, "Recipe.Deployment");
        }
        catch (ArgumentException exception)
        {
            return Invalid<RecipeDeployment>(exception);
        }
    }

    public async ValueTask<Result<RecipeDeployment>> RecordVerificationAsync(
        RecordRecipeVerificationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var readbackHash = RecipeConfigurationHasher.ComputeReadback(command.Readback);
            var idempotency = Idempotency(
                "recipe.deployment.verify",
                command.CommandId,
                command.DeploymentId,
                command.VerificationId,
                command.ExpectedRevision,
                command.RecipeId,
                command.StationId,
                command.VerifiedAtUtc,
                readbackHash,
                command.ActorId);
            var replay = await ReplayAsync<RecipeDeployment>(
                    idempotency,
                    "deployment",
                    command.DeploymentId,
                    "Recipe.Verification",
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }

            var deploymentResult = await GetDeploymentAsync(
                    command.DeploymentId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (deploymentResult.IsFailure)
            {
                return deploymentResult;
            }

            var current = deploymentResult.Value;
            if (!string.Equals(current.RecipeId, command.RecipeId, StringComparison.Ordinal)
                || !string.Equals(current.StationId, command.StationId, StringComparison.Ordinal))
            {
                return Conflict<RecipeDeployment>(
                    "Recipe.Verification.DeploymentMismatch",
                    "Verification route and station do not match the deployment.");
            }

            if (clock.UtcNow > current.Command.DeadlineUtc
                || command.VerifiedAtUtc > current.Command.DeadlineUtc)
            {
                return Conflict<RecipeDeployment>(
                    "Recipe.Verification.DeadlineExpired",
                    "Readback verification occurred after the deployment deadline.");
            }

            if (!await fencingTokenValidator.IsCurrentAsync(
                    current.StationId,
                    current.Command.FencingToken,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return Conflict<RecipeDeployment>(
                    "Recipe.Verification.StaleFencingToken",
                    "Verification cannot append under an expired station lease token.");
            }

            var verified = current.Verify(
                command.VerificationId,
                command.Readback,
                readbackHash,
                command.VerifiedAtUtc,
                command.ActorId);
            var stored = await store
                .AppendVerificationAsync(
                    verified,
                    command.ExpectedRevision,
                    idempotency,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapWrite(stored, "Recipe.Verification");
        }
        catch (ArgumentException exception)
        {
            return Invalid<RecipeDeployment>(exception);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict<RecipeDeployment>(
                "Recipe.Verification.State",
                exception.Message);
        }
    }

    public async ValueTask<Result<RecipeDeployment>> GetDeploymentAsync(
        Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        var deployment = await store
            .GetDeploymentAsync(deploymentId, cancellationToken)
            .ConfigureAwait(false);
        return deployment is null
            ? Result.Failure<RecipeDeployment>(ApplicationError.NotFound(
                "Recipe.Deployment.NotFound",
                $"Recipe deployment {deploymentId:D} was not found."))
            : Result.Success(deployment);
    }

    public async ValueTask<Result<RecipeChangeover>> StartChangeoverAsync(
        StartRecipeChangeoverCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var idempotency = Idempotency(
                "recipe.changeover.start",
                command.CommandId,
                command.ChangeoverId,
                command.AssignmentId,
                command.StationId,
                command.StartedAtUtc,
                command.ActorId);
            var replay = await ReplayAsync<RecipeChangeover>(
                    idempotency,
                    "changeover",
                    command.ChangeoverId,
                    "Recipe.Changeover",
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }

            var assignmentResult = await GetAssignmentAsync(
                    command.AssignmentId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (assignmentResult.IsFailure)
            {
                return Result.Failure<RecipeChangeover>(assignmentResult.Error);
            }

            var assignment = assignmentResult.Value;
            if (!string.Equals(assignment.StationId, command.StationId, StringComparison.Ordinal))
            {
                return Conflict<RecipeChangeover>(
                    "Recipe.Changeover.StationMismatch",
                    "Changeover station does not match the assignment.");
            }

            if (assignment.EffectiveUntilUtc is not null
                && command.StartedAtUtc >= assignment.EffectiveUntilUtc)
            {
                return Conflict<RecipeChangeover>(
                    "Recipe.Changeover.AssignmentExpired",
                    "Changeover cannot start for an expired assignment.");
            }

            var changeover = new RecipeChangeover(
                command.ChangeoverId,
                assignment.AssignmentId,
                assignment.RecipeId,
                assignment.VersionId,
                assignment.ProductModelId,
                assignment.StationId,
                command.StartedAtUtc,
                command.ActorId);
            var stored = await store
                .CreateChangeoverAsync(changeover, idempotency, cancellationToken)
                .ConfigureAwait(false);
            return MapWrite(stored, "Recipe.Changeover");
        }
        catch (ArgumentException exception)
        {
            return Invalid<RecipeChangeover>(exception);
        }
    }

    public async ValueTask<Result<RecipeChangeover>> AdvanceChangeoverAsync(
        AdvanceRecipeChangeoverCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var idempotency = ChangeoverIdempotency(
                command,
                "advance");
            var replay = await ReplayAsync<RecipeChangeover>(
                    idempotency,
                    "changeover",
                    command.ChangeoverId,
                    "Recipe.Changeover",
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }

            var currentResult = await GetChangeoverAsync(
                    command.ChangeoverId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (currentResult.IsFailure)
            {
                return currentResult;
            }

            if (!IsNextChangeoverState(
                    currentResult.Value.State,
                    command.TargetState))
            {
                return Conflict<RecipeChangeover>(
                    "Recipe.Changeover.Sequence",
                    $"Changeover cannot advance from {currentResult.Value.State} to {command.TargetState}.");
            }

            RecipeDeployment? deployment = null;
            if (command.TargetState is RecipeChangeoverState.Download
                or RecipeChangeoverState.ReadbackVerified)
            {
                var deploymentId = command.DeploymentId ?? currentResult.Value.DeploymentId;
                if (deploymentId is null)
                {
                    return Conflict<RecipeChangeover>(
                        "Recipe.Changeover.DeploymentRequired",
                        "Changeover transition requires a deployment.");
                }

                var deploymentResult = await GetDeploymentAsync(
                        deploymentId.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (deploymentResult.IsFailure)
                {
                    return Result.Failure<RecipeChangeover>(deploymentResult.Error);
                }

                deployment = deploymentResult.Value;
                if (deployment.AssignmentId != currentResult.Value.AssignmentId
                    || !string.Equals(
                        deployment.StationId,
                        currentResult.Value.StationId,
                        StringComparison.Ordinal))
                {
                    return Conflict<RecipeChangeover>(
                        "Recipe.Changeover.DeploymentMismatch",
                        "Deployment does not belong to the changeover assignment.");
                }

                if (command.TargetState == RecipeChangeoverState.ReadbackVerified
                    && deployment.Status != RecipeDeploymentStatus.Verified)
                {
                    return Conflict<RecipeChangeover>(
                        "Recipe.Changeover.ReadbackNotVerified",
                        "Changeover cannot continue before exact readback verification succeeds.");
                }
            }

            var next = currentResult.Value.Advance(
                command.TargetState,
                command.Evidence,
                command.OccurredAtUtc,
                command.ActorId,
                command.TargetState == RecipeChangeoverState.Download
                    ? deployment?.DeploymentId
                    : currentResult.Value.DeploymentId,
                command.WorkInProgressCount,
                command.LineClearanceConfirmed);
            return await AppendChangeoverAsync(
                    next,
                    command.ExpectedRevision,
                    idempotency,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Invalid<RecipeChangeover>(exception);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict<RecipeChangeover>(
                "Recipe.Changeover.Sequence",
                exception.Message);
        }
    }

    public ValueTask<Result<RecipeChangeover>> FailChangeoverAsync(
        TerminateRecipeChangeoverCommand command,
        CancellationToken cancellationToken = default) =>
        TerminateChangeoverAsync(command, fail: true, cancellationToken);

    public ValueTask<Result<RecipeChangeover>> CancelChangeoverAsync(
        TerminateRecipeChangeoverCommand command,
        CancellationToken cancellationToken = default) =>
        TerminateChangeoverAsync(command, fail: false, cancellationToken);

    public async ValueTask<Result<RecipeChangeover>> GetChangeoverAsync(
        Guid changeoverId,
        CancellationToken cancellationToken = default)
    {
        var changeover = await store
            .GetChangeoverAsync(changeoverId, cancellationToken)
            .ConfigureAwait(false);
        return changeover is null
            ? Result.Failure<RecipeChangeover>(ApplicationError.NotFound(
                "Recipe.Changeover.NotFound",
                $"Recipe changeover {changeoverId:D} was not found."))
            : Result.Success(changeover);
    }

    public async ValueTask<RecipeProductionReadinessResult> EvaluateAsync(
        RecipeProductionReadinessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.StationId)
            || string.IsNullOrWhiteSpace(request.ProductModelId)
            || string.IsNullOrWhiteSpace(request.RecipeId)
            || string.IsNullOrWhiteSpace(request.VersionId)
            || request.EvaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            return Block(
                "Recipe.Readiness.InvalidRequest",
                "Readiness identity fields are required and evaluation time must be UTC.");
        }

        var assignments = await store.ListAssignmentsAsync(
                request.RecipeId,
                request.StationId,
                request.ProductModelId,
                cancellationToken)
            .ConfigureAwait(false);
        var assignment = assignments.SingleOrDefault(candidate =>
            string.Equals(candidate.VersionId, request.VersionId, StringComparison.Ordinal)
            && candidate.IsEffectiveAt(request.EvaluatedAtUtc));
        if (assignment is null)
        {
            return Block(
                "Recipe.Assignment.MissingOrVersionMismatch",
                "No effective assignment matches the requested station, product, recipe and version.");
        }

        var deployments = await store.ListDeploymentsAsync(
                request.StationId,
                request.ProductModelId,
                cancellationToken)
            .ConfigureAwait(false);
        var latest = deployments
            .Where(candidate => candidate.AssignmentId == assignment.AssignmentId)
            .OrderByDescending(static candidate => candidate.CreatedAtUtc)
            .ThenByDescending(static candidate => candidate.Revision)
            .FirstOrDefault();
        if (latest is null)
        {
            return Block(
                "Recipe.Verification.Missing",
                "Assigned recipe has not been downloaded and verified.",
                assignment.AssignmentId,
                configurationSha256: assignment.ConfigurationSha256);
        }

        if (latest.Status != RecipeDeploymentStatus.Verified
            || latest.Verification?.Succeeded != true
            || !string.Equals(
                latest.Snapshot.ConfigurationSha256,
                assignment.ConfigurationSha256,
                StringComparison.Ordinal)
            || !string.Equals(latest.VersionId, request.VersionId, StringComparison.Ordinal))
        {
            return Block(
                "Recipe.Verification.FailedOrVersionMismatch",
                "Latest deployment is unverified, rejected, or does not match the assigned version.",
                assignment.AssignmentId,
                latest.DeploymentId,
                assignment.ConfigurationSha256);
        }

        var changeovers = await store.ListChangeoversAsync(
                request.StationId,
                assignment.AssignmentId,
                cancellationToken)
            .ConfigureAwait(false);
        var releasedChangeover = changeovers
            .Where(changeover =>
                changeover.State == RecipeChangeoverState.Completed
                && changeover.DeploymentId == latest.DeploymentId)
            .OrderByDescending(static changeover =>
                changeover.Audit.Last().OccurredAtUtc)
            .ThenByDescending(static changeover => changeover.Revision)
            .FirstOrDefault();
        if (releasedChangeover is null)
        {
            return Block(
                "Recipe.Changeover.FirstArticleNotConfirmed",
                "The verified deployment has not completed line clearance, readback and first-article confirmation.",
                assignment.AssignmentId,
                latest.DeploymentId,
                assignment.ConfigurationSha256);
        }

        return new RecipeProductionReadinessResult(
            true,
            assignment.AssignmentId,
            latest.DeploymentId,
            assignment.ConfigurationSha256,
            []);
    }

    public async ValueTask<StationRecipeAuthorityResult> ResolveAsync(
        string stationId,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId)
            || evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            return AuthorityBlock(
                "Recipe.Authority.InvalidRequest",
                "Station identity is required and authority evaluation time must be UTC.");
        }

        var changeovers = await store.ListChangeoversAsync(
                stationId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var latestChangeover = changeovers
            .OrderByDescending(static changeover =>
                changeover.Audit.Last().OccurredAtUtc)
            .ThenByDescending(static changeover => changeover.Revision)
            .ThenByDescending(static changeover => changeover.ChangeoverId)
            .FirstOrDefault();
        if (latestChangeover is null)
        {
            return AuthorityBlock(
                "Recipe.Authority.ChangeoverMissing",
                "The station has no completed recipe changeover authority.");
        }

        if (latestChangeover.State != RecipeChangeoverState.Completed
            || latestChangeover.DeploymentId is null)
        {
            return AuthorityBlock(
                "Recipe.Authority.ChangeoverNotReleased",
                $"The latest station changeover is {latestChangeover.State}; "
                + "production start requires a completed changeover.");
        }

        var assignment = await store.GetAssignmentAsync(
                latestChangeover.AssignmentId,
                cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null
            || !assignment.IsEffectiveAt(evaluatedAtUtc)
            || !string.Equals(
                assignment.StationId,
                stationId,
                StringComparison.Ordinal)
            || !string.Equals(
                assignment.RecipeId,
                latestChangeover.RecipeId,
                StringComparison.Ordinal)
            || !string.Equals(
                assignment.VersionId,
                latestChangeover.VersionId,
                StringComparison.Ordinal)
            || !string.Equals(
                assignment.ProductModelId,
                latestChangeover.ProductModelId,
                StringComparison.Ordinal))
        {
            return AuthorityBlock(
                "Recipe.Authority.AssignmentInvalid",
                "The released changeover no longer has an effective, exact station assignment.");
        }

        var readiness = await EvaluateAsync(
                new RecipeProductionReadinessRequest(
                    stationId,
                    assignment.ProductModelId,
                    assignment.RecipeId,
                    assignment.VersionId,
                    evaluatedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        if (!readiness.Allowed
            || readiness.AssignmentId != assignment.AssignmentId
            || readiness.DeploymentId != latestChangeover.DeploymentId
            || !string.Equals(
                readiness.ConfigurationSha256,
                assignment.ConfigurationSha256,
                StringComparison.Ordinal))
        {
            var blocks = readiness.Blocks.Count == 0
                ?
                [
                    new RecipeProductionReadinessBlock(
                        "Recipe.Authority.EvidenceMismatch",
                        "Assignment, deployment, readback, and completed changeover "
                        + "do not form one exact authority chain.")
                ]
                : readiness.Blocks;
            return new StationRecipeAuthorityResult(false, null, blocks);
        }

        return new StationRecipeAuthorityResult(
            true,
            new StationRecipeAuthority(
                stationId,
                assignment.ProductModelId,
                assignment.RecipeId,
                assignment.VersionId,
                assignment.AssignmentId,
                latestChangeover.DeploymentId.Value,
                assignment.ConfigurationSha256,
                latestChangeover.Audit.Last().OccurredAtUtc),
            []);
    }

    private async ValueTask<Result<RecipeChangeover>> TerminateChangeoverAsync(
        TerminateRecipeChangeoverCommand command,
        bool fail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var idempotency = Idempotency(
                fail ? "recipe.changeover.fail" : "recipe.changeover.cancel",
                command.CommandId,
                command.ChangeoverId,
                command.ExpectedRevision,
                command.Reason,
                command.OccurredAtUtc,
                command.ActorId);
            var replay = await ReplayAsync<RecipeChangeover>(
                    idempotency,
                    "changeover",
                    command.ChangeoverId,
                    "Recipe.Changeover",
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }

            var current = await GetChangeoverAsync(
                    command.ChangeoverId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.IsFailure)
            {
                return current;
            }

            var next = fail
                ? current.Value.Fail(
                    command.Reason,
                    command.OccurredAtUtc,
                    command.ActorId)
                : current.Value.Cancel(
                    command.Reason,
                    command.OccurredAtUtc,
                    command.ActorId);
            return await AppendChangeoverAsync(
                    next,
                    command.ExpectedRevision,
                    idempotency,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Invalid<RecipeChangeover>(exception);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict<RecipeChangeover>(
                "Recipe.Changeover.Terminal",
                exception.Message);
        }
    }

    private async ValueTask<Result<RecipeChangeover>> AppendChangeoverAsync(
        RecipeChangeover next,
        long expectedRevision,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken)
    {
        var stored = await store
            .AppendChangeoverAsync(
                next,
                expectedRevision,
                idempotency,
                cancellationToken)
            .ConfigureAwait(false);
        return MapWrite(stored, "Recipe.Changeover");
    }

    private static RecipeIdempotencyContext ChangeoverIdempotency(
        AdvanceRecipeChangeoverCommand command,
        string action) =>
        Idempotency(
            $"recipe.changeover.{action}",
            command.CommandId,
            command.ChangeoverId,
            command.ExpectedRevision,
            command.TargetState.ToString(),
            command.Evidence,
            command.DeploymentId,
            command.WorkInProgressCount,
            command.LineClearanceConfirmed,
            command.OccurredAtUtc,
            command.ActorId);

    private async ValueTask<Result<T>?> ReplayAsync<T>(
        RecipeIdempotencyContext idempotency,
        string streamKind,
        Guid streamId,
        string codePrefix,
        CancellationToken cancellationToken)
        where T : class
    {
        var replay = await store.CheckCommandAsync<T>(
                idempotency,
                streamKind,
                streamId,
                cancellationToken)
            .ConfigureAwait(false);
        return replay.Status switch
        {
            RecipeCommandCheckStatus.Missing => null,
            RecipeCommandCheckStatus.Replay when replay.Value is not null =>
                Result.Success(replay.Value),
            RecipeCommandCheckStatus.Conflict => Conflict<T>(
                $"{codePrefix}.CommandConflict",
                "The same command id was already used with different content."),
            _ => throw new InvalidOperationException(
                $"Recipe store returned invalid command check status {replay.Status}.")
        };
    }

    private static RecipeIdempotencyContext Idempotency(
        string scope,
        string commandId,
        params object?[] values)
    {
        if (string.IsNullOrWhiteSpace(commandId)
            || !string.Equals(commandId, commandId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Command id must be canonical non-empty text.",
                nameof(commandId));
        }

        return new RecipeIdempotencyContext(
            scope,
            commandId,
            RecipeConfigurationHasher.ComputeCommand(values));
    }

    private static Result<T> MapWrite<T>(
        RecipeStoreWriteResult<T> write,
        string codePrefix,
        string? overlapMessage = null)
        where T : class
    {
        return write.Status switch
        {
            RecipeStoreWriteStatus.Stored or RecipeStoreWriteStatus.Replay
                when write.Value is not null => Result.Success(write.Value),
            RecipeStoreWriteStatus.IdempotencyConflict => Conflict<T>(
                $"{codePrefix}.CommandConflict",
                "The same command id was already used with different content."),
            RecipeStoreWriteStatus.RevisionConflict => Conflict<T>(
                $"{codePrefix}.RevisionConflict",
                "Expected revision does not match the persisted revision."),
            RecipeStoreWriteStatus.AssignmentOverlap => Conflict<T>(
                $"{codePrefix}.Overlap",
                overlapMessage ?? "Recipe assignment overlaps an existing interval."),
            RecipeStoreWriteStatus.StaleFencingToken => Conflict<T>(
                $"{codePrefix}.StaleFencingToken",
                "Deployment fencing token is older than the station's accepted token."),
            _ => throw new InvalidOperationException(
                $"Recipe store returned invalid write status {write.Status}.")
        };
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(ApplicationError.Validation(code, message));

    private static Result<T> Invalid<T>(ArgumentException exception) =>
        Failure<T>("Recipe.Input.Invalid", exception.Message);

    private static Result<T> Conflict<T>(string code, string message) =>
        Result.Failure<T>(ApplicationError.Conflict(code, message));

    private static RecipeProductionReadinessResult Block(
        string code,
        string detail,
        Guid? assignmentId = null,
        Guid? deploymentId = null,
        string? configurationSha256 = null) =>
        new(
            false,
            assignmentId,
            deploymentId,
            configurationSha256,
            [new RecipeProductionReadinessBlock(code, detail)]);

    private static StationRecipeAuthorityResult AuthorityBlock(
        string code,
        string detail) =>
        new(
            false,
            null,
            [new RecipeProductionReadinessBlock(code, detail)]);

    private static bool IsNextChangeoverState(
        RecipeChangeoverState current,
        RecipeChangeoverState target) =>
        (current, target) is
            (RecipeChangeoverState.CurrentUnitCompletion, RecipeChangeoverState.LineClearance)
            or (RecipeChangeoverState.LineClearance, RecipeChangeoverState.Download)
            or (RecipeChangeoverState.Download, RecipeChangeoverState.ReadbackVerified)
            or (RecipeChangeoverState.ReadbackVerified, RecipeChangeoverState.FirstArticleConfirmed)
            or (RecipeChangeoverState.FirstArticleConfirmed, RecipeChangeoverState.Completed);
}
