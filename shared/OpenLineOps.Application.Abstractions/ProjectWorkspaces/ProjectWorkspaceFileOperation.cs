using System.Runtime.ExceptionServices;

namespace OpenLineOps.Application.Abstractions.ProjectWorkspaces;

public static class ProjectWorkspaceFileOperation
{
    public static void ThrowFailures(
        string aggregateMessage,
        Exception? operationFailure,
        Exception? cleanupFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateMessage);
        if (operationFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(
                aggregateMessage,
                operationFailure,
                cleanupFailure);
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }
}
