namespace OpenLineOps.Traceability.Api.Models;

public sealed record TraceRecordExportPackageResponse(
    string PackageFormat,
    DateTimeOffset ExportedAtUtc,
    TraceRecordResponse TraceRecord);
