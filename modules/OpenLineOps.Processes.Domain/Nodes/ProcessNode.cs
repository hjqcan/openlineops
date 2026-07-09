using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Domain.Nodes;

public sealed class ProcessNode : Entity<ProcessNodeId>
{
    private ProcessNode(
        ProcessNodeId id,
        ProcessNodeKind kind,
        string displayName,
        ProcessCapabilityId? requiredCapability,
        string? commandName,
        TimeSpan? commandTimeout,
        string? inputPayload,
        string? scriptLanguage,
        ProcessScriptEditorMode? scriptEditorMode,
        string? blocklyWorkspaceJson,
        string? scriptSourceCode,
        string? scriptSourceHash,
        string? scriptVersion,
        TimeSpan? scriptTimeout)
        : base(id)
    {
        Kind = kind;
        DisplayName = ProcessIdGuard.NotBlank(displayName, nameof(displayName));
        RequiredCapability = requiredCapability;
        CommandName = NormalizeOptional(commandName);
        CommandTimeout = commandTimeout;
        InputPayload = NormalizeOptional(inputPayload);
        ScriptLanguage = NormalizeOptional(scriptLanguage);
        ScriptEditorMode = scriptEditorMode;
        BlocklyWorkspaceJson = NormalizeOptional(blocklyWorkspaceJson);
        ScriptSourceCode = NormalizeOptionalPreservingContent(scriptSourceCode);
        ScriptSourceHash = NormalizeOptional(scriptSourceHash);
        ScriptVersion = NormalizeOptional(scriptVersion);
        ScriptTimeout = scriptTimeout;
    }

    public ProcessNodeKind Kind { get; }

    public string DisplayName { get; }

    public ProcessCapabilityId? RequiredCapability { get; }

    public string? CommandName { get; }

    public TimeSpan? CommandTimeout { get; }

    public string? InputPayload { get; }

    public string? ScriptLanguage { get; }

    public ProcessScriptEditorMode? ScriptEditorMode { get; }

    public string? BlocklyWorkspaceJson { get; }

    public string? ScriptSourceCode { get; }

    public string? ScriptSourceHash { get; }

    public string? ScriptVersion { get; }

    public TimeSpan? ScriptTimeout { get; }

    public bool RequiresCapability => Kind == ProcessNodeKind.Command;

    public bool IsPythonScript => Kind == ProcessNodeKind.PythonScript;

    public static ProcessNode Start(ProcessNodeId id, string displayName)
    {
        return new ProcessNode(
            id,
            ProcessNodeKind.Start,
            displayName,
            requiredCapability: null,
            commandName: null,
            commandTimeout: null,
            inputPayload: null,
            scriptLanguage: null,
            scriptEditorMode: null,
            blocklyWorkspaceJson: null,
            scriptSourceCode: null,
            scriptSourceHash: null,
            scriptVersion: null,
            scriptTimeout: null);
    }

    public static ProcessNode Command(
        ProcessNodeId id,
        string displayName,
        ProcessCapabilityId? requiredCapability,
        string? commandName = null,
        TimeSpan? commandTimeout = null,
        string? inputPayload = null)
    {
        return new ProcessNode(
            id,
            ProcessNodeKind.Command,
            displayName,
            requiredCapability,
            commandName,
            commandTimeout,
            inputPayload,
            scriptLanguage: null,
            scriptEditorMode: null,
            blocklyWorkspaceJson: null,
            scriptSourceCode: null,
            scriptSourceHash: null,
            scriptVersion: null,
            scriptTimeout: null);
    }

    public static ProcessNode PythonScript(
        ProcessNodeId id,
        string displayName,
        ProcessScriptEditorMode? editorMode,
        string? blocklyWorkspaceJson,
        string? sourceCode,
        string? scriptVersion = null,
        TimeSpan? scriptTimeout = null,
        string? inputPayload = null)
    {
        var normalizedSourceCode = NormalizeOptionalPreservingContent(sourceCode);

        return new ProcessNode(
            id,
            ProcessNodeKind.PythonScript,
            displayName,
            requiredCapability: null,
            commandName: null,
            commandTimeout: null,
            inputPayload: inputPayload,
            scriptLanguage: "Python",
            scriptEditorMode: editorMode,
            blocklyWorkspaceJson: blocklyWorkspaceJson,
            scriptSourceCode: normalizedSourceCode,
            scriptSourceHash: normalizedSourceCode is null
                ? null
                : ComputeSha256(normalizedSourceCode),
            scriptVersion: scriptVersion ?? "1",
            scriptTimeout: scriptTimeout);
    }

    public static ProcessNode Decision(ProcessNodeId id, string displayName)
    {
        return new ProcessNode(
            id,
            ProcessNodeKind.Decision,
            displayName,
            requiredCapability: null,
            commandName: null,
            commandTimeout: null,
            inputPayload: null,
            scriptLanguage: null,
            scriptEditorMode: null,
            blocklyWorkspaceJson: null,
            scriptSourceCode: null,
            scriptSourceHash: null,
            scriptVersion: null,
            scriptTimeout: null);
    }

    public static ProcessNode Delay(ProcessNodeId id, string displayName)
    {
        return new ProcessNode(
            id,
            ProcessNodeKind.Delay,
            displayName,
            requiredCapability: null,
            commandName: null,
            commandTimeout: null,
            inputPayload: null,
            scriptLanguage: null,
            scriptEditorMode: null,
            blocklyWorkspaceJson: null,
            scriptSourceCode: null,
            scriptSourceHash: null,
            scriptVersion: null,
            scriptTimeout: null);
    }

    public static ProcessNode End(ProcessNodeId id, string displayName)
    {
        return new ProcessNode(
            id,
            ProcessNodeKind.End,
            displayName,
            requiredCapability: null,
            commandName: null,
            commandTimeout: null,
            inputPayload: null,
            scriptLanguage: null,
            scriptEditorMode: null,
            blocklyWorkspaceJson: null,
            scriptSourceCode: null,
            scriptSourceHash: null,
            scriptVersion: null,
            scriptTimeout: null);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? NormalizeOptionalPreservingContent(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value;
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
