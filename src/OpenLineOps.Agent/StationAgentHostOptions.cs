using System.Globalization;

namespace OpenLineOps.Agent;

internal sealed record StationAgentHostOptions(
    string AgentId,
    string StationId,
    string DataDirectory,
    Uri BrokerUri,
    bool RequireBrokerTls,
    ushort PrefetchCount,
    ushort MaximumConcurrentJobs,
    string PackageDistributionDirectory,
    string PackageCacheDirectory,
    IReadOnlyDictionary<string, string> TrustedPackagePublicKeys,
    string RuntimeExecutablePath,
    string RuntimeWorkingDirectory,
    string ArtifactDirectory,
    string ArtifactExchangeDirectory,
    TimeSpan RuntimeTimeout,
    int MaximumRuntimeOutputBytes,
    IReadOnlyCollection<string> AllowedRestrictedExternalProgramHostAccounts,
    IReadOnlyCollection<string> AllowedRestrictedExternalProgramHostSids,
    string ExternalProgramAppContainerProfileNamespace,
    string SafetyExecutablePath,
    string SafetyWorkingDirectory,
    TimeSpan SafetyTimeout)
{
    public static StationAgentHostOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("OpenLineOps:Agent");
        var dataDirectory = ResolvePath(
            Required(section["DataDirectory"], "OpenLineOps:Agent:DataDirectory"));
        var brokerUriText = Required(
            section["BrokerUri"],
            "OpenLineOps:Agent:BrokerUri");
        if (!Uri.TryCreate(brokerUriText, UriKind.Absolute, out var brokerUri)
            || brokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidDataException(
                "OpenLineOps:Agent:BrokerUri must be an absolute amqp or amqps URI.");
        }

        var trustedKeys = section
            .GetSection("TrustedPackagePublicKeyFiles")
            .GetChildren()
            .ToDictionary(
                child => Required(child.Key, "Trusted package key id"),
                child => File.ReadAllText(ResolvePath(Required(
                    child.Value,
                    $"Trusted package public key file '{child.Key}'"))),
                StringComparer.Ordinal);
        if (trustedKeys.Count == 0)
        {
            throw new InvalidDataException(
                "At least one OpenLineOps:Agent:TrustedPackagePublicKeyFiles entry is required.");
        }

        var runtimeExecutablePath = ResolvePath(Required(
            section["RuntimeExecutablePath"],
            "OpenLineOps:Agent:RuntimeExecutablePath"));
        var allowedExternalProgramAccounts = CanonicalCollection(
            section.GetSection("AllowedRestrictedExternalProgramHostAccounts").Get<string[]>() ?? [],
            "OpenLineOps:Agent:AllowedRestrictedExternalProgramHostAccounts");
        var allowedExternalProgramSids = CanonicalCollection(
            section.GetSection("AllowedRestrictedExternalProgramHostSids").Get<string[]>() ?? [],
            "OpenLineOps:Agent:AllowedRestrictedExternalProgramHostSids");
        if (allowedExternalProgramAccounts.Length == 0 && allowedExternalProgramSids.Length == 0)
        {
            throw new InvalidDataException(
                "OpenLineOps Agent requires at least one restricted external program host account or SID.");
        }

        return new StationAgentHostOptions(
            Required(section["AgentId"], "OpenLineOps:Agent:AgentId"),
            Required(section["StationId"], "OpenLineOps:Agent:StationId"),
            dataDirectory,
            brokerUri,
            Boolean(section["RequireBrokerTls"], defaultValue: true),
            UShort(section["PrefetchCount"], 8, "OpenLineOps:Agent:PrefetchCount"),
            UShort(section["MaximumConcurrentJobs"], 4, "OpenLineOps:Agent:MaximumConcurrentJobs"),
            ResolvePath(Required(
                section["PackageDistributionDirectory"],
                "OpenLineOps:Agent:PackageDistributionDirectory")),
            ResolvePath(section["PackageCacheDirectory"], Path.Combine(dataDirectory, "content")),
            trustedKeys,
            runtimeExecutablePath,
            ResolvePath(section["RuntimeWorkingDirectory"], Path.Combine(dataDirectory, "work")),
            ResolvePath(section["ArtifactDirectory"], Path.Combine(dataDirectory, "artifacts")),
            ResolvePath(Required(
                section["ArtifactExchangeDirectory"],
                "OpenLineOps:Agent:ArtifactExchangeDirectory")),
            Duration(
                section["RuntimeTimeout"],
                TimeSpan.FromHours(1),
                "OpenLineOps:Agent:RuntimeTimeout"),
            PositiveInt(
                section["MaximumRuntimeOutputBytes"],
                1024 * 1024,
                "OpenLineOps:Agent:MaximumRuntimeOutputBytes"),
            allowedExternalProgramAccounts,
            allowedExternalProgramSids,
            Required(
                section["ExternalProgramAppContainerProfileNamespace"],
                "OpenLineOps:Agent:ExternalProgramAppContainerProfileNamespace"),
            ResolvePath(section["SafetyExecutablePath"], runtimeExecutablePath),
            ResolvePath(section["SafetyWorkingDirectory"], Path.Combine(dataDirectory, "safety")),
            Duration(
                section["SafetyTimeout"],
                TimeSpan.FromSeconds(5),
                "OpenLineOps:Agent:SafetyTimeout"));
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException($"{name} is required and must be canonical text.")
            : value;

    private static string ResolvePath(string? configured, string? fallback = null)
    {
        var value = configured ?? fallback
            ?? throw new InvalidDataException("A required Agent path is missing.");
        return Path.GetFullPath(value, AppContext.BaseDirectory);
    }

    private static bool Boolean(string? value, bool defaultValue) =>
        value is null
            ? defaultValue
            : bool.TryParse(value, out var parsed)
                ? parsed
                : throw new InvalidDataException($"'{value}' is not a Boolean Agent setting.");

    private static ushort UShort(string? value, ushort defaultValue, string name) =>
        value is null
            ? defaultValue
            : ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
              && parsed > 0
                ? parsed
                : throw new InvalidDataException($"{name} must be a positive UInt16.");

    private static int PositiveInt(string? value, int defaultValue, string name) =>
        value is null
            ? defaultValue
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
              && parsed > 0
                ? parsed
                : throw new InvalidDataException($"{name} must be a positive integer.");

    private static string[] CanonicalCollection(
        IReadOnlyCollection<string> values,
        string name)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)
                || char.IsWhiteSpace(value[0])
                || char.IsWhiteSpace(value[^1])
                || !result.Add(value))
            {
                throw new InvalidDataException($"{name} must contain unique canonical values.");
            }
        }

        return result.ToArray();
    }

    private static TimeSpan Duration(string? value, TimeSpan defaultValue, string name) =>
        value is null
            ? defaultValue
            : TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var parsed)
              && parsed > TimeSpan.Zero
                ? parsed
                : throw new InvalidDataException(
                    $"{name} must be a positive constant-format duration.");
}
