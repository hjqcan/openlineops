using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class StationControllerHandshakeFactIntegrity
{
    public const string GenesisSha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static StationControllerHandshakeFactRecord Create(
        StationControllerHandshakeFact fact,
        string documentJson,
        string previousFactSha256)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var payloadSha256 = Sha256(documentJson);
        return new StationControllerHandshakeFactRecord(
            fact,
            payloadSha256,
            previousFactSha256,
            ComputeFactSha256(fact, payloadSha256, previousFactSha256));
    }

    public static void Validate(
        StationControllerHandshakeFactRecord record,
        string documentJson,
        string expectedPreviousFactSha256)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(
                record.PreviousFactSha256,
                expectedPreviousFactSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                record.PayloadSha256,
                Sha256(documentJson),
                StringComparison.Ordinal)
            || !string.Equals(
                record.FactSha256,
                ComputeFactSha256(
                    record.Fact,
                    record.PayloadSha256,
                    record.PreviousFactSha256),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station controller handshake fact "
                + $"{record.Fact.StationId}/{record.Fact.Sequence} failed "
                + "hash-chain validation.");
        }
    }

    private static string ComputeFactSha256(
        StationControllerHandshakeFact fact,
        string payloadSha256,
        string previousFactSha256) =>
        Sha256(string.Join(
            "\n",
            "OpenLineOps.StationControllerHandshakeFact.v1",
            fact.StationId.Value,
            fact.Sequence.ToString(CultureInfo.InvariantCulture),
            fact.Kind.ToString(),
            fact.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
            payloadSha256,
            previousFactSha256));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
