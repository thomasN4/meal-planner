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
    public void Accents_can_be_left_off_when_typing()
    {
        // Half this kitchen is names nobody can type on the keyboard they own.
        List<InventoryItem> items = [Item("nước chấm"), Item("dry Bánh Đa Cua noodles")];

        Assert.Equal(["nước chấm"], Names(IngredientMatcher.Suggest(items, "nuoc cham")));
        // đ is a letter with a stroke, not an accent — FormD leaves it alone,
        // so it is mapped by hand.
        Assert.Equal(
            ["dry Bánh Đa Cua noodles"],
            Names(IngredientMatcher.Suggest(items, "banh da cua")));
    }

    [Fact]
    public void An_accent_folded_name_is_only_ever_a_suggestion_never_an_exact_match()
    {
        // The safety property behind the whole split. The form autofills from
        // ExactMatch and then upserts under the name in the box — and to
        // SQLite's NOCASE index "nuoc cham" is a different row from "nước
        // chấm", so folding here would quietly create a duplicate under the
        // unaccented spelling. The suggestion list fills the box with the
        // stored name first, which is why it is allowed to be loose.
        List<InventoryItem> items = [Item("nước chấm")];

        Assert.Null(IngredientMatcher.ExactMatch(items, "nuoc cham"));
        Assert.NotEmpty(IngredientMatcher.Suggest(items, "nuoc cham"));
        // The accented spelling still matches exactly, ignoring case.
        Assert.NotNull(IngredientMatcher.ExactMatch(items, "NƯỚC CHẤM"));
    }

    [Fact]
    public void A_typo_in_one_word_finds_a_long_name()
    {
        // 13 edits from the whole name, one from a word in it. Most rows in a
        // real kitchen are long and descriptive like this.
        List<InventoryItem> items = [Item("dry spaghetti noodles")];

        Assert.Equal(["dry spaghetti noodles"], Names(IngredientMatcher.Suggest(items, "spagetti")));
    }

    [Fact]
    public void Words_are_split_on_punctuation_as_well_as_spaces()
    {
        List<InventoryItem> items = [Item("lao gan ma (peanuts)"), Item("dry-fried salmon")];

        Assert.Equal(["lao gan ma (peanuts)"], Names(IngredientMatcher.Suggest(items, "peanust")));
        Assert.Equal(["dry-fried salmon"], Names(IngredientMatcher.Suggest(items, "salmen")));
    }

    [Fact]
    public void A_word_hit_ranks_below_a_whole_name_hit_even_when_it_is_the_closer_one()
    {
        // The rank has to be what decides, so this is built to fail without it:
        // the whole-name hit is the *worse* spelling (two edits out) and the
        // word hit is one edit out. Rank first means the name still wins.
        //
        // The obvious version of this test — a short name against a long one —
        // cannot fail, because a name containing a matching word is always
        // longer than that word, so the length tie-break quietly produces the
        // right order even with the ranks collapsed.
        List<InventoryItem> items = [Item("corianzeq"), Item("dry coriandee noodles")];

        Assert.Equal(
            ["corianzeq", "dry coriandee noodles"],
            Names(IngredientMatcher.Suggest(items, "coriander")));
        Assert.Equal(["corianzeq"], Names(IngredientMatcher.Suggest(items, "coriander", limit: 1)));
    }

    [Fact]
    public void Short_queries_still_tolerate_no_typos_at_word_level_either()
    {
        // The word pass reuses the same budget, so it must not reopen the door
        // that MaxDistanceFor closed for short queries — otherwise every
        // three-letter query starts matching a word in half the kitchen.
        List<InventoryItem> items = [Item("dry roasted peanuts (unsalted)")];

        // "dey" is one edit from the word "dry", and three characters long.
        Assert.Empty(IngredientMatcher.Suggest(items, "dey"));
        // Four characters buys one edit, so this one is allowed through.
        Assert.NotEmpty(IngredientMatcher.Suggest(items, "dery"));
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

    // NearMatch. The pairs below are the receipt scanner's real output — three
    // scans of one Metro photograph produced each left-hand spelling for the
    // stocked right-hand one (issue #30) — so they are the specification, not
    // examples.

    [Fact]
    public void A_reordered_name_is_a_near_match()
    {
        List<InventoryItem> items = [Item("Mars Twix Chocolat")];

        Assert.Equal("Mars Twix Chocolat",
            IngredientMatcher.NearMatch(items, "Chocolat Mars/Twix")?.Name);
        Assert.Equal("Mars Twix Chocolat",
            IngredientMatcher.NearMatch(items, "Chocolat Mars Twix")?.Name);
    }

    [Fact]
    public void A_till_truncated_word_finds_the_stocked_spelling()
    {
        List<InventoryItem> items = [Item("Canard catégorie A")];

        Assert.Equal("Canard catégorie A",
            IngredientMatcher.NearMatch(items, "Canard cat A")?.Name);
    }

    [Fact]
    public void An_accent_only_difference_is_a_near_match()
    {
        // ExactMatch refuses accent folding on purpose — to the NOCASE index
        // "Cafe" and "Café" are two rows, which is exactly the duplicate this
        // badge exists to put in front of a person.
        List<InventoryItem> items = [Item("Café")];

        Assert.Equal("Café", IngredientMatcher.NearMatch(items, "Cafe")?.Name);
    }

    [Fact]
    public void A_name_with_an_uncovered_word_is_not_near()
    {
        // Coverage is bidirectional: "basmati" has no counterpart in "Riz", so
        // the claim fails. A scanned "Riz" may well be the stocked basmati —
        // but it may not, and a wrong "Looks like" invites a wrong adopt.
        List<InventoryItem> items = [Item("Riz basmati"), Item("Lait d'amande")];

        Assert.Null(IngredientMatcher.NearMatch(items, "Riz"));
        Assert.Null(IngredientMatcher.NearMatch(items, "Lait"));
    }

    [Fact]
    public void Sharing_a_first_word_is_not_enough()
    {
        List<InventoryItem> items = [Item("Sauce soja")];

        Assert.Null(IngredientMatcher.NearMatch(items, "Sauce tomate"));
    }

    [Fact]
    public void Diverging_inside_a_word_is_not_a_prefix()
    {
        // "peas" is not a prefix of "peanut" — they part at the fourth letter —
        // and at four characters the typo budget is one edit, not the two this
        // needs. The substring scan AGENTS.md warns about would say yes here.
        List<InventoryItem> items = [Item("Peanut")];

        Assert.Null(IngredientMatcher.NearMatch(items, "Peas"));
    }

    [Fact]
    public void A_two_letter_prefix_does_not_match()
    {
        // Two letters would let "de" claim half the French in this kitchen.
        List<InventoryItem> items = [Item("Depuis toujours")];

        Assert.Null(IngredientMatcher.NearMatch(items, "De toujours"));
    }

    [Fact]
    public void An_exact_match_is_never_offered_as_near()
    {
        // The badge ladder asks ExactMatch first; answering the same row here
        // would put "Looks like Salt" on a line that *is* Salt.
        List<InventoryItem> items = [Item("Salt")];

        Assert.Null(IngredientMatcher.NearMatch(items, "salt"));
        Assert.Null(IngredientMatcher.NearMatch(items, "  Salt  "));
    }

    [Fact]
    public void Ties_prefer_the_fewest_fuzzy_words_then_the_shortest_name()
    {
        // "Chocolat Mars Twix" covers the query with three equal words;
        // "Chocolats Mars Twix" needs a typo match for its first. The row that
        // needed the least squinting is the likelier identity.
        List<InventoryItem> items = [Item("Chocolats Mars Twix"), Item("Chocolat Mars Twix")];

        Assert.Equal("Chocolat Mars Twix",
            IngredientMatcher.NearMatch(items, "Mars Twix Chocolat")?.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_name_has_no_near_match(string? name)
    {
        List<InventoryItem> items = [Item("Salt")];

        Assert.Null(IngredientMatcher.NearMatch(items, name));
        Assert.Null(IngredientMatcher.NearMatch(null, "Salt"));
    }
}
