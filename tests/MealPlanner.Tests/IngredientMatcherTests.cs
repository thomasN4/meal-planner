namespace MealPlanner.Tests;

/// <summary>
/// <see cref="IngredientMatcher"/> is pure, so it is tested the same way
/// <see cref="ClassifierParsingTests"/> tests parsing: no harness, no database,
/// no page. What the inventory page does with these results is covered by
/// <see cref="InventoryPageTests"/>.
/// </summary>
public class IngredientMatcherTests
{
    private static InventoryItem Item(
        string name,
        IngredientCategory category = IngredientCategory.Other,
        string quantity = "") =>
        new() { Name = name, Category = category, Quantity = quantity };

    private static string[] Names(IReadOnlyList<InventoryItem> items) =>
        items.Select(i => i.Name).ToArray();

    [Fact]
    public void Prefix_beats_substring_beats_typo()
    {
        List<InventoryItem> items = [Item("Malt"), Item("Sea salt"), Item("Salted butter")];

        // "Salted butter" starts with it, "Sea salt" only contains it, "Malt"
        // is two edits away — the ranking is the whole point of the ordering.
        Assert.Equal(
            ["Salted butter", "Sea salt", "Malt"],
            Names(IngredientMatcher.Suggest(items, "salt")));
    }

    [Fact]
    public void An_exact_match_is_not_offered_as_a_suggestion()
    {
        // The form names the exact match in its hint instead. Offering it here
        // too would put a row under the arrow keys that turns Enter from
        // "add this" into "fill the form with what I already typed".
        List<InventoryItem> items = [Item("Salt"), Item("Salted butter")];

        Assert.Equal(["Salted butter"], Names(IngredientMatcher.Suggest(items, "salt")));
    }

    [Fact]
    public void Matching_ignores_case_in_both_directions()
    {
        List<InventoryItem> items = [Item("coriander")];

        Assert.NotNull(IngredientMatcher.ExactMatch(items, "CORIANDER"));
        Assert.Equal(["coriander"], Names(IngredientMatcher.Suggest(items, "CORIAN")));

        List<InventoryItem> upper = [Item("CORIANDER")];
        Assert.NotNull(IngredientMatcher.ExactMatch(upper, "coriander"));
        Assert.Equal(["CORIANDER"], Names(IngredientMatcher.Suggest(upper, "corian")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_query_matches_nothing(string? query)
    {
        List<InventoryItem> items = [Item("Salt")];

        Assert.Empty(IngredientMatcher.Suggest(items, query));
        Assert.Null(IngredientMatcher.ExactMatch(items, query));
    }

    [Fact]
    public void A_query_is_trimmed_before_matching()
    {
        // The name box is free text and the service trims too — a trailing
        // space must not be the difference between updating a row and
        // creating one.
        List<InventoryItem> items = [Item("Salt")];

        Assert.Equal("Salt", IngredientMatcher.ExactMatch(items, "  salt  ")?.Name);
    }

    [Fact]
    public void Typos_are_tolerated_only_once_a_query_is_long_enough()
    {
        List<InventoryItem> items = [Item("Coriander"), Item("Oat")];

        // Nine characters, one edit out.
        Assert.Equal(["Coriander"], Names(IngredientMatcher.Suggest(items, "corainder")));

        // Three characters, also one edit out — but at that length nearly
        // everything in a kitchen is one edit from everything else.
        Assert.Empty(IngredientMatcher.Suggest(items, "oad"));
    }

    [Fact]
    public void A_transposition_costs_one_edit_not_two()
    {
        // Seven characters, so the budget is 1. Plain Levenshtein scores a
        // swap as 2 and would drop this; swapped letters are exactly what a
        // keyboard produces.
        List<InventoryItem> items = [Item("Parlsey")];

        Assert.Equal(["Parlsey"], Names(IngredientMatcher.Suggest(items, "Parsley")));
        Assert.Equal(1, IngredientMatcher.Distance("Parsley", "Parlsey", 2));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(40, 2)]
    public void The_typo_budget_grows_with_the_query_length(int length, int expected) =>
        Assert.Equal(expected, IngredientMatcher.MaxDistanceFor(length));

    [Fact]
    public void At_most_five_suggestions_come_back_and_they_are_the_best_ranked()
    {
        List<InventoryItem> items =
        [
            Item("Cod"), Item("Coffee"), Item("Coconut"), Item("Coriander"),
            Item("Corn"), Item("Cocoa"), Item("Chicory"), Item("Tabasco"),
        ];

        var suggestions = IngredientMatcher.Suggest(items, "co");

        Assert.Equal(IngredientMatcher.MaxSuggestions, suggestions.Count);
        // Tabasco only contains "co", so the six prefix hits crowd it out.
        Assert.DoesNotContain("Tabasco", Names(suggestions));
    }

    [Fact]
    public void Ties_break_by_length_then_name()
    {
        List<InventoryItem> items = [Item("Salt flakes"), Item("Salt"), Item("Salsa"), Item("Salami")];

        // All four start with "sal": shortest first, and "Salsa" before
        // "Salami"... which is longer, so length wins before the alphabet.
        Assert.Equal(
            ["Salt", "Salsa", "Salami", "Salt flakes"],
            Names(IngredientMatcher.Suggest(items, "sal")));
    }

    [Fact]
    public void Equal_length_ties_break_alphabetically()
    {
        List<InventoryItem> items = [Item("Salt"), Item("Sage")];

        Assert.Equal(["Sage", "Salt"], Names(IngredientMatcher.Suggest(items, "sa")));
    }

    [Fact]
    public void Nothing_matches_before_the_page_has_loaded_its_items()
    {
        // The page renders once before OnInitializedAsync completes, with
        // items still null.
        Assert.Null(IngredientMatcher.ExactMatch(null, "Salt"));
        Assert.Empty(IngredientMatcher.Suggest(null, "Salt"));
    }

    [Fact]
    public void Absurd_lengths_do_not_throw()
    {
        // Names clamp to 100 characters in InventoryService; a query comes
        // straight off a LAN-facing text box and is not clamped at all. The
        // edit-distance table is skipped past its bound, but prefix and
        // substring matching still work.
        var longName = new string('a', 100);
        List<InventoryItem> items = [Item(longName)];

        Assert.Equal([longName], Names(IngredientMatcher.Suggest(items, new string('a', 60))));
        Assert.Empty(IngredientMatcher.Suggest(items, new string('b', 200)));
    }
}
