using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed class DutModelDefinition : Entity<DutModelId>
{
    private DutModelDefinition(DutModelId id, string modelCode, string identityInputKey)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        ModelCode = ProductionIdGuard.NotBlank(modelCode, nameof(modelCode));
        IdentityInputKey = ProductionIdGuard.NotBlank(identityInputKey, nameof(identityInputKey));
    }

    public string ModelCode { get; }

    public string IdentityInputKey { get; }

    public static DutModelDefinition Create(
        DutModelId id,
        string modelCode,
        string identityInputKey)
    {
        return new DutModelDefinition(id, modelCode, identityInputKey);
    }
}
