using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Stations;

public sealed record StationAgentControlLease
{
    public const int MaximumOwnerIdentityLength = 128;

    public StationAgentControlLease(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        StationId = stationId;
        OwnerAgentId = RequireOwnerIdentity(ownerAgentId, nameof(ownerAgentId));
        OwnerInstanceId = RequireOwnerInstanceId(
            ownerInstanceId,
            nameof(ownerInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        LeaseProofSha256 = RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        RequireUtc(renewedAtUtc, nameof(renewedAtUtc));
        RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (renewedAtUtc < acquiredAtUtc)
        {
            throw new ArgumentException(
                "Lease renewal time cannot precede acquisition time.",
                nameof(renewedAtUtc));
        }

        if (expiresAtUtc < renewedAtUtc)
        {
            throw new ArgumentException(
                "Lease expiry cannot precede the latest renewal time.",
                nameof(expiresAtUtc));
        }

        FencingToken = fencingToken;
        AcquiredAtUtc = acquiredAtUtc;
        RenewedAtUtc = renewedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public StationId StationId { get; }

    public string OwnerAgentId { get; }

    public string OwnerInstanceId { get; }

    public long FencingToken { get; }

    public string LeaseProofSha256 { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public DateTimeOffset RenewedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsActiveAt(DateTimeOffset observedAtUtc)
    {
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        return ExpiresAtUtc > observedAtUtc;
    }

    public bool IsOwnedBy(string ownerAgentId, string ownerInstanceId) =>
        string.Equals(OwnerAgentId, ownerAgentId, StringComparison.Ordinal)
        && string.Equals(
            OwnerInstanceId,
            ownerInstanceId,
            StringComparison.Ordinal);

    public bool MatchesProofSha256(string leaseProofSha256)
    {
        var candidate = RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(LeaseProofSha256),
            Encoding.ASCII.GetBytes(candidate));
    }

    public static string RequireOwnerIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumOwnerIdentityLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character =>
                char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException(
                $"{parameterName} must be canonical non-whitespace text no longer "
                + $"than {MaximumOwnerIdentityLength} characters.",
                parameterName);
        }

        return value;
    }

    public static string RequireOwnerInstanceId(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(
                parsed.ToString("D"),
                value,
                StringComparison.Ordinal)
            || value[14] != '4'
            || value[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw new ArgumentException(
                $"{parameterName} must be a canonical lowercase UUIDv4 generated "
                + "for this Agent process boot.",
                parameterName);
        }

        return value;
    }

    public static string RequireProofSha256(string value, string parameterName)
    {
        if (value is null
            || value.Length != 64
            || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                $"{parameterName} must be a lowercase SHA-256 value.",
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
