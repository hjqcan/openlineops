using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenLineOps.Infrastructure.Data.Core.Repositories;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Repositories;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

public sealed class EfAlarmRepository(OperationsDbContext context)
    : BaseRepository<OperationsDbContext, Alarm, AlarmId>(context),
        IAlarmRepository
{
    public async Task<AlarmCommitOutcome> CommitAlarmChangesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var committed = await Db.CommitAsync(cancellationToken).ConfigureAwait(false);
            return committed
                ? AlarmCommitOutcome.Committed
                : AlarmCommitOutcome.NoChanges;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AlarmCommitOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            return AlarmCommitOutcome.UniqueConstraintConflict;
        }
    }

    public async Task<AlarmDefinition?> GetDefinitionAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);
        var definition = await Db.AlarmDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == id.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        EnsureDefinitionIntegrity(definition);
        return definition;
    }

    public async Task<AlarmDefinition?> GetDefinitionByCommandIdAsync(
        string commandId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);
        var definition = await Db.AlarmDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.RegistrationCommandId == commandId.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        EnsureDefinitionIntegrity(definition);
        return definition;
    }

    public void AddDefinition(AlarmDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Db.EnsureSchemaReady();
        Db.AlarmDefinitions.Add(definition);
    }

    public async Task<AlarmLifecycleFact?> GetFactByCommandIdAsync(
        string commandId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);
        var fact = await Db.AlarmLifecycleFacts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.CommandId == commandId.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        if (fact is not null)
        {
            await EnsureFactChainIntegrityAsync(
                    new AlarmId(fact.AlarmId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return fact;
    }

    public async Task<IReadOnlyCollection<AlarmLifecycleFact>> GetFactsAsync(
        AlarmId alarmId,
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);
        var facts = await Db.AlarmLifecycleFacts
            .AsNoTracking()
            .Where(fact => fact.AlarmId == alarmId.Value)
            .OrderBy(fact => fact.AlarmVersion)
            .ThenBy(fact => fact.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureFactChainIntegrity(facts);
        return facts;
    }

    public async Task<string> GetLastFactHashAsync(
        AlarmId alarmId,
        CancellationToken cancellationToken = default)
    {
        var facts = await GetFactsAsync(alarmId, cancellationToken).ConfigureAwait(false);
        return facts.LastOrDefault()?.ContentSha256 ?? string.Empty;
    }

    public void AddFact(AlarmLifecycleFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Db.EnsureSchemaReady();
        Db.AlarmLifecycleFacts.Add(fact);
    }

    public void UpdateWithExpectedVersion(Alarm aggregate, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Db.EnsureSchemaReady();
        DbSet.Update(aggregate);
        Db.Entry(aggregate)
            .Property(candidate => candidate.Version)
            .OriginalValue = expectedVersion;
    }

    public override async Task<Alarm?> GetByIdAsync(
        AlarmId id,
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        var alarm = DbSet.Local.SingleOrDefault(candidate => candidate.Id == id)
            ?? await base.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (alarm is not null)
        {
            await EnsureFactChainIntegrityAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return alarm;
    }

    public override async Task<IReadOnlyCollection<Alarm>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        return await base.GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Add(Alarm aggregate)
    {
        Db.EnsureSchemaReady();

        base.Add(aggregate);
    }

    public override void Update(Alarm aggregate)
    {
        Db.EnsureSchemaReady();

        base.Update(aggregate);
    }

    public override void Remove(Alarm aggregate)
    {
        Db.EnsureSchemaReady();

        base.Remove(aggregate);
    }

    public override async Task RemoveByIdAsync(
        AlarmId id,
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        await base.RemoveByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<Alarm>> GetOpenByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return [];
        }

        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        var alarms = await DbSet
            .AsNoTracking()
            .Where(alarm => alarm.StationId == stationId.Trim()
                && alarm.Status != AlarmStatus.Resolved)
            .OrderByDescending(alarm => alarm.RaisedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var alarm in alarms)
        {
            await EnsureFactChainIntegrityAsync(alarm.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return alarms;
    }

    public async Task<IReadOnlyCollection<Alarm>> GetByStatusAsync(
        AlarmStatus status,
        CancellationToken cancellationToken = default)
    {
        await Db.EnsureSchemaReadyAsync(cancellationToken).ConfigureAwait(false);

        var alarms = await DbSet
            .AsNoTracking()
            .Where(alarm => alarm.Status == status)
            .OrderByDescending(alarm => alarm.RaisedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var alarm in alarms)
        {
            await EnsureFactChainIntegrityAsync(alarm.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return alarms;
    }

    private async Task EnsureFactChainIntegrityAsync(
        AlarmId alarmId,
        CancellationToken cancellationToken)
    {
        var facts = await Db.AlarmLifecycleFacts
            .AsNoTracking()
            .Where(fact => fact.AlarmId == alarmId.Value)
            .OrderBy(fact => fact.AlarmVersion)
            .ThenBy(fact => fact.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureFactChainIntegrity(facts);
    }

    private static void EnsureFactChainIntegrity(
        IReadOnlyCollection<AlarmLifecycleFact> facts)
    {
        var previousHash = string.Empty;
        long previousVersion = 0;
        foreach (var fact in facts)
        {
            if (fact.AlarmVersion <= previousVersion
                || !string.Equals(
                    fact.PreviousSha256,
                    previousHash,
                    StringComparison.Ordinal)
                || !fact.HasValidContentHash())
            {
                throw new InvalidDataException(
                    "Alarm lifecycle fact-chain integrity verification failed.");
            }

            previousHash = fact.ContentSha256;
            previousVersion = fact.AlarmVersion;
        }
    }

    private static void EnsureDefinitionIntegrity(AlarmDefinition? definition)
    {
        if (definition is not null && !definition.HasValidContentHash())
        {
            throw new InvalidDataException(
                "Alarm definition content hash verification failed.");
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException
            {
                SqliteExtendedErrorCode: 1555 or 2067
            }
            or PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }
}
