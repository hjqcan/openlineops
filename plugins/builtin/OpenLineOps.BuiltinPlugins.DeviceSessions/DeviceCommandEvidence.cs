using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

internal static class DeviceCommandEvidence
{
    public static string RequestFingerprint(
        string requestKind,
        string? requestPayloadJson)
    {
        var canonical = DeviceSessionJournal.SerializePayload(
            new RequestEvidence(requestKind, requestPayloadJson));
        return Sha256(canonical);
    }

    public static string EnvelopeFingerprint(
        string requestFingerprint,
        long fencingToken,
        DateTimeOffset deadlineUtc,
        OpenLineOps.Plugin.Abstractions.PluginDeviceCommandIdempotencyClass
            idempotencyClass,
        OpenLineOps.Plugin.Abstractions.PluginDeviceCommandSafetyClass safetyClass)
    {
        var canonical = DeviceSessionJournal.SerializePayload(
            new EnvelopeEvidence(
                requestFingerprint,
                fencingToken,
                deadlineUtc,
                idempotencyClass,
                safetyClass));
        return Sha256(canonical);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record RequestEvidence(
        string RequestKind,
        string? RequestPayloadJson);

    private sealed record EnvelopeEvidence(
        string RequestFingerprint,
        long FencingToken,
        DateTimeOffset DeadlineUtc,
        OpenLineOps.Plugin.Abstractions.PluginDeviceCommandIdempotencyClass
            IdempotencyClass,
        OpenLineOps.Plugin.Abstractions.PluginDeviceCommandSafetyClass SafetyClass);
}
