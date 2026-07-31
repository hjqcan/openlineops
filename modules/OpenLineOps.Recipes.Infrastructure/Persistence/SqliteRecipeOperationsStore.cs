using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenLineOps.Recipes.Application.Persistence;
using OpenLineOps.Recipes.Domain.Assignments;
using OpenLineOps.Recipes.Domain.Changeovers;
using OpenLineOps.Recipes.Domain.Deployments;

namespace OpenLineOps.Recipes.Infrastructure.Persistence;

public sealed class SqliteRecipeOperationsStore : IRecipeOperationsStore, IDisposable
{
    private const string AssignmentStream = "assignment";
    private const string DeploymentStream = "deployment";
    private const string ChangeoverStream = "changeover";
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _schemaCreated;

    public SqliteRecipeOperationsStore(string connectionString)
    {
        _connectionString = RequireFileBackedConnectionString(connectionString);
    }

    public async ValueTask<RecipeCommandCheck<T>> CheckCommandAsync<T>(
        RecipeIdempotencyContext idempotency,
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ValidateIdempotency(idempotency);
        ValidateStreamKind(streamKind);
        if (streamId == Guid.Empty)
        {
            throw new ArgumentException("Stream id is required.", nameof(streamId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var existing = await GetCommandAsync(
                connection,
                transaction,
                idempotency,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return new RecipeCommandCheck<T>(
                RecipeCommandCheckStatus.Missing,
                null);
        }

        if (!string.Equals(
                existing.RequestSha256,
                idempotency.RequestSha256,
                StringComparison.Ordinal)
            || !string.Equals(existing.StreamKind, streamKind, StringComparison.Ordinal)
            || existing.StreamId != streamId)
        {
            return new RecipeCommandCheck<T>(
                RecipeCommandCheckStatus.Conflict,
                null);
        }

        var replay = await LoadRevisionAsync<T>(
                connection,
                transaction,
                streamKind,
                streamId,
                existing.ResultRevision,
                cancellationToken)
            .ConfigureAwait(false);
        return replay is null
            ? throw new InvalidDataException(
                "Recipe command references a missing fact stream.")
            : new RecipeCommandCheck<T>(
                RecipeCommandCheckStatus.Replay,
                replay);
    }

    public async ValueTask<RecipeStoreWriteResult<RecipeAssignment>> CreateAssignmentAsync(
        RecipeAssignment assignment,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return await ExecuteWriteAsync(
                idempotency,
                AssignmentStream,
                assignment.AssignmentId,
                async (connection, transaction, token) =>
                {
                    if (await LoadLatestAsync<RecipeAssignment>(
                            connection,
                            transaction,
                            AssignmentStream,
                            assignment.AssignmentId,
                            token)
                        .ConfigureAwait(false) is not null)
                    {
                        return new RecipeStoreWriteResult<RecipeAssignment>(
                            RecipeStoreWriteStatus.RevisionConflict,
                            null);
                    }

                    var assignments = await ListStreamsAsync<RecipeAssignment>(
                            connection,
                            transaction,
                            AssignmentStream,
                            token)
                        .ConfigureAwait(false);
                    if (assignments.Any(assignment.Overlaps))
                    {
                        return new RecipeStoreWriteResult<RecipeAssignment>(
                            RecipeStoreWriteStatus.AssignmentOverlap,
                            null);
                    }

                    await AppendFactAsync(
                            connection,
                            transaction,
                            AssignmentStream,
                            assignment.AssignmentId,
                            expectedRevision: 0,
                            "AssignmentCreated",
                            assignment.StationId,
                            assignment.CreatedAtUtc,
                            assignment,
                            fencingToken: null,
                            token)
                        .ConfigureAwait(false);
                    return new RecipeStoreWriteResult<RecipeAssignment>(
                        RecipeStoreWriteStatus.Stored,
                        assignment);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<RecipeAssignment?> GetAssignmentAsync(
        Guid assignmentId,
        CancellationToken cancellationToken = default) =>
        LoadLatestPublicAsync<RecipeAssignment>(
            AssignmentStream,
            assignmentId,
            cancellationToken);

    public async ValueTask<IReadOnlyCollection<RecipeAssignment>> ListAssignmentsAsync(
        string? recipeId = null,
        string? stationId = null,
        string? productModelId = null,
        CancellationToken cancellationToken = default)
    {
        var assignments = await ListPublicAsync<RecipeAssignment>(
                AssignmentStream,
                cancellationToken)
            .ConfigureAwait(false);
        return assignments
            .Where(assignment =>
                (recipeId is null
                    || string.Equals(
                        assignment.RecipeId,
                        recipeId,
                        StringComparison.Ordinal))
                && (stationId is null
                    || string.Equals(
                        assignment.StationId,
                        stationId,
                        StringComparison.Ordinal))
                && (productModelId is null
                    || string.Equals(
                        assignment.ProductModelId,
                        productModelId,
                        StringComparison.Ordinal)))
            .OrderBy(static assignment => assignment.EffectiveFromUtc)
            .ThenBy(static assignment => assignment.AssignmentId)
            .ToArray();
    }

    public async ValueTask<RecipeStoreWriteResult<RecipeDeployment>> CreateDeploymentAsync(
        RecipeDeployment deployment,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return await ExecuteWriteAsync(
                idempotency,
                DeploymentStream,
                deployment.DeploymentId,
                async (connection, transaction, token) =>
                {
                    if (await LoadLatestAsync<RecipeDeployment>(
                            connection,
                            transaction,
                            DeploymentStream,
                            deployment.DeploymentId,
                            token)
                        .ConfigureAwait(false) is not null)
                    {
                        return new RecipeStoreWriteResult<RecipeDeployment>(
                            RecipeStoreWriteStatus.RevisionConflict,
                            null);
                    }

                    var highestFence = await GetHighestFencingTokenAsync(
                            connection,
                            transaction,
                            deployment.StationId,
                            token)
                        .ConfigureAwait(false);
                    if (deployment.Command.FencingToken < highestFence)
                    {
                        return new RecipeStoreWriteResult<RecipeDeployment>(
                            RecipeStoreWriteStatus.StaleFencingToken,
                            null);
                    }

                    await AppendFactAsync(
                            connection,
                            transaction,
                            DeploymentStream,
                            deployment.DeploymentId,
                            expectedRevision: 0,
                            "DeploymentCreated",
                            deployment.StationId,
                            deployment.CreatedAtUtc,
                            deployment,
                            deployment.Command.FencingToken,
                            token)
                        .ConfigureAwait(false);
                    return new RecipeStoreWriteResult<RecipeDeployment>(
                        RecipeStoreWriteStatus.Stored,
                        deployment);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RecipeStoreWriteResult<RecipeDeployment>> AppendVerificationAsync(
        RecipeDeployment deployment,
        long expectedRevision,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return await ExecuteWriteAsync(
                idempotency,
                DeploymentStream,
                deployment.DeploymentId,
                async (connection, transaction, token) =>
                {
                    var highestFence = await GetHighestFencingTokenAsync(
                            connection,
                            transaction,
                            deployment.StationId,
                            token)
                        .ConfigureAwait(false);
                    if (deployment.Command.FencingToken < highestFence)
                    {
                        return new RecipeStoreWriteResult<RecipeDeployment>(
                            RecipeStoreWriteStatus.StaleFencingToken,
                            null);
                    }

                    var current = await LoadLatestAsync<RecipeDeployment>(
                            connection,
                            transaction,
                            DeploymentStream,
                            deployment.DeploymentId,
                            token)
                        .ConfigureAwait(false);
                    if (current is null
                        || current.Revision != expectedRevision
                        || deployment.Revision != checked(expectedRevision + 1))
                    {
                        return new RecipeStoreWriteResult<RecipeDeployment>(
                            RecipeStoreWriteStatus.RevisionConflict,
                            null);
                    }

                    await AppendFactAsync(
                            connection,
                            transaction,
                            DeploymentStream,
                            deployment.DeploymentId,
                            expectedRevision,
                            "VerificationRecorded",
                            deployment.StationId,
                            deployment.Verification!.VerifiedAtUtc,
                            deployment,
                            deployment.Command.FencingToken,
                            token)
                        .ConfigureAwait(false);
                    return new RecipeStoreWriteResult<RecipeDeployment>(
                        RecipeStoreWriteStatus.Stored,
                        deployment);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<RecipeDeployment?> GetDeploymentAsync(
        Guid deploymentId,
        CancellationToken cancellationToken = default) =>
        LoadLatestPublicAsync<RecipeDeployment>(
            DeploymentStream,
            deploymentId,
            cancellationToken);

    public async ValueTask<IReadOnlyCollection<RecipeDeployment>> ListDeploymentsAsync(
        string? stationId = null,
        string? productModelId = null,
        CancellationToken cancellationToken = default)
    {
        var deployments = await ListPublicAsync<RecipeDeployment>(
                DeploymentStream,
                cancellationToken)
            .ConfigureAwait(false);
        return deployments
            .Where(deployment =>
                (stationId is null
                    || string.Equals(
                        deployment.StationId,
                        stationId,
                        StringComparison.Ordinal))
                && (productModelId is null
                    || string.Equals(
                        deployment.ProductModelId,
                        productModelId,
                        StringComparison.Ordinal)))
            .OrderBy(static deployment => deployment.CreatedAtUtc)
            .ThenBy(static deployment => deployment.DeploymentId)
            .ToArray();
    }

    public async ValueTask<RecipeStoreWriteResult<RecipeChangeover>> CreateChangeoverAsync(
        RecipeChangeover changeover,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeover);
        return await ExecuteWriteAsync(
                idempotency,
                ChangeoverStream,
                changeover.ChangeoverId,
                async (connection, transaction, token) =>
                {
                    if (await LoadLatestAsync<RecipeChangeover>(
                            connection,
                            transaction,
                            ChangeoverStream,
                            changeover.ChangeoverId,
                            token)
                        .ConfigureAwait(false) is not null)
                    {
                        return new RecipeStoreWriteResult<RecipeChangeover>(
                            RecipeStoreWriteStatus.RevisionConflict,
                            null);
                    }

                    await AppendFactAsync(
                            connection,
                            transaction,
                            ChangeoverStream,
                            changeover.ChangeoverId,
                            expectedRevision: 0,
                            "ChangeoverStarted",
                            changeover.StationId,
                            changeover.StartedAtUtc,
                            changeover,
                            fencingToken: null,
                            token)
                        .ConfigureAwait(false);
                    return new RecipeStoreWriteResult<RecipeChangeover>(
                        RecipeStoreWriteStatus.Stored,
                        changeover);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RecipeStoreWriteResult<RecipeChangeover>> AppendChangeoverAsync(
        RecipeChangeover changeover,
        long expectedRevision,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeover);
        return await ExecuteWriteAsync(
                idempotency,
                ChangeoverStream,
                changeover.ChangeoverId,
                async (connection, transaction, token) =>
                {
                    var current = await LoadLatestAsync<RecipeChangeover>(
                            connection,
                            transaction,
                            ChangeoverStream,
                            changeover.ChangeoverId,
                            token)
                        .ConfigureAwait(false);
                    if (current is null
                        || current.Revision != expectedRevision
                        || changeover.Revision != checked(expectedRevision + 1))
                    {
                        return new RecipeStoreWriteResult<RecipeChangeover>(
                            RecipeStoreWriteStatus.RevisionConflict,
                            null);
                    }

                    var latestAudit = changeover.Audit.Last();
                    await AppendFactAsync(
                            connection,
                            transaction,
                            ChangeoverStream,
                            changeover.ChangeoverId,
                            expectedRevision,
                            latestAudit.Action,
                            changeover.StationId,
                            latestAudit.OccurredAtUtc,
                            changeover,
                            fencingToken: null,
                            token)
                        .ConfigureAwait(false);
                    return new RecipeStoreWriteResult<RecipeChangeover>(
                        RecipeStoreWriteStatus.Stored,
                        changeover);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<RecipeChangeover?> GetChangeoverAsync(
        Guid changeoverId,
        CancellationToken cancellationToken = default) =>
        LoadLatestPublicAsync<RecipeChangeover>(
            ChangeoverStream,
            changeoverId,
            cancellationToken);

    public async ValueTask<IReadOnlyCollection<RecipeChangeover>> ListChangeoversAsync(
        string? stationId = null,
        Guid? assignmentId = null,
        CancellationToken cancellationToken = default)
    {
        var changeovers = await ListPublicAsync<RecipeChangeover>(
                ChangeoverStream,
                cancellationToken)
            .ConfigureAwait(false);
        return changeovers
            .Where(changeover =>
                (stationId is null
                    || string.Equals(
                        changeover.StationId,
                        stationId,
                        StringComparison.Ordinal))
                && (assignmentId is null
                    || changeover.AssignmentId == assignmentId))
            .OrderBy(static changeover => changeover.StartedAtUtc)
            .ThenBy(static changeover => changeover.ChangeoverId)
            .ToArray();
    }

    public async ValueTask<IReadOnlyCollection<RecipeFactMetadata>> ListFactsAsync(
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken = default)
    {
        ValidateStreamKind(streamKind);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadFactRowsAsync(
                connection,
                transaction: null,
                streamKind,
                streamId,
                cancellationToken)
            .ConfigureAwait(false);
        var head = await GetStreamHeadAsync(
                connection,
                transaction: null,
                streamKind,
                streamId,
                cancellationToken)
            .ConfigureAwait(false);
        VerifyFactChain(rows, head);
        return rows.Select(static row => new RecipeFactMetadata(
                row.StreamKind,
                row.StreamId,
                row.Revision,
                row.FactKind,
                row.StationId,
                row.OccurredAtUtc,
                row.PayloadSha256,
                row.PreviousFactSha256,
                row.FactSha256))
            .ToArray();
    }

    private async ValueTask<RecipeStoreWriteResult<T>> ExecuteWriteAsync<T>(
        RecipeIdempotencyContext idempotency,
        string streamKind,
        Guid streamId,
        Func<SqliteConnection, SqliteTransaction, CancellationToken,
            ValueTask<RecipeStoreWriteResult<T>>> write,
        CancellationToken cancellationToken)
        where T : class
    {
        ValidateIdempotency(idempotency);
        ValidateStreamKind(streamKind);
        if (streamId == Guid.Empty)
        {
            throw new ArgumentException("Stream id is required.", nameof(streamId));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            var existing = await GetCommandAsync(
                    connection,
                    transaction,
                    idempotency,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(
                        existing.RequestSha256,
                        idempotency.RequestSha256,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        existing.StreamKind,
                        streamKind,
                        StringComparison.Ordinal)
                    || existing.StreamId != streamId)
                {
                    return new RecipeStoreWriteResult<T>(
                        RecipeStoreWriteStatus.IdempotencyConflict,
                        null);
                }

                var replay = await LoadRevisionAsync<T>(
                        connection,
                        transaction,
                        streamKind,
                        streamId,
                        existing.ResultRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                return replay is null
                    ? throw new InvalidDataException(
                        "Recipe command references a missing fact stream.")
                    : new RecipeStoreWriteResult<T>(
                        RecipeStoreWriteStatus.Replay,
                        replay);
            }

            var result = await write(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != RecipeStoreWriteStatus.Stored)
            {
                return result;
            }

            await InsertCommandAsync(
                    connection,
                    transaction,
                    idempotency,
                    streamKind,
                    streamId,
                    result.Value switch
                    {
                        RecipeAssignment assignment => assignment.Revision,
                        RecipeDeployment deployment => deployment.Revision,
                        RecipeChangeover changeover => changeover.Revision,
                        _ => throw new InvalidOperationException(
                            "Unsupported Recipes aggregate type.")
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<T?> LoadLatestPublicAsync<T>(
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken)
        where T : class
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadLatestAsync<T>(
                connection,
                transaction: null,
                streamKind,
                streamId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyCollection<T>> ListPublicAsync<T>(
        string streamKind,
        CancellationToken cancellationToken)
        where T : class
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ListStreamsAsync<T>(
                connection,
                transaction: null,
                streamKind,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyCollection<T>> ListStreamsAsync<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string streamKind,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stream_id
            FROM (
                SELECT stream_id
                FROM recipe_stream_heads
                WHERE stream_kind = $stream_kind
                UNION
                SELECT stream_id
                FROM recipe_facts
                WHERE stream_kind = $stream_kind
            )
            ORDER BY stream_id;
            """;
        command.Parameters.AddWithValue("$stream_kind", streamKind);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(Guid.ParseExact(reader.GetString(0), "D"));
            }
        }

        var aggregates = new List<T>(ids.Count);
        foreach (var id in ids)
        {
            var aggregate = await LoadLatestAsync<T>(
                    connection,
                    transaction,
                    streamKind,
                    id,
                    cancellationToken)
                .ConfigureAwait(false);
            if (aggregate is null)
            {
                throw new InvalidDataException(
                    $"Recipe {streamKind}/{id:D} fact stream is empty.");
            }

            aggregates.Add(aggregate);
        }

        return aggregates;
    }

    private static async ValueTask<T?> LoadLatestAsync<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken)
        where T : class
    {
        return await LoadRevisionAsync<T>(
                connection,
                transaction,
                streamKind,
                streamId,
                revision: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<T?> LoadRevisionAsync<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string streamKind,
        Guid streamId,
        long? revision,
        CancellationToken cancellationToken)
        where T : class
    {
        var rows = await ReadFactRowsAsync(
                connection,
                transaction,
                streamKind,
                streamId,
                cancellationToken)
            .ConfigureAwait(false);
        var head = await GetStreamHeadAsync(
                connection,
                transaction,
                streamKind,
                streamId,
                cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            if (head is not null)
            {
                throw new InvalidDataException(
                    $"Recipe fact stream {streamKind}/{streamId:D} is missing below its persisted head.");
            }

            return null;
        }

        VerifyFactChain(rows, head);
        var selected = revision is null
            ? rows[^1]
            : rows.SingleOrDefault(row => row.Revision == revision.Value)
                ?? throw new InvalidDataException(
                    $"Recipe fact stream {streamKind}/{streamId:D} does not contain command result revision {revision.Value}.");
        return Deserialize<T>(selected.PayloadJson);
    }

    private static async ValueTask<IReadOnlyList<FactRow>> ReadFactRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, fact_kind, station_id, occurred_at_utc,
                   payload_json, payload_sha256, previous_fact_sha256, fact_sha256
            FROM recipe_facts
            WHERE stream_kind = $stream_kind AND stream_id = $stream_id
            ORDER BY revision;
            """;
        command.Parameters.AddWithValue("$stream_kind", streamKind);
        command.Parameters.AddWithValue("$stream_id", streamId.ToString("D"));
        var rows = new List<FactRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FactRow(
                streamKind,
                streamId,
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseTimestamp(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return rows;
    }

    private static void VerifyFactChain(
        IReadOnlyList<FactRow> rows,
        StreamHead? head)
    {
        if (rows.Count == 0)
        {
            if (head is not null)
            {
                throw new InvalidDataException(
                    $"Recipe fact stream {head.StreamKind}/{head.StreamId:D} is missing below its persisted head.");
            }

            return;
        }

        if (head is null)
        {
            throw new InvalidDataException(
                $"Recipe fact stream {rows[0].StreamKind}/{rows[0].StreamId:D} has no persisted head.");
        }

        var previous = GenesisHash;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Revision != index + 1
                || !string.Equals(
                    row.PreviousFactSha256,
                    previous,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Recipe fact chain {row.StreamKind}/{row.StreamId:D} is discontinuous.");
            }

            var payloadSha256 = Hash(row.PayloadJson);
            if (!string.Equals(
                    payloadSha256,
                    row.PayloadSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Recipe fact payload {row.StreamKind}/{row.StreamId:D}/{row.Revision} was modified.");
            }

            var factSha256 = ComputeFactHash(
                row.StreamKind,
                row.StreamId,
                row.Revision,
                row.FactKind,
                row.StationId,
                row.OccurredAtUtc,
                row.PayloadSha256,
                row.PreviousFactSha256);
            if (!string.Equals(factSha256, row.FactSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Recipe fact hash {row.StreamKind}/{row.StreamId:D}/{row.Revision} is invalid.");
            }

            previous = row.FactSha256;
        }

        var latest = rows[^1];
        if (head.Revision != latest.Revision
            || !string.Equals(
                head.FactSha256,
                latest.FactSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Recipe fact stream {latest.StreamKind}/{latest.StreamId:D} does not match its persisted head.");
        }
    }

    private static async ValueTask AppendFactAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string streamKind,
        Guid streamId,
        long expectedRevision,
        string factKind,
        string stationId,
        DateTimeOffset occurredAtUtc,
        T payload,
        long? fencingToken,
        CancellationToken cancellationToken)
        where T : class
    {
        var current = await GetCurrentFactAsync(
                connection,
                transaction,
                streamKind,
                streamId,
                cancellationToken)
            .ConfigureAwait(false);
        var head = await GetStreamHeadAsync(
                connection,
                transaction,
                streamKind,
                streamId,
                cancellationToken)
            .ConfigureAwait(false);
        if ((current is null) != (head is null)
            || (current is not null
                && (head!.Revision != current.Revision
                    || !string.Equals(
                        head.FactSha256,
                        current.FactSha256,
                        StringComparison.Ordinal))))
        {
            throw new InvalidDataException(
                $"Recipe fact stream {streamKind}/{streamId:D} does not match its persisted head.");
        }

        var currentRevision = current?.Revision ?? 0;
        if (currentRevision != expectedRevision)
        {
            throw new DBConcurrencyException(
                $"Recipe fact stream expected revision {expectedRevision}, actual {currentRevision}.");
        }

        if (current is not null && occurredAtUtc < current.OccurredAtUtc)
        {
            throw new InvalidDataException(
                "Recipe fact time cannot precede the previous fact.");
        }

        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var payloadSha256 = Hash(payloadJson);
        var nextRevision = checked(expectedRevision + 1);
        var previousFactSha256 = current?.FactSha256 ?? GenesisHash;
        var factSha256 = ComputeFactHash(
            streamKind,
            streamId,
            nextRevision,
            factKind,
            stationId,
            occurredAtUtc,
            payloadSha256,
            previousFactSha256);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recipe_facts (
                stream_kind, stream_id, revision, fact_kind, station_id,
                occurred_at_utc, payload_json, payload_sha256,
                previous_fact_sha256, fact_sha256, fencing_token)
            VALUES (
                $stream_kind, $stream_id, $revision, $fact_kind, $station_id,
                $occurred_at_utc, $payload_json, $payload_sha256,
                $previous_fact_sha256, $fact_sha256, $fencing_token);
            """;
        command.Parameters.AddWithValue("$stream_kind", streamKind);
        command.Parameters.AddWithValue("$stream_id", streamId.ToString("D"));
        command.Parameters.AddWithValue("$revision", nextRevision);
        command.Parameters.AddWithValue("$fact_kind", factKind);
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$occurred_at_utc", FormatTimestamp(occurredAtUtc));
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$payload_sha256", payloadSha256);
        command.Parameters.AddWithValue("$previous_fact_sha256", previousFactSha256);
        command.Parameters.AddWithValue("$fact_sha256", factSha256);
        command.Parameters.AddWithValue(
            "$fencing_token",
            fencingToken is null ? DBNull.Value : fencingToken.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await AdvanceStreamHeadAsync(
                connection,
                transaction,
                streamKind,
                streamId,
                expectedRevision,
                previousFactSha256,
                nextRevision,
                factSha256,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<StreamHead?> GetStreamHeadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, fact_sha256
            FROM recipe_stream_heads
            WHERE stream_kind = $stream_kind AND stream_id = $stream_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stream_kind", streamKind);
        command.Parameters.AddWithValue("$stream_id", streamId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StreamHead(
                streamKind,
                streamId,
                reader.GetInt64(0),
                reader.GetString(1))
            : null;
    }

    private static async ValueTask AdvanceStreamHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string streamKind,
        Guid streamId,
        long expectedRevision,
        string expectedFactSha256,
        long nextRevision,
        string nextFactSha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (expectedRevision == 0)
        {
            command.CommandText = """
                INSERT INTO recipe_stream_heads (
                    stream_kind, stream_id, revision, fact_sha256)
                VALUES (
                    $stream_kind, $stream_id, $next_revision, $next_fact_sha256);
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE recipe_stream_heads
                SET revision = $next_revision, fact_sha256 = $next_fact_sha256
                WHERE stream_kind = $stream_kind
                  AND stream_id = $stream_id
                  AND revision = $expected_revision
                  AND fact_sha256 = $expected_fact_sha256;
                """;
            command.Parameters.AddWithValue("$expected_revision", expectedRevision);
            command.Parameters.AddWithValue(
                "$expected_fact_sha256",
                expectedFactSha256);
        }

        command.Parameters.AddWithValue("$stream_kind", streamKind);
        command.Parameters.AddWithValue("$stream_id", streamId.ToString("D"));
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        command.Parameters.AddWithValue("$next_fact_sha256", nextFactSha256);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException(
                $"Recipe fact stream head {streamKind}/{streamId:D} changed concurrently.");
        }
    }

    private static async ValueTask<CurrentFact?> GetCurrentFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision, fact_sha256, occurred_at_utc
            FROM recipe_facts
            WHERE stream_kind = $stream_kind AND stream_id = $stream_id
            ORDER BY revision DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stream_kind", streamKind);
        command.Parameters.AddWithValue("$stream_id", streamId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CurrentFact(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseTimestamp(reader.GetString(2)))
            : null;
    }

    private static async ValueTask<long> GetHighestFencingTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(fencing_token), 0)
            FROM recipe_facts
            WHERE stream_kind = 'deployment'
              AND fact_kind = 'DeploymentCreated'
              AND station_id = $station_id;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<CommandRow?> GetCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_sha256, stream_kind, stream_id, result_revision
            FROM recipe_commands
            WHERE scope = $scope AND command_id = $command_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$scope", idempotency.Scope);
        command.Parameters.AddWithValue("$command_id", idempotency.CommandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CommandRow(
                reader.GetString(0),
                reader.GetString(1),
                Guid.ParseExact(reader.GetString(2), "D"),
                reader.GetInt64(3))
            : null;
    }

    private static async ValueTask InsertCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecipeIdempotencyContext idempotency,
        string streamKind,
        Guid streamId,
        long resultRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recipe_commands (
                scope, command_id, request_sha256,
                stream_kind, stream_id, result_revision)
            VALUES (
                $scope, $command_id, $request_sha256,
                $stream_kind, $stream_id, $result_revision);
            """;
        command.Parameters.AddWithValue("$scope", idempotency.Scope);
        command.Parameters.AddWithValue("$command_id", idempotency.CommandId);
        command.Parameters.AddWithValue("$request_sha256", idempotency.RequestSha256);
        command.Parameters.AddWithValue("$stream_kind", streamKind);
        command.Parameters.AddWithValue("$stream_id", streamId.ToString("D"));
        command.Parameters.AddWithValue("$result_revision", resultRevision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaCreated) == 1)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaCreated) == 1)
            {
                return;
            }

            EnsureDatabaseDirectory();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS recipe_facts (
                    stream_kind TEXT NOT NULL,
                    stream_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0),
                    fact_kind TEXT NOT NULL,
                    station_id TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    payload_sha256 TEXT NOT NULL,
                    previous_fact_sha256 TEXT NOT NULL,
                    fact_sha256 TEXT NOT NULL,
                    fencing_token INTEGER NULL CHECK(fencing_token IS NULL OR fencing_token > 0),
                    PRIMARY KEY(stream_kind, stream_id, revision),
                    UNIQUE(fact_sha256)
                );

                CREATE INDEX IF NOT EXISTS ix_recipe_facts_station
                    ON recipe_facts(stream_kind, station_id, occurred_at_utc);

                CREATE TABLE IF NOT EXISTS recipe_stream_heads (
                    stream_kind TEXT NOT NULL,
                    stream_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision > 0),
                    fact_sha256 TEXT NOT NULL,
                    PRIMARY KEY(stream_kind, stream_id),
                    UNIQUE(fact_sha256)
                );

                CREATE TABLE IF NOT EXISTS recipe_commands (
                    scope TEXT NOT NULL,
                    command_id TEXT NOT NULL,
                    request_sha256 TEXT NOT NULL,
                    stream_kind TEXT NOT NULL,
                    stream_id TEXT NOT NULL,
                    result_revision INTEGER NOT NULL CHECK(result_revision > 0),
                    PRIMARY KEY(scope, command_id)
                );

                CREATE TRIGGER IF NOT EXISTS trg_recipe_facts_no_update
                BEFORE UPDATE ON recipe_facts
                BEGIN
                    SELECT RAISE(ABORT, 'recipe facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS trg_recipe_facts_no_delete
                BEFORE DELETE ON recipe_facts
                BEGIN
                    SELECT RAISE(ABORT, 'recipe facts are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS trg_recipe_commands_no_update
                BEFORE UPDATE ON recipe_commands
                BEGIN
                    SELECT RAISE(ABORT, 'recipe commands are append-only');
                END;

                CREATE TRIGGER IF NOT EXISTS trg_recipe_commands_no_delete
                BEFORE DELETE ON recipe_commands
                BEGIN
                    SELECT RAISE(ABORT, 'recipe commands are append-only');
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaCreated, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void EnsureDatabaseDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string RequireFileBackedConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "SQLite connection string is required.",
                nameof(connectionString));
        }

        var canonical = connectionString.Trim();
        var builder = new SqliteConnectionStringBuilder(canonical);
        if (builder.Mode == SqliteOpenMode.Memory
            || string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || (builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && builder.DataSource.Contains(
                    "mode=memory",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Recipes SQLite persistence requires a file-backed database.",
                nameof(connectionString));
        }

        return canonical;
    }

    private static void ValidateIdempotency(RecipeIdempotencyContext idempotency)
    {
        ArgumentNullException.ThrowIfNull(idempotency);
        if (string.IsNullOrWhiteSpace(idempotency.Scope)
            || string.IsNullOrWhiteSpace(idempotency.CommandId)
            || idempotency.RequestSha256.Length != 64
            || idempotency.RequestSha256.Any(static character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Recipe idempotency evidence is invalid.",
                nameof(idempotency));
        }
    }

    private static void ValidateStreamKind(string streamKind)
    {
        if (streamKind is not (AssignmentStream or DeploymentStream or ChangeoverStream))
        {
            throw new ArgumentException(
                "Recipe stream kind is not supported.",
                nameof(streamKind));
        }
    }

    private static JsonSerializerOptions CreateJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException(
            $"Persisted Recipes {typeof(T).Name} document is empty.");

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ComputeFactHash(
        string streamKind,
        Guid streamId,
        long revision,
        string factKind,
        string stationId,
        DateTimeOffset occurredAtUtc,
        string payloadSha256,
        string previousFactSha256) =>
        Hash(string.Join(
            '\n',
            streamKind,
            streamId.ToString("D"),
            revision.ToString(CultureInfo.InvariantCulture),
            factKind,
            stationId,
            FormatTimestamp(occurredAtUtc),
            payloadSha256,
            previousFactSha256));

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.ParseExact(
            timestamp,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    public void Dispose()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
        _gate.Dispose();
    }

    private sealed record CurrentFact(
        long Revision,
        string FactSha256,
        DateTimeOffset OccurredAtUtc);

    private sealed record StreamHead(
        string StreamKind,
        Guid StreamId,
        long Revision,
        string FactSha256);

    private sealed record CommandRow(
        string RequestSha256,
        string StreamKind,
        Guid StreamId,
        long ResultRevision);

    private sealed record FactRow(
        string StreamKind,
        Guid StreamId,
        long Revision,
        string FactKind,
        string StationId,
        DateTimeOffset OccurredAtUtc,
        string PayloadJson,
        string PayloadSha256,
        string PreviousFactSha256,
        string FactSha256);
}
