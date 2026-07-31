using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Quality.Application.Persistence;

namespace OpenLineOps.Quality.Application.Services;

internal static class QualityApplicationGuard
{
    private static readonly JsonSerializerOptions FingerprintJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string CanonicalText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                "Value must be a non-empty canonical string without surrounding whitespace.",
                parameterName);
        }

        return value;
    }

    public static string IdempotencyKey(string value)
    {
        var canonical = CanonicalText(value, nameof(value));
        if (canonical.Length > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Idempotency key cannot exceed 200 characters.");
        }

        return canonical;
    }

    public static QualityIdempotencyContext Idempotency<TCommand>(
        string scope,
        string idempotencyKey,
        TCommand command)
    {
        var key = IdempotencyKey(idempotencyKey);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command, FingerprintJsonOptions);
        var requestSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new QualityIdempotencyContext(scope, key, requestSha256);
    }
}
