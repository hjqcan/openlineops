using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Domain.Definitions;

namespace OpenLineOps.Processes.Application.FlowIr;

public interface IProcessFlowIrCompiler
{
    Result<FlowIrCompilation> Compile(ProcessDefinition definition);
}
