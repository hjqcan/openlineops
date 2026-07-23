using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;

namespace OpenLineOps.WindowsServiceToken.TestRelay;

internal sealed record SourceTokenRelayRequest(
    string RequestPath,
    string Nonce,
    uint SourceProcessId,
    long SourceProcessCreatedAtUtcTicks,
    string SourceExecutablePath,
    string SourceExecutableSha256,
    string ExpectedSourceServiceSid,
    string RelayBundleRoot,
    string RelayExecutablePath,
    string RelayExecutableSha256,
    string ControlPipeName);

[SupportedOSPlatform("windows")]
internal static class RelayProtocol
{
    private const int MaximumRequestBytes = 64 * 1024;
    private static readonly string[] RequestPropertyNames =
    [
        "nonce",
        "sourceProcessId",
        "sourceProcessCreatedAtUtcTicks",
        "sourceExecutablePath",
        "sourceExecutableSha256",
        "expectedSourceServiceSid",
        "relayBundleRoot",
        "relayExecutablePath",
        "relayExecutableSha256",
        "controlPipeName"
    ];

    public static string ParseInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 2
            || !string.Equals(args[0], "--request", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The relay accepts exactly '--request <canonical-absolute-json-path>'.");
        }

        return RequireCanonicalAbsoluteFile(args[1], "request path");
    }

    public static SourceTokenRelayRequest ReadRequest(string requestPath)
    {
        var fullRequestPath = RequireCanonicalAbsoluteFile(requestPath, "request path");
        RejectReparsePoint(fullRequestPath, "request path");
        var requestInfo = new FileInfo(fullRequestPath);
        if (requestInfo.Length <= 0 || requestInfo.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException(
                $"The relay request must contain 1 to {MaximumRequestBytes} bytes.");
        }

        var payload = File.ReadAllBytes(fullRequestPath);
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The relay request root must be an object.");
        }

        var expected = RequestPropertyNames.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"The relay request contains unknown property '{property.Name}'.");
            }
            if (!observed.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The relay request duplicates property '{property.Name}'.");
            }
        }

        if (observed.Count != RequestPropertyNames.Length)
        {
            var missing = RequestPropertyNames.Where(name => !observed.Contains(name));
            throw new InvalidDataException(
                "The relay request is missing properties: "
                + string.Join(", ", missing)
                + ".");
        }

        var nonce = RequireLowerHex(RequiredString(root, "nonce"), 64, "nonce");
        var sourceProcessId = RequiredUInt32(root, "sourceProcessId");
        if (sourceProcessId == 0 || sourceProcessId == Environment.ProcessId)
        {
            throw new InvalidDataException(
                "The source process identifier must be positive and distinct from the relay.");
        }

        var sourceCreatedAtUtcTicks = RequireUtcTicks(
            RequiredInt64(root, "sourceProcessCreatedAtUtcTicks"),
            "sourceProcessCreatedAtUtcTicks");
        var sourceExecutablePath = RequireCanonicalAbsoluteFile(
            RequiredString(root, "sourceExecutablePath"),
            "sourceExecutablePath");
        var sourceExecutableSha256 = RequireLowerHex(
            RequiredString(root, "sourceExecutableSha256"),
            64,
            "sourceExecutableSha256");
        var expectedSourceServiceSid = RequireServiceSid(
            RequiredString(root, "expectedSourceServiceSid"));
        var relayBundleRoot = RequireCanonicalAbsoluteDirectory(
            RequiredString(root, "relayBundleRoot"),
            "relayBundleRoot");
        var relayExecutablePath = RequireCanonicalAbsoluteFile(
            RequiredString(root, "relayExecutablePath"),
            "relayExecutablePath");
        var relativeRelayExecutable = Path.GetRelativePath(
            relayBundleRoot,
            relayExecutablePath);
        if (Path.IsPathRooted(relativeRelayExecutable)
            || relativeRelayExecutable.Equals("..", StringComparison.Ordinal)
            || relativeRelayExecutable.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFileName(relayExecutablePath),
                "OpenLineOps.WindowsServiceToken.TestRelay.exe",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Request property 'relayExecutablePath' must name the fixed relay executable beneath relayBundleRoot.");
        }

        var relayExecutableSha256 = RequireLowerHex(
            RequiredString(root, "relayExecutableSha256"),
            64,
            "relayExecutableSha256");
        var controlPipeName = RequirePipeName(
            RequiredString(root, "controlPipeName"),
            "controlPipeName");

        return new SourceTokenRelayRequest(
            fullRequestPath,
            nonce,
            sourceProcessId,
            sourceCreatedAtUtcTicks,
            sourceExecutablePath,
            sourceExecutableSha256,
            expectedSourceServiceSid,
            relayBundleRoot,
            relayExecutablePath,
            relayExecutableSha256,
            controlPipeName);
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } text
            || text.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' must be a non-empty control-free string.");
        }

        return text;
    }

    private static uint RequiredUInt32(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var result))
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' must be a UInt32 JSON number.");
        }

        return result;
    }

    private static long RequiredInt64(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' must be an Int64 JSON number.");
        }

        return result;
    }

    private static string RequireCanonicalAbsoluteFile(string value, string role)
    {
        var path = RequireCanonicalAbsolutePath(value, role);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The {role} is missing.", path);
        }

        return path;
    }

    private static string RequireCanonicalAbsoluteDirectory(string value, string role)
    {
        var path = RequireCanonicalAbsolutePath(value, role);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The {role} '{path}' is missing.");
        }
        RejectReparsePoint(path, role);
        return path;
    }

    private static string RequireCanonicalAbsolutePath(string value, string role)
    {
        if (!Path.IsPathFullyQualified(value)
            || !string.Equals(value, Path.GetFullPath(value), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The {role} must be a canonical absolute path.");
        }

        return value;
    }

    private static string RequireLowerHex(string value, int length, string role)
    {
        if (value.Length != length
            || value.Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new InvalidDataException(
                $"Request property '{role}' must be exactly {length} lowercase hexadecimal characters.");
        }

        return value;
    }

    private static long RequireUtcTicks(long value, string role)
    {
        if (value < DateTime.UnixEpoch.Ticks || value > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException(
                $"Request property '{role}' must be a valid UTC tick count.");
        }

        return value;
    }

    private static string RequireServiceSid(string value)
    {
        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Request property 'expectedSourceServiceSid' is not a canonical SID.",
                exception);
        }

        if (!string.Equals(sid.Value, value, StringComparison.Ordinal)
            || !value.StartsWith("S-1-5-80-", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Request property 'expectedSourceServiceSid' must be a canonical service SID.");
        }

        return value;
    }

    private static string RequirePipeName(string value, string role)
    {
        if (value.Length > 240
            || !value.StartsWith("openlineops-source-token-relay-", StringComparison.Ordinal)
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidDataException(
                $"Request property '{role}' is not a canonical relay pipe name.");
        }

        return value;
    }

    private static void RejectReparsePoint(string path, string role)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"The {role} must not be a reparse point or device.");
        }
    }
}
