using Microsoft.EntityFrameworkCore;

namespace MealPlanner.Tests;

/// <summary>
/// The normalization and diff contract AGENTS.md calls the trust boundary:
/// EF doesn't enforce [MaxLength] and SQLite ignores TEXT lengths, so if the
/// service stops clamping, nothing else will.
/// </summary>
public class InventoryServiceTests
{
    [Fact]
    public async Task Upsert_creates_a_new_item()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var change = await harness.Service.UpsertAsync("Paprika", "1 jar", IngredientCategory.DrySeasonings);

        Assert.Equal(ChangeKind.Created, change.Kind);
        Assert.Equal("Paprika", change.Name);
        Assert.Null(change.Before);
        Assert.Equal("1 jar", change.After);
        Assert.Equal(IngredientCategory.DrySeasonings, change.Category);
        Assert.Equal(1, await harness.CountAsync());
    }

    [Fact]
    public async Task Upsert_without_category_defaults_new_items_to_Other()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var change = await harness.Service.UpsertAsync("Mystery jar");

        Assert.Equal(IngredientCategory.Other, change.Category);
    }

    [Fact]
    public async Task Upsert_leaves_quantity_empty_when_unspecified()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        // Empty quantity is a first-class state: "we have some, nobody measured".
        var change = await harness.Service.UpsertAsync("Olive oil");

        Assert.Equal("", change.After);
        var stored = await harness.Service.FindAsync("Olive oil");
        Assert.Equal("", stored!.Quantity);
    }

    [Fact]
    public async Task Upsert_updates_an_existing_item_and_reports_the_old_quantity()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        var change = await harness.Service.UpsertAsync("Rice", "half a bag");

        Assert.Equal(ChangeKind.Updated, change.Kind);
        Assert.Equal("2 bags", change.Before);
        Assert.Equal("half a bag", change.After);
        Assert.Equal(1, await harness.CountAsync());
    }

    [Fact]
    public async Task Upsert_with_identical_values_reports_Unchanged()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains, "long grain");

        var change = await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains, "long grain");

        Assert.Equal(ChangeKind.Unchanged, change.Kind);
    }

    [Fact]
    public async Task Upsert_keeps_category_and_notes_when_they_are_not_supplied()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Basil", "1 pot", IngredientCategory.FreshHerbs, "windowsill");

        await harness.Service.UpsertAsync("Basil", "2 pots");

        var stored = await harness.Service.FindAsync("Basil");
        Assert.Equal(IngredientCategory.FreshHerbs, stored!.Category);
        Assert.Equal("windowsill", stored.Notes);
        Assert.Equal("2 pots", stored.Quantity);
    }

    [Fact]
    public async Task Upsert_with_empty_notes_clears_them()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Basil", "1 pot", IngredientCategory.FreshHerbs, "windowsill");

        // Empty string is a supplied value, unlike null — it means "clear this".
        await harness.Service.UpsertAsync("Basil", "1 pot", notes: "");

        var stored = await harness.Service.FindAsync("Basil");
        Assert.Equal("", stored!.Notes);
    }

    [Fact]
    public async Task Upsert_bumps_UpdatedAt()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags");
        var first = (await harness.Service.FindAsync("Rice"))!.UpdatedAt;

        await Task.Delay(5);
        await harness.Service.UpsertAsync("Rice", "1 bag");

        var second = (await harness.Service.FindAsync("Rice"))!.UpdatedAt;
        Assert.True(second > first, $"UpdatedAt did not advance: {first:O} -> {second:O}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Upsert_rejects_an_empty_name(string name)
    {
        await using var harness = await InventoryHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.UpsertAsync(name));
    }

    [Fact]
    public async Task Upsert_trims_surrounding_whitespace()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var change = await harness.Service.UpsertAsync("  Cumin  ", "  1 jar  ");

        Assert.Equal("Cumin", change.Name);
        Assert.Equal("1 jar", change.After);
    }

    [Fact]
    public async Task Upsert_clamps_an_overlong_name_to_100_characters()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        // Land a space on the boundary so the trailing-trim after the cut shows.
        var name = new string('a', 99) + " " + new string('b', 20);

        var change = await harness.Service.UpsertAsync(name);

        Assert.Equal(new string('a', 99), change.Name);
    }

    [Fact]
    public async Task Upsert_clamps_an_overlong_quantity_to_50_characters()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var change = await harness.Service.UpsertAsync("Flour", new string('q', 80));

        Assert.Equal(50, change.After!.Length);
    }

    [Fact]
    public async Task Upsert_clamps_overlong_notes_to_500_characters()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        await harness.Service.UpsertAsync("Flour", "1 bag", notes: new string('n', 900));

        var stored = await harness.Service.FindAsync("Flour");
        Assert.Equal(500, stored!.Notes!.Length);
    }

    [Fact]
    public async Task Names_are_case_insensitive_so_Salt_and_salt_are_one_row()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Salt", "1 box", IngredientCategory.DrySeasonings);

        var change = await harness.Service.UpsertAsync("salt", "2 boxes");

        Assert.Equal(ChangeKind.Updated, change.Kind);
        Assert.Equal(1, await harness.CountAsync());
        // The stored casing is the one that got there first.
        Assert.Equal("Salt", change.Name);
    }

    [Fact]
    public async Task Find_matches_case_insensitively()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Cheddar", "a block", IngredientCategory.Dairy);

        Assert.NotNull(await harness.Service.FindAsync("CHEDDAR"));
        Assert.Null(await harness.Service.FindAsync("Gouda"));
    }

    [Fact]
    public async Task GetAll_orders_by_the_stored_category_string_then_name()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Thyme", "1 bunch", IngredientCategory.FreshHerbs);
        await harness.Service.UpsertAsync("Apples", "4", IngredientCategory.Produce);
        await harness.Service.UpsertAsync("Basil", "1 pot", IngredientCategory.FreshHerbs);

        var all = await harness.Service.GetAllAsync();

        // Category is persisted as text, so OrderBy(i => i.Category) sorts in
        // SQL by the *string* — "FreshHerbs" before "Produce" — not by the enum
        // ordinal, where Produce comes first. Inventory.razor regroups by the
        // enum in memory so the UI is unaffected, but the MCP list_inventory
        // output is in this order. Pinned so the difference stays deliberate.
        Assert.Equal(["Basil", "Thyme", "Apples"], all.Select(i => i.Name).ToArray());
    }

    [Fact]
    public async Task GetAll_orders_by_name_within_a_category()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Thyme", "1 bunch", IngredientCategory.FreshHerbs);
        await harness.Service.UpsertAsync("Basil", "1 pot", IngredientCategory.FreshHerbs);
        await harness.Service.UpsertAsync("Chives", "1 bunch", IngredientCategory.FreshHerbs);

        var all = await harness.Service.GetAllAsync();

        Assert.Equal(["Basil", "Chives", "Thyme"], all.Select(i => i.Name).ToArray());
    }

    [Fact]
    public async Task Remove_deletes_the_item_and_reports_the_old_quantity()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Butter", "half a block", IngredientCategory.Dairy);

        var change = await harness.Service.RemoveAsync("butter");

        Assert.Equal(ChangeKind.Removed, change.Kind);
        Assert.Equal("half a block", change.Before);
        Assert.Null(change.After);
        Assert.Equal(IngredientCategory.Dairy, change.Category);
        Assert.Equal(0, await harness.CountAsync());
    }

    [Fact]
    public async Task Remove_reports_NotFound_for_an_item_that_was_never_there()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var change = await harness.Service.RemoveAsync("Saffron");

        Assert.Equal(ChangeKind.NotFound, change.Kind);
    }

    [Fact]
    public async Task Remove_rejects_an_empty_name()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RemoveAsync("  "));
    }

    [Fact]
    public async Task Delete_by_id_removes_the_row()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Eggs", "6", IngredientCategory.Dairy);
        var item = await harness.Service.FindAsync("Eggs");

        await harness.Service.DeleteAsync(item!.Id);

        Assert.Equal(0, await harness.CountAsync());
    }

    [Fact]
    public async Task Delete_by_unknown_id_is_a_no_op()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var seen = new List<InventoryChange>();
        harness.Notifier.Subscribe(change =>
        {
            seen.Add(change);
            return Task.CompletedTask;
        });

        await harness.Service.DeleteAsync(4242);

        Assert.Empty(seen);
    }

    [Fact]
    public async Task Save_creates_an_item_supplied_by_the_UI()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var item = new InventoryItem
        {
            Name = "  Oats  ",
            Quantity = "  1 bag  ",
            Category = IngredientCategory.Grains,
        };

        await harness.Service.SaveAsync(item);

        var stored = await harness.Service.FindAsync("Oats");
        Assert.NotNull(stored);
        Assert.Equal("1 bag", stored.Quantity);
    }

    [Fact]
    public async Task Save_updates_an_existing_item_by_id()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Oats", "1 bag", IngredientCategory.Grains);
        var item = (await harness.Service.FindAsync("Oats"))!;
        item.Quantity = "empty";

        await harness.Service.SaveAsync(item);

        var stored = await harness.Service.FindAsync("Oats");
        Assert.Equal("empty", stored!.Quantity);
        Assert.Equal(1, await harness.CountAsync());
    }

    [Fact]
    public async Task Save_clamps_the_same_fields_Upsert_does()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var item = new InventoryItem
        {
            Name = new string('a', 140),
            Quantity = new string('q', 80),
            Notes = new string('n', 900),
        };

        await harness.Service.SaveAsync(item);

        await using var db = await harness.Factory.CreateDbContextAsync();
        var stored = await db.InventoryItems.SingleAsync();
        Assert.Equal(100, stored.Name.Length);
        Assert.Equal(50, stored.Quantity.Length);
        Assert.Equal(500, stored.Notes!.Length);
    }

    [Fact]
    public async Task Save_reports_the_previous_quantity_in_its_diff()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Oats", "1 bag", IngredientCategory.Grains);
        var item = (await harness.Service.FindAsync("Oats"))!;
        // The caller edits the instance before handing it over, so "1 bag"
        // survives only in the database — issue #2's whole point.
        item.Quantity = "empty";

        var seen = Watch(harness);
        await harness.Service.SaveAsync(item);

        var change = Assert.Single(seen);
        Assert.Equal(ChangeKind.Updated, change.Kind);
        Assert.Equal("1 bag", change.Before);
        Assert.Equal("empty", change.After);
        Assert.Equal("\"Oats\": 1 bag → empty", change.Describe());
    }

    [Fact]
    public async Task Save_reports_a_previously_empty_quantity_as_unspecified()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Oats", "", IngredientCategory.Grains);
        var item = (await harness.Service.FindAsync("Oats"))!;
        item.Quantity = "1 bag";

        var seen = Watch(harness);
        await harness.Service.SaveAsync(item);

        // Genuinely-empty and "we didn't look" both used to render the same;
        // now only the former can reach Describe().
        Assert.Equal("", Assert.Single(seen).Before);
        Assert.Equal("\"Oats\": unspecified → 1 bag", seen[0].Describe());
    }

    [Fact]
    public async Task Save_of_a_new_item_has_no_previous_quantity()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        var item = new InventoryItem
        {
            Name = "Oats",
            Quantity = "1 bag",
            Category = IngredientCategory.Grains,
        };

        var seen = Watch(harness);
        await harness.Service.SaveAsync(item);

        var change = Assert.Single(seen);
        Assert.Equal(ChangeKind.Created, change.Kind);
        Assert.Null(change.Before);
    }

    [Fact]
    public async Task SetCategory_moves_the_item_without_touching_quantity_or_notes()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Coriander", "1 bunch", notes: "windowsill");

        await harness.Service.SetCategoryAsync("coriander", IngredientCategory.FreshHerbs);

        var stored = await harness.Service.FindAsync("Coriander");
        Assert.Equal(IngredientCategory.FreshHerbs, stored!.Category);
        // The whole reason this isn't an UpsertAsync call: quantity survives.
        Assert.Equal("1 bunch", stored.Quantity);
        Assert.Equal("windowsill", stored.Notes);
    }

    [Fact]
    public async Task SetCategory_describes_the_category_change_rather_than_the_quantity()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Coriander", "1 bunch");

        var seen = Watch(harness);
        var change = await harness.Service.SetCategoryAsync("Coriander", IngredientCategory.FreshHerbs);

        Assert.Equal(ChangeKind.Updated, change.Kind);
        // Before and After are quantities and are identical here, so without
        // PreviousCategory the diff would read "1 bunch → 1 bunch".
        Assert.Equal("\"Coriander\": Other → FreshHerbs", change.Describe());
        Assert.Equal("\"Coriander\": Other → FreshHerbs", Assert.Single(seen).Describe());
    }

    [Fact]
    public async Task SetCategory_with_onlyIf_leaves_a_hand_picked_category_alone()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Coriander", "1 bunch");
        // Someone classified it by hand while the classifier was still thinking.
        await harness.Service.SetCategoryAsync("Coriander", IngredientCategory.DrySeasonings);

        var seen = Watch(harness);
        var change = await harness.Service.SetCategoryAsync(
            "Coriander", IngredientCategory.FreshHerbs, onlyIf: IngredientCategory.Other);

        Assert.Equal(ChangeKind.Unchanged, change.Kind);
        Assert.Empty(seen);
        var stored = await harness.Service.FindAsync("Coriander");
        Assert.Equal(IngredientCategory.DrySeasonings, stored!.Category);
    }

    [Fact]
    public async Task SetCategory_to_the_category_it_already_has_publishes_nothing()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Basil", "1 pot", IngredientCategory.FreshHerbs);

        var seen = Watch(harness);
        var change = await harness.Service.SetCategoryAsync("Basil", IngredientCategory.FreshHerbs);

        Assert.Equal(ChangeKind.Unchanged, change.Kind);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task SetCategory_reports_NotFound_for_an_item_that_is_gone()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        // The categorizer writes seconds after the create, so the row really can
        // have been deleted in between.
        var change = await harness.Service.SetCategoryAsync("Saffron", IngredientCategory.DrySeasonings);

        Assert.Equal(ChangeKind.NotFound, change.Kind);
    }

    [Fact]
    public async Task SetCategory_rejects_an_empty_name()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.SetCategoryAsync("  ", IngredientCategory.Produce));
    }

    private static List<InventoryChange> Watch(InventoryHarness harness)
    {
        var seen = new List<InventoryChange>();
        harness.Notifier.Subscribe(change =>
        {
            seen.Add(change);
            return Task.CompletedTask;
        });
        return seen;
    }
}
