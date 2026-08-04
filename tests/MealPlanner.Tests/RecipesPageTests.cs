using MealPlanner.Components.Pages;

namespace MealPlanner.Tests;

/// <summary>
/// The Recipes page, against a fake <see cref="IRecipeGenerator"/>. The suite
/// never spawns the claude CLI and rendering a page is not going to be the
/// exception — what is worth testing here is the page's own guards: the
/// double-click race, the picker tracking the kitchen, the three roles reaching
/// the generator apart from each other, and the saved list refreshing itself
/// (recipes have no notifier, deliberately).
/// </summary>
public class RecipesPageTests
{
    private static readonly RecipeSuggestion FriedRice = new(
        "Fried rice",
        "Weeknight fried rice.",
        25,
        [new RecipeIngredient("Rice", "2 cups", true)],
        ["Cook the rice.", "Fry it."]);

    /// <summary>
    /// Types into the combobox and clicks the first row it offers. Every pick
    /// in this file goes through the real widget rather than the page's
    /// handler, because the widget is now shared and a page could stop being
    /// wired to it without a single one of these failing otherwise.
    /// </summary>
    private static void Pick(IRenderedComponent<Recipes> cut, string typed)
    {
        cut.Find("#pick-ingredient").Input(typed);
        cut.Find("li[role=option]").Click();
    }

    private static void ChooseRole(IRenderedComponent<Recipes> cut, string label) =>
        cut.FindAll("button.role-choice")
            .Single(b => b.TextContent.Trim() == label)
            .Click();

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

        cut.Find("button.generate").Click();
        // The disabled attribute alone races a double-click; the generating flag
        // is what actually holds. Click again while the first call is parked.
        cut.Find("button.generate").Click();

        Assert.Single(page.Generator.Requests);

        page.Generator.Gate.SetResult();
        cut.WaitForAssertion(() => Assert.Contains("Fried rice", cut.Markup, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Use up")]
    [InlineData("Include")]
    [InlineData("Exclude")]
    public async Task A_pick_is_dropped_when_the_item_leaves_the_kitchen(string role)
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        await page.Service.UpsertAsync("Peas", "1 bag", IngredientCategory.Frozen);
        var cut = page.RenderRecipes();

        ChooseRole(cut, role);
        Pick(cut, "Peas");

        // Someone else finishes the peas. The picker refreshes off the notifier
        // like the Inventory page, and the stale pick has to go with it —
        // otherwise the prompt asks Claude to cook with what is gone. Every
        // role, because the rebuild in RefreshInventoryAsync covers all three
        // and a filter that missed one would still look right for Use up.
        await page.OutOfCircuitService().RemoveAsync("Peas");

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("Peas", cut.Markup, StringComparison.Ordinal));

        cut.Find("button.generate").Click();

        var request = Assert.Single(page.Generator.Requests);
        Assert.DoesNotContain("Peas", request.UseUp);
        Assert.DoesNotContain("Peas", request.Include);
        Assert.DoesNotContain("Peas", request.Exclude);
    }

    [Theory]
    [InlineData("Use up")]
    [InlineData("Include")]
    [InlineData("Exclude")]
    public async Task A_pick_reaches_the_generator_under_its_own_role(string role)
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        var cut = page.RenderRecipes();

        ChooseRole(cut, role);
        Pick(cut, "Rice");
        cut.Find("button.generate").Click();

        // Asserted per role, and asserting the other two are *empty*, because
        // RecipeRequest takes three positionally-identical lists: swap two of
        // them at the call site and a single combined assertion still passes.
        var request = Assert.Single(page.Generator.Requests);
        Assert.Equal(role == "Use up" ? ["Rice"] : [], request.UseUp);
        Assert.Equal(role == "Include" ? ["Rice"] : [], request.Include);
        Assert.Equal(role == "Exclude" ? ["Rice"] : [], request.Exclude);
    }

    [Fact]
    public async Task Picking_from_the_combobox_adds_a_chip_under_the_selected_role()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Coriander", "1 bunch", IngredientCategory.FreshHerbs);
        var cut = page.RenderRecipes();

        ChooseRole(cut, "Exclude");
        Pick(cut, "corian");

        var row = cut.Find("div.role-row[data-role=Exclude]");
        Assert.Contains("Coriander", row.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("div.role-row[data-role=UseUp]"));
    }

    [Fact]
    public async Task The_pick_box_clears_after_a_pick()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Coriander", "1 bunch", IngredientCategory.FreshHerbs);
        var cut = page.RenderRecipes();

        Pick(cut, "corian");

        // Unlike the Inventory page's box, which keeps the name it just filled
        // in because Add is about to write it. Here the next thing the user
        // wants is to type another ingredient.
        Assert.Equal(string.Empty, cut.Find("#pick-ingredient").GetAttribute("value"));
        Assert.Empty(cut.FindAll("li[role=option]"));
    }

    [Fact]
    public async Task The_role_selector_stays_where_it_was_left_across_picks()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Coriander", "1 bunch", IngredientCategory.FreshHerbs);
        await page.Service.UpsertAsync("Chilli oil", "1 jar", IngredientCategory.Condiments);
        var cut = page.RenderRecipes();

        // The whole point of a sticky role: filing two exclusions is two picks,
        // not four clicks.
        ChooseRole(cut, "Exclude");
        Pick(cut, "corian");
        Pick(cut, "chilli");

        var row = cut.Find("div.role-row[data-role=Exclude]");
        Assert.Equal(2, row.QuerySelectorAll("span.pick-chip").Length);
        Assert.Empty(cut.FindAll("div.role-row[data-role=UseUp]"));
    }

    [Fact]
    public async Task An_exact_name_is_offered_in_the_recipes_box()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        var cut = page.RenderRecipes();

        cut.Find("#pick-ingredient").Input("Rice");

        // IngredientMatcher.Suggest drops an exact match for the Inventory
        // page's benefit — a row that can be arrowed onto would turn Enter from
        // "add" into "fill". This box has no such rule, and inheriting that one
        // would make an exactly-typed name the one thing you cannot pick.
        var option = Assert.Single(cut.FindAll("li[role=option]"));
        Assert.Contains("Rice", option.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picking_an_item_that_already_holds_a_role_moves_it()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        var cut = page.RenderRecipes();

        Pick(cut, "Rice");                  // Use up, the default
        ChooseRole(cut, "Exclude");
        Pick(cut, "Rice");

        // One item, one role. Two chips would mean the prompt asked for the
        // same name to be both used up and avoided.
        Assert.Single(cut.FindAll("span.pick-chip"));
        Assert.Single(cut.FindAll("div.role-row[data-role=Exclude]"));

        cut.Find("button.generate").Click();
        var request = Assert.Single(page.Generator.Requests);
        Assert.Empty(request.UseUp);
        Assert.Equal(["Rice"], request.Exclude);
    }

    [Fact]
    public async Task An_item_already_picked_under_the_selected_role_is_not_offered_again()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        var cut = page.RenderRecipes();

        Pick(cut, "Rice");
        cut.Find("#pick-ingredient").Input("Rice");
        Assert.Empty(cut.FindAll("li[role=option]"));

        // But it stays reachable under a different role, because picking it
        // again is the only way to move it.
        ChooseRole(cut, "Include");
        cut.Find("#pick-ingredient").Input("Rice");
        Assert.NotEmpty(cut.FindAll("li[role=option]"));
    }

    [Fact]
    public async Task Removing_a_chip_takes_the_pick_back_out()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        var cut = page.RenderRecipes();

        Pick(cut, "Rice");
        cut.Find("button.pick-remove").Click();

        Assert.Empty(cut.FindAll("span.pick-chip"));

        cut.Find("button.generate").Click();
        var request = Assert.Single(page.Generator.Requests);
        Assert.Empty(request.UseUp);
        Assert.Empty(request.Include);
        Assert.Empty(request.Exclude);
    }

    [Fact]
    public async Task The_brief_reaches_the_generator()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        var cut = page.RenderRecipes();

        cut.Find("#brief").Change("something warming, nothing spicy");
        cut.Find("button.generate").Click();

        var request = Assert.Single(page.Generator.Requests);
        Assert.Equal("something warming, nothing spicy", request.Brief);
    }

    [Fact]
    public async Task A_whitespace_brief_reaches_the_generator_as_an_empty_string()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        var cut = page.RenderRecipes();

        // A textarea nobody typed in but which caught a stray newline must not
        // arrive looking like the household asked for something.
        cut.Find("#brief").Change("  \n ");
        cut.Find("button.generate").Click();

        var request = Assert.Single(page.Generator.Requests);
        Assert.Equal(string.Empty, request.Brief);
    }

    [Fact]
    public async Task A_generator_that_comes_back_empty_says_so()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [];
        var cut = page.RenderRecipes();

        cut.Find("button.generate").Click();

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
        cut.Find("button.generate").Click();
        cut.WaitForAssertion(() => Assert.Contains("Fried rice", cut.Markup, StringComparison.Ordinal));

        var save = cut.Find("div.card-footer button");
        save.Click();

        // Saved recipes have no notifier — RecipeService deliberately does not
        // publish — so the page refreshes that list from its own handler.
        cut.WaitForAssertion(() => Assert.Equal("Saved", cut.Find("div.card-footer button").TextContent.Trim()));
        Assert.True(cut.Find("div.card-footer button").HasAttribute("disabled"));
        Assert.Single(await page.Recipes.GetAllAsync());
    }

    /// <summary>Generates and saves one recipe, leaving it in the saved list.</summary>
    private static void SaveOne(IRenderedComponent<Recipes> cut)
    {
        cut.Find("button.generate").Click();
        cut.WaitForAssertion(() => Assert.Contains("Fried rice", cut.Markup, StringComparison.Ordinal));
        cut.Find("div.card-footer button").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.recipe-delete")));
    }

    [Fact]
    public async Task Deleting_a_saved_recipe_asks_first()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [FriedRice];
        var cut = page.RenderRecipes();
        SaveOne(cut);

        cut.Find("button.recipe-delete").Click();

        // The trash arms the row; it does not delete it. Before this, one
        // misclick on a ✕ was the whole of a delete.
        Assert.Single(cut.FindAll("button.recipe-delete-confirm"));
        Assert.Empty(cut.FindAll("button.recipe-delete"));
        Assert.Single(await page.Recipes.GetAllAsync());
    }

    [Fact]
    public async Task Confirming_a_delete_removes_the_saved_recipe()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [FriedRice];
        var cut = page.RenderRecipes();
        SaveOne(cut);

        cut.Find("button.recipe-delete").Click();
        cut.Find("button.recipe-delete-confirm").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Nothing saved yet", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(await page.Recipes.GetAllAsync());
    }

    [Fact]
    public async Task Cancelling_a_delete_leaves_the_recipe_alone()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [FriedRice];
        var cut = page.RenderRecipes();
        SaveOne(cut);

        cut.Find("button.recipe-delete").Click();
        cut.Find("button.recipe-delete-cancel").Click();

        Assert.Single(cut.FindAll("button.recipe-delete"));
        Assert.Empty(cut.FindAll("button.recipe-delete-confirm"));
        Assert.Single(await page.Recipes.GetAllAsync());
    }

    [Fact]
    public async Task Only_one_saved_recipe_can_be_armed_at_a_time()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [FriedRice];
        var cut = page.RenderRecipes();
        SaveOne(cut);
        // A second row, saved from the same suggestion — the button re-enables
        // on the next generate.
        cut.Find("button.generate").Click();
        cut.WaitForAssertion(() => Assert.Equal("Save", cut.Find("div.card-footer button").TextContent.Trim()));
        cut.Find("div.card-footer button").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("button.recipe-delete").Count));

        cut.FindAll("button.recipe-delete")[0].Click();
        cut.FindAll("button.recipe-delete")[0].Click();

        // Arming the second disarms the first: an int?, not a set. Two armed
        // rows means two "Delete this recipe?" prompts and no way to tell which
        // one a click belongs to.
        Assert.Single(cut.FindAll("button.recipe-delete-confirm"));
    }

    [Fact]
    public async Task Arming_a_delete_moves_focus_to_the_confirm_button()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Rice", "1 bag", IngredientCategory.Grains);
        page.Generator.Result = [FriedRice];
        var cut = page.RenderRecipes();
        SaveOne(cut);

        // bUnit has no focus model, but it does service the interop call even
        // in Strict mode. Counted rather than asserted present: nothing else on
        // this page focuses anything, so a bare Contains would pass with the
        // arm doing nothing at all.
        var before = page.JSInterop.Invocations
            .Count(i => i.Identifier == "Blazor._internal.domWrapper.focus");

        cut.Find("button.recipe-delete").Click();

        Assert.Equal(
            before + 1,
            page.JSInterop.Invocations
                .Count(i => i.Identifier == "Blazor._internal.domWrapper.focus"));
    }
}
