namespace MealPlanner.Models;

/// <summary>
/// Where an ingredient lives / how it's grouped. FreshHerbs and DrySeasonings
/// are broken out from the obvious groups because this kitchen cares about them.
/// </summary>
public enum IngredientCategory
{
    Produce,
    FreshHerbs,
    DrySeasonings,
    MeatAndSeafood,
    Dairy,
    Grains,
    Canned,
    Frozen,
    Condiments,
    Baking,
    Beverages,
    Snacks,
    Other,
}
