using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.Runtime.Tests;

public sealed class PythonScriptWorkerExecutionResultTests
{
    [Fact]
    public void ToRuntimeResultRequiresExactCanonicalOutcomeToken()
    {
        var canonical = new PythonScriptWorkerExecutionResult("Completed", "{}", null)
            .ToRuntimeResult();
        var caseChanged = new PythonScriptWorkerExecutionResult("completed", "{}", null)
            .ToRuntimeResult();

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, canonical.Outcome);
        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, caseChanged.Outcome);
        Assert.Contains("case-sensitive", caseChanged.Reason, StringComparison.Ordinal);
    }
}
