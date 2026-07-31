using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Runtime.Contracts;

public sealed record StationExecutionGateResourceFenceClaim(
    string ResourceKind,
    string ResourceId,
    long FencingToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record StationAgentControlLeaseDispatchAuthority(
    string OwnerAgentId,
    string OwnerInstanceId,
    long FencingToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record StationExecutionGateClaim(
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    Guid ProductionUnitId,
    Guid RuntimeSessionId,
    string OperationRunId,
    int OperationAttempt,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string ProductionLineDefinitionId,
    string TopologyId,
    string ActorId,
    string PackageContentSha256,
    string OperationId,
    string FlowDefinitionId,
    string FlowVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    IReadOnlyCollection<StationExecutionGateResourceFenceClaim> ResourceFences,
    string InputsJson,
    DateTimeOffset RequestedAtUtc,
    string? Revision,
    StationAgentControlLeaseDispatchAuthority? AgentControlLease,
    int EvidenceVersion,
    DateTimeOffset AuthorizedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Evidence);

public static class StationExecutionGateClaimCanonicalizer
{
    public const int LegacyVersionWithoutAgentControlLease = 1;
    public const int CurrentVersion = 2;

    public static string ComputeSha256(StationExecutionGateClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(claim.ResourceFences);
        var canonical = new StringBuilder();
        Append(canonical, "version", claim.EvidenceVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "jobId", claim.JobId.ToString("D"));
        Append(canonical, "idempotencyKey", claim.IdempotencyKey);
        Append(canonical, "agentId", claim.AgentId);
        Append(canonical, "stationId", claim.StationId);
        Append(canonical, "stationSystemId", claim.StationSystemId);
        Append(canonical, "productionRunId", claim.ProductionRunId.ToString("D"));
        Append(canonical, "productionUnitId", claim.ProductionUnitId.ToString("D"));
        Append(canonical, "runtimeSessionId", claim.RuntimeSessionId.ToString("D"));
        Append(canonical, "operationRunId", claim.OperationRunId);
        Append(canonical, "operationAttempt", claim.OperationAttempt.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "productModelId", claim.ProductModelId);
        Append(canonical, "productionUnitIdentityInputKey", claim.ProductionUnitIdentityInputKey);
        Append(canonical, "productionUnitIdentityValue", claim.ProductionUnitIdentityValue);
        Append(canonical, "lotId", claim.LotId ?? string.Empty);
        Append(canonical, "carrierId", claim.CarrierId ?? string.Empty);
        Append(canonical, "projectId", claim.ProjectId);
        Append(canonical, "applicationId", claim.ApplicationId);
        Append(canonical, "projectSnapshotId", claim.ProjectSnapshotId);
        Append(canonical, "productionLineDefinitionId", claim.ProductionLineDefinitionId);
        Append(canonical, "topologyId", claim.TopologyId);
        Append(canonical, "actorId", claim.ActorId);
        Append(canonical, "packageContentSha256", claim.PackageContentSha256);
        Append(canonical, "operationId", claim.OperationId);
        Append(canonical, "flowDefinitionId", claim.FlowDefinitionId);
        Append(canonical, "flowVersionId", claim.FlowVersionId);
        Append(canonical, "configurationSnapshotId", claim.ConfigurationSnapshotId);
        Append(canonical, "recipeSnapshotId", claim.RecipeSnapshotId);
        foreach (var fence in claim.ResourceFences
                     .OrderBy(static item => item.ResourceKind, StringComparer.Ordinal)
                     .ThenBy(static item => item.ResourceId, StringComparer.Ordinal))
        {
            Append(canonical, "resourceKind", fence.ResourceKind);
            Append(canonical, "resourceId", fence.ResourceId);
            Append(canonical, "fencingToken", fence.FencingToken.ToString(CultureInfo.InvariantCulture));
            Append(canonical, "fenceExpiresAtUtc", Utc(fence.ExpiresAtUtc));
        }

        Append(canonical, "inputsJson", claim.InputsJson);
        Append(canonical, "requestedAtUtc", Utc(claim.RequestedAtUtc));
        if (claim.EvidenceVersion == CurrentVersion)
        {
            if (string.IsNullOrWhiteSpace(claim.Revision))
            {
                throw new InvalidDataException(
                    "Current Station execution gate claim requires a stable gate revision.");
            }

            var controlLease = claim.AgentControlLease
                ?? throw new InvalidDataException(
                    "Current Station execution gate claim requires Agent control lease authority.");
            Append(canonical, "gateRevision", claim.Revision);
            Append(canonical, "controlLeaseOwnerAgentId", controlLease.OwnerAgentId);
            Append(canonical, "controlLeaseOwnerInstanceId", controlLease.OwnerInstanceId);
            Append(
                canonical,
                "controlLeaseFencingToken",
                controlLease.FencingToken.ToString(CultureInfo.InvariantCulture));
            Append(
                canonical,
                "controlLeaseExpiresAtUtc",
                Utc(controlLease.ExpiresAtUtc));
        }
        else if (claim.EvidenceVersion != LegacyVersionWithoutAgentControlLease)
        {
            throw new InvalidDataException(
                $"Unsupported Station execution gate claim version {claim.EvidenceVersion}.");
        }

        Append(canonical, "authorizedAtUtc", Utc(claim.AuthorizedAtUtc));
        Append(canonical, "expiresAtUtc", Utc(claim.ExpiresAtUtc));
        Append(canonical, "evidence", claim.Evidence);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public static bool HasValidSha256(
        StationExecutionGateClaim claim,
        string fingerprint) =>
        fingerprint is { Length: 64 }
        && fingerprint.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
        && string.Equals(
            fingerprint,
            ComputeSha256(claim),
            StringComparison.Ordinal);

    private static string Utc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value.ToString("O", CultureInfo.InvariantCulture)
            : throw new ArgumentException(
                "Station execution gate claim timestamps must use UTC offset zero.");

    private static void Append(StringBuilder destination, string name, string? value)
    {
        value ??= string.Empty;
        destination
            .Append(name)
            .Append(':')
            .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }
}
