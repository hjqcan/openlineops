using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenLineOps.Infrastructure.Data.Core.ValueConversion;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

internal sealed class AlarmLifecycleFactConfiguration :
    IEntityTypeConfiguration<AlarmLifecycleFact>
{
    public void Configure(EntityTypeBuilder<AlarmLifecycleFact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "operations_alarm_lifecycle_facts",
            tableBuilder =>
            {
                tableBuilder.HasTrigger("operations_alarm_lifecycle_facts_no_update");
                tableBuilder.HasTrigger("operations_alarm_lifecycle_facts_no_delete");
            });
        builder.HasKey(fact => fact.Sequence);
        builder.Property(fact => fact.Sequence)
            .ValueGeneratedOnAdd();
        builder.Property(fact => fact.FactId)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(fact => fact.AlarmId)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(fact => fact.CommandId)
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(fact => fact.CommandFingerprint)
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(fact => fact.Action)
            .HasConversion(new CanonicalEnumToStringConverter<AlarmLifecycleAction>())
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(fact => fact.ActorId)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(fact => fact.OccurredAtUtc)
            .HasConversion(
                value => value.UtcTicks,
                value => new DateTimeOffset(value, TimeSpan.Zero))
            .HasColumnType("bigint")
            .IsRequired();
        builder.Property(fact => fact.PayloadJson)
            .HasMaxLength(8_000)
            .IsRequired();
        builder.Property(fact => fact.PreviousSha256)
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(fact => fact.ContentSha256)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(fact => fact.FactId)
            .IsUnique();
        builder.HasIndex(fact => fact.CommandId)
            .IsUnique();
        builder.HasIndex(fact => new { fact.AlarmId, fact.AlarmVersion })
            .IsUnique();
        builder.HasIndex(fact => new { fact.AlarmId, fact.Sequence });
    }
}
