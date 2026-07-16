using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed record HttpStationArtifactTransferOptions(
    string LocalArtifactDirectory,
    Uri CoordinatorBaseUri,
    string BearerToken,
    string AgentId,
    string StationId,
    TimeSpan RequestTimeout)
{
    public override string ToString() =>
        $"HttpStationArtifactTransferOptions {{ AgentId = {AgentId}, StationId = {StationId}, "
        + $"CoordinatorEndpoint = {CoordinatorBaseUri.Scheme}://{CoordinatorBaseUri.Authority}, "
        + "BearerToken = [REDACTED] }";
}

public sealed class HttpStationArtifactTransfer : IStationArtifactTransfer
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumReceiptResponseBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly HttpClient _httpClient;
    private readonly string _localRoot;
    private readonly Uri _uploadUri;
    private readonly string _bearerToken;
    private readonly string _agentId;
    private readonly string _stationId;
    private readonly TimeSpan _requestTimeout;

    public HttpStationArtifactTransfer(
        HttpStationArtifactTransferOptions options,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _localRoot = DirectoryRoot(
            options.LocalArtifactDirectory,
            nameof(options.LocalArtifactDirectory));
        ValidateCoordinatorUri(options.CoordinatorBaseUri);
        _uploadUri = new Uri(
            EnsureTrailingSlash(options.CoordinatorBaseUri),
            "api/traceability/artifacts");
        _bearerToken = Required(options.BearerToken, nameof(options.BearerToken));
        ValidateBearerToken(_bearerToken);
        _agentId = Required(options.AgentId, nameof(options.AgentId));
        _stationId = Required(options.StationId, nameof(options.StationId));
        _ = StationArtifactReceiptIdentity.Create(
            _agentId,
            _stationId,
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "validation.bin",
            "validation",
            null,
            0,
            new string('0', 64));
        _requestTimeout = options.RequestTimeout > TimeSpan.Zero
            && options.RequestTimeout != Timeout.InfiniteTimeSpan
                ? options.RequestTimeout
                : throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Station artifact upload timeout must be greater than zero.");
    }

    public async ValueTask<StationJobArtifact> PublishAsync(
        StationJobId jobId,
        PendingStationJobArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        Validate(jobId, artifact);
        var sourcePath = ResolveLocal(jobId, artifact.LocalArtifactKey);
        await VerifyFileAsync(sourcePath, artifact.SizeBytes, artifact.Sha256, cancellationToken)
            .ConfigureAwait(false);

        var expectedReceipt = StationArtifactReceiptIdentity.Create(
            _agentId,
            _stationId,
            jobId.Value,
            artifact.Name,
            artifact.Kind,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256);
        await using var source = OpenRead(sourcePath);
        using var content = new StreamContent(source, BufferSize);
        content.Headers.ContentLength = artifact.SizeBytes;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, _uploadUri)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        AddHeader(request, StationArtifactUploadProtocol.AgentIdHeader, _agentId);
        AddHeader(request, StationArtifactUploadProtocol.StationIdHeader, _stationId);
        AddHeader(
            request,
            StationArtifactUploadProtocol.JobIdHeader,
            jobId.Value.ToString("D"));
        AddHeader(
            request,
            StationArtifactUploadProtocol.ArtifactNameHeader,
            StationArtifactUploadProtocol.EncodeArtifactName(artifact.Name));
        AddHeader(
            request,
            StationArtifactUploadProtocol.ArtifactKindHeader,
            StationArtifactUploadProtocol.EncodeArtifactKind(artifact.Kind));
        AddHeader(
            request,
            StationArtifactUploadProtocol.ArtifactSizeHeader,
            artifact.SizeBytes.ToString(CultureInfo.InvariantCulture));
        AddHeader(
            request,
            StationArtifactUploadProtocol.ArtifactSha256Header,
            artifact.Sha256);
        if (artifact.MediaType is not null)
        {
            AddHeader(
                request,
                StationArtifactUploadProtocol.ArtifactMediaTypeHeader,
                StationArtifactUploadProtocol.EncodeMediaType(artifact.MediaType));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Coordinator rejected Station artifact upload with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var responseMediaType = response.Content.Headers.ContentType?.MediaType;
        if (responseMediaType is null
            || (!string.Equals(
                    responseMediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase)
                && !responseMediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Coordinator returned a Station artifact receipt with a non-JSON content type.");
        }

        var receiptDocument = await ReadBoundedAsync(
                response.Content,
                MaximumReceiptResponseBytes,
                timeout.Token)
            .ConfigureAwait(false);
        StationArtifactReceipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<StationArtifactReceipt>(
                receiptDocument,
                JsonOptions)
                ?? throw new InvalidDataException(
                    "Coordinator returned an empty Station artifact receipt.");
            StationArtifactReceiptIdentity.Validate(receipt);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                "Coordinator returned an invalid Station artifact receipt.",
                exception);
        }

        if (!Equals(receipt, expectedReceipt))
        {
            throw new InvalidDataException(
                "Coordinator returned a Station artifact receipt for different evidence.");
        }

        return new StationJobArtifact(
            artifact.Name,
            artifact.Kind,
            receipt.StorageKey,
            receipt.ReceiptId,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256);
    }

    public async ValueTask ReleaseLocalAsync(
        StationJobId jobId,
        PendingStationJobArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        Validate(jobId, artifact);
        var sourcePath = ResolveLocal(jobId, artifact.LocalArtifactKey);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        await VerifyFileAsync(sourcePath, artifact.SizeBytes, artifact.Sha256, cancellationToken)
            .ConfigureAwait(false);
        File.SetAttributes(sourcePath, File.GetAttributes(sourcePath) & ~FileAttributes.ReadOnly);
        File.Delete(sourcePath);
        RemoveEmptyLocalDirectories(Path.GetDirectoryName(sourcePath)!);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("Station artifact receipt response is too large.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Station artifact receipt response is too large.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new InvalidDataException($"Station artifact header '{name}' is invalid.");
        }
    }

    private string ResolveLocal(StationJobId jobId, string localArtifactKey)
    {
        var segments = CanonicalSegments(localArtifactKey, nameof(localArtifactKey));
        if (!string.Equals(segments[0], jobId.Value.ToString("N"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Local artifact '{localArtifactKey}' does not belong to Station Job {jobId}.");
        }

        RejectReparsePointsFromVolumeRoot(_localRoot);
        var path = ResolveInside(_localRoot, localArtifactKey);
        RejectReparsePoints(_localRoot, path);
        return path;
    }

    private static async ValueTask VerifyFileAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || info.Attributes.HasFlag(FileAttributes.Directory)
            || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new FileNotFoundException(
                "Station artifact is missing or is not a regular file.",
                path);
        }

        if (info.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"Station artifact '{path}' has length {info.Length}, expected {expectedSize}.");
        }

        await using var stream = OpenRead(path);
        var actualSha256 = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station artifact '{path}' does not match its declared SHA-256.");
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void Validate(StationJobId jobId, PendingStationJobArtifact artifact)
    {
        if (jobId.Value == Guid.Empty)
        {
            throw new ArgumentException("Station Job id cannot be empty.", nameof(jobId));
        }

        ArgumentNullException.ThrowIfNull(artifact);
        _ = Required(artifact.Kind, nameof(artifact.Kind));
        _ = CanonicalSegments(artifact.LocalArtifactKey, nameof(artifact.LocalArtifactKey));
        _ = StationArtifactReceiptIdentity.Create(
            "validation-agent",
            "validation-station",
            jobId.Value,
            artifact.Name,
            artifact.Kind,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256);
    }

    private static string DirectoryRoot(string value, string parameterName)
    {
        var root = Path.GetFullPath(Required(value, parameterName))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(root)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Station artifact directory cannot be a filesystem root.",
                parameterName);
        }

        Directory.CreateDirectory(root);
        RejectReparsePointsFromVolumeRoot(root);
        RejectReparsePoints(root, root);
        return root;
    }

    private static string ResolveInside(string root, string relativePath)
    {
        _ = CanonicalSegments(relativePath, nameof(relativePath));
        var rootedPrefix = root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new InvalidDataException(
                $"Station artifact path '{relativePath}' escapes its configured root.");
    }

    private static string[] CanonicalSegments(string value, string parameterName)
    {
        _ = Required(value, parameterName);
        if (value.Contains('\\') || value.StartsWith('/') || value.EndsWith('/'))
        {
            throw new ArgumentException(
                $"{parameterName} must be a canonical relative path.",
                parameterName);
        }

        var segments = value.Split('/');
        return segments.Any(segment => segment.Length == 0 || segment is "." or "..")
            ? throw new ArgumentException(
                $"{parameterName} contains an unsafe path segment.",
                parameterName)
            : segments;
    }

    private static void RejectReparsePoints(string root, string path)
    {
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var current = Path.GetFullPath(path);
        while (current.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Station artifact path '{current}' contains a reparse point.");
            }

            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException("Station artifact path has no parent directory.");
        }

        throw new InvalidDataException($"Station artifact path '{path}' escapes '{root}'.");
    }

    private static void RejectReparsePointsFromVolumeRoot(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            var attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"Station artifact path '{current}' is not a regular directory.");
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null)
            {
                return;
            }

            current = parent;
        }
    }

    private void RemoveEmptyLocalDirectories(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!string.Equals(current, _localRoot, StringComparison.OrdinalIgnoreCase)
               && current.StartsWith(
                   _localRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
               && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/')
        ? uri
        : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute);

    private static void ValidateCoordinatorUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Coordinator base URI must be HTTPS, except loopback HTTP used for local execution.",
                nameof(uri));
        }
    }

    private static void ValidateBearerToken(string token)
    {
        if (token.Length is < 43 or > 86
            || token.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Station Agent bearer token must be a 32-64 byte base64url secret.",
                nameof(token));
        }

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length is < 32 or > 64)
            {
                throw new FormatException();
            }

            var canonical = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            if (canonical.Length != token.Length
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(canonical),
                    System.Text.Encoding.ASCII.GetBytes(token)))
            {
                throw new FormatException();
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "Station Agent bearer token must be a 32-64 byte base64url secret.",
                nameof(token),
                exception);
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
        || value.Any(char.IsControl)
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}
