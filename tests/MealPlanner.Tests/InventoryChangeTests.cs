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

    [Fact]
    public void A_rename_names_both_spellings()
    {
        var change = new InventoryChange("Rice", ChangeKind.Updated, "2 bags", "2 bags", IngredientCategory.Grains)
        {
            PreviousName = "rise",
        };

        Assert.Equal("Renamed \"rise\" to \"Rice\"", change.Describe());
    }

    [Fact]
    public void A_rename_that_also_changed_a_quantity_still_leads_with_the_rename()
    {
        // One headline per change, the same way the category branches pick one
        // axis. The row is in front of the user showing the rest.
        var change = new InventoryChange("Rice", ChangeKind.Updated, "2 bags", "1 bag", IngredientCategory.Grains)
        {
            PreviousName = "rise",
            PreviousCategory = IngredientCategory.Other,
        };

        Assert.Equal("Renamed \"rise\" to \"Rice\"", change.Describe());
    }

    [Fact]
    public void A_category_move_that_also_changed_the_quantity_reports_both()
    {
        // SetCategoryAsync passes the same quantity on both sides and reads as a
        // pure move; the row editor can change both in one write, and a diff
        // that hid half of it would be the same footgun the add form's hint
        // exists to close.
        var moveOnly = new InventoryChange("Rice", ChangeKind.Updated, "2 bags", "2 bags", IngredientCategory.Grains)
        {
            PreviousCategory = IngredientCategory.Other,
        };
        var moveAndRequantify = moveOnly with { Before = "2 bags", After = "1 bag" };

        Assert.Equal("\"Rice\": Other → Grains", moveOnly.Describe());
        Assert.Equal("\"Rice\": Other → Grains, 2 bags → 1 bag", moveAndRequantify.Describe());
    }

    [Fact]
    public void A_note_only_change_says_so_instead_of_reporting_the_quantity_twice()
    {
        // Found in browser testing: a note-only save fell through to the
        // quantity branch and announced "3 bags → 3 bags", so the only feedback
        // a save gives described a no-op — and clearing a note read exactly the
        // same as writing one.
        var same = new InventoryChange("Rice", ChangeKind.Updated, "3 bags", "3 bags", IngredientCategory.Grains);

        Assert.Equal(
            "\"Rice\": note added",
            (same with { PreviousNotes = null, Notes = "top shelf" }).Describe());
        Assert.Equal(
            "\"Rice\": note updated",
            (same with { PreviousNotes = "top shelf", Notes = "back of the pantry" }).Describe());
        Assert.Equal(
            "\"Rice\": note cleared",
            (same with { PreviousNotes = "top shelf", Notes = "" }).Describe());
    }

    [Fact]
    public void A_change_that_left_the_note_alone_describes_the_quantity()
    {
        // Both null is "the note was not supplied, or did not move" — the
        // quantity is then the only honest headline.
        var change = new InventoryChange("Rice", ChangeKind.Updated, "2 bags", "3 bags", IngredientCategory.Grains)
        {
            PreviousNotes = null,
            Notes = null,
        };

        Assert.Equal("\"Rice\": 2 bags → 3 bags", change.Describe());
    }

    [Fact]
    public void A_quantity_change_still_leads_even_when_the_note_moved_too()
    {
        // One headline per change, the same convention the rename and category
        // branches follow. The note-only branch is for the case where reporting
        // the quantity would be reporting nothing at all.
        var change = new InventoryChange("Rice", ChangeKind.Updated, "2 bags", "3 bags", IngredientCategory.Grains)
        {
            PreviousNotes = "top shelf",
            Notes = "back of the pantry",
        };

        Assert.Equal("\"Rice\": 2 bags → 3 bags", change.Describe());
    }

    [Fact]
    public void NameTaken_names_the_spelling_that_was_refused()
    {
        Assert.Equal("\"Rice\" is already on the list", Describe(ChangeKind.NameTaken, null, null));
    }
}
