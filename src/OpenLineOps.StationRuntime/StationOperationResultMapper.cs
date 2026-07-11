using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.StationRuntime.Contracts;
using DomainRuntimeCommandStatus = OpenLineOps.Runtime.Domain.Commands.RuntimeCommandStatus;

namespace OpenLineOps.StationRuntime;

internal static class StationOperationResultMapper
{
    public static async ValueTask<StationOperationResultDocument> MapAsync(
        StationOperationRequestDocument request,
        RuntimeSession session,
        string workDirectory,
        DateTimeOffset fallbackStartedAtUtc,
        CancellationToken cancellationToken)
    {
        var (executionStatus, judgement) = ResolveAxes(session);
        var failure = executionStatus == ExecutionStatus.Completed
            ? (Code: (string?)null, Reason: (string?)null)
            : ResolveFailure(session, executionStatus);
        var steps = session.Steps
            .OrderBy(step => step.StartedAtUtc)
            .ThenBy(step => step.Id.Value)
            .Select(step => new StationOperationStepEvidence(
                step.Id.Value,
                step.NodeId.Value,
                step.ActionId.Value,
                step.TargetKind,
                step.TargetId,
                step.DisplayName,
                step.Status.ToString(),
                step.StartedAtUtc,
                step.CompletedAtUtc,
                CanonicalOptionalReason(step.FailureReason)))
            .ToArray();
        var stepById = session.Steps.ToDictionary(step => step.Id);
        var commands = session.Commands
            .OrderBy(command => command.CreatedAtUtc)
            .ThenBy(command => command.Id.Value)
            .Select(command =>
            {
                var step = stepById[command.StepId];
                return new StationOperationCommandEvidence(
                    command.Id.Value,
                    command.StepId.Value,
                    step.NodeId.Value,
                    command.ActionId.Value,
                    command.TargetKind,
                    command.TargetId,
                    command.TargetCapability.Value,
                    command.CommandName,
                    command.Status.ToString(),
                    command.CreatedAtUtc,
                    command.DeadlineAtUtc,
                    command.AcceptedAtUtc,
                    command.StartedAtUtc,
                    command.CompletedAtUtc,
                    command.ResultPayload,
                    CanonicalOptionalReason(command.FailureReason),
                    command.ResultJudgement);
            })
            .ToArray();
        var incidents = session.Incidents
            .OrderBy(incident => incident.OccurredAtUtc)
            .ThenBy(incident => incident.Id.Value)
            .Select(incident => new StationOperationIncidentEvidence(
                incident.Id.Value,
                incident.Severity.ToString(),
                incident.Code,
                BoundedReason(incident.Message),
                incident.OccurredAtUtc))
            .ToArray();
        var artifacts = await ResolveArtifactsAsync(
                session.Commands,
                workDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        return new StationOperationResultDocument(
            StationOperationDocumentContract.ResultSchema,
            request.JobId,
            request.RuntimeSessionId,
            executionStatus,
            judgement,
            ResolveOutputs(session.Commands),
            steps.Count(step => string.Equals(step.Status, "Completed", StringComparison.Ordinal)),
            commands.Length,
            incidents.Length,
            session.StartedAtUtc ?? fallbackStartedAtUtc,
            session.CompletedAtUtc ?? DateTimeOffset.UtcNow,
            steps,
            commands,
            incidents,
            artifacts,
            failure.Code,
            failure.Reason);
    }

    private static (ExecutionStatus Status, ResultJudgement Judgement) ResolveAxes(
        RuntimeSession session)
    {
        if (session.Status == RuntimeSessionStatus.Completed)
        {
            var judgements = session.Commands
                .Where(command => command.ResultJudgement is not null)
                .Select(command => command.ResultJudgement!.Value)
                .ToArray();
            if (judgements.Contains(ResultJudgement.Aborted))
            {
                return (ExecutionStatus.Completed, ResultJudgement.Aborted);
            }

            if (judgements.Contains(ResultJudgement.Failed))
            {
                return (ExecutionStatus.Completed, ResultJudgement.Failed);
            }

            return judgements.Contains(ResultJudgement.Passed)
                ? (ExecutionStatus.Completed, ResultJudgement.Passed)
                : (ExecutionStatus.Completed, ResultJudgement.NotApplicable);
        }

        if (session.Status is RuntimeSessionStatus.Canceled or RuntimeSessionStatus.Stopped)
        {
            return (ExecutionStatus.Canceled, ResultJudgement.Aborted);
        }

        if (session.Status != RuntimeSessionStatus.Failed)
        {
            throw new InvalidDataException(
                $"Runtime Session ended in non-terminal status {session.Status}.");
        }

        if (session.Commands.Any(command => command.Status == DomainRuntimeCommandStatus.TimedOut))
        {
            return (ExecutionStatus.TimedOut, ResultJudgement.Unknown);
        }

        if (session.Commands.Any(command => command.Status == DomainRuntimeCommandStatus.Rejected))
        {
            return (ExecutionStatus.Rejected, ResultJudgement.Unknown);
        }

        return (ExecutionStatus.Failed, ResultJudgement.Unknown);
    }

    private static (string Code, string Reason) ResolveFailure(
        RuntimeSession session,
        ExecutionStatus status)
    {
        var incident = session.Incidents
            .OrderByDescending(candidate => candidate.OccurredAtUtc)
            .FirstOrDefault();
        if (incident is not null)
        {
            return (incident.Code, BoundedReason(incident.Message));
        }

        var command = session.Commands
            .Where(candidate => candidate.FailureReason is not null)
            .OrderByDescending(candidate => candidate.CompletedAtUtc)
            .FirstOrDefault();
        return command is null
            ? ($"StationRuntime.{status}", $"Station Runtime ended as {status}.")
            : ($"StationRuntime.Command{command.Status}", BoundedReason(command.FailureReason!));
    }

    private static JsonElement ResolveOutputs(IReadOnlyCollection<RuntimeCommand> commands)
    {
        var outputs = ProductionContextOutputReader.ReadExplicitMany(commands
            .Where(candidate =>
                candidate.Status == DomainRuntimeCommandStatus.Completed)
            .OrderBy(candidate => candidate.CompletedAtUtc)
            .ThenBy(candidate => candidate.Id.Value)
            .Select(candidate => candidate.ResultPayload));

        return JsonSerializer.SerializeToElement(
            outputs.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(
                item => item.Key,
                item => new { kind = item.Value.Kind.ToString(), value = item.Value.CanonicalValue },
                StringComparer.Ordinal),
            StationOperationDocumentJson.CreateOptions());
    }

    private static async ValueTask<IReadOnlyList<StationOperationArtifactEvidence>> ResolveArtifactsAsync(
        IReadOnlyCollection<RuntimeCommand> commands,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var evidenceRoot = Path.Combine(Path.GetFullPath(workDirectory), "evidence");
        var artifacts = new Dictionary<string, StationOperationArtifactEvidence>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            var evidence = RuntimeCommandEvidencePayload.Read(command.ResultPayload);
            if (evidence is null)
            {
                continue;
            }

            foreach (var artifact in evidence.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveInside(evidenceRoot, artifact.StorageKey);
                if (!File.Exists(path))
                {
                    throw new InvalidDataException(
                        $"Command artifact '{artifact.StorageKey}' is missing from Station evidence.");
                }

                var info = new FileInfo(path);
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var sha256 = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (info.Length != artifact.SizeBytes
                    || !string.Equals(sha256, artifact.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Command artifact '{artifact.StorageKey}' integrity differs from Runtime evidence.");
                }

                var relativePath = $"evidence/{artifact.StorageKey}";
                var mapped = new StationOperationArtifactEvidence(
                    relativePath,
                    artifact.Name,
                    artifact.Kind,
                    artifact.MediaType,
                    artifact.SizeBytes,
                    artifact.Sha256);
                if (artifacts.TryGetValue(relativePath, out var existing) && existing != mapped)
                {
                    throw new InvalidDataException(
                        $"Command artifact '{artifact.StorageKey}' has conflicting evidence.");
                }

                artifacts[relativePath] = mapped;
            }
        }

        return artifacts.Values.OrderBy(artifact => artifact.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string ResolveInside(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\')
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Artifact storage key is not a safe relative path.");
        }

        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new InvalidDataException("Artifact storage key escapes the evidence root.");
    }

    private static string BoundedReason(string value)
    {
        var normalized = new string(value
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray()).Trim();
        if (normalized.Length == 0)
        {
            normalized = "Station Runtime execution failed.";
        }

        return normalized.Length <= 4096 ? normalized : normalized[..4096];
    }

    private static string? CanonicalOptionalReason(string? value) =>
        value is null ? null : BoundedReason(value);
}
