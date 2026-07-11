namespace OpenLineOps.Runtime.Domain.Resources;

public enum MaterialSlotResolution
{
    CurrentMaterialSlot = 1,
    AvailableSlotInGroup = 2
}

public sealed record MaterialSlotRequirement
{
    public MaterialSlotRequirement(
        MaterialSlotResolution resolution,
        string topologyTargetId,
        IEnumerable<string>? eligibleSlotIds = null)
    {
        if (!Enum.IsDefined(resolution))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolution),
                resolution,
                "Unsupported material Slot resolution.");
        }

        Resolution = resolution;
        TopologyTargetId = Required(topologyTargetId, nameof(topologyTargetId));
        var slots = eligibleSlotIds?.Select(slotId => Required(slotId, nameof(eligibleSlotIds)))
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (slots.Distinct(StringComparer.Ordinal).Count() != slots.Length
            || slots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != slots.Length)
        {
            throw new ArgumentException(
                "Eligible material Slot ids must be unique and cannot differ only by case.",
                nameof(eligibleSlotIds));
        }

        if ((resolution == MaterialSlotResolution.AvailableSlotInGroup) != (slots.Length > 0))
        {
            throw new ArgumentException(
                "AvailableSlotInGroup requires eligible Slot ids; CurrentMaterialSlot cannot declare them.",
                nameof(eligibleSlotIds));
        }

        EligibleSlotIds = slots;
    }

    public MaterialSlotResolution Resolution { get; }

    public string TopologyTargetId { get; }

    public IReadOnlyList<string> EligibleSlotIds { get; }

    private static string Required(string value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
        || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException($"{fieldName} must be a canonical value.", fieldName)
            : value;
}
