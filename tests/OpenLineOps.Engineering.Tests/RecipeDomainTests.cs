using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Recipes;

namespace OpenLineOps.Engineering.Tests;

public sealed class RecipeDomainTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 31, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TypedParametersCanonicalizeValuesAndRetainSchema()
    {
        var parameter = new RecipeParameter(
            "voltage.target",
            " 24.5000 ",
            RecipeParameterType.Decimal,
            " V ",
            minimum: 20m,
            maximum: 28m,
            allowedValues: null,
            required: true);

        Assert.Equal("voltage.target", parameter.Key);
        Assert.Equal("24.5", parameter.Value);
        Assert.Equal(RecipeParameterType.Decimal, parameter.Type);
        Assert.Equal("V", parameter.Unit);
        Assert.Equal(20m, parameter.Minimum);
        Assert.Equal(28m, parameter.Maximum);
        Assert.True(parameter.Required);
        Assert.Empty(parameter.AllowedValues);
    }

    [Fact]
    public void EnumParameterRejectsAValueOutsideItsSchema()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RecipeParameter(
            "scan.mode",
            "Unknown",
            RecipeParameterType.Enum,
            unit: null,
            minimum: null,
            maximum: null,
            allowedValues: ["Automatic", "Manual"],
            required: true));

        Assert.Contains("configured enum values", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericParameterRejectsAValueOutsideItsLimits()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RecipeParameter(
            "pressure.minimum",
            "2.5",
            RecipeParameterType.Decimal,
            "bar",
            minimum: 4m,
            maximum: 8m,
            allowedValues: null,
            required: true));

        Assert.Contains("greater than or equal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecipeRequiresValidateApproveReleaseOrder()
    {
        var recipe = CreateRecipe();

        Assert.False(recipe.Release(CreatedAtUtc.AddMinutes(1)).Succeeded);
        Assert.True(recipe.Validate(CreatedAtUtc.AddMinutes(1)).Succeeded);
        Assert.False(recipe.Release(CreatedAtUtc.AddMinutes(2)).Succeeded);
        Assert.True(recipe.Approve("engineer.alice", CreatedAtUtc.AddMinutes(2)).Succeeded);
        Assert.True(recipe.Release(CreatedAtUtc.AddMinutes(3)).Succeeded);

        Assert.Equal(RecipeStatus.Released, recipe.Status);
        Assert.Equal("engineer.alice", recipe.ApprovedBy);
        Assert.Equal(CreatedAtUtc.AddMinutes(1), recipe.ValidatedAtUtc);
        Assert.Equal(CreatedAtUtc.AddMinutes(2), recipe.ApprovedAtUtc);
        Assert.Equal(CreatedAtUtc.AddMinutes(3), recipe.ReleasedAtUtc);
        Assert.Equal(recipe.ReleasedAtUtc, recipe.PublishedAtUtc);
    }

    [Fact]
    public void CompatibilityPublishProducesCompleteLifecycleEvidence()
    {
        var recipe = CreateRecipe();
        var publishedAtUtc = CreatedAtUtc.AddMinutes(1);

        Assert.True(recipe.Publish(publishedAtUtc).Succeeded);

        Assert.Equal(RecipeStatus.Released, recipe.Status);
        Assert.Equal(publishedAtUtc, recipe.ValidatedAtUtc);
        Assert.Equal(publishedAtUtc, recipe.ApprovedAtUtc);
        Assert.Equal(publishedAtUtc, recipe.ReleasedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(recipe.ApprovedBy));
    }

    [Fact]
    public void ReleasedRecipeIsImmutableAndCanBeRetired()
    {
        var recipe = CreateRecipe();
        Assert.True(recipe.Publish(CreatedAtUtc.AddMinutes(1)).Succeeded);

        var mutation = recipe.AddOrUpdateParameter("voltage.target", "12");
        var retirement = recipe.Retire(CreatedAtUtc.AddMinutes(2));

        Assert.False(mutation.Succeeded);
        Assert.True(retirement.Succeeded);
        Assert.Equal(RecipeStatus.Retired, recipe.Status);
        Assert.Equal(CreatedAtUtc.AddMinutes(2), recipe.RetiredAtUtc);
    }

    [Fact]
    public void RestoringAPreviousPublishedDocumentBackfillsLifecycleEvidence()
    {
        var publishedAtUtc = CreatedAtUtc.AddMinutes(1);

        var recipe = Recipe.Restore(
            new RecipeId("recipe.compatible"),
            new RecipeVersionId("recipe.compatible@1.0.0"),
            "Compatible Recipe",
            RecipeStatus.Published,
            CreatedAtUtc,
            publishedAtUtc,
            [new RecipeParameter("mode", "Automatic")]);

        Assert.Equal(RecipeStatus.Released, recipe.Status);
        Assert.Equal(publishedAtUtc, recipe.ValidatedAtUtc);
        Assert.Equal(publishedAtUtc, recipe.ApprovedAtUtc);
        Assert.Equal(publishedAtUtc, recipe.ReleasedAtUtc);
    }

    private static Recipe CreateRecipe()
    {
        var recipe = Recipe.Create(
            new RecipeId("recipe.functional-test"),
            new RecipeVersionId("recipe.functional-test@1.0.0"),
            "Functional Test",
            CreatedAtUtc);
        Assert.True(recipe.AddOrUpdateParameter(new RecipeParameter(
            "voltage.target",
            "24",
            RecipeParameterType.Decimal,
            "V",
            minimum: 20m,
            maximum: 28m,
            allowedValues: null,
            required: true)).Succeeded);
        return recipe;
    }
}
