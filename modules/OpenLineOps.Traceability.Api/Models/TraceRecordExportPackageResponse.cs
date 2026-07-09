namespace OpenLineOps.Traceability.Api.Models;

public sealed record TraceRecordExportPackageResponse(
    string PackageFormatVersion,
    DateTimeOffset ExportedAtUtc,
    TraceRecordResponse TraceRecord);
