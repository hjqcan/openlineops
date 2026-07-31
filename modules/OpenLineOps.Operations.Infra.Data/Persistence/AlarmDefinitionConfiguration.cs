using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenLineOps.Infrastructure.Data.Core.ValueConversion;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

internal sealed class AlarmDefinitionConfiguration :
    IEntityTypeConfiguration<AlarmDefinition>
{
    public void Configure(EntityTypeBuilder<AlarmDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(
            "operations_alarm_definitions",
            tableBuilder =>
            {
                tableBuilder.HasTrigger("operations_alarm_definitions_no_update");
                tableBuilder.HasTrigger("operations_alarm_definitions_no_delete");
            });
        builder.HasKey(definition => definition.Id);

        builder.Property(definition => definition.Id)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(definition => definition.StationId)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(definition => definition.Source)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(definition => definition.Severity)
            .HasConversion(new CanonicalEnumToStringConverter<AlarmSeverity>())
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(definition => definition.Title)
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(definition => definition.Description)
            .HasMaxLength(1_000)
            .IsRequired();
        builder.Property(definition => definition.EscalationAction)
            .HasConversion(new CanonicalEnumToStringConverter<AlarmPolicyAction>())
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(definition => definition.CreatedBy)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(definition => definition.CreatedAtUtc)
            .HasConversion(
                value => value.UtcTicks,
                value => new DateTimeOffset(value, TimeSpan.Zero))
            .HasColumnType("bigint")
            .IsRequired();
        builder.Property(definition => definition.RegistrationCommandId)
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(definition => definition.CommandFingerprint)
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(definition => definition.ContentSha256)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(definition => definition.RegistrationCommandId)
            .IsUnique();
        builder.HasIndex(definition => new { definition.StationId, definition.Source });
    }
}
