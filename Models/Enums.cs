namespace MealPlanner.Models;

/// <summary>
/// A qualitative stock level. This household rarely measures anything, so we
/// track "how much is left" on a rough scale rather than with numbers/units.
/// </summary>
public enum StockLevel
{
    Out,
    Low,
    Medium,
    High,
}

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
