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

/// <summary>
/// The three features that call a model. Spelled exactly like the
/// <c>appsettings.json</c> section names (<c>Categorization</c>, not
/// <c>IngredientClassification</c>) so the pass that wires these settings up
/// maps one to one instead of carrying a second lookup table.
/// </summary>
public enum AiFeature
{
    Categorization,
    RecipeGeneration,
    ReceiptScanning,
}

/// <summary>
/// Who answers the call. <see cref="ClaudeCli"/> is today's arrangement — the
/// <c>claude</c> CLI on a subscription, needing no key of its own; the rest are
/// HTTP APIs behind a key.
/// </summary>
public enum AiProvider
{
    ClaudeCli,
    AnthropicApi,
    OpenAi,
    OpenRouter,
}

/// <summary>
/// How hard the model should think.
/// <para>
/// An enum, against the <c>Effort { get; set; } = "low"</c> string the three
/// options classes carry. A string tempts a caller to pass the stored word
/// straight through, and the providers do not agree on the spelling —
/// <c>--effort low</c> on the CLI, <c>output_config.effort</c> on the Anthropic
/// API, <c>reasoning.effort</c> on OpenAI. An enum forces an explicit
/// per-provider spelling function on the day that matters.
/// </para>
/// <para>
/// <see cref="Minimal"/> exists only because OpenAI offers it; nothing else
/// does, and <c>AiCatalog</c> is what keeps it off the other providers'
/// dropdowns.
/// </para>
/// </summary>
public enum AiEffort
{
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}
