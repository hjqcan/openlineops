using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.Api;

internal sealed class DesktopProcessHandshake
{
    internal const string FileEnvironmentVariable = "OPENLINEOPS_DESKTOP_HANDSHAKE_FILE";
    internal const string NonceEnvironmentVariable = "OPENLINEOPS_DESKTOP_HANDSHAKE_NONCE";
    internal const string ChallengeHeader = "X-OpenLineOps-Desktop-Challenge";
    internal const string ProofHeader = "X-OpenLineOps-Desktop-Proof";
    internal const string Endpoint = "/health/desktop-process";

    private readonly string _filePath;
    private readonly string _nonce;

    private DesktopProcessHandshake(string filePath, string nonce)
    {
        _filePath = filePath;
        _nonce = nonce;
    }

    internal static DesktopProcessHandshake? FromEnvironment()
    {
        var filePath = Environment.GetEnvironmentVariable(FileEnvironmentVariable);
        var nonce = Environment.GetEnvironmentVariable(NonceEnvironmentVariable);
        if (filePath is null && nonce is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(filePath)
            || !Path.IsPathFullyQualified(filePath)
            || string.IsNullOrWhiteSpace(nonce)
            || !IsCanonicalBase64Url(nonce, 43))
        {
            throw new InvalidOperationException(
                "Desktop process handshake requires one absolute file and one 256-bit nonce.");
        }

        return new DesktopProcessHandshake(Path.GetFullPath(filePath), nonce);
    }

    internal IResult Prove(HttpRequest request, HttpResponse response)
    {
        var challenge = request.Headers[ChallengeHeader].ToString();
        if (!HttpMethods.IsGet(request.Method)
            || !request.Headers.TryGetValue(ChallengeHeader, out var values)
            || values.Count != 1
            || !IsCanonicalBase64Url(challenge, 43))
        {
            return Results.BadRequest();
        }

        var proof = ComputeProof(_nonce, challenge);
        response.Headers[ProofHeader] = proof;
        response.Headers.CacheControl = "no-store";
        return Results.NoContent();
    }

    internal async Task PublishBoundEndpointAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Kestrel did not publish its bound addresses.");
        if (addresses.Count != 1
            || !Uri.TryCreate(addresses.Single(), UriKind.Absolute, out var address)
            || !IsLoopbackHttpOrigin(address))
        {
            throw new InvalidOperationException(
                "Desktop process handshake requires exactly one bound loopback HTTP origin.");
        }

        ValidateAuthorizedTargetPath();
        await using (var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            ValidateAuthorizedTargetHandle(stream.SafeFileHandle);
            stream.SetLength(0);
            await JsonSerializer.SerializeAsync(
                stream,
                new DesktopProcessHandshakeDocument(
                    Environment.ProcessId,
                    address.GetLeftPart(UriPartial.Authority)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    internal static string ComputeProof(string nonce, string challenge)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(nonce));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(challenge)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private void ValidateAuthorizedTargetPath()
    {
        var parent = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Desktop process handshake file has no parent.");
        var parentAttributes = File.GetAttributes(parent);
        var targetAttributes = File.GetAttributes(_filePath);
        if ((parentAttributes & FileAttributes.Directory) == 0
            || (parentAttributes & FileAttributes.ReparsePoint) != 0
            || (targetAttributes & FileAttributes.Directory) != 0
            || (targetAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Desktop process handshake target must be one authorized regular file.");
        }
    }

    private static bool IsLoopbackHttpOrigin(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.IsLoopback
        && uri.Port > 0
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsCanonicalBase64Url(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static void ValidateAuthorizedTargetHandle(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Desktop process handshake file identity is supported only on Windows.");
        }
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                "Could not verify the Desktop process handshake file identity.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0
            || information.NumberOfLinks != 1)
        {
            throw new InvalidOperationException(
                "Desktop process handshake target must be one authorized regular file handle.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ByHandleFileInformation
    {
        public readonly uint FileAttributes;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public readonly uint VolumeSerialNumber;
        public readonly uint FileSizeHigh;
        public readonly uint FileSizeLow;
        public readonly uint NumberOfLinks;
        public readonly uint FileIndexHigh;
        public readonly uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    private sealed record DesktopProcessHandshakeDocument(int ProcessId, string Origin);
}
