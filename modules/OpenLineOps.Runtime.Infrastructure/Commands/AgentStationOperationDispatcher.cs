using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class AgentStationOperationDispatcher(
    IStationJobGateway gateway,
    IStationDeploymentResolver deploymentResolver) : IStationOperationDispatcher
{
    public async ValueTask<StationOperationDispatchResult> DispatchAsync(
        StationOperationDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = await deploymentResolver.ResolveAsync(
            new StationDeploymentRequest(
                request.Run.ProjectId,
                request.Run.ApplicationId,
                request.Run.ProjectSnapshotId,
                request.Operation.Definition.StationSystemId),
            cancellationToken).ConfigureAwait(false);
        using var inputs = JsonDocument.Parse("{}");
        var message = new StationJobRequested(
            Guid.NewGuid(),
            DeterministicJobId(request.IdempotencyKey),
            request.IdempotencyKey,
            route.AgentId,
            route.StationId,
            request.Operation.Definition.StationSystemId,
            request.Run.RunId.Value,
            request.Operation.OperationRunId,
            request.Operation.Attempt,
            request.Run.ProductionUnitIdentity.ModelId,
            request.Run.ProductionUnitIdentity.InputKey,
            request.Run.ProductionUnitIdentity.Value,
            request.Run.LotId,
            request.Run.CarrierId,
            request.Run.ProjectId,
            request.Run.ApplicationId,
            request.Run.ProjectSnapshotId,
            route.PackageContentSha256,
            request.Operation.Definition.OperationId,
            request.Operation.Definition.ProcessDefinitionId.Value,
            request.Operation.Definition.ProcessVersionId.Value,
            request.Operation.Definition.ConfigurationSnapshotId.Value,
            request.Operation.Definition.RecipeSnapshotId.Value,
            request.ResourceLeases.Select(lease => new StationResourceFence(
                lease.Resource.Kind.ToString(),
                lease.Resource.ResourceId,
                lease.FencingToken,
                lease.ExpiresAtUtc)).ToArray(),
            inputs.RootElement.Clone(),
            DateTimeOffset.UtcNow);
        var completion = await gateway.DispatchAsync(message, cancellationToken)
            .ConfigureAwait(false);
        if (completion.JobId != message.JobId
            || !string.Equals(
                completion.IdempotencyKey,
                message.IdempotencyKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station Agent returned a completion for a different idempotent job.");
        }

        return new StationOperationDispatchResult(
            completion.ExecutionStatus,
            completion.Judgement,
            ParseOutputs(completion.Outputs),
            0,
            0,
            0,
            completion.CompletedAtUtc,
            completion.FailureCode,
            completion.FailureReason);
    }

    private static Dictionary<string, ProductionContextValue> ParseOutputs(
        JsonElement outputs)
    {
        if (outputs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Station output context must be a JSON object.");
        }

        var values = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        foreach (var property in outputs.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("kind", out var kindElement)
                || !property.Value.TryGetProperty("value", out var valueElement)
                || property.Value.EnumerateObject().Count() != 2
                || kindElement.ValueKind != JsonValueKind.String
                || valueElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Station output '{property.Name}' must contain only string kind and value fields.");
            }

            var kindToken = kindElement.GetString();
            if (!Enum.TryParse<ProductionContextValueKind>(kindToken, false, out var kind)
                || !Enum.IsDefined(kind)
                || !string.Equals(kind.ToString(), kindToken, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Station output '{property.Name}' has invalid kind '{kindToken}'.");
            }

            if (!values.TryAdd(
                    property.Name,
                    new ProductionContextValue(kind, valueElement.GetString()!)))
            {
                throw new InvalidDataException($"Station output '{property.Name}' is duplicated.");
            }
        }

        return values;
    }

    private static Guid DeterministicJobId(string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return new Guid(hash.AsSpan(0, 16));
    }
}
