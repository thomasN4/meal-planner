using System.Text.Json;
using MealPlanner.Data;
using MealPlanner.Models;
using Microsoft.EntityFrameworkCore;

namespace MealPlanner.Services;

/// <summary>One ingredient of a saved recipe — name and free-text quantity.
/// The have/missing flag is deliberately absent: it was a snapshot of the
/// pantry at generation time and would be stale by tonight.</summary>
public sealed record SavedIngredient(string Name, string Quantity);

/// <summary>A recipe the household chose to keep.</summary>
public sealed record SavedRecipe(
    int Id,
    string Title,
    string Description,
    int Minutes,
    IReadOnlyList<SavedIngredient> Ingredients,
    IReadOnlyList<string> Steps,
    DateTime CreatedAt);

/// <summary>
/// The single choke point for saved-recipe reads/writes, owning the JSON
/// (de)serialization of the <see cref="Recipe"/> columns and the length
/// clamping EF's <c>[MaxLength]</c> doesn't enforce — the payload comes from
/// a model, so the service is the trust boundary, same as
/// <see cref="InventoryService"/>.
/// <para>
/// Unlike <see cref="InventoryService"/> there is no retry loop and no
/// notifier publish. The retries exist there because upsert-by-name is
/// read-then-write against a NOCASE unique index; recipes have no natural key
/// and no unique index, so every save is a fresh insert that cannot collide.
/// And there is no out-of-circuit writer (MCP does not touch recipes), so the
/// page that saved refreshes itself instead of a notifier doing it.
/// </para>
/// </summary>
public class RecipeService
{
    private const int MaxIngredients = 40;
    private const int MaxSteps = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDbContextFactory<MealPlannerDbContext> _factory;

    public RecipeService(IDbContextFactory<MealPlannerDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<SavedRecipe>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Recipes
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);
        return rows.Select(ToSaved).ToList();
    }

    /// <summary>
    /// Persists a suggestion. Throws <see cref="ArgumentException"/> when the
    /// title is empty — the UI guards before calling, per the same convention
    /// as <see cref="InventoryService"/>.
    /// </summary>
    public async Task<SavedRecipe> SaveAsync(RecipeSuggestion suggestion, CancellationToken ct = default)
    {
        var title = Clamp(suggestion.Title.Trim(), 100);
        if (title.Length == 0)
        {
            throw new ArgumentException("A recipe needs a title.", nameof(suggestion));
        }

        var ingredients = suggestion.Ingredients
            .Select(i => new SavedIngredient(Clamp(i.Name.Trim(), 100), Clamp(i.Quantity.Trim(), 50)))
            .Where(i => i.Name.Length > 0)
            .Take(MaxIngredients)
            .ToList();

        var steps = suggestion.Steps
            .Select(s => Clamp(s.Trim(), 500))
            .Where(s => s.Length > 0)
            .Take(MaxSteps)
            .ToList();

        var entity = new Recipe
        {
            Title = title,
            Description = Clamp(suggestion.Description.Trim(), 500),
            Minutes = Math.Clamp(suggestion.Minutes, 1, 1440),
            IngredientsJson = JsonSerializer.Serialize(ingredients, JsonOptions),
            StepsJson = JsonSerializer.Serialize(steps, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        };

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Recipes.Add(entity);
        await db.SaveChangesAsync(ct);

        return ToSaved(entity);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var recipe = await db.Recipes.FindAsync([id], ct);
        if (recipe is null)
        {
            return;
        }

        db.Recipes.Remove(recipe);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another circuit already deleted it — nothing to do.
        }
    }

    private static SavedRecipe ToSaved(Recipe recipe) => new(
        recipe.Id,
        recipe.Title,
        recipe.Description,
        recipe.Minutes,
        Deserialize<SavedIngredient>(recipe.IngredientsJson),
        Deserialize<string>(recipe.StepsJson),
        recipe.CreatedAt);

    private static IReadOnlyList<T> Deserialize<T>(string json)
    {
        // This service is the only writer of these columns, but a hand-edited
        // row shouldn't take the whole saved list down with it.
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Clamp(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
