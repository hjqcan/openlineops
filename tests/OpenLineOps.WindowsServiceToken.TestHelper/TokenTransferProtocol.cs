using System.Runtime.Versioning;
using System.Text.Json;

namespace OpenLineOps.WindowsServiceToken.TestHelper;

internal sealed record TokenTransferInvocation(bool IsRelay, string RequestPath);

internal sealed record WindowsServiceTokenTransferRequest(
    string RequestPath,
    string HelperServiceName,
    string SourceServiceName,
    string Nonce,
    uint SourceProcessId,
    long SourceProcessCreatedAtUtcTicks,
    string SourceExecutablePath,
    string SourceExecutableSha256,
    string ExpectedSourceServiceSid,
    string RunnerSid,
    string HelperBundleRoot,
    string HelperExecutablePath,
    string HelperExecutableSha256,
    string CoordinationPipeName,
    string ControlPipeName,
    string ResultPath);

internal sealed record WindowsServiceTokenTransferResult(
    string Nonce,
    uint SourceProcessId,
    uint RelayProcessId,
    long RelayProcessCreatedAtUtcTicks,
    bool HelperIdentityValidated,
    bool SourceServiceValidated,
    bool SourceProcessValidated,
    bool RelayProcessValidated,
    bool SourceTokenValidated,
    bool ControlPipeConnected,
    bool ReceiptReceived,
    string FailurePhase,
    string FailureReason,
    int Win32Error);

[SupportedOSPlatform("windows")]
internal static class TokenTransferProtocol
{
    private const int MaximumRequestBytes = 64 * 1024;
    private static readonly string[] RequestPropertyNames =
    [
        "helperServiceName",
        "sourceServiceName",
        "nonce",
        "sourceProcessId",
        "sourceProcessCreatedAtUtcTicks",
        "sourceExecutablePath",
        "sourceExecutableSha256",
        "expectedSourceServiceSid",
        "runnerSid",
        "helperBundleRoot",
        "helperExecutablePath",
        "helperExecutableSha256",
        "coordinationPipeName",
        "controlPipeName",
        "resultPath"
    ];

    public static TokenTransferInvocation ParseInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 2
            || args[0] is not ("--request" or "--relay-request"))
        {
            throw new InvalidDataException(
                "The helper accepts exactly '--request <canonical-absolute-json-path>' as its SCM role or '--relay-request <canonical-absolute-json-path>' as its source-token relay role.");
        }

        return new TokenTransferInvocation(
            string.Equals(args[0], "--relay-request", StringComparison.Ordinal),
            RequireCanonicalAbsoluteFile(args[1], "request path"));
    }

    public static WindowsServiceTokenTransferRequest ReadServiceRequest(
        string requestPath) =>
        ReadRequest(requestPath, requireResultDestination: true);

    public static WindowsServiceTokenTransferRequest ReadRelayRequest(
        string requestPath) =>
        ReadRequest(requestPath, requireResultDestination: false);

    private static WindowsServiceTokenTransferRequest ReadRequest(
        string requestPath,
        bool requireResultDestination)
    {
        var fullRequestPath = RequireCanonicalAbsoluteFile(requestPath, "request path");
        RejectReparsePoint(fullRequestPath, "request path");
        var requestInfo = new FileInfo(fullRequestPath);
        if (requestInfo.Length <= 0 || requestInfo.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException(
                $"The token-transfer request must contain 1 to {MaximumRequestBytes} bytes.");
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
            throw new InvalidDataException("The token-transfer request root must be an object.");
        }

        var expected = RequestPropertyNames.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"The token-transfer request contains unknown property '{property.Name}'.");
            }

            if (!observed.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The token-transfer request duplicates property '{property.Name}'.");
            }
        }

        if (observed.Count != RequestPropertyNames.Length)
        {
            var missing = RequestPropertyNames.Where(name => !observed.Contains(name));
            throw new InvalidDataException(
                "The token-transfer request is missing properties: "
                + string.Join(", ", missing) + ".");
        }

        var helperServiceName = RequireServiceName(
            RequiredString(root, "helperServiceName"),
            "helperServiceName");
        var sourceServiceName = RequireServiceName(
            RequiredString(root, "sourceServiceName"),
            "sourceServiceName");
        if (string.Equals(helperServiceName, sourceServiceName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The helper service and source Station service must be distinct.");
        }

        var nonce = RequireLowerHex(
            RequiredString(root, "nonce"),
            64,
            "nonce");
        var sourceProcessId = RequiredUInt32(root, "sourceProcessId");
        if (sourceProcessId == 0 || sourceProcessId == Environment.ProcessId)
        {
            throw new InvalidDataException(
                "The source process identifier must be positive and distinct from the helper process.");
        }

        var sourceCreatedAtUtcTicks = RequireUtcTicks(
            RequiredInt64(root, "sourceProcessCreatedAtUtcTicks"),
            "sourceProcessCreatedAtUtcTicks");
        var sourceExecutablePath = RequireCanonicalAbsolutePath(
            RequiredString(root, "sourceExecutablePath"),
            "sourceExecutablePath");
        var helperBundleRoot = RequireCanonicalAbsoluteDirectory(
            RequiredString(root, "helperBundleRoot"),
            "helperBundleRoot");
        var helperExecutablePath = RequireCanonicalAbsoluteFile(
            RequiredString(root, "helperExecutablePath"),
            "helperExecutablePath");
        var relativeHelperExecutable = Path.GetRelativePath(
            helperBundleRoot,
            helperExecutablePath);
        if (Path.IsPathRooted(relativeHelperExecutable)
            || relativeHelperExecutable.Equals("..", StringComparison.Ordinal)
            || relativeHelperExecutable.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFileName(helperExecutablePath),
                "OpenLineOps.WindowsServiceToken.TestHelper.exe",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Request property 'helperExecutablePath' must name the fixed helper executable beneath helperBundleRoot.");
        }
        var coordinationPipeName = RequirePipeName(
            RequiredString(root, "coordinationPipeName"),
            "coordinationPipeName");
        var controlPipeName = RequirePipeName(
            RequiredString(root, "controlPipeName"),
            "controlPipeName");
        if (string.Equals(
                coordinationPipeName,
                controlPipeName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The coordination and source-token control pipes must be distinct.");
        }

        var resultPathValue = RequiredString(root, "resultPath");
        var resultPath = requireResultDestination
            ? RequireCanonicalAbsoluteDestination(
                resultPathValue,
                "resultPath")
            : RequireCanonicalAbsolutePath(
                resultPathValue,
                "resultPath");
        if (string.Equals(resultPath, fullRequestPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The result path must not overwrite the request path.");
        }

        var sourceSha256 = RequireLowerHex(
            RequiredString(root, "sourceExecutableSha256"),
            64,
            "sourceExecutableSha256");
        var expectedSourceServiceSid = RequireCanonicalServiceSid(
            RequiredString(root, "expectedSourceServiceSid"));
        var runnerSid = RequireCanonicalSid(
            RequiredString(root, "runnerSid"),
            "runnerSid");
        var helperSha256 = RequireLowerHex(
            RequiredString(root, "helperExecutableSha256"),
            64,
            "helperExecutableSha256");

        return new WindowsServiceTokenTransferRequest(
            fullRequestPath,
            helperServiceName,
            sourceServiceName,
            nonce,
            sourceProcessId,
            sourceCreatedAtUtcTicks,
            sourceExecutablePath,
            sourceSha256,
            expectedSourceServiceSid,
            runnerSid,
            helperBundleRoot,
            helperExecutablePath,
            helperSha256,
            coordinationPipeName,
            controlPipeName,
            resultPath);
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

    private static string RequireServiceName(string value, string propertyName)
    {
        if (value.Length is < 1 or > 80
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                                      && character is not ('.' or '_' or '-')))
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' must contain 1 to 80 ASCII letters, digits, '.', '_', or '-'.");
        }

        return value;
    }

    private static string RequirePipeName(string value, string propertyName)
    {
        if (value.Length is < 1 or > 200
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                                      && character is not ('.' or '_' or '-')))
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' must contain 1 to 200 ASCII letters, digits, '.', '_', or '-'.");
        }

        return value;
    }

    private static long RequireUtcTicks(long value, string propertyName)
    {
        if (value < DateTime.UnixEpoch.Ticks || value > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' is outside the supported UTC DateTime tick range.");
        }

        return value;
    }

    private static string RequireLowerHex(string value, int length, string propertyName)
    {
        if (value.Length != length
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' must contain exactly {length} lowercase hexadecimal characters.");
        }

        return value;
    }

    private static string RequireCanonicalServiceSid(string value)
    {
        System.Security.Principal.SecurityIdentifier sid;
        try
        {
            sid = new System.Security.Principal.SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Request property 'expectedSourceServiceSid' is not a valid SID.",
                exception);
        }

        if (!string.Equals(sid.Value, value, StringComparison.Ordinal)
            || !value.StartsWith("S-1-5-80-", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Request property 'expectedSourceServiceSid' must be a canonical NT SERVICE SID.");
        }

        return value;
    }

    private static string RequireCanonicalSid(string value, string propertyName)
    {
        System.Security.Principal.SecurityIdentifier sid;
        try
        {
            sid = new System.Security.Principal.SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' is not a valid SID.",
                exception);
        }

        if (!string.Equals(sid.Value, value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Request property '{propertyName}' must be a canonical SID.");
        }

        return value;
    }

    private static string RequireCanonicalAbsoluteFile(string value, string description)
    {
        var fullPath = RequireCanonicalAbsolutePath(value, description);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The {description} does not exist.", fullPath);
        }

        return fullPath;
    }

    private static string RequireCanonicalAbsoluteDirectory(string value, string description)
    {
        var fullPath = RequireCanonicalAbsolutePath(value, description);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The {description} does not exist: '{fullPath}'.");
        }

        RejectReparsePoint(fullPath, description);
        return fullPath;
    }

    private static string RequireCanonicalAbsoluteDestination(string value, string description)
    {
        var fullPath = RequireCanonicalAbsolutePath(value, description);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new InvalidDataException($"The {description} must not already exist.");
        }

        var parent = Path.GetDirectoryName(fullPath)
                     ?? throw new InvalidDataException($"The {description} has no parent directory.");
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                $"The {description} parent directory does not exist: '{parent}'.");
        }

        RejectReparsePoint(parent, $"{description} parent directory");
        return fullPath;
    }

    private static string RequireCanonicalAbsolutePath(string value, string description)
    {
        if (!Path.IsPathFullyQualified(value)
            || Path.EndsInDirectorySeparator(value))
        {
            throw new InvalidDataException(
                $"The {description} must be a fully qualified file path without a trailing separator.");
        }

        var fullPath = Path.GetFullPath(value);
        if (!string.Equals(fullPath, value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The {description} must already be in canonical absolute form.");
        }

        var root = Path.GetPathRoot(fullPath)
                   ?? throw new InvalidDataException($"The {description} has no volume root.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
        {
            throw new InvalidDataException(
                $"The {description} must reside on a ready local fixed volume.");
        }

        return fullPath;
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {description} must not be a reparse point.");
        }
    }
}
