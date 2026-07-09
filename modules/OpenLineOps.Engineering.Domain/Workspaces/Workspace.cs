using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Engineering.Domain.Identifiers;

namespace OpenLineOps.Engineering.Domain.Workspaces;

public sealed class Workspace : AggregateRoot<WorkspaceId>
{
    private Workspace(WorkspaceId id, string displayName, DateTimeOffset createdAtUtc)
        : base(id)
    {
        DisplayName = EngineeringIdGuard.NotBlank(displayName, nameof(displayName));
        CreatedAtUtc = createdAtUtc;
    }

    public string DisplayName { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static Workspace Create(WorkspaceId id, string displayName, DateTimeOffset createdAtUtc)
    {
        return new Workspace(id, displayName, createdAtUtc);
    }

    public static Workspace Restore(WorkspaceId id, string displayName, DateTimeOffset createdAtUtc)
    {
        return new Workspace(id, displayName, createdAtUtc);
    }
}
