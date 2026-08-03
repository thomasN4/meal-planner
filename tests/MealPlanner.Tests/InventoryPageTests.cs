using AngleSharp.Dom;
using MealPlanner.Components.Pages;

namespace MealPlanner.Tests;

/// <summary>
/// The Inventory page's own invariants, rendered against the real service
/// graph. The service tests already prove the writes are correct; these prove
/// the page reacts to them the way AGENTS.md says it must — refreshing only
/// from the notifier, and keeping the accordion where the user left it.
/// </summary>
public class InventoryPageTests
{
    [Fact]
    public async Task A_write_from_another_circuit_reaches_the_table()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderInventory();

        // Not page.Service: this is an MCP tool, or the kitchen's other tab.
        // Nothing tells the page about it except the notifier.
        await page.OutOfCircuitService().UpsertAsync("Paprika", "1 jar", IngredientCategory.DrySeasonings);

        cut.WaitForAssertion(() =>
            Assert.Contains("Dry Seasonings", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_category_the_user_opened_stays_open_across_a_live_refresh()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Paprika", "1 jar", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        Toggle(cut, "Dry Seasonings");
        Assert.Contains("Paprika", cut.Markup, StringComparison.Ordinal);

        // A write from elsewhere re-reads the whole list and re-renders. The
        // expanded set is page state, not row state, so it has to survive.
        await page.OutOfCircuitService().UpsertAsync("Cumin", "1 jar", IngredientCategory.DrySeasonings);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", Header(cut, "Dry Seasonings").GetAttribute("aria-expanded"));
            Assert.Contains("Cumin", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_collapsed_category_still_announces_that_it_is_collapsed()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Paprika", "1 jar", IngredientCategory.DrySeasonings);

        var cut = page.RenderInventory();

        // Blazor drops a bool attribute that is false, which left the collapsed
        // state unannounced until aria-expanded was made a string. Assert the
        // attribute is present and "false", not merely absent.
        Assert.Equal("false", Header(cut, "Dry Seasonings").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task Adding_a_name_that_differs_only_in_case_moves_the_existing_row()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Salt", "1 box", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        // The NOCASE unique index makes "salt" and "Salt" one row, and an upsert
        // applies the category the form was holding — so this is a move, not a
        // second row. Adding by hand what Claude already stocked has to land
        // somewhere the user can see.
        cut.Find("input.form-control").Input("salt");
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", Header(cut, "Other").GetAttribute("aria-expanded"));
            Assert.Contains("Salt", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Equal(1, await page.CountAsync());
    }

    [Fact]
    public async Task Adding_clears_the_form_and_resets_the_category()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderInventory();

        cut.Find("input.form-control").Input("Coriander");
        cut.Find("select.form-select").Change(nameof(IngredientCategory.FreshHerbs));
        cut.Find("button.btn-primary").Click();

        // The status alert used to be awaited before this ran, which left the
        // name sitting in the box for three seconds. Clearing has to happen on
        // the way out of the handler, not after the dismissal timer.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", cut.Find("input.form-control").GetAttribute("value") ?? "");
            Assert.Equal(
                nameof(IngredientCategory.Other),
                cut.Find("select.form-select").GetAttribute("value"));
        });
    }

    [Fact]
    public async Task Adding_reports_what_it_did_in_the_live_region()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderInventory();

        cut.Find("input.form-control").Input("Coriander");
        cut.Find("button.btn-primary").Click();

        // Covers that the diff reaches the live region at all. It does not cover
        // ShowStatus's StateHasChanged: bUnit renders at handler completion
        // either way, so deleting that call leaves this green. The paint-timing
        // bug it guards needs a real circuit, and there is no honest way to
        // reproduce it here — don't let this test's name imply otherwise.
        cut.WaitForAssertion(() =>
            Assert.Contains("Added \"Coriander\"", cut.Find("div[role=status]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_add_button_is_disabled_until_a_name_is_typed()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderInventory();

        Assert.True(cut.Find("button.btn-primary").HasAttribute("disabled"));

        // @bind:event="oninput" costs a round trip per keystroke and is
        // deliberate: the button's disabled state is read live.
        cut.Find("input.form-control").Input("Coriander");

        Assert.False(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_category_move_from_another_circuit_opens_the_destination()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Coriander", "1 bunch", IngredientCategory.Other);
        var cut = page.RenderInventory();

        // What the auto-categorizer does a few seconds after an add. Without the
        // PreviousCategory branch the row vanishes into a collapsed group right
        // after the user watched it land in Other.
        await page.OutOfCircuitService().SetCategoryAsync("Coriander", IngredientCategory.FreshHerbs);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", Header(cut, "Fresh Herbs").GetAttribute("aria-expanded"));
            Assert.Contains("Coriander", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_category_selection_that_is_not_a_real_enum_value_is_ignored()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Paprika", "1 jar", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();
        Toggle(cut, "Dry Seasonings");

        cut.Find("select.form-select-sm").Change("NotACategory");

        // Enum.TryParse would also accept "99" and hand back an undefined value,
        // which is why the guard checks Enum.IsDefined too.
        cut.Find("select.form-select-sm").Change("99");

        var item = await page.Service.FindAsync("Paprika");
        Assert.Equal(IngredientCategory.DrySeasonings, item?.Category);
    }

    [Fact]
    public async Task Removing_a_row_takes_it_out_of_the_table()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Paprika", "1 jar", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();
        Toggle(cut, "Dry Seasonings");

        cut.Find("button.item-remove").Click();

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("Paprika", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(0, await page.CountAsync());
    }

    [Fact]
    public async Task Disposing_the_page_takes_it_off_the_notifier()
    {
        await using var page = await PageHarness.CreateAsync();
        page.RenderInventory();

        await page.DisposeComponentsAsync();

        // The notifier logs how many subscribers it fanned out to, so the count
        // is observable without reaching into its private list. A page that
        // failed to unsubscribe would leave a dead circuit being published to
        // for the lifetime of the app.
        await page.OutOfCircuitService().UpsertAsync("Paprika", "1 jar", IngredientCategory.DrySeasonings);

        Assert.Contains(page.Log.Entries, e => e.Message.Contains("to 0 subscriber(s)", StringComparison.Ordinal));
    }

    /// <summary>The accordion button for a category, found by its visible label.</summary>
    private static IElement Header(IRenderedComponent<Inventory> cut, string label) =>
        cut.FindAll("button.category-toggle")
            .Single(b => b.QuerySelector(".category-label")?.TextContent == label);

    private static void Toggle(IRenderedComponent<Inventory> cut, string label) =>
        Header(cut, label).Click();
}
