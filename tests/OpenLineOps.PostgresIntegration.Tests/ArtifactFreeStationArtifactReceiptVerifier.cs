using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Execution;

namespace OpenLineOps.PostgresIntegration.Tests;

internal sealed class ArtifactFreeStationArtifactReceiptVerifier
    : IStationArtifactReceiptVerifier
{
    public static ArtifactFreeStationArtifactReceiptVerifier Instance { get; } = new();

    public ValueTask VerifyAsync(
        StationJobCompleted completion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (completion.Artifacts.Count != 0)
        {
            throw new StationArtifactReceiptRejectedException(
                "The artifact-free integration fixture received an artifact receipt.");
        }

        return ValueTask.CompletedTask;
    }
}
