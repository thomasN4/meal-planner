using MealPlanner.Mcp;

namespace MealPlanner.Tests;

/// <summary>
/// The MCP surface Claude talks to. Its job is to be a thin, forgiving wrapper
/// over <see cref="InventoryService"/>: parse loosely, never throw at the
/// transport, and hand back a sentence a model can act on. An escaping
/// exception here becomes an opaque tool error on the other end.
/// </summary>
public class InventoryToolsTests
{
    [Fact]
    public async Task List_says_so_when_the_inventory_is_empty()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var result = await InventoryTools.ListInventory(harness.Service);

        Assert.Equal("Inventory is empty.", result);
    }

    [Fact]
    public async Task List_reports_a_count_and_one_line_per_item()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);
        await harness.Service.UpsertAsync("Basil", "1 pot", IngredientCategory.FreshHerbs, "windowsill");

        var result = await InventoryTools.ListInventory(harness.Service);

        Assert.StartsWith("count: 2", result);
        Assert.Contains("- Rice: quantity=2 bags; category=Grains;", result);
        Assert.Contains("notes=windowsill", result);
    }

    [Fact]
    public async Task List_spells_out_an_unspecified_quantity()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Olive oil");

        var result = await InventoryTools.ListInventory(harness.Service);

        // An empty string here would read to Claude as "no quantity field".
        Assert.Contains("quantity=(unspecified)", result);
    }

    [Fact]
    public async Task List_filters_by_category_case_insensitively()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);
        await harness.Service.UpsertAsync("Basil", "1 pot", IngredientCategory.FreshHerbs);

        var result = await InventoryTools.ListInventory(harness.Service, "freshherbs");

        Assert.Contains("Basil", result);
        Assert.DoesNotContain("Rice", result);
    }

    [Fact]
    public async Task List_of_an_empty_category_says_which_category_was_empty()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        var result = await InventoryTools.ListInventory(harness.Service, "Dairy");

        Assert.Equal("No inventory items in category Dairy.", result);
    }

    [Fact]
    public async Task List_ignores_a_blank_category_filter()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        var result = await InventoryTools.ListInventory(harness.Service, "   ");

        Assert.Contains("Rice", result);
    }

    [Theory]
    [InlineData("Vegetables")]
    // Enum.TryParse happily accepts numeric text, so the Enum.IsDefined guard
    // is the only thing standing between Claude and an out-of-range category.
    [InlineData("999")]
    [InlineData("-1")]
    public async Task An_unknown_category_returns_the_valid_values_instead_of_throwing(string category)
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var result = await InventoryTools.ListInventory(harness.Service, category);

        Assert.StartsWith($"Unknown category \"{category}\".", result);
        Assert.Contains("FreshHerbs", result);
    }

    [Fact]
    public async Task Upsert_returns_a_diff_and_writes_through_the_service()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var result = await InventoryTools.UpsertItem(harness.Service, "Rice", "2 bags", "grains");

        Assert.Equal("Added \"Rice\" (2 bags) to Grains", result);
        Assert.Equal(IngredientCategory.Grains, (await harness.Service.FindAsync("Rice"))!.Category);
    }

    [Fact]
    public async Task Upsert_treats_an_omitted_quantity_as_unspecified()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var result = await InventoryTools.UpsertItem(harness.Service, "Olive oil");

        Assert.Equal("Added \"Olive oil\" to Other", result);
    }

    [Fact]
    public async Task Upsert_with_an_unknown_category_writes_nothing()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var result = await InventoryTools.UpsertItem(harness.Service, "Rice", "2 bags", "Vegetables");

        Assert.StartsWith("Unknown category", result);
        Assert.Equal(0, await harness.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Upsert_turns_an_empty_name_into_a_message_rather_than_an_exception(string name)
    {
        await using var harness = await InventoryHarness.CreateAsync();

        // The UI guards before calling; MCP callers cannot, so the tool must
        // catch ArgumentException and answer in words.
        var result = await InventoryTools.UpsertItem(harness.Service, name, "2 bags");

        Assert.Contains("must not be empty", result);
        Assert.Equal(0, await harness.CountAsync());
    }

    [Fact]
    public async Task Upsert_turns_a_failed_write_into_a_message_rather_than_an_exception()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        // Read-only before the first connection opens: reads work, the write
        // fails. Stands in for the service exhausting its retry budget, which
        // is the other way a DbUpdateException reaches this catch.
        File.SetAttributes(harness.DatabasePath, FileAttributes.ReadOnly);

        try
        {
            var result = await InventoryTools.UpsertItem(harness.Service, "Rice", "2 bags");

            // An exception here would reach Claude as an opaque transport
            // error; a sentence is something it can act on.
            Assert.Contains("Could not save", result);
            Assert.Contains("Rice", result);
        }
        finally
        {
            File.SetAttributes(harness.DatabasePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Remove_returns_a_diff()
    {
        await using var harness = await InventoryHarness.CreateAsync();
        await harness.Service.UpsertAsync("Rice", "2 bags", IngredientCategory.Grains);

        var result = await InventoryTools.RemoveItem(harness.Service, "rice");

        Assert.Equal("Removed \"Rice\"", result);
        Assert.Equal(0, await harness.CountAsync());
    }

    [Fact]
    public async Task Remove_of_a_missing_item_says_not_found()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var result = await InventoryTools.RemoveItem(harness.Service, "Saffron");

        Assert.Equal("\"Saffron\" not found", result);
    }

    [Fact]
    public async Task Remove_turns_an_empty_name_into_a_message_rather_than_an_exception()
    {
        await using var harness = await InventoryHarness.CreateAsync();

        var result = await InventoryTools.RemoveItem(harness.Service, "  ");

        Assert.Contains("must not be empty", result);
    }
}
