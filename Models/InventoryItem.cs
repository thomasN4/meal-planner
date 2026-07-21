using System.ComponentModel.DataAnnotations;

namespace MealPlanner.Models;

/// <summary>
/// A single thing the kitchen has (or is out of). One row per ingredient name.
/// </summary>
public class InventoryItem
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public StockLevel Quantity { get; set; } = StockLevel.Medium;

    public IngredientCategory Category { get; set; } = IngredientCategory.Other;

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
