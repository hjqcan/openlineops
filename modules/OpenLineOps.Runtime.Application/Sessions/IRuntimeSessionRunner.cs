using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Runtime.Application.Sessions;

public interface IRuntimeSessionRunner
{
    ValueTask<Result<RuntimeSessionRunResult>> RunAsync(
        StartRuntimeSessionRequest request,
        CancellationToken cancellationToken = default);
}
