using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Runtime.Application.Runs;

public static class StationJobIdentity
{
    public static Guid CreateJobId(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static string CreateCancellationIdempotencyKey(string jobIdempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobIdempotencyKey);
        return $"{jobIdempotencyKey}/cancel";
    }
}
