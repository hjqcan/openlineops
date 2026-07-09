using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Projects;

namespace OpenLineOps.Engineering.Application.Persistence;

public interface IEngineeringProjectRepository
{
    Task SaveAsync(EngineeringProject project, CancellationToken cancellationToken = default);

    Task<EngineeringProject?> GetByIdAsync(
        EngineeringProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<EngineeringProject>> ListAsync(
        CancellationToken cancellationToken = default);
}
