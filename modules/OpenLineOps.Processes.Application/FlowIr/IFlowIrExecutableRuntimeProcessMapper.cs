using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Application.Processes;

namespace OpenLineOps.Processes.Application.FlowIr;

public interface IFlowIrExecutableRuntimeProcessMapper
{
    Result<ExecutableRuntimeProcess> Map(FlowIrDocument document);
}
