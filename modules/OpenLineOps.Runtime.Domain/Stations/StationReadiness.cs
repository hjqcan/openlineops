namespace OpenLineOps.Runtime.Domain.Stations;

public enum StationPrerequisite
{
    InterlocksSatisfied,
    Homed,
    CriticalDevicesHealthy,
    RecipeVerified,
    CalibrationValid,
    SafetyPermitGranted
}

[Flags]
public enum StationReadinessRequirement
{
    None = 0,
    InterlocksSatisfied = 1 << 0,
    Homed = 1 << 1,
    CriticalDevicesHealthy = 1 << 2,
    RecipeVerified = 1 << 3,
    CalibrationValid = 1 << 4,
    SafetyPermitGranted = 1 << 5,
    SafeToPrepare = InterlocksSatisfied
        | CriticalDevicesHealthy
        | SafetyPermitGranted,
    ResetCompleted = SafeToPrepare | Homed,
    ReadyToExecute = ResetCompleted
        | RecipeVerified
        | CalibrationValid
}

public sealed record StationReadiness(
    bool InterlocksSatisfied,
    bool Homed,
    bool CriticalDevicesHealthy,
    bool RecipeVerified,
    bool CalibrationValid,
    bool SafetyPermitGranted)
{
    public static StationReadiness NotReady { get; } = new(
        InterlocksSatisfied: false,
        Homed: false,
        CriticalDevicesHealthy: false,
        RecipeVerified: false,
        CalibrationValid: false,
        SafetyPermitGranted: false);

    public static StationReadiness ReadyToExecute { get; } = new(
        InterlocksSatisfied: true,
        Homed: true,
        CriticalDevicesHealthy: true,
        RecipeVerified: true,
        CalibrationValid: true,
        SafetyPermitGranted: true);

    public IReadOnlyList<StationPrerequisite> GetMissing(
        StationReadinessRequirement requirements)
    {
        if ((requirements & ~StationReadinessRequirement.ReadyToExecute) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requirements),
                requirements,
                "Station readiness requirements contain undefined flags.");
        }

        var missing = new List<StationPrerequisite>(6);
        AddIfMissing(
            requirements,
            StationReadinessRequirement.InterlocksSatisfied,
            InterlocksSatisfied,
            StationPrerequisite.InterlocksSatisfied,
            missing);
        AddIfMissing(
            requirements,
            StationReadinessRequirement.Homed,
            Homed,
            StationPrerequisite.Homed,
            missing);
        AddIfMissing(
            requirements,
            StationReadinessRequirement.CriticalDevicesHealthy,
            CriticalDevicesHealthy,
            StationPrerequisite.CriticalDevicesHealthy,
            missing);
        AddIfMissing(
            requirements,
            StationReadinessRequirement.RecipeVerified,
            RecipeVerified,
            StationPrerequisite.RecipeVerified,
            missing);
        AddIfMissing(
            requirements,
            StationReadinessRequirement.CalibrationValid,
            CalibrationValid,
            StationPrerequisite.CalibrationValid,
            missing);
        AddIfMissing(
            requirements,
            StationReadinessRequirement.SafetyPermitGranted,
            SafetyPermitGranted,
            StationPrerequisite.SafetyPermitGranted,
            missing);
        return missing.AsReadOnly();
    }

    private static void AddIfMissing(
        StationReadinessRequirement requirements,
        StationReadinessRequirement candidate,
        bool satisfied,
        StationPrerequisite prerequisite,
        List<StationPrerequisite> missing)
    {
        if ((requirements & candidate) == candidate && !satisfied)
        {
            missing.Add(prerequisite);
        }
    }
}
