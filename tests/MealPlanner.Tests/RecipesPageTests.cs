namespace MealPlanner.Tests;

/// <summary>
/// The Recipes page, against a fake <see cref="IRecipeGenerator"/>. The suite
/// never spawns the claude CLI and rendering a page is not going to be the
/// exception — what is worth testing here is the page's own guards: the
/// double-click race, the must-use picker tracking the kitchen, and the saved
/// list refreshing itself (recipes have no notifier, deliberately).
/// </summary>
public class RecipesPageTests
{
    private static readonly RecipeSuggestion FriedRice = new(
        "Fried rice",
        "Weeknight fried rice.",
        25,
        [new RecipeIngredient("Rice", "2 cups", true)],
        ["Cook the rice.", "Fry it."]);

    [Fact]
    public async Task Generation_switched_off_replaces_the_controls_with_a_note()
    {
        await using var page = await PageHarness.CreateAsync();
        page.RecipeOptions.Enabled = false;

        var cut = page.RenderRecipes();

        Assert.Contains("Recipe generation is switched off", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button.btn-primary"));
    }

    [Fact]
    public async Task A_second_generate_click_while_one_is_running_is_ignored()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Generator.Result = [FriedRice];
        var cut = page.RenderRecipes();

        cut.Find("button.btn-primary").Click();
        // The disabled attribute alone races a double-click; the generating flag
        // is what actually holds. Click again while the first call is parked.
        cut.Find("button.btn-primary").Click();

        Assert.Single(page.Generator.Requests);

        page.Generator.Gate.SetResult();
        cut.WaitForAssertion(() => Assert.Contains("Fried rice", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_must_use_pick_is_dropped_when_the_item_leaves_the_kitchen()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        await page.Service.UpsertAsync("Peas", "1 bag", IngredientCategory.Frozen);
        var peas = await page.Service.FindAsync("Peas");
        var cut = page.RenderRecipes();

        cut.Find($"#use-{peas!.Id}").Change(true);

        // Someone else finishes the peas. The picker refreshes off the notifier
        // like the Inventory page, and the stale pick has to go with it —
        // otherwise the prompt asks Claude to cook with what is gone.
        await page.OutOfCircuitService().RemoveAsync("Peas");

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("Peas", cut.Markup, StringComparison.Ordinal));

        cut.Find("button.btn-primary").Click();

        var request = Assert.Single(page.Generator.Requests);
        Assert.DoesNotContain("Peas", request.MustUse);
    }

    [Fact]
    public async Task A_must_use_pick_reaches_the_generator()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        var cut = page.RenderRecipes();

        cut.Find("input.form-check-input").Change(true);
        cut.Find("button.btn-primary").Click();

        var request = Assert.Single(page.Generator.Requests);
        Assert.Equal("Rice", Assert.Single(request.MustUse));
    }

    [Fact]
    public async Task A_generator_that_comes_back_empty_says_so()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [];
        var cut = page.RenderRecipes();

        cut.Find("button.btn-primary").Click();

        // The generator never throws — an empty list means "nothing usable came
        // back". Silently rendering nothing would read as a broken button.
        cut.WaitForAssertion(() =>
            Assert.Contains("couldn't come up with anything", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saving_a_suggestion_lists_it_and_the_button_will_not_fire_twice()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [FriedRice];
        var cut = page.RenderRecipes();
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("Fried rice", cut.Markup, StringComparison.Ordinal));

        var save = cut.Find("div.card-footer button");
        save.Click();

        // Saved recipes have no notifier — RecipeService deliberately does not
        // publish — so the page refreshes that list from its own handler.
        cut.WaitForAssertion(() => Assert.Equal("Saved", cut.Find("div.card-footer button").TextContent.Trim()));
        Assert.True(cut.Find("div.card-footer button").HasAttribute("disabled"));
        Assert.Single(await page.Recipes.GetAllAsync());
    }

    [Fact]
    public async Task Deleting_a_saved_recipe_takes_it_off_the_list()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [FriedRice];
        var cut = page.RenderRecipes();
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("Fried rice", cut.Markup, StringComparison.Ordinal));
        cut.Find("div.card-footer button").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button[aria-label^='Delete']")));

        cut.Find("button[aria-label^='Delete']").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Nothing saved yet", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(await page.Recipes.GetAllAsync());
    }
}
