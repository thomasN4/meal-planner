namespace MealPlanner.Tests;

/// <summary>
/// <see cref="ClaudeRecipeGenerator.ParseRecipes"/> reads the output of
/// another process, so it must survive input nobody controls — and it is
/// where the have/missing flag is decided, which the model must not be able
/// to decide alone. No process is spawned here; the function is pure.
/// </summary>
public class RecipeParsingTests
{
    private static IReadOnlySet<string> Pantry(params string[] names) =>
        names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<RecipeSuggestion> Parse(
        string output, IReadOnlySet<string> pantry, out string? problem) =>
        ClaudeRecipeGenerator.ParseRecipes(output, pantry, out problem);

    private static List<RecipeSuggestion> ParseExcluding(
        string output, IReadOnlySet<string> pantry, IReadOnlySet<string> excluded, out string? problem) =>
        ClaudeRecipeGenerator.ParseRecipes(output, pantry, out problem, excluded);

    private const string TwoRecipes =
        """
        {"recipes":[
          {"title":"Fried rice","description":"Weeknight fried rice.","minutes":25,
           "ingredients":[
             {"name":"Rice","quantity":"2 cups","inventoryName":"Rice"},
             {"name":"Soy sauce","quantity":"a splash","inventoryName":null}],
           "steps":["Cook the rice.","Fry everything together."]},
          {"title":"Egg drop soup","description":"Light and fast.","minutes":15,
           "ingredients":[{"name":"Eggs","quantity":"3","inventoryName":"Eggs"}],
           "steps":["Simmer stock.","Whisk in the eggs."]}
        ]}
        """;

    [Fact]
    public void A_well_formed_response_yields_recipes_with_ingredients_and_steps_in_order()
    {
        var recipes = Parse(TwoRecipes, Pantry("Rice", "Eggs"), out var problem);

        Assert.Null(problem);
        Assert.Equal(["Fried rice", "Egg drop soup"], recipes.Select(r => r.Title));
        Assert.Equal(25, recipes[0].Minutes);
        Assert.Equal(["Rice", "Soy sauce"], recipes[0].Ingredients.Select(i => i.Name));
        Assert.Equal(["Cook the rice.", "Fry everything together."], recipes[0].Steps);
    }

    [Fact]
    public void An_inventory_claim_is_verified_case_insensitively()
    {
        var recipes = Parse(
            """
            {"recipes":[{"title":"Pasta night","description":"","minutes":30,
              "ingredients":[{"name":"Spaghetti","quantity":"a box","inventoryName":"PASTA"}],
              "steps":["Boil."]}]}
            """,
            Pantry("pasta"), out _);

        // The model's fuzzy mapping ("Spaghetti" → the pantry's "pasta") is
        // exactly what the claim field is for.
        Assert.True(recipes[0].Ingredients[0].Have);
    }

    [Fact]
    public void A_claim_the_pantry_does_not_hold_is_marked_missing()
    {
        var recipes = Parse(
            """
            {"recipes":[{"title":"Truffle toast","description":"","minutes":10,
              "ingredients":[{"name":"Truffle","quantity":"a shaving","inventoryName":"Truffles"}],
              "steps":["Toast."]}]}
            """,
            Pantry("Bread"), out _);

        // A hallucinated (or injected) claim must degrade to "missing",
        // never to a false "have".
        Assert.False(recipes[0].Ingredients[0].Have);
    }

    [Fact]
    public void A_null_claim_whose_own_name_matches_the_pantry_still_counts_as_have()
    {
        var recipes = Parse(
            """
            {"recipes":[{"title":"Plain rice","description":"","minutes":20,
              "ingredients":[{"name":"rice","quantity":"1 cup","inventoryName":null}],
              "steps":["Cook."]}]}
            """,
            Pantry("Rice"), out _);

        Assert.True(recipes[0].Ingredients[0].Have);
    }

    [Fact]
    public void Surrounding_chatter_does_not_defeat_the_parse()
    {
        var recipes = Parse(
            "Reading configuration…\n" + TwoRecipes + "\nDone.",
            Pantry("Rice", "Eggs"), out var problem);

        Assert.Null(problem);
        Assert.Equal(2, recipes.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("Error: not authenticated")]
    [InlineData("""{"recipes":[{"title":"Soup"}""")] // truncated
    [InlineData("""{"suggestions":[]}""")] // wrong shape
    [InlineData("""{"recipes":"Soup"}""")] // recipes not an array
    public void Unusable_output_yields_no_recipes_and_a_reason(string output)
    {
        var recipes = Parse(output, Pantry("Rice"), out var problem);

        Assert.NotNull(problem);
        Assert.Empty(recipes);
    }

    [Theory]
    [InlineData("""{"description":"no title","minutes":10,"ingredients":[{"name":"Rice","quantity":"","inventoryName":null}],"steps":["Cook."]}""")]
    [InlineData("""{"title":"","description":"empty title","minutes":10,"ingredients":[{"name":"Rice","quantity":"","inventoryName":null}],"steps":["Cook."]}""")]
    [InlineData("""{"title":"No minutes","description":"","ingredients":[{"name":"Rice","quantity":"","inventoryName":null}],"steps":["Cook."]}""")]
    [InlineData("""{"title":"Stringy minutes","description":"","minutes":"20","ingredients":[{"name":"Rice","quantity":"","inventoryName":null}],"steps":["Cook."]}""")]
    [InlineData("""{"title":"No steps","description":"","minutes":10,"ingredients":[{"name":"Rice","quantity":"","inventoryName":null}],"steps":[]}""")]
    public void A_recipe_missing_a_required_field_is_dropped_without_failing_the_batch(string badRecipe)
    {
        var recipes = Parse(
            $$"""
            {"recipes":[{{badRecipe}},
              {"title":"Survivor","description":"","minutes":10,
               "ingredients":[{"name":"Rice","quantity":"1 cup","inventoryName":null}],
               "steps":["Cook."]}]}
            """,
            Pantry("Rice"), out var problem);

        Assert.Equal("1 unusable recipe(s)", problem);
        Assert.Equal(["Survivor"], recipes.Select(r => r.Title));
    }

    [Fact]
    public void A_recipe_with_no_usable_ingredients_is_dropped()
    {
        var recipes = Parse(
            """
            {"recipes":[{"title":"Air soup","description":"","minutes":5,
              "ingredients":[{"name":"","quantity":"","inventoryName":null},"nonsense"],
              "steps":["Stir."]}]}
            """,
            Pantry("Rice"), out var problem);

        Assert.Empty(recipes);
        Assert.Equal("1 unusable recipe(s), none left", problem);
    }

    [Fact]
    public void An_empty_recipes_array_reports_a_problem()
    {
        var recipes = Parse("""{"recipes":[]}""", Pantry("Rice"), out var problem);

        Assert.Empty(recipes);
        Assert.Equal("no recipes returned", problem);
    }

    [Theory]
    [InlineData(100000, 1440)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void Absurd_minutes_are_clamped_rather_than_dropped(int minutes, int expected)
    {
        // A wild time estimate is a bad estimate, not a bad recipe.
        var recipes = Parse(
            $$"""
            {"recipes":[{"title":"Slow feast","description":"","minutes":{{minutes}},
              "ingredients":[{"name":"Rice","quantity":"","inventoryName":null}],
              "steps":["Wait."]}]}
            """,
            Pantry("Rice"), out _);

        Assert.Equal(expected, recipes[0].Minutes);
    }

    [Fact]
    public void A_recipe_claiming_an_excluded_inventory_row_is_dropped()
    {
        // The first recipe reaches for the row the household said to avoid.
        // Dropped whole rather than stripped of the ingredient: the steps would
        // still call for it, and these are alternative choices, so losing one
        // still leaves the other.
        var recipes = ParseExcluding(TwoRecipes, Pantry("Rice", "Eggs"), Pantry("RICE"), out var problem);

        Assert.Equal(["Egg drop soup"], recipes.Select(r => r.Title));
        Assert.Equal("1 recipe(s) used an excluded ingredient", problem);
    }

    [Fact]
    public void A_recipe_naming_an_excluded_row_without_a_claim_is_dropped()
    {
        var recipes = ParseExcluding(
            """
            {"recipes":[{"title":"Pea soup","description":"","minutes":20,
              "ingredients":[{"name":"peas","quantity":"a bag","inventoryName":null}],
              "steps":["Simmer."]}]}
            """,
            Pantry("Peas"), Pantry("Peas"), out _);

        // No claim, but the name is the row verbatim — the same fallback the
        // have flag uses one line above. Without it, dropping the claim field
        // is all it takes to slip an exclusion past.
        Assert.Empty(recipes);
    }

    [Fact]
    public void An_exclusion_that_matches_nothing_leaves_every_recipe_standing()
    {
        var recipes = ParseExcluding(
            TwoRecipes, Pantry("Rice", "Eggs"), Pantry("Anchovies"), out var problem);

        // The guard against an over-eager check quietly emptying the page.
        Assert.Equal(2, recipes.Count);
        Assert.Null(problem);
    }

    [Fact]
    public void Every_recipe_dropped_for_an_exclusion_still_reports_a_reason()
    {
        var recipes = ParseExcluding(
            TwoRecipes, Pantry("Rice", "Eggs"), Pantry("Rice", "Eggs"), out var problem);

        Assert.Empty(recipes);
        Assert.Equal("2 recipe(s) used an excluded ingredient, none left", problem);
    }

    [Fact]
    public void A_paraphrase_of_an_excluded_row_is_not_caught()
    {
        var recipes = ParseExcluding(
            """
            {"recipes":[{"title":"Petits pois","description":"","minutes":20,
              "ingredients":[{"name":"petits pois","quantity":"a bag","inventoryName":null}],
              "steps":["Simmer."]}]}
            """,
            Pantry("Peas"), Pantry("Peas"), out _);

        // This test asserts the limit rather than a guarantee, and it cannot be
        // broken to prove it bites — there is no line to delete, because there
        // is no line. Verification catches a *claim* on an excluded row; a
        // synonym is only ever asked for in the prompt. Deleting this test
        // would leave the bound undocumented, which is how it turns into a
        // promise nobody made. Same honesty as
        // Adding_reports_what_it_did_in_the_live_region.
        Assert.Single(recipes);
    }
}
