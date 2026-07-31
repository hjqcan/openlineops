using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenLineOps.Infrastructure.Data.Core.Identifiers;
using OpenLineOps.Infrastructure.Data.Core.ValueConversion;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Identifiers;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

internal sealed class AlarmConfiguration : IEntityTypeConfiguration<Alarm>
{
    public void Configure(EntityTypeBuilder<Alarm> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("operations_alarms");

        builder.HasKey(aggregate => aggregate.Id);

        builder.Property(aggregate => aggregate.Id)
            .HasStronglyTypedIdConversion<AlarmId, string>()
            .HasMaxLength(160)
            .IsRequired();

        builder.Property(aggregate => aggregate.StationId)
            .HasMaxLength(160)
            .IsRequired();

        builder.Property(aggregate => aggregate.Source)
            .HasMaxLength(160)
            .IsRequired();

        builder.Property(aggregate => aggregate.SourceId)
            .HasMaxLength(160);

        builder.Property(aggregate => aggregate.DefinitionId)
            .HasMaxLength(160);

        builder.Property(aggregate => aggregate.Severity)
            .HasConversion(new CanonicalEnumToStringConverter<OpenLineOps.Operations.Domain.Shared.Enums.AlarmSeverity>())
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(aggregate => aggregate.Status)
            .HasConversion(new CanonicalEnumToStringConverter<OpenLineOps.Operations.Domain.Shared.Enums.AlarmStatus>())
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(aggregate => aggregate.Title)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(aggregate => aggregate.Description)
            .HasMaxLength(1_000)
            .IsRequired();

        builder.Property(aggregate => aggregate.RaisedAtUtc)
            .HasConversion(
                value => value.UtcTicks,
                value => new DateTimeOffset(value, TimeSpan.Zero))
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(aggregate => aggregate.LastChangedAtUtc)
            .HasConversion(
                value => value.UtcTicks,
                value => new DateTimeOffset(value, TimeSpan.Zero))
            .HasColumnType("bigint")
            .IsRequired();

        builder.Property(aggregate => aggregate.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(aggregate => aggregate.SourceClearedBy)
            .HasMaxLength(160);

        builder.Property(aggregate => aggregate.SourceClearedAtUtc)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null)
            .HasColumnType("bigint");

        builder.Property(aggregate => aggregate.SourceClearanceNote)
            .HasMaxLength(1_000);

        builder.Property(aggregate => aggregate.EscalationAction)
            .HasConversion(new CanonicalEnumToStringConverter<OpenLineOps.Operations.Domain.Shared.Enums.AlarmPolicyAction>())
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(aggregate => aggregate.AcknowledgedBy)
            .HasMaxLength(160);

        builder.Property(aggregate => aggregate.AcknowledgedAtUtc)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null)
            .HasColumnType("bigint");

        builder.Property(aggregate => aggregate.AcknowledgementComment)
            .HasMaxLength(1_000);

        builder.Property(aggregate => aggregate.ResolvedBy)
            .HasMaxLength(160);

        builder.Property(aggregate => aggregate.ResolvedAtUtc)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null)
            .HasColumnType("bigint");

        builder.Property(aggregate => aggregate.ResolutionNote)
            .HasMaxLength(1_000);

        builder.Property(aggregate => aggregate.ShelvedBy)
            .HasMaxLength(160);

        builder.Property(aggregate => aggregate.ShelfComment)
            .HasMaxLength(1_000);

        builder.Property(aggregate => aggregate.ShelvedAtUtc)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null)
            .HasColumnType("bigint");

        builder.Property(aggregate => aggregate.ShelvedUntilUtc)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null)
            .HasColumnType("bigint");

        builder.Property(aggregate => aggregate.SuppressionSource)
            .HasMaxLength(160);

        builder.Property(aggregate => aggregate.SuppressionReason)
            .HasMaxLength(1_000);

        builder.Property(aggregate => aggregate.SuppressedBy)
            .HasMaxLength(160);

        builder.Property(aggregate => aggregate.SuppressedAtUtc)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null)
            .HasColumnType("bigint");

        builder.Property(aggregate => aggregate.SuppressedUntilUtc)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null)
            .HasColumnType("bigint");

        builder.Ignore(aggregate => aggregate.DomainEvents);
        builder.Ignore(aggregate => aggregate.IsOpen);

        builder.HasIndex(aggregate => new { aggregate.StationId, aggregate.Status });
        builder.HasIndex(aggregate => aggregate.RaisedAtUtc);
    }
}
