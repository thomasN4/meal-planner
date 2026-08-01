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

/// <summary>
/// What kind of meal a recipe request is for. An enum rather than free text:
/// it feeds a select box and the generation prompt, and free text would be a
/// second injection surface for no benefit in a one-household app.
/// </summary>
public enum MealType
{
    Breakfast,
    Lunch,
    Dinner,
    Snack,
    Dessert,
}
