using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.Messages;
using OpenLineOps.Integration.Domain.Serialization;

namespace OpenLineOps.Integration.Application.Serialization;

public static class IntegrationMessageCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(WorkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IntegrationCanonicalJson.Normalize(JsonSerializer.Serialize(
            new WorkRequestDocument(
                request.Id.Value,
                request.WorkOrderId.Value,
                request.Kind.ToString(),
                request.Status.ToString(),
                request.SourceSystem,
                request.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                request.PayloadJson),
            JsonOptions));
    }

    public static string Encode(WorkResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return IntegrationCanonicalJson.Normalize(JsonSerializer.Serialize(
            new WorkResponseDocument(
                response.Id.Value,
                response.RequestId.Value,
                response.WorkOrderId.Value,
                response.Status.ToString(),
                response.TargetSystem,
                response.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                response.PayloadJson),
            JsonOptions));
    }

    public static WorkResponse DecodeResponse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<WorkResponseDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Persisted work response is empty.");
        return new WorkResponse(
            new WorkResponseId(document.Id),
            new WorkRequestId(document.RequestId),
            new WorkOrderId(document.WorkOrderId),
            ParseEnum<WorkResponseStatus>(document.Status),
            document.TargetSystem,
            ParseUtc(document.OccurredAtUtc),
            document.PayloadJson);
    }

    public static string ComputeSha256(string canonicalJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
               && Enum.IsDefined(parsed)
               && string.Equals(value, parsed.ToString(), StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException(
                $"Persisted enum token '{value}' is not a canonical {typeof(TEnum).Name}.");
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.TryParseExact(
                   value,
                   "O",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed)
               && parsed.Offset == TimeSpan.Zero
            ? parsed
            : throw new InvalidDataException(
                $"Persisted timestamp '{value}' is not canonical UTC.");
    }

    private sealed record WorkRequestDocument(
        string Id,
        string WorkOrderId,
        string Kind,
        string Status,
        string SourceSystem,
        string OccurredAtUtc,
        string PayloadJson);

    private sealed record WorkResponseDocument(
        string Id,
        string RequestId,
        string WorkOrderId,
        string Status,
        string TargetSystem,
        string OccurredAtUtc,
        string PayloadJson);
}
