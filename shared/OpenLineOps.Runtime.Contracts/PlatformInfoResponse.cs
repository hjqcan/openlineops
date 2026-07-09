namespace OpenLineOps.Runtime.Contracts;

public sealed record PlatformInfoResponse(
    string ProductName,
    string ServiceName,
    string Version,
    string Runtime,
    string Environment);
