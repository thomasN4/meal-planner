using System.ComponentModel.DataAnnotations;

namespace MealPlanner.Models;

/// <summary>
/// A saved recipe suggestion. Ingredients and steps live in JSON string
/// columns rather than child tables: nothing ever queries by ingredient in a
/// one-household app, and <c>RecipeService</c> owns the (de)serialization the
/// same way it owns length clamping — the service is the trust boundary.
/// No unique index on Title: saving the same suggestion twice is a user
/// choice, not a conflict.
/// </summary>
public class Recipe
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    public int Minutes { get; set; }

    /// <summary>JSON array of <c>{"name","quantity"}</c> objects. The
    /// have/missing split is deliberately not stored — it is a snapshot of the
    /// pantry at generation time and goes stale the moment anything is cooked.</summary>
    public string IngredientsJson { get; set; } = "[]";

    /// <summary>JSON array of step strings.</summary>
    public string StepsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
