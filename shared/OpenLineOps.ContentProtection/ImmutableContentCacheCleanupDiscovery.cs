namespace OpenLineOps.ContentProtection;

public static class ImmutableContentCacheCleanupDiscovery
{
    public const int MaximumDirectEntryCount = 8192;

    public static IReadOnlyList<string> DiscoverPackageContentHashes(
        string packageCacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageCacheRoot);
        string[] entries = [.. Directory
            .EnumerateFileSystemEntries(
                packageCacheRoot,
                "*",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumDirectEntryCount + 1)];
        if (entries.Length > MaximumDirectEntryCount)
        {
            throw new InvalidDataException(
                $"Station package cleanup cache exceeds {MaximumDirectEntryCount} direct entries.");
        }

        var contentHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var attributes = File.GetAttributes(entry);
            var leaf = Path.GetFileName(entry);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0
                || !TryReadPackageContentHash(leaf, out var contentSha256))
            {
                throw new InvalidDataException(
                    $"Station package cleanup cache entry '{leaf}' is not a canonical transaction directory.");
            }

            _ = contentHashes.Add(contentSha256);
        }

        return [.. contentHashes.Order(StringComparer.Ordinal)];
    }

    private static bool TryReadPackageContentHash(
        string leaf,
        out string contentSha256)
    {
        if (IsLowerHexSha256(leaf))
        {
            contentSha256 = leaf;
            return true;
        }

        string[] segments = leaf.Split('.');
        if (segments.Length == 3
            && segments[0].Length == 0
            && IsLowerHexSha256(segments[1])
            && string.Equals(segments[2], "installed", StringComparison.Ordinal))
        {
            contentSha256 = segments[1];
            return true;
        }

        if (segments.Length == 4
            && segments[0].Length == 0
            && IsLowerHexSha256(segments[1])
            && Guid.TryParseExact(segments[2], "N", out Guid transactionId)
            && string.Equals(
                segments[2],
                transactionId.ToString("N"),
                StringComparison.Ordinal)
            && segments[3] is "installing" or "committing")
        {
            contentSha256 = segments[1];
            return true;
        }

        contentSha256 = string.Empty;
        return false;
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
