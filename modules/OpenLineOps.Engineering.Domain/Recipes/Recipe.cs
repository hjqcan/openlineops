using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Operations;

namespace OpenLineOps.Engineering.Domain.Recipes;

public sealed class Recipe : AggregateRoot<RecipeId>
{
    private readonly List<RecipeParameter> _parameters = [];

    private Recipe(
        RecipeId id,
        RecipeVersionId versionId,
        string displayName,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        VersionId = versionId;
        DisplayName = EngineeringIdGuard.NotBlank(displayName, nameof(displayName));
        CreatedAtUtc = createdAtUtc;
        Status = RecipeStatus.Draft;
    }

    public RecipeVersionId VersionId { get; }

    public string DisplayName { get; }

    public RecipeStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public IReadOnlyCollection<RecipeParameter> Parameters => _parameters.AsReadOnly();

    public bool IsPublished => Status == RecipeStatus.Published;

    public static Recipe Create(
        RecipeId id,
        RecipeVersionId versionId,
        string displayName,
        DateTimeOffset createdAtUtc)
    {
        return new Recipe(id, versionId, displayName, createdAtUtc);
    }

    public static Recipe Restore(
        RecipeId id,
        RecipeVersionId versionId,
        string displayName,
        RecipeStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? publishedAtUtc,
        IEnumerable<RecipeParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var recipe = new Recipe(id, versionId, displayName, createdAtUtc)
        {
            Status = status,
            PublishedAtUtc = publishedAtUtc
        };

        recipe._parameters.AddRange(parameters);
        return recipe;
    }

    public EngineeringOperationResult AddOrUpdateParameter(string key, string value)
    {
        var draftResult = EnsureDraft();
        if (!draftResult.Succeeded)
        {
            return draftResult;
        }

        var parameter = new RecipeParameter(key, value);
        var existingIndex = _parameters.FindIndex(candidate =>
            string.Equals(candidate.Key, parameter.Key, StringComparison.Ordinal));

        if (existingIndex >= 0)
        {
            _parameters[existingIndex] = parameter;
        }
        else
        {
            _parameters.Add(parameter);
        }

        return EngineeringOperationResult.Accepted("Recipe parameter saved.");
    }

    public EngineeringOperationResult Publish(DateTimeOffset publishedAtUtc)
    {
        if (Status == RecipeStatus.Published)
        {
            return EngineeringOperationResult.Accepted("Recipe is already published.");
        }

        Status = RecipeStatus.Published;
        PublishedAtUtc = publishedAtUtc;

        return EngineeringOperationResult.Accepted("Recipe published.");
    }

    private EngineeringOperationResult EnsureDraft()
    {
        if (Status != RecipeStatus.Draft)
        {
            return EngineeringOperationResult.Rejected(
                "Engineering.RecipeImmutable",
                $"Recipe {Id} cannot be changed after publication.");
        }

        return EngineeringOperationResult.Accepted();
    }
}
