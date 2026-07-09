namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed record ExternalPluginPackageSignature(
    string Algorithm,
    string Signature,
    string? KeyId = null);

public static class ExternalPluginPackageSignatureAlgorithms
{
    public const string RsaSha256 = "RSA-SHA256";
}
