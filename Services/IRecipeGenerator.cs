using MealPlanner.Models;

namespace MealPlanner.Services;

/// <summary>What the household asked for: meal type, optional time budget in
/// minutes, and inventory item names they want used up.</summary>
public sealed record RecipeRequest(
    MealType MealType,
    int? MaxMinutes,
    IReadOnlyList<string> MustUse);

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
