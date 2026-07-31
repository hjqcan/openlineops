using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class StationLifecycleFactIntegrity
{
    public const string GenesisSha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static StationLifecycleFactMetadata Create(
        StationId stationId,
        long sequence,
        long lifecycleRevision,
        string documentJson,
        StationLifecycle station,
        string previousFactSha256)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(station);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(lifecycleRevision);
        var kind = lifecycleRevision == 0
            ? "Created"
            : "SnapshotCommitted";
        var payloadSha256 = Sha256(documentJson);
        var occurredAtUtc = station.LastChangedAtUtc;
        return new StationLifecycleFactMetadata(
            stationId,
            sequence,
            lifecycleRevision,
            kind,
            occurredAtUtc,
            payloadSha256,
            previousFactSha256,
            ComputeFactSha256(
                stationId,
                sequence,
                lifecycleRevision,
                kind,
                occurredAtUtc,
                payloadSha256,
                previousFactSha256));
    }

    public static void Validate(
        StationLifecycleFactMetadata fact,
        string documentJson,
        string expectedPreviousFactSha256,
        long expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.Sequence != expectedSequence
            || !string.Equals(
                fact.PreviousFactSha256,
                expectedPreviousFactSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                fact.PayloadSha256,
                Sha256(documentJson),
                StringComparison.Ordinal)
            || !string.Equals(
                fact.FactSha256,
                ComputeFactSha256(
                    fact.StationId,
                    fact.Sequence,
                    fact.LifecycleRevision,
                    fact.Kind,
                    fact.OccurredAtUtc,
                    fact.PayloadSha256,
                    fact.PreviousFactSha256),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station lifecycle fact {fact.StationId}/{fact.Sequence} "
                + "failed hash-chain validation.");
        }
    }

    private static string ComputeFactSha256(
        StationId stationId,
        long sequence,
        long lifecycleRevision,
        string kind,
        DateTimeOffset occurredAtUtc,
        string payloadSha256,
        string previousFactSha256) =>
        Sha256(string.Join(
            "\n",
            "OpenLineOps.StationLifecycleFact.v1",
            stationId.Value,
            sequence.ToString(CultureInfo.InvariantCulture),
            lifecycleRevision.ToString(CultureInfo.InvariantCulture),
            kind,
            occurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
            payloadSha256,
            previousFactSha256));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
