namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeDynamicActionSlot(
    string SlotId,
    string ChildActionIdPrefix,
    int SequenceBase,
    string SourceMappingMode)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SlotId)
        && !string.IsNullOrWhiteSpace(ChildActionIdPrefix)
        && SequenceBase > 0
        && !string.IsNullOrWhiteSpace(SourceMappingMode);
}
