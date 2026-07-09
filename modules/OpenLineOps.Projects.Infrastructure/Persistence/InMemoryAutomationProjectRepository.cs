using System.Collections.Concurrent;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Projects;

namespace OpenLineOps.Projects.Infrastructure.Persistence;

public sealed class InMemoryAutomationProjectRepository : IAutomationProjectRepository
{
    private readonly ConcurrentDictionary<AutomationProjectId, AutomationProject> _projects = [];
    private int _saveCount;

    public int SaveCount => Volatile.Read(ref _saveCount);

    public ValueTask SaveAsync(AutomationProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();

        _projects[project.Id] = project;
        Interlocked.Increment(ref _saveCount);

        return ValueTask.CompletedTask;
    }

    public ValueTask<AutomationProject?> GetByIdAsync(
        AutomationProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _projects.TryGetValue(projectId, out var project);

        return ValueTask.FromResult(project);
    }

    public ValueTask<IReadOnlyCollection<AutomationProject>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var projects = _projects.Values
            .OrderBy(project => project.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyCollection<AutomationProject>>(projects);
    }
}
