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

    public DateTimeOffset? ValidatedAtUtc { get; private set; }

    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public IReadOnlyCollection<RecipeParameter> Parameters => _parameters.AsReadOnly();

    public bool IsPublished => Status == RecipeStatus.Released;

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
        IEnumerable<RecipeParameter> parameters,
        DateTimeOffset? validatedAtUtc = null,
        DateTimeOffset? approvedAtUtc = null,
        string? approvedBy = null,
        DateTimeOffset? retiredAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var releasedAtUtc = publishedAtUtc;
        var restoredValidatedAtUtc = validatedAtUtc;
        var restoredApprovedAtUtc = approvedAtUtc;
        var restoredApprovedBy = approvedBy;
        if (status is RecipeStatus.Released or RecipeStatus.Retired)
        {
            restoredValidatedAtUtc ??= releasedAtUtc;
            restoredApprovedAtUtc ??= releasedAtUtc;
            restoredApprovedBy = string.IsNullOrWhiteSpace(restoredApprovedBy)
                ? "openlineops.compatibility"
                : restoredApprovedBy.Trim();
        }

        var recipe = new Recipe(id, versionId, displayName, createdAtUtc)
        {
            Status = status,
            ValidatedAtUtc = restoredValidatedAtUtc,
            ApprovedAtUtc = restoredApprovedAtUtc,
            ApprovedBy = restoredApprovedBy,
            ReleasedAtUtc = releasedAtUtc,
            PublishedAtUtc = releasedAtUtc,
            RetiredAtUtc = retiredAtUtc
        };

        recipe._parameters.AddRange(parameters);
        recipe.EnsureRestoredLifecycleIsConsistent();
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

    public EngineeringOperationResult AddOrUpdateParameter(RecipeParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        var draftResult = EnsureDraft();
        if (!draftResult.Succeeded)
        {
            return draftResult;
        }

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

    public EngineeringOperationResult Validate(DateTimeOffset validatedAtUtc)
    {
        if (Status == RecipeStatus.Validated)
        {
            return EngineeringOperationResult.Accepted("Recipe is already validated.");
        }

        if (Status != RecipeStatus.Draft)
        {
            return InvalidTransition(RecipeStatus.Validated);
        }

        EnsureTimestampIsNotBeforeCreation(validatedAtUtc, nameof(validatedAtUtc));
        Status = RecipeStatus.Validated;
        ValidatedAtUtc = validatedAtUtc;
        return EngineeringOperationResult.Accepted("Recipe validated.");
    }

    public EngineeringOperationResult Approve(
        string approvedBy,
        DateTimeOffset approvedAtUtc)
    {
        if (Status == RecipeStatus.Approved)
        {
            return EngineeringOperationResult.Accepted("Recipe is already approved.");
        }

        if (Status != RecipeStatus.Validated)
        {
            return InvalidTransition(RecipeStatus.Approved);
        }

        var normalizedActor = EngineeringIdGuard.NotBlank(approvedBy, nameof(approvedBy));
        EnsureTimestampIsNotBefore(
            approvedAtUtc,
            ValidatedAtUtc,
            nameof(approvedAtUtc),
            "validation");

        Status = RecipeStatus.Approved;
        ApprovedBy = normalizedActor;
        ApprovedAtUtc = approvedAtUtc;
        return EngineeringOperationResult.Accepted("Recipe approved.");
    }

    public EngineeringOperationResult Release(DateTimeOffset releasedAtUtc)
    {
        if (Status == RecipeStatus.Released)
        {
            return EngineeringOperationResult.Accepted("Recipe is already released.");
        }

        if (Status != RecipeStatus.Approved)
        {
            return InvalidTransition(RecipeStatus.Released);
        }

        EnsureTimestampIsNotBefore(
            releasedAtUtc,
            ApprovedAtUtc,
            nameof(releasedAtUtc),
            "approval");

        Status = RecipeStatus.Released;
        ReleasedAtUtc = releasedAtUtc;
        PublishedAtUtc = releasedAtUtc;
        return EngineeringOperationResult.Accepted("Recipe released.");
    }

    public EngineeringOperationResult Publish(DateTimeOffset publishedAtUtc)
    {
        if (Status == RecipeStatus.Released)
        {
            return EngineeringOperationResult.Accepted("Recipe is already published.");
        }

        if (Status == RecipeStatus.Draft)
        {
            var validation = Validate(publishedAtUtc);
            if (!validation.Succeeded)
            {
                return validation;
            }
        }

        if (Status == RecipeStatus.Validated)
        {
            var approval = Approve("openlineops.compatibility", publishedAtUtc);
            if (!approval.Succeeded)
            {
                return approval;
            }
        }

        var release = Release(publishedAtUtc);
        if (!release.Succeeded)
        {
            return release;
        }

        return EngineeringOperationResult.Accepted("Recipe published.");
    }

    public EngineeringOperationResult Retire(DateTimeOffset retiredAtUtc)
    {
        if (Status == RecipeStatus.Retired)
        {
            return EngineeringOperationResult.Accepted("Recipe is already retired.");
        }

        if (Status != RecipeStatus.Released)
        {
            return InvalidTransition(RecipeStatus.Retired);
        }

        EnsureTimestampIsNotBefore(
            retiredAtUtc,
            ReleasedAtUtc,
            nameof(retiredAtUtc),
            "release");
        Status = RecipeStatus.Retired;
        RetiredAtUtc = retiredAtUtc;
        return EngineeringOperationResult.Accepted("Recipe retired.");
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

    private EngineeringOperationResult InvalidTransition(RecipeStatus target)
    {
        return EngineeringOperationResult.Rejected(
            "Engineering.InvalidRecipeLifecycleTransition",
            $"Recipe {Id} cannot transition from {Status} to {target}.");
    }

    private void EnsureRestoredLifecycleIsConsistent()
    {
        if (ValidatedAtUtc is not null)
        {
            EnsureTimestampIsNotBeforeCreation(
                ValidatedAtUtc.Value,
                nameof(ValidatedAtUtc));
        }

        if (ApprovedAtUtc is not null)
        {
            EnsureTimestampIsNotBefore(
                ApprovedAtUtc.Value,
                ValidatedAtUtc,
                nameof(ApprovedAtUtc),
                "validation");
        }

        if (ReleasedAtUtc is not null)
        {
            EnsureTimestampIsNotBefore(
                ReleasedAtUtc.Value,
                ApprovedAtUtc,
                nameof(ReleasedAtUtc),
                "approval");
        }

        if (RetiredAtUtc is not null)
        {
            EnsureTimestampIsNotBefore(
                RetiredAtUtc.Value,
                ReleasedAtUtc,
                nameof(RetiredAtUtc),
                "release");
        }

        if (Status >= RecipeStatus.Validated && ValidatedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Restored recipe {Id} is {Status} without a validation timestamp.");
        }

        if (Status >= RecipeStatus.Approved
            && (ApprovedAtUtc is null || string.IsNullOrWhiteSpace(ApprovedBy)))
        {
            throw new InvalidOperationException(
                $"Restored recipe {Id} is {Status} without approval evidence.");
        }

        if (Status is RecipeStatus.Released or RecipeStatus.Retired
            && ReleasedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Restored recipe {Id} is {Status} without a release timestamp.");
        }

        if (Status == RecipeStatus.Retired && RetiredAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Restored recipe {Id} is retired without a retirement timestamp.");
        }
    }

    private void EnsureTimestampIsNotBeforeCreation(
        DateTimeOffset timestamp,
        string parameterName)
    {
        EnsureTimestampIsNotBefore(
            timestamp,
            CreatedAtUtc,
            parameterName,
            "creation");
    }

    private static void EnsureTimestampIsNotBefore(
        DateTimeOffset timestamp,
        DateTimeOffset? precedingTimestamp,
        string parameterName,
        string precedingStage)
    {
        if (precedingTimestamp is null || timestamp < precedingTimestamp)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Recipe lifecycle time cannot be before {precedingStage}.");
        }
    }
}
