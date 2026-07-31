using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Recipes.Api.Controllers;
using OpenLineOps.Recipes.Api.DependencyInjection;
using OpenLineOps.Recipes.Api.Models;
using OpenLineOps.Recipes.Application.Contracts;
using OpenLineOps.Recipes.Application.Fencing;
using OpenLineOps.Recipes.Application.Hashing;
using OpenLineOps.Recipes.Application.Readiness;
using OpenLineOps.Recipes.Application.Recipes;
using OpenLineOps.Recipes.Application.Services;
using OpenLineOps.Recipes.Domain.Changeovers;
using OpenLineOps.Recipes.Domain.Deployments;
using OpenLineOps.Recipes.Domain.Recipes;
using OpenLineOps.Recipes.Infrastructure.Persistence;

namespace OpenLineOps.Recipes.Tests;

public sealed class RecipeOperationsTests
{
    [Fact]
    public async Task AssignmentRejectsOverlapAndChangedReplay()
    {
        using var environment = new RecipeTestEnvironment();
        var command = environment.AssignmentCommand();

        var first = await environment.Service.CreateAssignmentAsync(command);
        var replay = await environment.Service.CreateAssignmentAsync(command);
        var changedReplay = await environment.Service.CreateAssignmentAsync(
            command with { ProductModelId = "product-b" });
        var overlapping = await environment.Service.CreateAssignmentAsync(
            environment.AssignmentCommand(
                assignmentId: Guid.NewGuid(),
                commandId: "assignment-overlap"));

        Assert.True(first.IsSuccess, first.Error.ToString());
        Assert.True(replay.IsSuccess, replay.Error.ToString());
        Assert.Equal(first.Value.AssignmentId, replay.Value.AssignmentId);
        Assert.True(changedReplay.IsFailure);
        Assert.Equal(
            "Conflict.Recipe.Assignment.CommandConflict",
            changedReplay.Error.Code);
        Assert.True(overlapping.IsFailure);
        Assert.Equal("Conflict.Recipe.Assignment.Overlap", overlapping.Error.Code);
        Assert.Single((await environment.Service.ListAssignmentsAsync()).Value);
    }

    [Fact]
    public async Task ExpiredAssignmentAndStaleFencingTokenBlockDeployment()
    {
        using var environment = new RecipeTestEnvironment();
        var expiredAssignment = environment.AssignmentCommand(
            effectiveFromUtc: environment.Clock.UtcNow.AddHours(-2),
            effectiveUntilUtc: environment.Clock.UtcNow.AddHours(-1));
        Assert.True((await environment.Service.CreateAssignmentAsync(expiredAssignment)).IsSuccess);

        var expired = await environment.Service.CreateDeploymentAsync(
            environment.DeploymentCommand(expiredAssignment.AssignmentId));

        Assert.True(expired.IsFailure);
        Assert.Equal(
            "Conflict.Recipe.Assignment.NotEffective",
            expired.Error.Code);

        var activeAssignment = environment.AssignmentCommand(
            assignmentId: Guid.NewGuid(),
            productModelId: "product-b",
            commandId: "assignment-active");
        Assert.True((await environment.Service.CreateAssignmentAsync(activeAssignment)).IsSuccess);
        environment.Fencing.CurrentToken = 12;

        var stale = await environment.Service.CreateDeploymentAsync(
            environment.DeploymentCommand(
                activeAssignment.AssignmentId,
                fencingToken: 11));

        Assert.True(stale.IsFailure);
        Assert.Equal(
            "Conflict.Recipe.Deployment.StaleFencingToken",
            stale.Error.Code);
    }

    [Fact]
    public async Task TamperedReadbackCreatesRejectedFactAndBlocksReadiness()
    {
        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);
        var deployment = environment.DeploymentCommand(assignment.AssignmentId);
        var deployed = await environment.Service.CreateDeploymentAsync(deployment);
        Assert.True(deployed.IsSuccess, deployed.Error.ToString());

        var before = await environment.Service.EvaluateAsync(
            environment.ReadinessRequest());
        var tampered = environment.Readback().ToArray();
        var voltageIndex = Array.FindIndex(
            tampered,
            static parameter => parameter.Key == "voltage");
        tampered[voltageIndex] = new RecipeReadbackParameter(
            tampered[voltageIndex].Key,
            tampered[voltageIndex].Type,
            tampered[voltageIndex].Unit,
            "6");
        var verified = await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                deployment.DeploymentId,
                tampered,
                "verification-tampered"));
        var after = await environment.Service.EvaluateAsync(
            environment.ReadinessRequest());
        var facts = await environment.Store.ListFactsAsync(
            "deployment",
            deployment.DeploymentId);

        Assert.False(before.Allowed);
        Assert.True(verified.IsSuccess, verified.Error.ToString());
        Assert.Equal(RecipeDeploymentStatus.Rejected, verified.Value.Status);
        Assert.False(verified.Value.Verification!.Succeeded);
        Assert.Contains(
            verified.Value.Verification.Parameters,
            static parameter => !parameter.ValueMatches);
        Assert.False(after.Allowed);
        Assert.Equal(2, facts.Count);
        Assert.Equal("VerificationRecorded", facts.Last().FactKind);
    }

    [Fact]
    public async Task VerifiedDeploymentSurvivesRestartAndDuplicateCommands()
    {
        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);
        var deployment = environment.DeploymentCommand(assignment.AssignmentId);
        var deployed = await environment.Service.CreateDeploymentAsync(deployment);
        var duplicateDeployment = await environment.Service.CreateDeploymentAsync(deployment);
        Assert.True(deployed.IsSuccess);
        Assert.True(duplicateDeployment.IsSuccess);

        var verification = environment.VerificationCommand(
            deployment.DeploymentId,
            environment.Readback(),
            "verification-exact");
        var verified = await environment.Service.RecordVerificationAsync(verification);
        var duplicateVerification =
            await environment.Service.RecordVerificationAsync(verification);
        var changedVerification =
            await environment.Service.RecordVerificationAsync(
                verification with
                {
                    Readback =
                    [
                        .. verification.Readback.Where(
                            static parameter => parameter.Key != "voltage"),
                        new RecipeReadbackParameter(
                            "voltage",
                            RecipeParameterValueType.Decimal,
                            "V",
                            "6")
                    ]
                });

        Assert.True(verified.IsSuccess, verified.Error.ToString());
        Assert.True(duplicateVerification.IsSuccess, duplicateVerification.Error.ToString());
        Assert.True(changedVerification.IsFailure);
        Assert.Equal(
            "Conflict.Recipe.Verification.CommandConflict",
            changedVerification.Error.Code);
        Assert.Equal(RecipeDeploymentStatus.Verified, duplicateVerification.Value.Status);
        Assert.Equal(2, duplicateVerification.Value.Revision);

        var beforeChangeover = await environment.Service.EvaluateAsync(
            environment.ReadinessRequest());
        Assert.False(beforeChangeover.Allowed);
        Assert.Equal(
            "Recipe.Changeover.FirstArticleNotConfirmed",
            beforeChangeover.Blocks.Single().Code);
        await environment.CompleteChangeoverAsync(
            assignment.AssignmentId,
            deployment.DeploymentId);

        environment.RestartStore();
        var recovered = await environment.Service.GetDeploymentAsync(
            deployment.DeploymentId);
        var readiness = await environment.Service.EvaluateAsync(
            environment.ReadinessRequest());

        Assert.True(recovered.IsSuccess);
        Assert.Equal(RecipeDeploymentStatus.Verified, recovered.Value.Status);
        Assert.True(readiness.Allowed);
        Assert.Equal(deployment.DeploymentId, readiness.DeploymentId);
        Assert.Equal(environment.Revision.ConfigurationSha256, readiness.ConfigurationSha256);
        var wrongVersion = await environment.Service.EvaluateAsync(
            environment.ReadinessRequest() with { VersionId = "revision-other" });
        Assert.False(wrongVersion.Allowed);
        Assert.Equal(
            "Recipe.Assignment.MissingOrVersionMismatch",
            wrongVersion.Blocks.Single().Code);
    }

    [Fact]
    public async Task StationStartAuthorityUsesLatestCompletedChangeoverChain()
    {
        using var environment = new RecipeTestEnvironment();
        var assignmentCommand = environment.AssignmentCommand();
        var assignment = await environment.Service.CreateAssignmentAsync(
            assignmentCommand);
        Assert.True(assignment.IsSuccess, assignment.Error.ToString());

        var deploymentCommand = environment.DeploymentCommand(
            assignment.Value.AssignmentId);
        var deployment = await environment.Service.CreateDeploymentAsync(
            deploymentCommand);
        Assert.True(deployment.IsSuccess, deployment.Error.ToString());
        var verification = await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                deployment.Value.DeploymentId,
                environment.Readback(),
                "station-authority-readback"));
        Assert.True(verification.IsSuccess, verification.Error.ToString());

        var missingChangeover = await environment.Service.ResolveAsync(
            RecipeTestEnvironment.StationId,
            environment.Clock.UtcNow);
        Assert.False(missingChangeover.Allowed);
        Assert.Equal(
            "Recipe.Authority.ChangeoverMissing",
            missingChangeover.Blocks.Single().Code);

        await environment.CompleteChangeoverAsync(
            assignment.Value.AssignmentId,
            deployment.Value.DeploymentId);
        var ready = await environment.Service.ResolveAsync(
            RecipeTestEnvironment.StationId,
            environment.Clock.UtcNow);

        Assert.True(ready.Allowed);
        var authority = Assert.IsType<StationRecipeAuthority>(ready.Authority);
        Assert.Equal(assignment.Value.AssignmentId, authority.AssignmentId);
        Assert.Equal(deployment.Value.DeploymentId, authority.DeploymentId);
        Assert.Equal(RecipeTestEnvironment.RecipeId, authority.RecipeId);
        Assert.Equal(RecipeTestEnvironment.VersionId, authority.VersionId);
        Assert.Equal(
            environment.Revision.ConfigurationSha256,
            authority.ConfigurationSha256);

        environment.Clock.UtcNow = environment.Clock.UtcNow.AddMinutes(10);
        var nextChangeover = environment.StartChangeoverCommand(
            assignment.Value.AssignmentId) with
        {
            ChangeoverId = Guid.NewGuid(),
            CommandId = "station-authority-next-changeover"
        };
        Assert.True(
            (await environment.Service.StartChangeoverAsync(nextChangeover))
            .IsSuccess);
        var blocked = await environment.Service.ResolveAsync(
            RecipeTestEnvironment.StationId,
            environment.Clock.UtcNow);
        Assert.False(blocked.Allowed);
        Assert.Equal(
            "Recipe.Authority.ChangeoverNotReleased",
            blocked.Blocks.Single().Code);
    }

    [Fact]
    public async Task VerificationRejectsOutOfOrderExpiredAndExpiredLeaseFacts()
    {
        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);
        var deployment = environment.DeploymentCommand(assignment.AssignmentId);
        Assert.True((await environment.Service.CreateDeploymentAsync(deployment)).IsSuccess);

        var outOfOrder = await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                deployment.DeploymentId,
                environment.Readback(),
                "verification-out-of-order") with
            {
                ExpectedRevision = 2
            });
        var beforeCreation = await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                deployment.DeploymentId,
                environment.Readback(),
                "verification-before-creation") with
            {
                VerifiedAtUtc = environment.Clock.UtcNow.AddMinutes(-1)
            });
        var afterDeadline = await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                deployment.DeploymentId,
                environment.Readback(),
                "verification-after-deadline") with
            {
                VerifiedAtUtc = environment.Clock.UtcNow.AddMinutes(11)
            });
        environment.Clock.UtcNow = environment.Clock.UtcNow.AddMinutes(11);
        var spoofedSourceTime = await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                deployment.DeploymentId,
                environment.Readback(),
                "verification-spoofed-source-time") with
            {
                VerifiedAtUtc = RecipeTestEnvironment.BaseTimeUtc.AddMinutes(1)
            });
        environment.Clock.UtcNow = RecipeTestEnvironment.BaseTimeUtc;
        environment.Fencing.CurrentToken = 11;
        var expiredLease = await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                deployment.DeploymentId,
                environment.Readback(),
                "verification-expired-lease"));

        Assert.Equal(
            "Conflict.Recipe.Verification.RevisionConflict",
            outOfOrder.Error.Code);
        Assert.Equal(
            "Validation.Recipe.Input.Invalid",
            beforeCreation.Error.Code);
        Assert.Equal(
            "Conflict.Recipe.Verification.DeadlineExpired",
            afterDeadline.Error.Code);
        Assert.Equal(
            "Conflict.Recipe.Verification.DeadlineExpired",
            spoofedSourceTime.Error.Code);
        Assert.Equal(
            "Conflict.Recipe.Verification.StaleFencingToken",
            expiredLease.Error.Code);
        Assert.Single(await environment.Store.ListFactsAsync(
            "deployment",
            deployment.DeploymentId));
    }

    [Fact]
    public async Task VerificationStoreRejectsFencingRaceAfterNewerDeployment()
    {
        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);
        var oldDeployment = environment.DeploymentCommand(
            assignment.AssignmentId,
            commandId: "deployment-fence-10");
        Assert.True((await environment.Service.CreateDeploymentAsync(oldDeployment)).IsSuccess);

        environment.Fencing.CurrentToken = 11;
        var newDeployment = environment.DeploymentCommand(
            assignment.AssignmentId,
            deploymentId: Guid.NewGuid(),
            fencingToken: 11,
            commandId: "deployment-fence-11");
        Assert.True((await environment.Service.CreateDeploymentAsync(newDeployment)).IsSuccess);

        environment.Fencing.AcceptAnyPositiveToken = true;
        var raced = await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                oldDeployment.DeploymentId,
                environment.Readback(),
                "verification-old-fence-after-new"));

        Assert.True(raced.IsFailure);
        Assert.Equal(
            "Conflict.Recipe.Verification.StaleFencingToken",
            raced.Error.Code);
        Assert.Single(await environment.Store.ListFactsAsync(
            "deployment",
            oldDeployment.DeploymentId));
    }

    [Fact]
    public async Task ChangeoverEnforcesLineClearanceDeploymentAndExactSequence()
    {
        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);
        var start = environment.StartChangeoverCommand(assignment.AssignmentId);
        var started = await environment.Service.StartChangeoverAsync(start);
        Assert.True(started.IsSuccess);

        var jump = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                1,
                RecipeChangeoverState.Download,
                "jump"));
        var wipPresent = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                1,
                RecipeChangeoverState.LineClearance,
                "line checked",
                workInProgressCount: 1,
                lineClearanceConfirmed: true));
        var clearedCommand = environment.AdvanceChangeoverCommand(
            start.ChangeoverId,
            1,
            RecipeChangeoverState.LineClearance,
            "all WIP removed",
            workInProgressCount: 0,
            lineClearanceConfirmed: true,
            commandId: "changeover-cleared");
        var cleared = await environment.Service.AdvanceChangeoverAsync(clearedCommand);
        var duplicateCleared =
            await environment.Service.AdvanceChangeoverAsync(clearedCommand);

        Assert.Equal("Conflict.Recipe.Changeover.Sequence", jump.Error.Code);
        Assert.Equal("Conflict.Recipe.Changeover.Sequence", wipPresent.Error.Code);
        Assert.True(cleared.IsSuccess);
        Assert.True(duplicateCleared.IsSuccess);
        Assert.Equal(2, duplicateCleared.Value.Revision);
        Assert.Equal(0, duplicateCleared.Value.Audit.Last().WorkInProgressCount);
        Assert.True(duplicateCleared.Value.Audit.Last().LineClearanceConfirmed);

        var missingDeployment = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                2,
                RecipeChangeoverState.Download,
                "download requested"));
        Assert.Equal(
            "Conflict.Recipe.Changeover.DeploymentRequired",
            missingDeployment.Error.Code);

        var deployment = environment.DeploymentCommand(assignment.AssignmentId);
        Assert.True((await environment.Service.CreateDeploymentAsync(deployment)).IsSuccess);
        var downloaded = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                2,
                RecipeChangeoverState.Download,
                "configuration downloaded",
                deployment.DeploymentId));
        Assert.True(downloaded.IsSuccess, downloaded.Error.ToString());

        var unverified = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                3,
                RecipeChangeoverState.ReadbackVerified,
                "readback checked",
                deployment.DeploymentId));
        Assert.Equal(
            "Conflict.Recipe.Changeover.ReadbackNotVerified",
            unverified.Error.Code);

        Assert.True((await environment.Service.RecordVerificationAsync(
            environment.VerificationCommand(
                deployment.DeploymentId,
                environment.Readback(),
                "changeover-verification"))).IsSuccess);
        var readback = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                3,
                RecipeChangeoverState.ReadbackVerified,
                "exact readback accepted",
                deployment.DeploymentId));
        var firstArticle = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                4,
                RecipeChangeoverState.FirstArticleConfirmed,
                "first article accepted"));
        var completed = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                5,
                RecipeChangeoverState.Completed,
                "changeover released"));

        Assert.True(readback.IsSuccess);
        Assert.True(firstArticle.IsSuccess);
        Assert.True(completed.IsSuccess);
        Assert.Equal(RecipeChangeoverState.Completed, completed.Value.State);
        Assert.Equal(6, completed.Value.Audit.Count);
        Assert.Equal(6, (await environment.Store.ListFactsAsync(
            "changeover",
            start.ChangeoverId)).Count);
    }

    [Fact]
    public async Task ChangeoverFailureAuditSurvivesRestartAndTimeCannotRegress()
    {
        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);
        var start = environment.StartChangeoverCommand(assignment.AssignmentId);
        Assert.True((await environment.Service.StartChangeoverAsync(start)).IsSuccess);

        var regressed = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                1,
                RecipeChangeoverState.LineClearance,
                "invalid old evidence",
                workInProgressCount: 0,
                lineClearanceConfirmed: true) with
            {
                OccurredAtUtc = start.StartedAtUtc.AddTicks(-1)
            });
        Assert.True(regressed.IsFailure);
        Assert.Equal("Validation.Recipe.Input.Invalid", regressed.Error.Code);

        var cleared = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                1,
                RecipeChangeoverState.LineClearance,
                "WIP removed before failure",
                workInProgressCount: 0,
                lineClearanceConfirmed: true,
                commandId: "failure-changeover-cleared"));
        Assert.True(cleared.IsSuccess);
        var deployment = environment.DeploymentCommand(
            assignment.AssignmentId,
            commandId: "failure-changeover-deployment");
        Assert.True((await environment.Service.CreateDeploymentAsync(deployment)).IsSuccess);
        var downloaded = await environment.Service.AdvanceChangeoverAsync(
            environment.AdvanceChangeoverCommand(
                start.ChangeoverId,
                2,
                RecipeChangeoverState.Download,
                "download completed before fault",
                deployment.DeploymentId,
                commandId: "failure-changeover-downloaded"));
        Assert.True(downloaded.IsSuccess);

        var failed = await environment.Service.FailChangeoverAsync(
            new TerminateRecipeChangeoverCommand(
                start.ChangeoverId,
                3,
                "operator detected retained material",
                start.StartedAtUtc.AddMinutes(4),
                "operator-1",
                "changeover-failed"));

        Assert.True(failed.IsSuccess);
        Assert.Equal(RecipeChangeoverState.Failed, failed.Value.State);

        environment.RestartStore();
        var recovered = await environment.Service.GetChangeoverAsync(start.ChangeoverId);
        Assert.Equal(RecipeChangeoverState.Failed, recovered.Value.State);
        Assert.Equal(deployment.DeploymentId, recovered.Value.DeploymentId);
        Assert.Equal("Failed", recovered.Value.Audit.Last().Action);
        Assert.Equal(
            "operator detected retained material",
            recovered.Value.Audit.Last().Evidence);
    }

    [Fact]
    public async Task FactHashChainDetectsPersistedPayloadTampering()
    {
        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);

        await using (var connection = new SqliteConnection(environment.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE recipe_facts
                SET payload_json = payload_json || ' '
                WHERE stream_kind = 'assignment' AND stream_id = $stream_id;
                """;
            command.Parameters.AddWithValue(
                "$stream_id",
                assignment.AssignmentId.ToString("D"));
            await Assert.ThrowsAsync<SqliteException>(async () =>
                await command.ExecuteNonQueryAsync());

            command.Parameters.Clear();
            command.CommandText = "DROP TRIGGER trg_recipe_facts_no_update;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                UPDATE recipe_facts
                SET payload_json = payload_json || ' '
                WHERE stream_kind = 'assignment' AND stream_id = $stream_id;
                """;
            command.Parameters.AddWithValue(
                "$stream_id",
                assignment.AssignmentId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await environment.Store.GetAssignmentAsync(assignment.AssignmentId));
    }

    [Fact]
    public async Task PersistedStreamHeadDetectsTailRollbackAfterRestart()
    {
        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);
        var deployment = environment.DeploymentCommand(assignment.AssignmentId);
        Assert.True((await environment.Service.CreateDeploymentAsync(deployment)).IsSuccess);
        var verification = environment.VerificationCommand(
            deployment.DeploymentId,
            environment.Readback(),
            "verification-before-tail-delete");
        Assert.True((await environment.Service.RecordVerificationAsync(verification)).IsSuccess);

        await using (var connection = new SqliteConnection(environment.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER trg_recipe_facts_no_delete;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                DELETE FROM recipe_facts
                WHERE stream_kind = 'deployment'
                  AND stream_id = $stream_id
                  AND revision = 2;
                """;
            command.Parameters.AddWithValue(
                "$stream_id",
                deployment.DeploymentId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        environment.RestartStore();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await environment.Service.RecordVerificationAsync(verification));
    }

    [Fact]
    public void TypedSnapshotsRejectFalseHashesAndNonCanonicalReadback()
    {
        var parameters = new[]
        {
            new RecipeParameterSnapshot(
                "voltage",
                RecipeParameterValueType.Decimal,
                "V",
                "5")
        };

        Assert.Throws<ArgumentException>(() => new RecipeRevisionSnapshot(
            "recipe-a",
            "v1",
            "Recipe A",
            RecipeTestEnvironment.BaseTimeUtc,
            parameters,
            new string('0', 64)));
        Assert.Throws<ArgumentException>(() => new RecipeReadbackParameter(
            "voltage",
            RecipeParameterValueType.Decimal,
            "V",
            "5.0"));
        var optional = new RecipeParameterSnapshot(
            "optionalNote",
            RecipeParameterValueType.String,
            null,
            string.Empty,
            required: false);
        var optionalReadback = new RecipeReadbackParameter(
            "optionalNote",
            RecipeParameterValueType.String,
            null,
            string.Empty);
        Assert.Empty(optional.CanonicalValue);
        Assert.False(optional.Required);
        Assert.Empty(optionalReadback.Value);
        Assert.Throws<ArgumentException>(() =>
            new SqliteRecipeOperationsStore("Data Source=:memory:"));
    }

    [Fact]
    public async Task ApiUsesStrictJsonLeastPrivilegeAndStationClaims()
    {
        var services = new ServiceCollection();
        services.AddControllers().AddOpenLineOpsRecipesApi();
        using var provider = services.BuildServiceProvider();
        var json = provider.GetRequiredService<IOptions<JsonOptions>>().Value;
        Assert.Equal(
            JsonUnmappedMemberHandling.Disallow,
            json.JsonSerializerOptions.UnmappedMemberHandling);
        Assert.False(json.JsonSerializerOptions.PropertyNameCaseInsensitive);

        Assert.Equal(
            OpenLineOpsApiSecurity.EngineeringPolicy,
            typeof(RecipeEngineeringController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
        Assert.Equal(
            OpenLineOpsApiSecurity.StationAgentPolicy,
            typeof(RecipeStationAgentController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
        Assert.Equal(
            OpenLineOpsApiSecurity.OperatorPolicy,
            typeof(RecipeOperatorController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);

        using var environment = new RecipeTestEnvironment();
        var assignment = environment.AssignmentCommand();
        Assert.True((await environment.Service.CreateAssignmentAsync(assignment)).IsSuccess);
        var controller = new RecipeStationAgentController(environment.Service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = Principal(
                        OpenLineOpsApiSecurity.StationAgentRole,
                        "other-station")
                }
            }
        };
        var result = await controller.CreateDeploymentAsync(
            RecipeTestEnvironment.RecipeId,
            assignment.AssignmentId,
            new CreateRecipeDeploymentRequest(
                Guid.NewGuid(),
                assignment.AssignmentId,
                "api-deployment",
                environment.Fencing.CurrentToken,
                environment.Clock.UtcNow.AddMinutes(5)),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    private static ClaimsPrincipal Principal(string role, string stationId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "api-agent"),
                new Claim(ClaimTypes.Role, role),
                new Claim(OpenLineOpsApiSecurity.StationIdClaim, stationId)
            ],
            "test",
            ClaimTypes.Name,
            ClaimTypes.Role));
}

internal sealed class RecipeTestEnvironment : IDisposable
{
    public const string RecipeId = "recipe-functional";
    public const string VersionId = "revision-3";
    public const string ProductModelId = "product-a";
    public const string StationId = "station-test";
    public static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-recipes-tests-{Guid.NewGuid():N}");
    private SqliteRecipeOperationsStore _store;

    public RecipeTestEnvironment()
    {
        Directory.CreateDirectory(_root);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "recipes.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
        var parameters = new[]
        {
            new RecipeParameterSnapshot(
                "enabled",
                RecipeParameterValueType.Boolean,
                null,
                "true"),
            new RecipeParameterSnapshot(
                "voltage",
                RecipeParameterValueType.Decimal,
                "V",
                "5")
        };
        Revision = new RecipeRevisionSnapshot(
            RecipeId,
            VersionId,
            "Functional recipe",
            BaseTimeUtc.AddDays(-1),
            parameters,
            RecipeConfigurationHasher.Compute(RecipeId, VersionId, parameters));
        Clock = new TestClock(BaseTimeUtc);
        Fencing = new MutableFencingTokenValidator(10);
        _store = new SqliteRecipeOperationsStore(ConnectionString);
        Service = CreateService();
    }

    public string ConnectionString { get; }

    public RecipeRevisionSnapshot Revision { get; }

    public TestClock Clock { get; }

    public MutableFencingTokenValidator Fencing { get; }

    public SqliteRecipeOperationsStore Store => _store;

    public RecipeOperationsService Service { get; private set; }

    public CreateRecipeAssignmentCommand AssignmentCommand(
        Guid? assignmentId = null,
        string productModelId = ProductModelId,
        DateTimeOffset? effectiveFromUtc = null,
        DateTimeOffset? effectiveUntilUtc = null,
        string commandId = "assignment-create") =>
        new(
            assignmentId ?? Guid.NewGuid(),
            RecipeId,
            VersionId,
            productModelId,
            StationId,
            effectiveFromUtc ?? Clock.UtcNow.AddHours(-1),
            effectiveUntilUtc ?? Clock.UtcNow.AddHours(1),
            "engineer-1",
            commandId);

    public CreateRecipeDeploymentCommand DeploymentCommand(
        Guid assignmentId,
        Guid? deploymentId = null,
        long? fencingToken = null,
        string commandId = "deployment-create") =>
        new(
            deploymentId ?? Guid.NewGuid(),
            assignmentId,
            RecipeId,
            StationId,
            new DeploymentCommand(
                commandId,
                fencingToken ?? Fencing.CurrentToken,
                Clock.UtcNow.AddMinutes(10)),
            "station-agent-1");

    public RecordRecipeVerificationCommand VerificationCommand(
        Guid deploymentId,
        IEnumerable<RecipeReadbackParameter> readback,
        string commandId) =>
        new(
            deploymentId,
            Guid.NewGuid(),
            ExpectedRevision: 1,
            RecipeId,
            StationId,
            readback.ToArray(),
            Clock.UtcNow.AddMinutes(1),
            "station-agent-1",
            commandId);

    public IReadOnlyCollection<RecipeReadbackParameter> Readback() =>
        Revision.Parameters.Select(parameter => new RecipeReadbackParameter(
                parameter.Key,
                parameter.Type,
                parameter.Unit,
                parameter.CanonicalValue))
            .ToArray();

    public RecipeProductionReadinessRequest ReadinessRequest() =>
        new(
            StationId,
            ProductModelId,
            RecipeId,
            VersionId,
            Clock.UtcNow);

    public StartRecipeChangeoverCommand StartChangeoverCommand(
        Guid assignmentId) =>
        new(
            Guid.NewGuid(),
            assignmentId,
            StationId,
            Clock.UtcNow,
            "operator-1",
            "changeover-start");

    public AdvanceRecipeChangeoverCommand AdvanceChangeoverCommand(
        Guid changeoverId,
        long expectedRevision,
        RecipeChangeoverState targetState,
        string evidence,
        Guid? deploymentId = null,
        int? workInProgressCount = null,
        bool? lineClearanceConfirmed = null,
        string? commandId = null) =>
        new(
            changeoverId,
            expectedRevision,
            targetState,
            evidence,
            deploymentId,
            workInProgressCount,
            lineClearanceConfirmed,
            Clock.UtcNow.AddMinutes(expectedRevision),
            "operator-1",
            commandId ?? $"changeover-{targetState}-{expectedRevision}");

    public async Task CompleteChangeoverAsync(
        Guid assignmentId,
        Guid deploymentId)
    {
        var start = StartChangeoverCommand(assignmentId) with
        {
            ChangeoverId = Guid.NewGuid(),
            CommandId = $"readiness-changeover-start-{Guid.NewGuid():N}"
        };
        Assert.True((await Service.StartChangeoverAsync(start)).IsSuccess);
        Assert.True((await Service.AdvanceChangeoverAsync(
            AdvanceChangeoverCommand(
                start.ChangeoverId,
                1,
                RecipeChangeoverState.LineClearance,
                "WIP is zero and line is clear.",
                workInProgressCount: 0,
                lineClearanceConfirmed: true,
                commandId: $"readiness-clear-{Guid.NewGuid():N}"))).IsSuccess);
        Assert.True((await Service.AdvanceChangeoverAsync(
            AdvanceChangeoverCommand(
                start.ChangeoverId,
                2,
                RecipeChangeoverState.Download,
                "Assigned configuration downloaded.",
                deploymentId,
                commandId: $"readiness-download-{Guid.NewGuid():N}"))).IsSuccess);
        Assert.True((await Service.AdvanceChangeoverAsync(
            AdvanceChangeoverCommand(
                start.ChangeoverId,
                3,
                RecipeChangeoverState.ReadbackVerified,
                "Exact readback verification is linked.",
                deploymentId,
                commandId: $"readiness-readback-{Guid.NewGuid():N}"))).IsSuccess);
        Assert.True((await Service.AdvanceChangeoverAsync(
            AdvanceChangeoverCommand(
                start.ChangeoverId,
                4,
                RecipeChangeoverState.FirstArticleConfirmed,
                "First article accepted.",
                commandId: $"readiness-first-article-{Guid.NewGuid():N}"))).IsSuccess);
        Assert.True((await Service.AdvanceChangeoverAsync(
            AdvanceChangeoverCommand(
                start.ChangeoverId,
                5,
                RecipeChangeoverState.Completed,
                "Changeover released for production.",
                commandId: $"readiness-complete-{Guid.NewGuid():N}"))).IsSuccess);
    }

    public void RestartStore()
    {
        _store.Dispose();
        _store = new SqliteRecipeOperationsStore(ConnectionString);
        Service = CreateService();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (!Directory.Exists(_root))
        {
            return;
        }

        var resolved = Path.GetFullPath(_root);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith(
                "openlineops-recipes-tests-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to delete an unexpected Recipes test directory.");
        }

        Directory.Delete(resolved, recursive: true);
    }

    private RecipeOperationsService CreateService() =>
        new(_store, new FixedRevisionSource(Revision), Fencing, Clock);

    internal sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    internal sealed class MutableFencingTokenValidator(long currentToken)
        : IStationFencingTokenValidator
    {
        public long CurrentToken { get; set; } = currentToken;

        public bool AcceptAnyPositiveToken { get; set; }

        public ValueTask<bool> IsCurrentAsync(
            string stationId,
            long fencingToken,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                string.Equals(stationId, StationId, StringComparison.Ordinal)
                && (AcceptAnyPositiveToken
                    ? fencingToken > 0
                    : fencingToken == CurrentToken));
    }

    private sealed class FixedRevisionSource(RecipeRevisionSnapshot revision)
        : IReleasedRecipeRevisionSource
    {
        public ValueTask<RecipeRevisionSnapshot?> GetReleasedAsync(
            string recipeId,
            string versionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<RecipeRevisionSnapshot?>(
                string.Equals(recipeId, revision.RecipeId, StringComparison.Ordinal)
                && string.Equals(versionId, revision.VersionId, StringComparison.Ordinal)
                    ? revision
                    : null);
    }
}
