using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>
/// What the household asked for: meal type, an optional time budget in
/// minutes, and inventory item names sorted into the three
/// <see cref="IngredientRole"/>s.
/// <para>
/// <paramref name="Brief"/> is free text: whatever the household felt like
/// saying that a select box cannot ("something warming", "no oven"). It is
/// clamped by <see cref="ClaudeRecipeGenerator"/> rather than here — this
/// record is a DTO, not a trust boundary.
/// </para>
/// <para>
/// Three lists rather than the page's own name→role dictionary, because the
/// prompt payload needs three arrays either way and a test asserting on one of
/// them says what it means. The cost is that this record cannot enforce "a name
/// appears in at most one list" — that invariant lives in the dictionary on
/// <c>Recipes.razor</c>, which is the only thing that builds one of these.
/// </para>
/// </summary>
public sealed record RecipeRequest(
    MealType MealType,
    int? MaxMinutes,
    IReadOnlyList<string> UseUp,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    string Brief);

/// <summary>
/// One ingredient of a suggested recipe. <paramref name="Have"/> is computed
/// by the app against the real inventory, never taken from the model's word —
/// see <see cref="ClaudeRecipeGenerator.ParseRecipes"/>.
/// </summary>
public sealed record RecipeIngredient(string Name, string Quantity, bool Have);

/// <summary>A single generated recipe suggestion, ephemeral until saved.</summary>
public sealed record RecipeSuggestion(
    string Title,
    string Description,
    int Minutes,
    IReadOnlyList<RecipeIngredient> Ingredients,
    IReadOnlyList<string> Steps);

/// <summary>
/// Generates recipe suggestions from the current inventory. The caller
/// supplies the inventory snapshot: the generator has no database access, so
/// the same snapshot drives both the prompt and the have/missing verification.
/// <para>
/// Implementations must not throw — an empty list means "nothing usable came
/// back" and the log carries the reason. The only exception let out is
/// <see cref="OperationCanceledException"/> when the caller's own token fired.
/// </para>
/// </summary>
public interface IRecipeGenerator
{
    Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(
        RecipeRequest request,
        IReadOnlyList<InventoryItem> inventory,
        CancellationToken ct = default);
}
