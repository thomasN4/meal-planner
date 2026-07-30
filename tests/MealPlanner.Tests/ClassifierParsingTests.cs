namespace MealPlanner.Tests;

/// <summary>
/// <see cref="ClaudeIngredientClassifier.ParseResults"/> reads the output of
/// another process, so it is the one place in the feature that must survive
/// input nobody controls. No process is spawned here — the function is pure.
/// </summary>
public class ClassifierParsingTests
{
    private static IngredientCategory?[] Parse(string output, int expectedCount, out string? problem) =>
        ClaudeIngredientClassifier.ParseResults(output, expectedCount, out problem);

    [Fact]
    public void A_well_formed_response_maps_every_name()
    {
        var categories = Parse(
            """{"results":[{"index":0,"category":"FreshHerbs"},{"index":1,"category":"Grains"}]}""",
            2, out var problem);

        Assert.Null(problem);
        Assert.Equal([IngredientCategory.FreshHerbs, IngredientCategory.Grains], categories);
    }

    [Fact]
    public void Results_are_placed_by_index_not_by_arrival_order()
    {
        // The model is free to answer out of order, and does not have to echo
        // the name it was given — index is the only reliable join.
        var categories = Parse(
            """{"results":[{"index":2,"category":"Dairy"},{"index":0,"category":"Produce"}]}""",
            3, out _);

        Assert.Equal(IngredientCategory.Produce, categories[0]);
        Assert.Null(categories[1]);
        Assert.Equal(IngredientCategory.Dairy, categories[2]);
    }

    [Fact]
    public void Category_names_are_matched_case_insensitively()
    {
        var categories = Parse("""{"results":[{"index":0,"category":"freshherbs"}]}""", 1, out _);

        Assert.Equal(IngredientCategory.FreshHerbs, categories[0]);
    }

    [Fact]
    public void Surrounding_chatter_does_not_defeat_the_parse()
    {
        // The schema pins the model's answer; nothing pins what the CLI itself
        // may print around it.
        var categories = Parse(
            "Reading configuration…\n{\"results\":[{\"index\":0,\"category\":\"Baking\"}]}\n",
            1, out var problem);

        Assert.Null(problem);
        Assert.Equal(IngredientCategory.Baking, categories[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("Error: not authenticated")]
    [InlineData("""{"results":[{"index":0,"category":"Grains"}""")] // truncated
    [InlineData("""{"categories":["Grains"]}""")] // wrong shape
    [InlineData("""{"results":"Grains"}""")] // results not an array
    public void Unusable_output_yields_all_nulls_and_a_reason(string output)
    {
        var categories = Parse(output, 2, out var problem);

        Assert.NotNull(problem);
        Assert.Equal([null, null], categories);
    }

    [Theory]
    [InlineData("Vegetables")] // not a category
    [InlineData("999")] // Enum.TryParse accepts this; Enum.IsDefined is what rejects it
    [InlineData("-1")]
    [InlineData("")]
    public void An_unknown_category_drops_only_that_entry(string category)
    {
        var categories = Parse(
            $$"""{"results":[{"index":0,"category":"{{category}}"},{"index":1,"category":"Dairy"}]}""",
            2, out var problem);

        Assert.NotNull(problem);
        Assert.Null(categories[0]);
        // One bad row must not cost its neighbours their categories.
        Assert.Equal(IngredientCategory.Dairy, categories[1]);
    }

    [Theory]
    [InlineData("""{"results":[{"index":7,"category":"Dairy"},{"index":0,"category":"Grains"}]}""")]
    [InlineData("""{"results":[{"index":-1,"category":"Dairy"},{"index":0,"category":"Grains"}]}""")]
    [InlineData("""{"results":[{"category":"Dairy"},{"index":0,"category":"Grains"}]}""")]
    [InlineData("""{"results":[{"index":"0","category":"Dairy"},{"index":0,"category":"Grains"}]}""")]
    public void An_index_that_cannot_be_trusted_drops_only_that_entry(string output)
    {
        // An out-of-range index reaching the array would be an IndexOutOfRange
        // in a background loop, i.e. the classifier taking the app down.
        var categories = Parse(output, 2, out var problem);

        Assert.NotNull(problem);
        Assert.Equal(IngredientCategory.Grains, categories[0]);
        Assert.Null(categories[1]);
    }

    [Fact]
    public void An_empty_results_array_reports_every_name_as_unanswered()
    {
        var categories = Parse("""{"results":[]}""", 3, out var problem);

        Assert.Equal("3 name(s) went unanswered", problem);
        Assert.Equal([null, null, null], categories);
    }

    [Fact]
    public void A_duplicate_index_does_not_throw()
    {
        var categories = Parse(
            """{"results":[{"index":0,"category":"Dairy"},{"index":0,"category":"Grains"}]}""",
            1, out _);

        Assert.Equal(IngredientCategory.Grains, categories[0]);
    }
}
