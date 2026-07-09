using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Instances;
using OpenLineOps.Infrastructure.Data.Core.Identifiers;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

internal sealed class DeviceInstanceConfiguration : IEntityTypeConfiguration<DeviceInstance>
{
    public void Configure(EntityTypeBuilder<DeviceInstance> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("device_instances_ef");

        builder.HasKey(instance => instance.Id);

        builder.Property(instance => instance.Id)
            .HasStronglyTypedIdConversion<DeviceInstanceId, string>()
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(instance => instance.DefinitionId)
            .HasStronglyTypedIdConversion<DeviceDefinitionId, string>()
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(instance => instance.StationId)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(instance => instance.DisplayName)
            .HasMaxLength(200)
            .IsRequired();

        builder.OwnsOne(instance => instance.Endpoint, endpoint =>
        {
            endpoint.Property(item => item.Protocol)
                .HasColumnName("endpoint_protocol")
                .HasMaxLength(64)
                .IsRequired();
            endpoint.Property(item => item.Address)
                .HasColumnName("endpoint_address")
                .HasMaxLength(512)
                .IsRequired();
        });

        builder.Property(instance => instance.RegisteredAtUtc)
            .IsRequired();

        builder.Property(instance => instance.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(instance => instance.ConnectedAtUtc);
        builder.Property(instance => instance.LastDisconnectedAtUtc);
        builder.Property(instance => instance.FaultReason).HasMaxLength(1000);

        builder.Ignore(instance => instance.DomainEvents);

        builder.HasIndex(instance => instance.StationId);
        builder.HasIndex(instance => new { instance.DefinitionId, instance.Status });
    }
}
