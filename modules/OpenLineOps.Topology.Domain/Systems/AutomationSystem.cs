using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;
using System.Collections.ObjectModel;

namespace OpenLineOps.Topology.Domain.Systems;

public abstract class AutomationSystem : Entity<AutomationSystemId>
{
    private readonly List<CapabilityContractId> _requiredCapabilities;
    private readonly List<CapabilityContractId> _providedCapabilities;

    protected AutomationSystem(
        AutomationSystemId id,
        AutomationSystemId? parentSystemId,
        string systemType,
        string displayName,
        IEnumerable<CapabilityContractId> requiredCapabilities,
        IEnumerable<CapabilityContractId> providedCapabilities,
        IReadOnlyDictionary<string, string> metadata)
        : base(id)
    {
        ParentSystemId = parentSystemId;
        SystemType = TopologyIdGuard.NotBlank(systemType, nameof(systemType));
        DisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        _requiredCapabilities = requiredCapabilities.Distinct().ToList();
        _providedCapabilities = providedCapabilities.Distinct().ToList();
        Metadata = new ReadOnlyDictionary<string, string>(CopyMetadata(metadata));
    }

    public AutomationSystemId? ParentSystemId { get; }

    public abstract SystemKind Kind { get; }

    public string SystemType { get; private set; }

    public string DisplayName { get; private set; }

    public IReadOnlyCollection<CapabilityContractId> RequiredCapabilities => _requiredCapabilities.AsReadOnly();

    public IReadOnlyCollection<CapabilityContractId> ProvidedCapabilities => _providedCapabilities.AsReadOnly();

    public IReadOnlyDictionary<string, string> Metadata { get; private set; }

    public static AutomationSystem Create(
        AutomationSystemId id,
        AutomationSystemId? parentSystemId,
        SystemKind kind,
        string systemType,
        string displayName,
        IEnumerable<CapabilityContractId>? requiredCapabilities = null,
        IEnumerable<CapabilityContractId>? providedCapabilities = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return kind switch
        {
            SystemKind.System => new GeneralAutomationSystem(
                id,
                parentSystemId,
                systemType,
                displayName,
                requiredCapabilities ?? [],
                providedCapabilities ?? [],
                metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)),
            SystemKind.Station => new StationSystem(
                id,
                parentSystemId,
                systemType,
                displayName,
                requiredCapabilities ?? [],
                providedCapabilities ?? [],
                metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported automation system kind.")
        };
    }

    internal void Update(
        string systemType,
        string displayName,
        IReadOnlyDictionary<string, string> metadata)
    {
        var validatedSystemType = TopologyIdGuard.NotBlank(systemType, nameof(systemType));
        var validatedDisplayName = TopologyIdGuard.NotBlank(displayName, nameof(displayName));
        var validatedMetadata = new ReadOnlyDictionary<string, string>(CopyMetadata(metadata));

        SystemType = validatedSystemType;
        DisplayName = validatedDisplayName;
        Metadata = validatedMetadata;
    }

    private static SortedDictionary<string, string> CopyMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            var key = TopologyIdGuard.NotBlank(pair.Key, "metadata key");
            var value = TopologyIdGuard.NotBlank(pair.Value, $"metadata value for {key}");
            if (!copy.TryAdd(key, value))
            {
                throw new ArgumentException($"Duplicate metadata key '{key}'.", nameof(metadata));
            }
        }

        return copy;
    }
}
