namespace MealPlanner.Services;

/// <summary>
/// Settings for recipe generation, bound from the "RecipeGeneration" section
/// of appsettings.json. Several defaults deliberately diverge from
/// <see cref="CategorizationOptions"/> — don't "fix" them to match.
/// </summary>
public sealed class RecipeGenerationOptions
{
    public const string SectionName = "RecipeGeneration";

    /// <summary>
    /// Turns the feature off: the /recipes page renders a note instead of the
    /// Generate button, and no claude process is ever spawned.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Resolved on PATH unless given an absolute path.</summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>
    /// "sonnet" answers at interactive latency; "opus" is one config edit away
    /// for better ideas at a longer wait.
    /// </summary>
    public string Model { get; set; } = "sonnet";

    /// <summary>
    /// Deliberately "medium" where classification uses "low". Classification's
    /// answer is one enum value with no deliberation to buy; three coherent
    /// recipes constrained by pantry, time budget and must-use items is a task
    /// where extra thinking actually shows up in the output.
    /// </summary>
    public string Effort { get; set; } = "medium";

    /// <summary>
    /// Higher than classification's 90s: long-form generation at medium effort
    /// can legitimately outrun the classifier's ceiling. The real limit is how
    /// long someone will watch a spinner, and Cancel is always on screen.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 180;
}
