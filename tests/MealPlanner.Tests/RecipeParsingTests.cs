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
}
