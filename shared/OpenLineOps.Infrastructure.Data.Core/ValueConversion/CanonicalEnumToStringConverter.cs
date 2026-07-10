using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OpenLineOps.Domain.Abstractions.Serialization;

namespace OpenLineOps.Infrastructure.Data.Core.ValueConversion;

public sealed class CanonicalEnumToStringConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public CanonicalEnumToStringConverter()
        : base(
            value => value.ToString(),
            token => Parse(token))
    {
    }

    private static TEnum Parse(string token)
    {
        return CanonicalEnumToken.TryParse<TEnum>(token, out var value)
            ? value
            : throw new InvalidOperationException(
                $"Persisted {typeof(TEnum).Name} value '{token}' is invalid. Expected an exact, " +
                $"case-sensitive token: {CanonicalEnumToken.ExpectedTokens<TEnum>()}.");
    }
}
