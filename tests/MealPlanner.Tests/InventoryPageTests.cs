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
    public async Task Adding_a_name_that_differs_only_in_case_updates_the_existing_row_in_place()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Salt", "1 box", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        // This test used to assert the row moved to Other, and called that
        // intentional. It was not: UpsertAsync always writes the quantity it is
        // handed, so typing "salt" over what Claude had stocked as
        // "Salt / 1 box / Dry Seasonings" both retagged the row and erased
        // "1 box", with nothing to warn the user but the row jumping groups.
        // What the old test really proved is that the NOCASE unique index keeps
        // this to one row — which CountAsync still proves below.
        cut.Find("#new-name").Input("salt");

        // The form now says what it is about to overwrite, before it does.
        Assert.Equal(
            nameof(IngredientCategory.DrySeasonings),
            cut.Find("#new-category").GetAttribute("value"));
        Assert.Equal("1 box", cut.Find("#new-quantity").GetAttribute("value"));

        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", Header(cut, "Dry Seasonings").GetAttribute("aria-expanded"));
            Assert.Contains("Salt", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Equal(1, await page.CountAsync());

        // Re-categorising on purpose still works — change the select, which
        // marks the field touched and switches autofill off for it.
        var stored = await page.Service.FindAsync("Salt");
        Assert.Equal("1 box", stored?.Quantity);
        Assert.Equal(IngredientCategory.DrySeasonings, stored?.Category);
    }

    [Fact]
    public async Task An_exact_name_fills_in_the_existing_category_and_quantity()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Salt", "1 box", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("salt");

        Assert.Equal(
            nameof(IngredientCategory.DrySeasonings),
            cut.Find("#new-category").GetAttribute("value"));
        Assert.Equal("1 box", cut.Find("#new-quantity").GetAttribute("value"));
        // And says so in words, so the fill is not something the user has to
        // notice out of the corner of their eye.
        Assert.Contains("Salt", cut.Find("div.name-hint").TextContent, StringComparison.Ordinal);
        Assert.Equal("Update", cut.Find("button.btn-primary").TextContent.Trim());
    }

    [Fact]
    public async Task A_near_miss_only_suggests_and_never_fills()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Coriander", "1 bunch", IngredientCategory.FreshHerbs);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("corian");

        // The pin for the whole design: a partial name offers the row but must
        // not adopt its quantity. Typing "Salsa" past "Sal" would otherwise
        // inherit whatever "Salt" happens to hold.
        Assert.Contains(
            "Coriander",
            cut.Find("li[role=option]").TextContent,
            StringComparison.Ordinal);
        Assert.Equal(
            nameof(IngredientCategory.Other),
            cut.Find("#new-category").GetAttribute("value"));
        Assert.Equal("", cut.Find("#new-quantity").GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task Clicking_a_suggestion_fills_the_form()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Coriander", "1 bunch", IngredientCategory.FreshHerbs);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("corian");
        cut.Find("li[role=option]").Click();

        Assert.Equal("Coriander", cut.Find("#new-name").GetAttribute("value"));
        Assert.Equal(
            nameof(IngredientCategory.FreshHerbs),
            cut.Find("#new-category").GetAttribute("value"));
        Assert.Equal("1 bunch", cut.Find("#new-quantity").GetAttribute("value"));
        Assert.Empty(cut.FindAll("li[role=option]"));

        // What this does NOT cover: bUnit has no focus model, so it dispatches
        // the click without the blur that a browser fires first. Deleting
        // @onmousedown:preventDefault leaves this test green while making the
        // list unclickable for real. That directive is browser-verified only —
        // same honesty as Adding_reports_what_it_did_in_the_live_region.
    }

    [Fact]
    public async Task Arrow_down_highlights_a_suggestion_and_enter_accepts_it()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Cod", "2 fillets", IngredientCategory.MeatAndSeafood);
        await page.Service.UpsertAsync("Coffee", "1 bag", IngredientCategory.Beverages);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("co");
        cut.Find("#new-name").KeyDown(Key.Down);

        var first = cut.FindAll("li[role=option]")[0];
        Assert.Equal("true", first.GetAttribute("aria-selected"));
        Assert.Equal(first.GetAttribute("id"), cut.Find("#new-name").GetAttribute("aria-activedescendant"));

        cut.Find("#new-name").KeyDown(Key.Enter);

        // Enter accepted the highlighted row rather than adding "co" as a new
        // ingredient — the shortest name sorts first, so this is "Cod".
        Assert.Equal("Cod", cut.Find("#new-name").GetAttribute("value"));
        Assert.Equal("2 fillets", cut.Find("#new-quantity").GetAttribute("value"));
        Assert.Equal(2, await page.CountAsync());
    }

    [Fact]
    public async Task Enter_still_adds_when_nothing_is_highlighted()
    {
        await using var page = await PageHarness.CreateAsync();
        // Seeded so that typing the name below leaves the suggestion list open
        // with nothing arrowed onto. An empty inventory would make this test
        // pass no matter what Enter does, because there would be no suggestion
        // for it to wrongly accept.
        await page.Service.UpsertAsync("Coriander seeds", "1 jar", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("Coriander");
        Assert.NotEmpty(cut.FindAll("li[role=option]"));

        // The muscle memory the page had before suggestions existed: an open
        // list is not a selected one, so Enter still adds what was typed.
        cut.Find("#new-name").KeyDown(Key.Enter);

        cut.WaitForAssertion(() =>
            Assert.Contains(
                "Added \"Coriander\"",
                cut.Find("div[role=status]").TextContent,
                StringComparison.Ordinal));
        Assert.Equal(2, await page.CountAsync());
    }

    [Fact]
    public async Task Escape_closes_the_suggestion_list()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Coriander", "1 bunch", IngredientCategory.FreshHerbs);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("corian");
        Assert.NotEmpty(cut.FindAll("li[role=option]"));

        cut.Find("#new-name").KeyDown(Key.Escape);

        Assert.Empty(cut.FindAll("li[role=option]"));
        Assert.Equal("false", cut.Find("#new-name").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task A_quantity_the_user_typed_survives_an_exact_match()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Salt", "1 box", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("sal");
        cut.Find("#new-quantity").Change("2 boxes");
        cut.Find("#new-name").Input("salt");

        // The whole point of the feature is updating the quantity, so autofill
        // must never overwrite one the user has already typed.
        Assert.Equal("2 boxes", cut.Find("#new-quantity").GetAttribute("value"));
        // The category was left alone, so it still fills.
        Assert.Equal(
            nameof(IngredientCategory.DrySeasonings),
            cut.Find("#new-category").GetAttribute("value"));

        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("2 boxes", cut.Markup, StringComparison.Ordinal));
        var stored = await page.Service.FindAsync("Salt");
        Assert.Equal("2 boxes", stored?.Quantity);
        Assert.Equal(IngredientCategory.DrySeasonings, stored?.Category);
    }

    [Fact]
    public async Task The_hint_shows_the_change_it_is_about_to_write_not_just_the_stored_row()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("black pepper", "infinite", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("black pepper");
        Assert.Contains("infinite", cut.Find("div.name-hint").TextContent, StringComparison.Ordinal);

        cut.Find("#new-quantity").Change("half a jar");

        // The hint is the one thing warning the user what Add is about to
        // overwrite, so it cannot keep describing the stored row once the form
        // has diverged from it — it used to read "· infinite" while Update
        // wrote "half a jar".
        var hint = cut.Find("div.name-hint").TextContent;
        Assert.Contains("infinite", hint, StringComparison.Ordinal);
        Assert.Contains("half a jar", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hint_shows_a_category_the_user_picked_by_hand()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("black pepper", "infinite", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("black pepper");
        cut.Find("#new-category").Change(nameof(IngredientCategory.Baking));

        var hint = cut.Find("div.name-hint").TextContent;
        Assert.Contains("Dry Seasonings", hint, StringComparison.Ordinal);
        Assert.Contains("Baking", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_stops_matching_takes_the_autofill_back_out()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Salt", "1 box", IngredientCategory.DrySeasonings);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("salt");
        Assert.Equal("1 box", cut.Find("#new-quantity").GetAttribute("value"));

        // Kept typing: this is a new ingredient now, and it must not carry the
        // old row's quantity into the database with it.
        cut.Find("#new-name").Input("saltz");

        Assert.Equal("", cut.Find("#new-quantity").GetAttribute("value") ?? "");
        Assert.Equal(
            nameof(IngredientCategory.Other),
            cut.Find("#new-category").GetAttribute("value"));
    }

    [Fact]
    public async Task Adding_clears_the_suggestion_state()
    {
        await using var page = await PageHarness.CreateAsync();
        await page.Service.UpsertAsync("Coriander", "1 bunch", IngredientCategory.FreshHerbs);
        var cut = page.RenderInventory();

        cut.Find("#new-name").Input("coriander");
        cut.Find("button.btn-primary").Click();

        // The hint has to go with the rest of the form. Left behind, it would
        // claim the empty box is still editing a row.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", cut.Find("div.name-hint").TextContent.Trim());
            Assert.Empty(cut.FindAll("li[role=option]"));
            Assert.Equal("", cut.Find("#new-name").GetAttribute("value") ?? "");
            Assert.Equal("", cut.Find("#new-quantity").GetAttribute("value") ?? "");
            Assert.Equal("Add", cut.Find("button.btn-primary").TextContent.Trim());
        });
    }

    [Fact]
    public async Task A_closed_combobox_still_announces_that_it_is_closed()
    {
        await using var page = await PageHarness.CreateAsync();
        var cut = page.RenderInventory();

        // Same bug the accordion already paid for: Blazor drops a bool
        // attribute that is false, so aria-expanded has to be a string or the
        // closed state goes unannounced.
        Assert.Equal("false", cut.Find("#new-name").GetAttribute("aria-expanded"));
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
