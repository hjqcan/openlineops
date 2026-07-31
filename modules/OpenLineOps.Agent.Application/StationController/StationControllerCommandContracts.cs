using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace OpenLineOps.Agent.Application.StationController;

[DebuggerDisplay("{StationId}/{OwnerInstanceId} (lease proof redacted)")]
public sealed class StationAgentProcessIdentity
{
    public StationAgentProcessIdentity(
        string agentId,
        string stationId,
        Guid ownerInstanceId,
        string? leaseHandle = null)
    {
        AgentId = Required(agentId, nameof(agentId));
        StationId = Required(stationId, nameof(stationId));
        var canonical = ownerInstanceId.ToString("D");
        if (ownerInstanceId == Guid.Empty
            || canonical[14] != '4'
            || canonical[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw new ArgumentException(
                "Owner instance id must be a UUIDv4 generated for this process boot.",
                nameof(ownerInstanceId));
        }

        OwnerInstanceId = canonical;
        LeaseHandle = leaseHandle is null
            ? NewLeaseHandle()
            : RequireLeaseHandle(leaseHandle, nameof(leaseHandle));
    }

    public string AgentId { get; }

    public string StationId { get; }

    public string OwnerInstanceId { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    [JsonIgnore]
    public string LeaseHandle { get; }

    public static StationAgentProcessIdentity Create(
        string agentId,
        string stationId) => new(agentId, stationId, Guid.NewGuid());

    public override string ToString() =>
        $"StationAgentProcessIdentity {{ AgentId = {AgentId}, "
        + $"StationId = {StationId}, OwnerInstanceId = {OwnerInstanceId}, "
        + "LeaseHandle = [REDACTED] }";

    private static string NewLeaseHandle() => Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string RequireLeaseHandle(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 43
            || value.Any(static character =>
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "Lease handle must be a 256-bit unpadded base64url capability.",
                parameterName);
        }

        return value;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{parameterName} must be canonical text.",
                parameterName)
            : value;
}

[DebuggerDisplay("{StationId}/{FencingToken} (handle redacted)")]
public sealed class StationAgentControlLeaseGrant
{
    public StationAgentControlLeaseGrant(
        string stationId,
        string ownerInstanceId,
        long fencingToken,
        DateTimeOffset expiresAtUtc,
        string leaseHandle)
    {
        StationId = Required(stationId, nameof(stationId));
        OwnerInstanceId = Required(ownerInstanceId, nameof(ownerInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        LeaseHandle = RequiredSecret(leaseHandle, nameof(leaseHandle));
        FencingToken = fencingToken;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string StationId { get; }

    public string OwnerInstanceId { get; }

    public long FencingToken { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    [JsonIgnore]
    public string LeaseHandle { get; }

    public StationAgentControlLeaseGrant Renewed(DateTimeOffset expiresAtUtc) =>
        new(
            StationId,
            OwnerInstanceId,
            FencingToken,
            expiresAtUtc,
            LeaseHandle);

    public override string ToString() =>
        $"StationAgentControlLeaseGrant {{ StationId = {StationId}, "
        + $"OwnerInstanceId = {OwnerInstanceId}, FencingToken = {FencingToken}, "
        + $"ExpiresAtUtc = {ExpiresAtUtc:O}, LeaseHandle = [REDACTED] }}";

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                $"{parameterName} is required.",
                parameterName)
            : value;

    private static string RequiredSecret(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 43)
        {
            throw new ArgumentException(
                "Lease handle must be a 256-bit unpadded base64url capability.",
                parameterName);
        }

        if (value.Any(static character =>
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "Lease handle must be unpadded base64url text.",
                parameterName);
        }

        return value;
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName);
        }
    }
}

public enum StationControllerCommandIdempotency
{
    Idempotent = 1,
    ConditionallyIdempotent = 2,
    NonIdempotent = 3
}

public sealed record StationControllerCommandEnvelope(
    string StationId,
    string OwnerAgentId,
    string OwnerInstanceId,
    string CommandId,
    string ControllerSessionId,
    long CommandSequence,
    long FencingToken,
    string Trigger,
    string ExpectedMode,
    string ExpectedCompletionState,
    StationControllerCommandIdempotency Idempotency,
    string SafetyClass,
    string? ConfirmedRecipeId,
    string? ConfirmedRecipeVersion,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset DeadlineUtc,
    long IssuedOperationalEpoch = 1,
    string? RecipeAssignmentId = null,
    string? RecipeDeploymentId = null,
    string? RecipeConfigurationSha256 = null,
    int ContractVersion = StationControllerCommandFingerprint.CurrentVersion);

public enum StationPhysicalControllerExecutionOutcome
{
    Completed = 1,
    Failed = 2,
    CompletionUnknown = 3
}

public sealed record StationControllerObservation(
    string ControllerSessionId,
    long HeartbeatSequence,
    long CommandSequence,
    long AcknowledgedCommandSequence,
    string ObservedMode,
    string ObservedState,
    long StateSequence,
    bool Busy,
    bool Completed,
    bool Error,
    string? ErrorCode,
    bool RecipeConfirmed,
    string? ConfirmedRecipeId,
    string? ConfirmedRecipeVersion,
    string? RecipeAssignmentId,
    string? RecipeDeploymentId,
    string? RecipeConfigurationSha256,
    bool SafetyPermitGranted,
    DateTimeOffset ObservedAtUtc);

public sealed record StationPhysicalControllerExecutionResult(
    StationPhysicalControllerExecutionOutcome Outcome,
    StationControllerObservation? Observation,
    string? ErrorCode,
    string? ErrorReason);

public interface IStationPhysicalControllerExecutor
{
    /// <summary>
    /// Executes a command only after durably rejecting any fencing token below
    /// the controller's per-station high-water mark. Implementations must not
    /// acknowledge physical completion until the exact command sequence has
    /// been observed from the controller.
    /// </summary>
    ValueTask<StationPhysicalControllerExecutionResult> ExecuteAsync(
        StationControllerCommandEnvelope command,
        CancellationToken cancellationToken = default);

    ValueTask<StationControllerObservation> ObserveAsync(
        string stationId,
        CancellationToken cancellationToken = default);
}

public sealed record StationControllerHandshakeReport(
    string OwnerInstanceId,
    long AgentFencingToken,
    string ControllerSessionId,
    long HeartbeatSequence,
    long CommandSequence,
    long AcknowledgedCommandSequence,
    bool Busy,
    bool Completed,
    bool Error,
    string? ErrorCode,
    bool RecipeConfirmed,
    string? ConfirmedRecipeId,
    string? ConfirmedRecipeVersion,
    bool SafetyPermitGranted,
    DateTimeOffset SourceTimestampUtc,
    string CommandId,
    long CommandFencingToken,
    string ObservedMode,
    string ObservedState,
    long StateSequence,
    string Reason);

public sealed record StationControllerCommandAcknowledgement(
    string OwnerInstanceId,
    long FencingToken,
    string CommandId,
    string ControllerSessionId,
    long CommandSequence,
    string ObservedMode,
    string ObservedState,
    long StateSequence,
    string Reason);

public interface IStationControllerCoordinatorClient
{
    ValueTask<StationAgentControlLeaseGrant> AcquireLeaseAsync(
        StationAgentProcessIdentity identity,
        CancellationToken cancellationToken = default);

    ValueTask<DateTimeOffset> RenewLeaseAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseLeaseAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        CancellationToken cancellationToken = default);

    ValueTask<StationControllerCommandEnvelope?> PollCommandAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        CancellationToken cancellationToken = default);

    ValueTask ReportHandshakeAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        StationControllerHandshakeReport report,
        CancellationToken cancellationToken = default);

    ValueTask AcknowledgeCommandAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        StationControllerCommandAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);
}

public sealed class StationAgentControlLeaseRejectedException(string message) :
    InvalidOperationException(message);
