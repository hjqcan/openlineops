namespace OpenLineOps.Agent;

internal sealed record StationAgentCommandLine(
    bool ProvisionContentCache,
    string? RemoveContentCachePackageSha256,
    string[] ConfigurationArguments)
{
    internal const string ProvisionContentCacheSwitch = "--provision-content-cache";
    internal const string RemoveContentCachePackageSwitch =
        "--remove-content-cache-package";

    public static StationAgentCommandLine Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var provisionContentCache = false;
        string? removeContentCachePackageSha256 = null;
        var configurationArguments = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(
                    argument,
                    RemoveContentCachePackageSwitch,
                    StringComparison.Ordinal))
            {
                if (removeContentCachePackageSha256 is not null)
                {
                    throw new InvalidDataException(
                        $"{RemoveContentCachePackageSwitch} may be specified only once.");
                }

                if (++index >= arguments.Count)
                {
                    throw new InvalidDataException(
                        $"{RemoveContentCachePackageSwitch} requires one lowercase SHA-256 value.");
                }

                var contentSha256 = arguments[index];
                if (contentSha256.Length != 64
                    || contentSha256.Any(static character =>
                        character is not (>= '0' and <= '9')
                            and not (>= 'a' and <= 'f')))
                {
                    throw new InvalidDataException(
                        $"{RemoveContentCachePackageSwitch} requires one lowercase SHA-256 value.");
                }

                removeContentCachePackageSha256 = contentSha256;
                continue;
            }

            if (!string.Equals(
                    argument,
                    ProvisionContentCacheSwitch,
                    StringComparison.Ordinal))
            {
                configurationArguments.Add(argument);
                continue;
            }

            if (provisionContentCache)
            {
                throw new InvalidDataException(
                    $"{ProvisionContentCacheSwitch} may be specified only once.");
            }

            provisionContentCache = true;
        }

        if (provisionContentCache && removeContentCachePackageSha256 is not null)
        {
            throw new InvalidDataException(
                $"{ProvisionContentCacheSwitch} and {RemoveContentCachePackageSwitch} are mutually exclusive.");
        }

        return new StationAgentCommandLine(
            provisionContentCache,
            removeContentCachePackageSha256,
            configurationArguments.ToArray());
    }
}
