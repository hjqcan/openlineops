using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Agent.Contracts;

public sealed record StationArtifactReceipt(
    string ReceiptId,
    string AgentId,
    string StationId,
    Guid JobId,
    string ArtifactName,
    string ArtifactKind,
    string? MediaType,
    long SizeBytes,
    string Sha256,
    string StorageKey);

public static class StationArtifactReceiptIdentity
{
    private const string ReceiptDomain = "openlineops.station-artifact-receipt";

    public const int MaximumAgentIdLength = StationIdentityContract.MaximumLength;
    public const int MaximumStationIdLength = StationIdentityContract.MaximumLength;
    public const int MaximumArtifactNameLength = 255;
    public const int MaximumArtifactKindLength = 128;
    public const int MaximumMediaTypeLength = 255;
    public const int MaximumIdentityUtf8Bytes = 1024;

    public static StationArtifactReceipt Create(
        string agentId,
        string stationId,
        Guid jobId,
        string artifactName,
        string artifactKind,
        string? mediaType,
        long sizeBytes,
        string sha256)
    {
        _ = StationIdentityContract.Require(agentId, nameof(agentId));
        _ = StationIdentityContract.Require(stationId, nameof(stationId));
        RequireCanonical(
            artifactName,
            MaximumArtifactNameLength,
            nameof(artifactName));
        RequireCanonical(
            artifactKind,
            MaximumArtifactKindLength,
            nameof(artifactKind));
        RequireOptionalCanonical(
            mediaType,
            MaximumMediaTypeLength,
            nameof(mediaType));
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Station artifact Job id cannot be empty.", nameof(jobId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        RequireSha256(sha256, nameof(sha256));

        var agentScope = Sha256($"{agentId}\n{stationId}");
        var artifactScope = Sha256(artifactName);
        var storageKey = string.Join(
            '/',
            "station-artifacts",
            agentScope[..2],
            agentScope,
            "jobs",
            jobId.ToString("N"),
            "artifacts",
            artifactScope,
            "sha256",
            sha256[..2],
            sha256 + SafeExtension(artifactName));
        var receiptId = Sha256(string.Join(
            '\n',
            ReceiptDomain,
            agentId,
            stationId,
            jobId.ToString("D"),
            artifactName,
            artifactKind,
            mediaType ?? string.Empty,
            sizeBytes.ToString(CultureInfo.InvariantCulture),
            sha256,
            storageKey));

        return new StationArtifactReceipt(
            receiptId,
            agentId,
            stationId,
            jobId,
            artifactName,
            artifactKind,
            mediaType,
            sizeBytes,
            sha256,
            storageKey);
    }

    public static string ReceiptStorageKey(string receiptId)
    {
        RequireSha256(receiptId, nameof(receiptId));
        return $"station-artifact-receipts/{receiptId[..2]}/{receiptId}.json";
    }

    public static void Validate(StationArtifactReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var expected = Create(
            receipt.AgentId,
            receipt.StationId,
            receipt.JobId,
            receipt.ArtifactName,
            receipt.ArtifactKind,
            receipt.MediaType,
            receipt.SizeBytes,
            receipt.Sha256);
        if (!Equals(expected, receipt))
        {
            throw new InvalidDataException("Station artifact receipt identity is not canonical.");
        }
    }

    private static string Sha256(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string SafeExtension(string artifactName)
    {
        var extension = Path.GetExtension(artifactName).ToLowerInvariant();
        return extension.Length is > 1 and <= 16
            && extension[0] == '.'
            && extension[1..].All(char.IsAsciiLetterOrDigit)
                ? extension
                : string.Empty;
    }

    private static void RequireSha256(string value, string parameterName)
    {
        if (value is not { Length: 64 }
            || value.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Value must be one lowercase SHA-256 digest.",
                parameterName);
        }
    }

    private static void RequireCanonical(
        string value,
        int maximumLength,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || Encoding.UTF8.GetByteCount(value) > MaximumIdentityUtf8Bytes
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Value must be canonical non-empty text no longer than {maximumLength} characters or {MaximumIdentityUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }
    }

    private static void RequireOptionalCanonical(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (value is not null)
        {
            RequireCanonical(value, maximumLength, parameterName);
        }
    }
}

public static class StationArtifactUploadProtocol
{
    public const int MaximumEncodedArtifactNameHeaderLength = 3072;
    public const int MaximumEncodedArtifactKindHeaderLength = 1024;
    public const int MaximumEncodedMediaTypeHeaderLength = 3072;
    public const string AgentIdHeader = "X-OpenLineOps-Agent-Id";
    public const string StationIdHeader = "X-OpenLineOps-Station-Id";
    public const string JobIdHeader = "X-OpenLineOps-Job-Id";
    public const string ArtifactNameHeader = "X-OpenLineOps-Artifact-Name";
    public const string ArtifactKindHeader = "X-OpenLineOps-Artifact-Kind";
    public const string ArtifactSizeHeader = "X-OpenLineOps-Artifact-Size";
    public const string ArtifactSha256Header = "X-OpenLineOps-Artifact-Sha256";
    public const string ArtifactMediaTypeHeader = "X-OpenLineOps-Artifact-Media-Type";

    public static string EncodeArtifactName(string artifactName)
    {
        _ = StationArtifactReceiptIdentity.Create(
            "validation-agent",
            "validation-station",
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            artifactName,
            "validation",
            null,
            0,
            new string('0', 64));
        return Uri.EscapeDataString(artifactName);
    }

    public static string DecodeArtifactName(string encodedArtifactName)
    {
        try
        {
            if (encodedArtifactName.Length > MaximumEncodedArtifactNameHeaderLength)
            {
                throw new ArgumentException("Encoded Station artifact name is too long.");
            }

            var decoded = Uri.UnescapeDataString(encodedArtifactName);
            if (!string.Equals(
                    EncodeArtifactName(decoded),
                    encodedArtifactName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Encoded Station artifact name is not canonical.");
            }
            _ = StationArtifactReceiptIdentity.Create(
                "validation-agent",
                "validation-station",
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                decoded,
                "validation",
                null,
                0,
                new string('0', 64));
            return decoded;
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            throw new InvalidDataException("Station artifact name header is invalid.", exception);
        }
    }

    public static string EncodeArtifactKind(string artifactKind)
    {
        _ = StationArtifactReceiptIdentity.Create(
            "validation-agent",
            "validation-station",
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "artifact.bin",
            artifactKind,
            null,
            0,
            new string('0', 64));
        return Uri.EscapeDataString(artifactKind);
    }

    public static string DecodeArtifactKind(string encodedArtifactKind)
    {
        try
        {
            if (encodedArtifactKind.Length > MaximumEncodedArtifactKindHeaderLength)
            {
                throw new ArgumentException("Encoded Station artifact kind is too long.");
            }

            var decoded = Uri.UnescapeDataString(encodedArtifactKind);
            if (!string.Equals(
                    EncodeArtifactKind(decoded),
                    encodedArtifactKind,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Encoded Station artifact kind is not canonical.");
            }

            return decoded;
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            throw new InvalidDataException("Station artifact kind header is invalid.", exception);
        }
    }

    public static string EncodeMediaType(string mediaType)
    {
        _ = StationArtifactReceiptIdentity.Create(
            "validation-agent",
            "validation-station",
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "artifact.bin",
            "validation",
            mediaType,
            0,
            new string('0', 64));
        return Uri.EscapeDataString(mediaType);
    }

    public static string DecodeMediaType(string encodedMediaType)
    {
        try
        {
            if (encodedMediaType.Length > MaximumEncodedMediaTypeHeaderLength)
            {
                throw new ArgumentException("Encoded Station artifact media type is too long.");
            }

            var decoded = Uri.UnescapeDataString(encodedMediaType);
            if (!string.Equals(
                    EncodeMediaType(decoded),
                    encodedMediaType,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Encoded Station artifact media type is not canonical.");
            }
            _ = StationArtifactReceiptIdentity.Create(
                "validation-agent",
                "validation-station",
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "artifact.bin",
                "validation",
                decoded,
                0,
                new string('0', 64));
            return decoded;
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            throw new InvalidDataException("Station artifact media type header is invalid.", exception);
        }
    }
}
