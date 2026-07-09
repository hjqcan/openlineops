using System.Collections.Concurrent;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class InMemoryEngineeringProjectRepository : IEngineeringProjectRepository
{
    private readonly ConcurrentDictionary<string, EngineeringProject> _projects = new(StringComparer.Ordinal);

    public Task SaveAsync(EngineeringProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();

        _projects[project.Id.Value] = project;

        return Task.CompletedTask;
    }

    public Task<EngineeringProject?> GetByIdAsync(
        EngineeringProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        cancellationToken.ThrowIfCancellationRequested();

        _projects.TryGetValue(projectId.Value, out var project);

        return Task.FromResult(project);
    }

    public Task<IReadOnlyCollection<EngineeringProject>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyCollection<EngineeringProject>>(_projects.Values.ToArray());
    }
}
