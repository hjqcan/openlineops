using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenLineOps.Infrastructure.Data.Core.Identifiers;
using OpenLineOps.Infrastructure.Data.Core.ValueConversion;
using OpenLineOps.SampleInspection.Domain.Identifiers;
using OpenLineOps.SampleInspection.Domain.Plans;

namespace OpenLineOps.SampleInspection.Infrastructure.Persistence;

internal sealed class InspectionPlanConfiguration : IEntityTypeConfiguration<InspectionPlan>
{
    public void Configure(EntityTypeBuilder<InspectionPlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inspection_plans");

        builder.HasKey(plan => plan.Id);

        builder.Property(plan => plan.Id)
            .HasStronglyTypedIdConversion<InspectionPlanId, string>()
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(plan => plan.DisplayName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(plan => plan.TargetDeviceId)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(plan => plan.Status)
            .HasConversion(new CanonicalEnumToStringConverter<InspectionPlanStatus>())
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(plan => plan.CreatedAtUtc)
            .IsRequired();

        builder.Property(plan => plan.ActivatedAtUtc);

        builder.Ignore(plan => plan.DomainEvents);

        builder.HasIndex(plan => plan.TargetDeviceId);
    }
}
