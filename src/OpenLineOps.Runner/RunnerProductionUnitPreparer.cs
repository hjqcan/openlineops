using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runner;

public interface IRunnerProductionUnitPreparer
{
    ValueTask<Result<RunnerProductionUnitPreparation>> PrepareAsync(
        PublishedProjectSnapshotDetails snapshot,
        RunnerRunOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record RunnerProductionUnitPreparation(
    Guid ProductionUnitId,
    string ProductModelId,
    string IdentityInputKey,
    string IdentityValue,
    string EntryStationSystemId);

public sealed class RunnerProductionUnitPreparer(
    IProjectReleaseSnapshotReader releaseReader,
    IProductionMaterialRepository materials,
    ProductionMaterialService materialService,
    IClock clock) : IRunnerProductionUnitPreparer
{
    public async ValueTask<Result<RunnerProductionUnitPreparation>> PrepareAsync(
        PublishedProjectSnapshotDetails snapshot,
        RunnerRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var releaseResult = await releaseReader
                .OpenAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            if (releaseResult.IsFailure)
            {
                return Failure(releaseResult.Error);
            }

            var release = releaseResult.Value.Artifact;
            var line = release.Metadata.ProductionLine;
            var entryOperation = line.Operations.SingleOrDefault(operation => string.Equals(
                operation.OperationId,
                line.EntryOperationId,
                StringComparison.Ordinal));
            if (entryOperation is null)
            {
                return Failure(ApplicationError.Conflict(
                    "Runner.ReleaseEntryOperationInvalid",
                    $"Immutable release {snapshot.SnapshotId} has no unique entry Operation."));
            }

            var productionUnitId = new ProductionUnitId(options.ProductionUnitId);
            var entry = await materials
                .GetProductionUnitAsync(productionUnitId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                var registered = await materialService.RegisterUnitAsync(
                        new RegisterProductionUnitCommand(
                            productionUnitId,
                            line.ProductModel.ProductModelId,
                            line.ProductModel.IdentityInputKey,
                            options.ProductionUnitIdentityValue,
                            null,
                            options.ActorId,
                            clock.UtcNow),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!registered.Succeeded
                    && !string.Equals(
                        registered.Code,
                        "Runtime.ProductionMaterialAlreadyExists",
                        StringComparison.Ordinal))
                {
                    return Failure(ApplicationError.Conflict(registered.Code, registered.Message));
                }

                entry = await materials
                    .GetProductionUnitAsync(productionUnitId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (entry is null)
            {
                return Failure(ApplicationError.Conflict(
                    "Runner.ProductionUnitRegistrationLost",
                    $"Production Unit {productionUnitId} was not durable after registration."));
            }

            var unit = entry.Aggregate;
            if (!string.Equals(
                    unit.ProductModelId,
                    line.ProductModel.ProductModelId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    unit.IdentityKey,
                    line.ProductModel.IdentityInputKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    unit.IdentityValue,
                    options.ProductionUnitIdentityValue,
                    StringComparison.Ordinal))
            {
                return Failure(ApplicationError.Conflict(
                    "Runner.ProductionUnitIdentityConflict",
                    $"Production Unit {productionUnitId} differs from the frozen release identity."));
            }

            var requestedRunId = new OpenLineOps.Runtime.Domain.Identifiers.ProductionRunId(
                options.ProductionRunId);
            var isSameRun = unit.ActiveProductionRunId == requestedRunId
                || unit.LastProductionRunId == requestedRunId;
            var entryLocation = MaterialLocation.AtStation(
                line.LineDefinitionId,
                entryOperation.StationSystemId);
            if (unit.Location is null)
            {
                var arrived = await materialService.ArriveAsync(
                        new ArriveMaterialCommand(
                            MaterialReference.ForProductionUnit(productionUnitId),
                            entryLocation,
                            options.ActorId,
                            clock.UtcNow),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!arrived.Succeeded)
                {
                    return Failure(ApplicationError.Conflict(arrived.Code, arrived.Message));
                }
            }
            else if (!Equals(unit.Location, entryLocation) && !isSameRun)
            {
                return Failure(ApplicationError.Conflict(
                    "Runner.ProductionUnitNotAtEntryStation",
                    $"Production Unit {productionUnitId} is at {unit.Location}, not entry Station "
                    + $"{line.LineDefinitionId}/{entryOperation.StationSystemId}."));
            }

            if (!isSameRun && unit.Disposition != ProductDisposition.InProcess)
            {
                return Failure(ApplicationError.Conflict(
                    "Runner.ProductionUnitDispositionRejected",
                    $"Production Unit {productionUnitId} cannot start a new run from {unit.Disposition}."));
            }

            return Result.Success(new RunnerProductionUnitPreparation(
                productionUnitId.Value,
                line.ProductModel.ProductModelId,
                line.ProductModel.IdentityInputKey,
                options.ProductionUnitIdentityValue,
                entryOperation.StationSystemId));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return Failure(ApplicationError.Conflict(
                "Projects.ProjectReleaseInvalid",
                $"Immutable release for project snapshot {snapshot.SnapshotId} is invalid: "
                + exception.Message));
        }
    }

    private static Result<RunnerProductionUnitPreparation> Failure(ApplicationError error) =>
        Result.Failure<RunnerProductionUnitPreparation>(error);
}
