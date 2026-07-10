namespace OpenLineOps.Processes.Infrastructure.Persistence;

internal sealed record ProjectProcessBlocklyBlockVersionDocument(
    string Schema,
    int SchemaVersion,
    string ProjectId,
    string ApplicationId,
    string BlockType,
    int Version,
    string Category,
    string DisplayName,
    string BlocklyJson,
    string PythonCodeTemplate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public const string CurrentSchema = "openlineops.process-blockly-block-version";

    public const int CurrentSchemaVersion = 1;
}
