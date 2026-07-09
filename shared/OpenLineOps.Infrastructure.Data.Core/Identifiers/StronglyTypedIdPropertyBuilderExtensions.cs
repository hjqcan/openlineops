using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace OpenLineOps.Infrastructure.Data.Core.Identifiers;

public static class StronglyTypedIdPropertyBuilderExtensions
{
    public static PropertyBuilder<TId> HasStronglyTypedIdConversion<TId, TValue>(
        this PropertyBuilder<TId> propertyBuilder)
        where TId : notnull
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(propertyBuilder);

        propertyBuilder.HasConversion(new StronglyTypedIdValueConverter<TId, TValue>());

        return propertyBuilder;
    }
}
