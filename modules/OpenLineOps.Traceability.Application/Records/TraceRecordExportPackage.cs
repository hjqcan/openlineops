namespace OpenLineOps.Traceability.Application.Records;

public sealed record TraceRecordExportPackage(
    string PackageFormatVersion,
    DateTimeOffset ExportedAtUtc,
    TraceRecordDetails TraceRecord);
