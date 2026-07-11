using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed class ProductModelDefinition : Entity<ProductModelId>
{
    private ProductModelDefinition(ProductModelId id, string modelCode, string identityInputKey)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        ModelCode = ProductionIdGuard.NotBlank(modelCode, nameof(modelCode));
        IdentityInputKey = ProductionIdGuard.NotBlank(identityInputKey, nameof(identityInputKey));
    }

    public string ModelCode { get; }

    public string IdentityInputKey { get; }

    public static ProductModelDefinition Create(
        ProductModelId id,
        string modelCode,
        string identityInputKey)
    {
        return new ProductModelDefinition(id, modelCode, identityInputKey);
    }
}
