namespace OpenLineOps.Traceability.Application.Records;

public sealed record TraceRecordExportPackage(
    string PackageFormat,
    DateTimeOffset ExportedAtUtc,
    TraceRecordDetails TraceRecord);
