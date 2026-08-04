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

/// <summary>
/// What the household wants done with one inventory item when recipes are
/// generated. An item holds at most one of these.
/// <para>
/// <see cref="UseUp"/> and <see cref="Include"/> both mean "in <em>every</em>
/// recipe", which only makes sense once you know the generator returns 2–3
/// recipes as <em>alternative choices for one meal</em> — the household cooks
/// exactly one of them. A constraint that held in only one of the three would
/// be a coin flip. UseUp goes further and asks the recipe to finish the stocked
/// amount, which is why the two are not one role.
/// </para>
/// </summary>
public enum IngredientRole
{
    UseUp,
    Include,
    Exclude,
}
