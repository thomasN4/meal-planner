using Microsoft.EntityFrameworkCore;

namespace MealPlanner.Tests;

/// <summary>
/// <see cref="RecipeService"/> against real SQLite via the harness. No write
/// races here on purpose: recipes are plain autoincrement inserts with no
/// unique index, so unlike the inventory there is nothing to race.
/// </summary>
public class RecipeServiceTests
{
    private static RecipeSuggestion Suggestion(
        string title = "Fried rice",
        string description = "Weeknight fried rice.",
        int minutes = 25,
        IReadOnlyList<RecipeIngredient>? ingredients = null,
        IReadOnlyList<string>? steps = null) =>
        new(
            title,
            description,
            minutes,
            ingredients ?? [new("Rice", "2 cups", Have: true), new("Soy sauce", "a splash", Have: false)],
            steps ?? ["Cook the rice.", "Fry everything together."]);

    [Fact]
    public async Task Saving_a_suggestion_round_trips_ingredients_and_steps()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewRecipeService();

        var saved = await service.SaveAsync(Suggestion());
        var reloaded = Assert.Single(await service.GetAllAsync());

        Assert.Equal(saved.Id, reloaded.Id);
        Assert.Equal("Fried rice", reloaded.Title);
        Assert.Equal("Weeknight fried rice.", reloaded.Description);
        Assert.Equal(25, reloaded.Minutes);
        Assert.Equal(
            [new SavedIngredient("Rice", "2 cups"), new SavedIngredient("Soy sauce", "a splash")],
            reloaded.Ingredients);
        Assert.Equal(["Cook the rice.", "Fry everything together."], reloaded.Steps);
    }

    [Fact]
    public async Task Overlong_fields_are_clamped_before_save()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewRecipeService();

        await service.SaveAsync(Suggestion(
            title: new string('t', 300),
            description: new string('d', 900),
            minutes: 99999,
            ingredients: [new(new string('n', 300), new string('q', 300), Have: false)],
            steps: [new string('s', 900)]));

        var saved = Assert.Single(await service.GetAllAsync());
        Assert.Equal(100, saved.Title.Length);
        Assert.Equal(500, saved.Description.Length);
        Assert.Equal(1440, saved.Minutes);
        Assert.Equal(100, saved.Ingredients[0].Name.Length);
        Assert.Equal(50, saved.Ingredients[0].Quantity.Length);
        Assert.Equal(500, saved.Steps[0].Length);
    }

    [Fact]
    public async Task An_empty_title_is_rejected()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewRecipeService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveAsync(Suggestion(title: "   ")));
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task Saved_recipes_come_back_newest_first()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewRecipeService();

        await service.SaveAsync(Suggestion(title: "First"));
        await service.SaveAsync(Suggestion(title: "Second"));

        var titles = (await service.GetAllAsync()).Select(r => r.Title);
        Assert.Equal(["Second", "First"], titles);
    }

    [Fact]
    public async Task Deleting_a_recipe_removes_it_and_deleting_twice_is_a_no_op()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewRecipeService();
        var saved = await service.SaveAsync(Suggestion());

        await service.DeleteAsync(saved.Id);
        Assert.Empty(await service.GetAllAsync());

        // Already gone — from this service or another circuit — is done, not
        // an error.
        await service.DeleteAsync(saved.Id);
    }

    [Fact]
    public async Task The_have_flag_is_not_persisted()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var service = harness.NewRecipeService();
        await service.SaveAsync(Suggestion());

        await using var db = await harness.Factory.CreateDbContextAsync();
        var row = await db.Recipes.SingleAsync();

        // The split is a pantry snapshot that goes stale the moment anything
        // is cooked; only name and quantity belong in the column.
        Assert.DoesNotContain("have", row.IngredientsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\"", row.IngredientsJson);
        Assert.Contains("\"quantity\"", row.IngredientsJson);
    }
}
