using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Packages;

internal static class StationPackageContentHasher
{
    public static string Compute(IEnumerable<StationPackageEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            Append(hash, entry.Path);
            Append(hash, "\n");
            Append(hash, entry.Length.ToString(CultureInfo.InvariantCulture));
            Append(hash, "\n");
            Append(hash, entry.Sha256);
            Append(hash, "\n");
            Append(hash, entry.MediaType);
            Append(hash, "\n");
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}
