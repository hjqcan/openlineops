using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace OpenLineOps.Infrastructure.Data.Core.Identifiers;

public sealed class StronglyTypedIdValueConverter<TId, TValue> : ValueConverter<TId, TValue>
    where TId : notnull
    where TValue : notnull
{
    public StronglyTypedIdValueConverter()
        : base(ToProviderExpression(), FromProviderExpression())
    {
    }

    private static Expression<Func<TId, TValue>> ToProviderExpression()
    {
        var id = Expression.Parameter(typeof(TId), "id");
        var valueProperty = GetValueProperty();
        var value = Expression.Property(id, valueProperty);

        return Expression.Lambda<Func<TId, TValue>>(value, id);
    }

    private static Expression<Func<TValue, TId>> FromProviderExpression()
    {
        var value = Expression.Parameter(typeof(TValue), "value");
        var constructor = GetValueConstructor();
        var id = Expression.New(constructor, value);

        return Expression.Lambda<Func<TValue, TId>>(id, value);
    }

    private static PropertyInfo GetValueProperty()
    {
        var property = typeof(TId).GetProperty(
            "Value",
            BindingFlags.Instance | BindingFlags.Public);

        if (property is null || property.PropertyType != typeof(TValue))
        {
            throw new InvalidOperationException(
                $"{typeof(TId).Name} must expose a public Value property of type {typeof(TValue).Name}.");
        }

        return property;
    }

    private static ConstructorInfo GetValueConstructor()
    {
        var constructor = typeof(TId).GetConstructor([typeof(TValue)]);
        if (constructor is null)
        {
            throw new InvalidOperationException(
                $"{typeof(TId).Name} must expose a constructor with a single {typeof(TValue).Name} parameter.");
        }

        return constructor;
    }
}
