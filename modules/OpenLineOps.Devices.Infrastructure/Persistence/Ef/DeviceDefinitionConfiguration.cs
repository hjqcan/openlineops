using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Infrastructure.Data.Core.Identifiers;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

internal sealed class DeviceDefinitionConfiguration : IEntityTypeConfiguration<DeviceDefinition>
{
    public void Configure(EntityTypeBuilder<DeviceDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("device_definitions_ef");

        builder.HasKey(definition => definition.Id);

        builder.Property(definition => definition.Id)
            .HasStronglyTypedIdConversion<DeviceDefinitionId, string>()
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(definition => definition.DisplayName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(definition => definition.PluginId)
            .HasMaxLength(160)
            .IsRequired();

        builder.Property(definition => definition.CreatedAtUtc)
            .IsRequired();

        builder.Ignore(definition => definition.DomainEvents);
        builder.HasIndex(definition => definition.PluginId);

        builder.OwnsMany(definition => definition.Capabilities, capability =>
        {
            capability.ToTable("device_definition_capabilities_ef");
            capability.WithOwner().HasForeignKey("DefinitionId");
            capability.Property<DeviceDefinitionId>("DefinitionId")
                .HasStronglyTypedIdConversion<DeviceDefinitionId, string>()
                .HasMaxLength(120)
                .IsRequired();

            capability.HasKey("DefinitionId", nameof(DeviceCapability.Id));

            capability.Property(item => item.Id)
                .HasStronglyTypedIdConversion<DeviceCapabilityId, string>()
                .HasColumnName("capability_id")
                .HasMaxLength(160)
                .IsRequired();

            capability.Property(item => item.DisplayName)
                .HasMaxLength(200)
                .IsRequired();
        });

        builder.OwnsMany(definition => definition.Commands, command =>
        {
            command.ToTable("device_definition_commands_ef");
            command.WithOwner().HasForeignKey("DefinitionId");
            command.Property<DeviceDefinitionId>("DefinitionId")
                .HasStronglyTypedIdConversion<DeviceDefinitionId, string>()
                .HasMaxLength(120)
                .IsRequired();

            command.HasKey("DefinitionId", nameof(DeviceCommandDefinition.Id));

            command.Property(item => item.Id)
                .HasStronglyTypedIdConversion<DeviceCommandDefinitionId, string>()
                .HasColumnName("command_definition_id")
                .HasMaxLength(180)
                .IsRequired();

            command.Property(item => item.CapabilityId)
                .HasStronglyTypedIdConversion<DeviceCapabilityId, string>()
                .HasColumnName("capability_id")
                .HasMaxLength(160)
                .IsRequired();

            command.Property(item => item.CommandName)
                .HasMaxLength(160)
                .IsRequired();

            command.Property(item => item.InputSchema);
            command.Property(item => item.OutputSchema);
            command.Property(item => item.Timeout).IsRequired();
            command.Property(item => item.MaxRetries).IsRequired();
        });

        builder.Navigation(definition => definition.Capabilities).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(definition => definition.Commands).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
