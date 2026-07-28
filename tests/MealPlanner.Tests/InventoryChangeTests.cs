namespace MealPlanner.Tests;

/// <summary>
/// <see cref="InventoryChange.Describe"/> is what the UI shows a household
/// member and what an MCP tool call returns to Claude, so its wording is a
/// contract rather than a detail.
/// </summary>
public class InventoryChangeTests
{
    private static string Describe(ChangeKind kind, string? before, string? after) =>
        new InventoryChange("Rice", kind, before, after, IngredientCategory.Grains).Describe();

    [Fact]
    public void Created_with_a_quantity_names_the_category()
    {
        Assert.Equal("Added \"Rice\" (2 bags) to Grains", Describe(ChangeKind.Created, null, "2 bags"));
    }

    [Fact]
    public void Created_without_a_quantity_omits_the_parenthetical()
    {
        Assert.Equal("Added \"Rice\" to Grains", Describe(ChangeKind.Created, null, ""));
    }

    [Fact]
    public void Updated_shows_both_sides_of_the_change()
    {
        Assert.Equal("\"Rice\": 2 bags → 1 bag", Describe(ChangeKind.Updated, "2 bags", "1 bag"));
    }

    [Fact]
    public void An_empty_quantity_reads_as_unspecified_rather_than_blank()
    {
        // Free-text quantity means empty is common; it must not render as "".
        Assert.Equal("\"Rice\": unspecified → 1 bag", Describe(ChangeKind.Updated, "", "1 bag"));
        Assert.Equal("\"Rice\" unchanged (unspecified)", Describe(ChangeKind.Unchanged, "", ""));
    }

    [Fact]
    public void Removed_and_NotFound_read_plainly()
    {
        Assert.Equal("Removed \"Rice\"", Describe(ChangeKind.Removed, "2 bags", null));
        Assert.Equal("\"Rice\" not found", Describe(ChangeKind.NotFound, null, null));
    }
}
