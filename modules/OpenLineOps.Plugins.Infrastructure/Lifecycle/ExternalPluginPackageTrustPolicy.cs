using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class ExternalPluginPackageTrustPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ExternalPluginSandboxOptions _options;

    public ExternalPluginPackageTrustPolicy(ExternalPluginSandboxOptions? options = null)
    {
        _options = options ?? new ExternalPluginSandboxOptions();
    }

    public ExternalPluginPackageTrustResult Evaluate(
        PluginPackageDescriptor package,
        string entryAssemblyPath)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!_options.RequireTrustedPackage && !_options.RequireSignedPackage)
        {
            return ExternalPluginPackageTrustResult.Trusted();
        }

        var actualHash = ComputeSha256(entryAssemblyPath);
        if (_options.RequireSignedPackage)
        {
            var signatureResult = VerifySignature(package, entryAssemblyPath, actualHash);
            return signatureResult.Succeeded
                ? ExternalPluginPackageTrustResult.Trusted(actualHash)
                : signatureResult;
        }

        var expectedHash = FindExpectedHash(package.Manifest.Id);
        if (expectedHash is null)
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' is not trusted because no entry assembly SHA-256 hash is configured.");
        }

        if (!string.Equals(actualHash, NormalizeHash(expectedHash), StringComparison.OrdinalIgnoreCase))
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' entry assembly hash does not match the configured trusted hash.");
        }

        return ExternalPluginPackageTrustResult.Trusted(actualHash);
    }

    private ExternalPluginPackageTrustResult VerifySignature(
        PluginPackageDescriptor package,
        string entryAssemblyPath,
        string entryAssemblySha256)
    {
        var signaturePath = ResolveSignaturePath(package);
        if (signaturePath is null)
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' signature file is outside package directory '{package.PackagePath}'.");
        }

        if (!File.Exists(signaturePath))
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' is not trusted because signature file '{signaturePath}' was not found.");
        }

        var trustedPublicKey = FindTrustedPublicKey(package.Manifest.Id);
        if (trustedPublicKey is null)
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' is not trusted because no trusted package signing public key is configured.");
        }

        ExternalPluginPackageSignature? signature;
        try
        {
            var signatureJson = File.ReadAllText(signaturePath);
            signature = JsonSerializer.Deserialize<ExternalPluginPackageSignature>(signatureJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' signature file is invalid JSON: {exception.Message}");
        }

        if (signature is null)
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' signature file is empty.");
        }

        if (string.IsNullOrWhiteSpace(signature.Algorithm))
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' signature algorithm is missing.");
        }

        if (string.IsNullOrWhiteSpace(signature.Signature))
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' signature value is missing.");
        }

        if (!string.Equals(
                signature.Algorithm,
                ExternalPluginPackageSignatureAlgorithms.RsaSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' signature algorithm '{signature.Algorithm}' is not supported.");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException)
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' signature value is not valid Base64.");
        }

        var manifestSha256 = ComputeSha256(package.ManifestPath);
        var payload = ExternalPluginPackageSignaturePayload.Create(
            package,
            entryAssemblyPath,
            entryAssemblySha256,
            manifestSha256);

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(trustedPublicKey);

            var verified = rsa.VerifyData(
                Encoding.UTF8.GetBytes(payload),
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            return verified
                ? ExternalPluginPackageTrustResult.Trusted(entryAssemblySha256)
                : ExternalPluginPackageTrustResult.Rejected(
                    $"Plugin package '{package.Manifest.Id}' signature verification failed.");
        }
        catch (CryptographicException exception)
        {
            return ExternalPluginPackageTrustResult.Rejected(
                $"Plugin package '{package.Manifest.Id}' trusted signing public key is invalid: {exception.Message}");
        }
    }

    private string? FindExpectedHash(string pluginId)
    {
        if (_options.TrustedEntryAssemblySha256.TryGetValue(pluginId, out var exact))
        {
            return exact;
        }

        return _options.TrustedEntryAssemblySha256.TryGetValue("*", out var wildcard)
            ? wildcard
            : null;
    }

    private string? FindTrustedPublicKey(string pluginId)
    {
        if (_options.TrustedPackageSigningPublicKeys.TryGetValue(pluginId, out var exact))
        {
            return exact;
        }

        return _options.TrustedPackageSigningPublicKeys.TryGetValue("*", out var wildcard)
            ? wildcard
            : null;
    }

    private string? ResolveSignaturePath(PluginPackageDescriptor package)
    {
        var packagePath = Path.GetFullPath(package.PackagePath);
        var fileName = string.IsNullOrWhiteSpace(_options.PackageSignatureFileName)
            ? "openlineops-plugin.signature.json"
            : _options.PackageSignatureFileName.Trim();
        var signaturePath = Path.GetFullPath(Path.Combine(packagePath, fileName));

        return IsPathInsideDirectory(signaturePath, packagePath)
            ? signaturePath
            : null;
    }

    private static bool IsPathInsideDirectory(string candidatePath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, candidatePath);

        return !relativePath.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);

        return Convert.ToHexString(hash);
    }

    private static string NormalizeHash(string hash)
    {
        return hash
            .Replace(":", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Trim();
    }
}

public sealed record ExternalPluginPackageTrustResult(
    bool Succeeded,
    string? EntryAssemblySha256,
    string? FailureReason)
{
    public static ExternalPluginPackageTrustResult Trusted(string? entryAssemblySha256 = null)
    {
        return new ExternalPluginPackageTrustResult(true, entryAssemblySha256, null);
    }

    public static ExternalPluginPackageTrustResult Rejected(string failureReason)
    {
        return new ExternalPluginPackageTrustResult(false, null, failureReason);
    }
}
