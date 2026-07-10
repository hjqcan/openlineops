using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Infrastructure.Scripting;

namespace OpenLineOps.Processes.Tests;

public sealed class PythonScriptDefinitionValidatorTests
{
    [Fact]
    public async Task ValidateAsyncAcceptsValidPythonSource()
    {
        var validator = new PythonScriptDefinitionValidator();
        var node = ProcessNode.PythonScript(
            new ProcessNodeId("valid-script"),
            "Valid Script",
            sourceCode: "result = {'ok': True}",
            scriptTimeout: TimeSpan.FromSeconds(5));

        var report = await validator.ValidateAsync(node);

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task ValidateAsyncRejectsInvalidPythonSource()
    {
        var validator = new PythonScriptDefinitionValidator();
        var node = ProcessNode.PythonScript(
            new ProcessNodeId("invalid-script"),
            "Invalid Script",
            sourceCode: "if True\n    result = 1",
            scriptTimeout: TimeSpan.FromSeconds(5));

        var report = await validator.ValidateAsync(node);

        Assert.False(report.IsValid);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("SYNTAX", issue.Code);
        Assert.True(issue.Line > 0);
    }
}
