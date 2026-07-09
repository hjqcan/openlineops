using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Projects;

namespace OpenLineOps.Projects.Application.Persistence;

public interface IAutomationProjectRepository
{
    ValueTask SaveAsync(AutomationProject project, CancellationToken cancellationToken = default);

    ValueTask<AutomationProject?> GetByIdAsync(
        AutomationProjectId projectId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<AutomationProject>> ListAsync(
        CancellationToken cancellationToken = default);
}
